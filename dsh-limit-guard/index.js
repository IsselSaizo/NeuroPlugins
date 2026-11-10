// dsh-limit-guard — Host plugin for DeepSeek Harness.
//
// Watches usage limits before every agent step and, when a limit is close,
// tells the agent to stop, write a handoff file and end the turn, so work is
// not lost when the quota runs out. Sources are pluggable and optional:
//
//   context  – the session context window (ctx.tokenMeter), always available
//   wiolett  – Wiolett inference subscription windows (GET /v1/usage, x-api-key)
//   codex    – ChatGPT/Codex subscription windows (chatgpt.com/backend-api/wham/usage)
//              using the OAuth credential stored by `dsh-openai-codex-auth`
//
// v0.3 adds:
//   * a deterministic stop latch: after the wrap-up notice the agent gets a small
//     step budget, then automatic steps (goal rounds, mid-turn continuations) are
//     refused until the window resets or a human explicitly continues;
//   * goal integration: an active goal is moved to `blocked` (code `usage-limit`)
//     instead of being re-driven into the exhausted quota round after round;
//   * futile-retry suppression: a request that failed because the quota is gone
//     is not retried by `dsh-llm-retry`;
//   * a human message above the warn threshold no longer triggers a second
//     handoff (that overwrote the good STATUS.md in the "continue" chat);
//   * per-task accounting (how many % of each window a task consumed), history,
//     pace/ETA and a JSON API for the Settings → Limit Guard page.
//
// Account sources apply only to agents whose provider matches the source.
// No secret ever leaves this module; only percentages and reset times.

import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'
import fs from 'node:fs'
import os from 'node:os'
import nodePath from 'node:path'

export const name = 'limit-guard'
export const VERSION = '0.3.0'
// Every service is optional and read lazily with ctx.get(), so the guard stays
// harmless in profiles without them. Configuration fields and defaults are
// documented in README.md and cordis.patch.yml.

/**
 * Import a package shipped with the Harness. A bundle installed from a local
 * directory is linked, so bare `@deepseek-ai/*` specifiers do not resolve
 * from its real path; fall back to resolving from the Host entry module.
 */
async function hostImport(spec) {
  try {
    return await import(spec)
  } catch (first) {
    const anchors = [process.argv[1], process.execPath].filter(Boolean)
    for (const anchor of anchors) {
      try {
        const resolved = createRequire(anchor).resolve(spec)
        return await import(pathToFileURL(resolved).href)
      } catch {}
    }
    throw first
  }
}

const { defineTool } = await hostImport('@deepseek-ai/dsh-tools')
let credentialRef = (value) => value
try {
  credentialRef = (await hostImport('@deepseek-ai/dsh-credentials')).credentialRef ?? credentialRef
} catch {}
let dshHome = () => process.env.DSH_HOME?.trim() || nodePath.join(os.homedir(), '.dsh')
try {
  const hp = await hostImport('@deepseek-ai/dsh-home-paths')
  if (typeof hp.resolveDshHome === 'function') dshHome = () => hp.resolveDshHome()
} catch {}

const NOTICE_SOURCE = 'limit-guard'
const HOUR = 3600 * 1000

function pct(n) {
  return `${Math.round(n * 10) / 10}%`
}

function fmtTime(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return 'unknown'
  const d = new Date(ms)
  return `${d.toLocaleString('ru-RU', { hour12: false })} (${d.toISOString()})`
}

function fmtDuration(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return ''
  const m = Math.round(ms / 60000)
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60)
  if (h < 48) return `${h} h ${m % 60} min`
  return `${Math.floor(h / 24)} d ${h % 24} h`
}

function randomId() {
  return globalThis.crypto.randomUUID()
}

function noticeMessage(text, summary) {
  return Object.freeze({
    id: randomId(),
    role: 'user',
    content: Object.freeze([Object.freeze({ type: 'text', text })]),
    source: Object.freeze({ kind: NOTICE_SOURCE, form: 'notice', summary }),
  })
}

async function fetchJson(url, headers, timeoutMs) {
  const response = await fetch(url, {
    method: 'GET',
    redirect: 'error',
    headers: { Accept: 'application/json', ...headers },
    signal: AbortSignal.timeout(timeoutMs),
  })
  if (!response.ok) {
    await response.body?.cancel().catch(() => {})
    throw new Error(`HTTP ${response.status}`)
  }
  const text = await response.text()
  if (text.length > 1024 * 1024) throw new Error('response too large')
  return JSON.parse(text)
}

function messageText(message, max = 160) {
  const parts = []
  const content = message?.content
  if (typeof content === 'string') parts.push(content)
  else if (Array.isArray(content)) for (const b of content) if (b && typeof b.text === 'string') parts.push(b.text)
  const text = parts.join(' ').replace(/\s+/g, ' ').trim()
  return text.length > max ? `${text.slice(0, max - 1)}…` : text
}

function sessionIdOf(session) {
  try { return String(session?.header?.id ?? session?.id ?? '') || undefined } catch { return undefined }
}

function parentIdOf(session) {
  try {
    const p = session?.header?.parentSession
    if (typeof p === 'string') return p
    if (p && typeof p === 'object') return String(p.id ?? p.sessionId ?? '') || undefined
  } catch {}
  return undefined
}

// ---------- source parsers (pure) ----------

/** Wiolett: { api, subscription: { '5h'|'7d'|'30d': { configured, percentage, recoveryAt } } } */
export function parseWiolett(payload) {
  const windows = []
  const push = (label, w) => {
    if (!w || typeof w !== 'object' || w.configured !== true) return
    const used = Number(w.percentage)
    if (!Number.isFinite(used)) return
    const resetAt = Date.parse(w.recoveryAt)
    windows.push({ label, usedPercent: Math.min(100, Math.max(0, used)), resetAt: Number.isFinite(resetAt) ? resetAt : 0 })
  }
  push('api', payload?.api)
  const sub = payload?.subscription
  if (sub && typeof sub === 'object') for (const key of Object.keys(sub).slice(0, 8)) push(key, sub[key])
  return { windows, limited: windows.some(w => w.usedPercent >= 100) }
}

/** Codex: { rate_limit: { primary_window, secondary_window, limit_reached }, additional_rate_limits: [...] } */
export function parseCodex(payload, now = Date.now()) {
  const windows = []
  const pushWindow = (prefix, w) => {
    if (!w || typeof w !== 'object') return
    const used = Number(w.used_percent)
    if (!Number.isFinite(used)) return
    const seconds = Number(w.limit_window_seconds)
    const resetAtSec = Number(w.reset_at)
    const resetAfter = Number(w.reset_after_seconds)
    const resetAt = Number.isFinite(resetAtSec) ? resetAtSec * 1000 : Number.isFinite(resetAfter) ? now + Math.max(0, resetAfter) * 1000 : 0
    const span = Number.isFinite(seconds) && seconds > 0 ? (seconds >= 86400 ? `${Math.round(seconds / 86400)}d` : `${Math.round(seconds / 3600)}h`) : '?'
    windows.push({ label: `${prefix} ${span}`, usedPercent: Math.min(100, Math.max(0, used)), resetAt })
  }
  const add = (prefix, rl) => {
    if (!rl || typeof rl !== 'object') return
    pushWindow(prefix, rl.primary_window)
    pushWindow(prefix, rl.secondary_window)
  }
  add('Codex', payload?.rate_limit)
  if (Array.isArray(payload?.additional_rate_limits)) {
    for (const entry of payload.additional_rate_limits.slice(0, 8)) {
      if (entry && typeof entry.limit_name === 'string' && entry.limit_name.length <= 80) add(entry.limit_name, entry.rate_limit)
    }
  }
  const rl = payload?.rate_limit
  const limited = !!rl && typeof rl === 'object' && (rl.limit_reached === true || rl.allowed === false)
  return { windows, limited }
}

/**
 * Pace of one window from (time, used%) samples taken since its last reset.
 * Returns %/hour and, when the window would run out before its reset, the ETA.
 */
export function computePace(samples, now = Date.now(), lookbackMs = 90 * 60000) {
  const recent = samples.filter(s => now - s.t <= lookbackMs)
  if (recent.length < 2) return undefined
  // keep only the tail after the last drop (a reset or a sliding-window recovery)
  let from = 0
  for (let i = 1; i < recent.length; i++) if (recent[i].used < recent[i - 1].used - 0.5) from = i
  const tail = recent.slice(from)
  if (tail.length < 2) return undefined
  const first = tail[0], last = tail[tail.length - 1]
  const span = last.t - first.t
  if (span < 10 * 60000) return undefined
  const perHour = Math.max(0, (last.used - first.used) / (span / HOUR))
  const out = { perHour: Math.round(perHour * 10) / 10, spanMin: Math.round(span / 60000) }
  if (perHour > 0.05) {
    const etaAt = now + ((100 - last.used) / perHour) * HOUR
    if (!last.resetAt || etaAt < last.resetAt) out.etaAt = Math.round(etaAt)
  }
  return out
}

/** Did the window reset between two readings? */
export function windowRolled(start, end) {
  if (!start || !end) return false
  if (start.resetAt && end.resetAt && end.resetAt - start.resetAt > 30 * 60000) return true
  return end.usedPercent < start.usedPercent - 5
}

const SOURCE_DEFAULTS = {
  wiolett: { enabled: true, url: 'https://ops.wiolett.net/api/inference/v1/usage', credential: 'OPFIELD_API_KEY', providers: ['opfield'] },
  codex: { enabled: true, url: 'https://chatgpt.com/backend-api/wham/usage', credential: 'PI_OAUTH_OPENAI_CODEX', providers: ['openai-codex'] },
}

/** Settings a user may change from the dashboard; persisted in the state file. */
const TUNABLE = {
  enabled: 'boolean',
  blockGoals: 'boolean',
  stopRetryOnQuota: 'boolean',
  warnPercent: 'percent',
  hardStopPercent: 'percent',
  contextWarnPercent: 'percent',
  graceSteps: 'steps',
}

export function apply(ctx, rawConfig) {
  const config = {
    enabled: true, warnPercent: 90, contextWarnPercent: 85, hardStopPercent: 98, graceSteps: 8, handoffSteps: 2,
    blockGoals: true, stopRetryOnQuota: true, historySize: 60,
    cacheMs: 60000, requestTimeoutMs: 6000, handoffPath: '.agent/handoff/STATUS.md', stateFile: '',
    ...(rawConfig ?? {}),
  }
  config.wiolett = { ...SOURCE_DEFAULTS.wiolett, ...(rawConfig?.wiolett ?? {}) }
  config.codex = { ...SOURCE_DEFAULTS.codex, ...(rawConfig?.codex ?? {}) }
  const startedAt = Date.now()
  const cache = new Map() // source -> { at, reading | error }
  const inflight = new Map()
  const lastForced = new Map()
  const samples = new Map() // `${source}:${label}` -> [{ t, used, resetAt }]
  const perAgent = new WeakMap() // agent -> guard state
  const openTasks = new Map() // agent -> task
  const sessionParent = new Map() // subagent session id -> parent session id
  const sessionTask = new Map() // session id -> open task
  const sessionEnd = new Map() // session id -> last turn/end kind

  // ---------- persistent state ----------
  const statePath = config.stateFile || nodePath.join(dshHome(), 'limit-guard', 'state.json')
  const store = { version: 1, settings: {}, history: [], events: [] }
  try {
    const raw = JSON.parse(fs.readFileSync(statePath, 'utf8'))
    if (raw && typeof raw === 'object') {
      if (raw.settings && typeof raw.settings === 'object') store.settings = sanitizeSettings(raw.settings)
      if (Array.isArray(raw.history)) store.history = raw.history.slice(0, config.historySize)
      if (Array.isArray(raw.events)) store.events = raw.events.slice(0, 80)
    }
  } catch {}
  let saveTimer
  function saveNow() {
    clearTimeout(saveTimer); saveTimer = undefined
    try {
      fs.mkdirSync(nodePath.dirname(statePath), { recursive: true })
      const tmp = `${statePath}.${process.pid}.tmp`
      fs.writeFileSync(tmp, JSON.stringify(store, null, 1))
      fs.renameSync(tmp, statePath)
    } catch (error) {
      ctx.logger?.warn?.('limit-guard: cannot save state %s', String(error))
    }
  }
  function scheduleSave() {
    if (saveTimer) return
    saveTimer = setTimeout(saveNow, 1500)
    saveTimer.unref?.()
  }
  try { ctx.effect(() => () => { if (saveTimer) saveNow() }, 'limit-guard: flush state') } catch {}

  function sanitizeSettings(input) {
    const out = {}
    for (const [key, kind] of Object.entries(TUNABLE)) {
      if (!Object.prototype.hasOwnProperty.call(input, key)) continue
      const v = input[key]
      if (kind === 'boolean' && typeof v === 'boolean') out[key] = v
      if (kind === 'percent' && Number.isFinite(v) && v >= 1 && v <= 101) out[key] = Math.round(v * 10) / 10
      if (kind === 'steps' && Number.isInteger(v) && v >= 1 && v <= 50) out[key] = v
    }
    return out
  }
  const eff = (key) => (Object.prototype.hasOwnProperty.call(store.settings, key) ? store.settings[key] : config[key])

  function logEvent(kind, agent, detail) {
    const sid = agent ? sessionIdOf(agent.session) : undefined
    store.events.unshift({ ts: Date.now(), kind, session: sid ? sid.slice(0, 8) : '', detail: String(detail ?? '').slice(0, 240) })
    if (store.events.length > 80) store.events.length = 80
    scheduleSave()
  }

  function agentState(agent) {
    let s = perAgent.get(agent)
    if (!s) {
      s = { mode: 'normal', reason: '', since: 0, stepsSinceWarn: 0, handoffWritten: false, stepsAfterHandoff: 0, hardWarned: false, ctxWarned: new Set(), goalBlocked: false }
      perAgent.set(agent, s)
    }
    return s
  }
  function resetLatch(s) {
    s.mode = 'normal'; s.reason = ''; s.since = 0; s.stepsSinceWarn = 0; s.handoffWritten = false; s.stepsAfterHandoff = 0; s.hardWarned = false; s.goalBlocked = false
  }

  async function resolveCredential(refName) {
    const credentials = ctx.get('credentials')
    if (!credentials) return undefined
    try {
      const hit = await credentials.resolve(credentialRef(refName))
      return hit?.value
    } catch {
      return undefined
    }
  }

  const sources = {
    wiolett: {
      enabled: () => config.wiolett.enabled,
      providers: () => config.wiolett.providers,
      async read() {
        const key = await resolveCredential(config.wiolett.credential)
        if (!key) throw new Error(`credential ${config.wiolett.credential} is not set`)
        return parseWiolett(await fetchJson(config.wiolett.url, { 'x-api-key': key }, config.requestTimeoutMs))
      },
    },
    codex: {
      enabled: () => config.codex.enabled,
      providers: () => config.codex.providers,
      async read() {
        const raw = await resolveCredential(config.codex.credential)
        if (!raw) throw new Error('OpenAI Codex is not signed in')
        let cred
        try { cred = JSON.parse(raw) } catch { throw new Error('unreadable Codex credential') }
        if (typeof cred?.access !== 'string' || typeof cred?.accountId !== 'string') throw new Error('unreadable Codex credential')
        if (typeof cred.expires === 'number' && cred.expires < Date.now()) throw new Error('Codex token expired; it refreshes on the next Codex request')
        return parseCodex(await fetchJson(config.codex.url, {
          Authorization: `Bearer ${cred.access}`,
          'chatgpt-account-id': cred.accountId,
        }, config.requestTimeoutMs))
      },
    },
  }

  function recordSamples(id, reading) {
    const now = Date.now()
    for (const w of reading.windows) {
      const key = `${id}:${w.label}`
      let list = samples.get(key)
      if (!list) { list = []; samples.set(key, list) }
      const last = list[list.length - 1]
      if (last && now - last.t < 20000) list[list.length - 1] = { t: now, used: w.usedPercent, resetAt: w.resetAt }
      else list.push({ t: now, used: w.usedPercent, resetAt: w.resetAt })
      while (list.length > 0 && now - list[0].t > 6 * HOUR) list.shift()
      if (list.length > 400) list.splice(0, list.length - 400)
    }
  }

  async function readSource(id, { force = false } = {}) {
    const hit = cache.get(id)
    // forced refreshes are rate-limited so a chatty dashboard cannot hammer the provider
    if (force && Date.now() - (lastForced.get(id) ?? 0) < 15000) force = false
    if (!force && hit && Date.now() - hit.at < config.cacheMs) return hit
    if (inflight.has(id)) return inflight.get(id)
    if (force) lastForced.set(id, Date.now())
    const p = (async () => {
      let entry
      try {
        entry = { at: Date.now(), reading: await sources[id].read() }
        recordSamples(id, entry.reading)
      } catch (error) {
        entry = { at: Date.now(), error: String(error?.message ?? error).slice(0, 200) }
      }
      cache.set(id, entry)
      return entry
    })().finally(() => inflight.delete(id))
    inflight.set(id, p)
    return p
  }

  function providerOf(agent) {
    try {
      const rc = agent?.session?.requestContext?.()
      if (rc?.provider) return String(rc.provider)
    } catch {}
    const p = agent?.options?.provider
    return typeof p === 'string' ? p : undefined
  }

  function sourcesFor(agent) {
    const provider = providerOf(agent)
    return Object.keys(sources).filter(id => sources[id].enabled() && provider !== undefined && sources[id].providers().includes(provider))
  }

  function contextReading(agent) {
    const meter = ctx.get('tokenMeter')
    const session = agent?.session
    if (!meter || !session) return undefined
    try {
      const window = session.requestContext?.()?.contextWindow
      if (!Number.isFinite(window) || window <= 0) return undefined
      const m = meter.measure(session)
      const used = m.totalTokens
      return { label: 'context', usedPercent: Math.min(100, (used / window) * 100), usedTokens: used, windowTokens: window }
    } catch {
      return undefined
    }
  }

  function cwdOf(agent) {
    try {
      const wd = ctx.get('workingDirectory')
      if (wd && agent?.session) return wd.get(agent.session)
    } catch {}
    return agent?.session?.header?.cwd
  }

  async function snapshot(agent, opts) {
    const account = []
    for (const id of sourcesFor(agent)) {
      const entry = await readSource(id, opts)
      account.push({ source: id, ...entry })
    }
    return { provider: providerOf(agent), context: contextReading(agent), account }
  }

  function worstAccount(snap) {
    let worst
    for (const s of snap.account) {
      if (!s.reading) continue
      for (const w of s.reading.windows) {
        if (!worst || w.usedPercent > worst.usedPercent) worst = { ...w, source: s.source }
      }
      if (s.reading.limited && (!worst || worst.usedPercent < 100)) worst = { label: 'limit reached', usedPercent: 100, resetAt: 0, source: s.source }
    }
    return worst
  }

  function flatWindows(snap) {
    const out = []
    for (const s of snap.account) {
      if (!s.reading) continue
      for (const w of s.reading.windows) out.push({ key: `${s.source}:${w.label}`, source: s.source, label: w.label, usedPercent: w.usedPercent, resetAt: w.resetAt })
    }
    return out
  }

  function handoffFile(agent) {
    const cwd = cwdOf(agent)
    return cwd ? `${String(cwd).replace(/[\\/]+$/, '')}/${config.handoffPath}` : config.handoffPath
  }

  function handoffText(agent, reason, isSubagent) {
    const path = handoffFile(agent)
    if (isSubagent) {
      return [
        `⚠️ LIMIT GUARD: ${reason}`,
        'Stop starting new work now. Finish only the edit in progress, then end your turn with a compact report to your parent: what is done, what is not, files touched, the exact next step.',
        `The guard refuses further automatic steps after ${eff('graceSteps')} steps.`,
      ].join('\n')
    }
    return [
      `⚠️ LIMIT GUARD: ${reason}`,
      'Stop feature work now. Do not start new edits, builds or subagents.',
      '1. Finish or revert only the edit that is half-applied, so the project compiles as before.',
      `2. Write the handoff file \`${path}\` (create folders if needed; overwrite an older one) in the user's language with these sections:`,
      '   - Goal: the task in 1-3 sentences, as the user stated it.',
      '   - Done: what is finished and verified.',
      '   - In progress: what is partially done and its exact state.',
      '   - Next steps: a numbered list starting with the very next action.',
      '   - Files touched: paths with one line each.',
      '   - Open questions / risks.',
      `   - Limits: ${reason}`,
      '3. End your turn with a 3-5 line summary for the user, the reset time above, and the sentence: "Чтобы продолжить после сброса лимита, напишите: продолжи по .agent/handoff/STATUS.md".',
      `Do not continue the task after the handoff: the guard refuses further automatic steps (goal rounds included) ${eff('handoffSteps')} steps after the handoff file is written, or after ${eff('graceSteps')} steps at most. An active goal is marked blocked (usage-limit) and must be resumed by the user after the reset.`,
    ].join('\n')
  }

  function budgetText(worst) {
    const left = Math.max(0, 100 - worst.usedPercent)
    return [
      `ℹ️ LIMIT GUARD: ${describeWindow(worst)} — about ${pct(left)} left.`,
      'The user wrote this message knowing the limit is low; do what they ask, but keep it compact: no subagent fan-out, no long builds or broad searches unless required.',
      `Mention the remaining limit and the reset time in one short line of your reply. Do not rewrite an existing handoff file unless the user asks; the guard will ask for a fresh handoff at ${eff('hardStopPercent')}%.`,
    ].join('\n')
  }

  function nearlyOutText(worst) {
    return [
      `⛔ LIMIT GUARD: ${describeWindow(worst)} — the quota is practically exhausted.`,
      'Do not start multi-step work. Answer the user briefly: the limit, the reset time, and what you would do next.',
      `If work from this chat is unfinished and \`${config.handoffPath}\` is missing or stale, update it in one write; otherwise do not touch it. The guard refuses automatic steps after ${eff('graceSteps')} steps.`,
    ].join('\n')
  }

  function stoppedText(worst) {
    return [
      `⛔ LIMIT GUARD: work is paused until the limit resets (${worst ? describeWindow(worst) : 'limit reached'}).`,
      'Only acknowledge this input in one short reply (record it in the handoff file if it matters for the next chat). Do not start new work; further automatic steps are refused.',
    ].join('\n')
  }

  function describeWindow(w) {
    const reset = w.resetAt ? `, resets ${fmtTime(w.resetAt)}${fmtDuration(w.resetAt - Date.now()) ? ` (in ${fmtDuration(w.resetAt - Date.now())})` : ''}` : ''
    return `${w.source ? `${w.source} ` : ''}${w.label} used ${pct(w.usedPercent)}${reset}`
  }

  // ---------- goal integration ----------

  function blockGoal(agent, why) {
    if (!eff('blockGoals')) return false
    const goals = ctx.get('goals')
    if (!goals || typeof goals.get !== 'function' || typeof goals.block !== 'function') return false
    try {
      const goal = goals.get(agent)
      if (!goal || goal.phase !== 'active') return false
      const result = goals.block(agent, { id: goal.id, revision: goal.revision }, {
        code: 'usage-limit',
        message: `Limit Guard: ${why}. Resume the goal after the limit resets.`,
      })
      Promise.resolve(result).catch(error => ctx.logger?.warn?.('limit-guard: goal block failed %s', String(error)))
      agentState(agent).goalBlocked = true
      logEvent('goal-blocked', agent, why)
      return true
    } catch (error) {
      ctx.logger?.warn?.('limit-guard: goal block failed %s', String(error))
      return false
    }
  }

  function goalContinues(agent) {
    try {
      const goal = ctx.get('goals')?.get?.(agent)
      return !!goal && goal.phase === 'active' && goal.activation === 'armed'
    } catch {
      return false
    }
  }

  // ---------- per-task accounting ----------

  function taskForSession(sid) {
    let cur = sid
    for (let i = 0; i < 8 && cur; i++) {
      const t = sessionTask.get(cur)
      if (t) return t
      cur = sessionParent.get(cur)
    }
    return undefined
  }

  function openTask(agent, snap, messages) {
    if (openTasks.has(agent)) return openTasks.get(agent)
    const sid = sessionIdOf(agent.session)
    const human = messages.find(m => m?.source?.kind === 'user')
    const task = {
      id: randomId().slice(0, 8),
      sessionId: sid ?? '',
      cwd: String(cwdOf(agent) ?? ''),
      provider: snap.provider ?? '',
      model: '',
      prompt: messageText(human, 140),
      startedAt: Date.now(),
      start: { windows: flatWindows(snap), context: snap.context ? Math.round(snap.context.usedPercent * 10) / 10 : null },
      tokens: { input: 0, cached: 0, output: 0, reasoning: 0, requests: 0 },
      guard: { warned: false, stopped: false, rejected: 0, goalBlocked: false, retryStopped: false },
    }
    openTasks.set(agent, task)
    if (sid) sessionTask.set(sid, task)
    return task
  }

  async function closeTask(agent, why) {
    const task = openTasks.get(agent)
    if (!task) return
    openTasks.delete(agent)
    if (task.sessionId && sessionTask.get(task.sessionId) === task) sessionTask.delete(task.sessionId)
    let snap
    try { snap = await snapshot(agent, { force: true }) } catch { snap = { account: [], context: undefined } }
    const endWindows = flatWindows(snap)
    const windows = []
    for (const end of endWindows) {
      const start = task.start.windows.find(w => w.key === end.key)
      const rolled = windowRolled(start, end)
      const delta = start ? (rolled ? end.usedPercent : end.usedPercent - start.usedPercent) : null
      windows.push({ key: end.key, source: end.source, label: end.label, start: start ? start.usedPercent : null, end: end.usedPercent, delta: delta === null ? null : Math.round(delta * 10) / 10, rolled })
    }
    const st = perAgent.get(agent)
    const record = {
      id: task.id,
      sessionId: task.sessionId.slice(0, 8),
      cwd: task.cwd,
      provider: task.provider,
      model: task.model,
      prompt: task.prompt,
      startedAt: task.startedAt,
      endedAt: Date.now(),
      windows,
      context: { start: task.start.context, end: snap.context ? Math.round(snap.context.usedPercent * 10) / 10 : null },
      tokens: task.tokens,
      end: why ?? sessionEnd.get(task.sessionId) ?? 'completed',
      guard: { ...task.guard, mode: st?.mode ?? 'normal' },
    }
    store.history.unshift(record)
    if (store.history.length > config.historySize) store.history.length = config.historySize
    scheduleSave()
  }

  // ---------- step gate ----------

  ctx.on('agent/pre-step', async (payload, next) => {
    const decision = await next()
    if (!decision || decision.kind !== 'enter') return decision
    const agent = payload.agent
    const isSubagent = agent?.session?.header?.origin === 'subagent'
    const sid = sessionIdOf(agent?.session)
    if (isSubagent && sid) {
      const parent = parentIdOf(agent.session)
      if (parent) sessionParent.set(sid, parent)
    }
    const messages = Array.isArray(decision.messages) ? decision.messages : []
    const human = messages.some(m => m?.source?.kind === 'user')
    const goalMessage = messages.find(m => m?.source?.kind === 'goal')
    const meaningful = messages.filter(m => m?.source?.kind !== 'runtime-context' && m?.source?.kind !== NOTICE_SOURCE)

    let snap
    try {
      snap = await snapshot(agent, { force: human })
    } catch (error) {
      ctx.logger?.warn?.('limit-guard: snapshot failed %s', String(error))
      return decision
    }
    if (human && !isSubagent) openTask(agent, snap, messages)
    if (!eff('enabled')) return decision

    const state = agentState(agent)
    const task = !isSubagent ? openTasks.get(agent) : undefined
    const warnAt = eff('warnPercent')
    const hardAt = eff('hardStopPercent')
    const worst = worstAccount(snap)
    const used = worst ? worst.usedPercent : 0
    const notices = []

    if (state.mode !== 'normal' && used < warnAt) {
      logEvent('recovered', agent, worst ? describeWindow(worst) : 'no account reading')
      resetLatch(state)
    }

    const reject = (why) => {
      state.mode = 'stopped'
      if (task) { task.guard.rejected += 1; task.guard.stopped = true }
      logEvent('reject', agent, why)
      ctx.logger?.info?.('limit-guard: refusing step (%s)', why)
      return { kind: 'reject' }
    }
    const startWrapUp = (reason, hard) => {
      state.mode = 'handoff'
      state.reason = reason
      state.since = Date.now()
      state.stepsSinceWarn = 0
      state.handoffWritten = false
      state.stepsAfterHandoff = 0
      if (hard) state.hardWarned = true
      if (task) task.guard.warned = true
      logEvent(hard ? 'hard-warn' : 'warn', agent, reason)
      notices.push(noticeMessage(handoffText(agent, `${reason}.`, isSubagent), `limit ${pct(used)}`))
      if (!isSubagent && blockGoal(agent, reason) && task) task.guard.goalBlocked = true
    }

    if (worst && used >= warnAt) {
      const reason = `account limit ${describeWindow(worst)}`
      if (human) {
        // A person explicitly asked for work: never refuse, never order a second handoff
        // (that is what overwrote STATUS.md in the "continue" chat and looped).
        if (used >= hardAt) {
          state.mode = 'handoff'; state.reason = reason; state.since = Date.now()
          state.stepsSinceWarn = 0; state.handoffWritten = false; state.stepsAfterHandoff = 0; state.hardWarned = true
          notices.push(noticeMessage(nearlyOutText(worst), `limit ${pct(used)}`))
          logEvent('nearly-out', agent, reason)
        } else {
          if (state.mode !== 'override') logEvent('override', agent, `user continued at ${pct(used)}`)
          state.mode = 'override'
          state.hardWarned = false
          notices.push(noticeMessage(budgetText(worst), `limit ${pct(used)}`))
        }
      } else {
        if (state.mode === 'normal') {
          startWrapUp(reason, used >= hardAt)
        } else if (state.mode === 'override') {
          if (used >= hardAt && !state.hardWarned) startWrapUp(reason, true)
        } else if (state.mode === 'handoff') {
          state.stepsSinceWarn += 1
          if (state.handoffWritten) state.stepsAfterHandoff += 1
          if (goalMessage) {
            blockGoal(agent, reason)
            return reject('goal round during handoff')
          }
          const spent = state.handoffWritten ? state.stepsAfterHandoff > eff('handoffSteps') : state.stepsSinceWarn > eff('graceSteps')
          if (spent) {
            state.mode = 'stopped'
            logEvent('stop', agent, state.handoffWritten ? 'handoff written, turn closed' : `grace of ${eff('graceSteps')} steps used`)
          }
        }
        if (state.mode === 'stopped') {
          if (goalMessage) {
            blockGoal(agent, reason)
            return reject('goal round while stopped')
          }
          if (meaningful.length === 0) return reject('automatic continuation while stopped')
          // A subagent report or a reminder: let it be acknowledged once, then the
          // following empty continuation is refused.
          notices.push(noticeMessage(stoppedText(worst), `limit ${pct(used)}`))
        }
      }
    }

    const c = snap.context
    if (c && c.usedPercent >= eff('contextWarnPercent') && notices.length === 0) {
      const key = `ctx:${Math.floor(c.usedPercent / 5)}`
      if (!state.ctxWarned.has('ctx') || (human && !state.ctxWarned.has(key))) {
        state.ctxWarned.add('ctx'); state.ctxWarned.add(key)
        logEvent('context', agent, `context ${pct(c.usedPercent)}`)
        notices.push(noticeMessage(handoffText(agent, `context window ${pct(c.usedPercent)} full (${c.usedTokens} of ${c.windowTokens} tokens). After the handoff, recommend continuing in a new session.`, isSubagent), `context ${pct(c.usedPercent)}`))
      }
    }

    if (notices.length === 0) return decision
    return { ...decision, messages: [...decision.messages, ...notices] }
  })

  // ---------- handoff detection ----------

  const handoffNeedle = config.handoffPath.replace(/\\+/g, '/').replace(/^\.?\//, '').toLowerCase()
  ctx.on('tools/post-execute', async (exec, result, next) => {
    const decision = await next()
    try {
      const agent = exec?.agent
      const state = agent ? perAgent.get(agent) : undefined
      if (state && state.mode === 'handoff' && !state.handoffWritten && result?.isError !== true && !/^(read|grep|glob|.*search.*|.*list.*)$/i.test(String(exec.name ?? ''))) {
        let args = ''
        try { args = JSON.stringify(exec.arguments ?? '') } catch {}
        if (args.replace(/\\+/g, '/').toLowerCase().includes(handoffNeedle)) {
          state.handoffWritten = true
          state.stepsAfterHandoff = 0
          logEvent('handoff', agent, `${exec.name} → ${config.handoffPath}`)
        }
      }
    } catch {}
    return decision
  })

  // ---------- futile retry suppression ----------
  // Prepended so it runs outside dsh-llm-retry: once the account quota is gone,
  // retrying (5x in normal mode, forever in `always` mode) only burns time.
  ctx.on('agent/request-error', async (payload, next) => {
    try {
      if (eff('enabled') && eff('stopRetryOnQuota')) {
        const agent = payload.agent
        const failure = payload.failure ?? {}
        const code = String(failure.code ?? '')
        let exhausted = code === 'QUOTA' || code === 'ACCOUNT_QUOTA'
        let why = exhausted ? `provider reported ${code}` : ''
        if (!exhausted && code === 'RATE_LIMIT') {
          const snap = await snapshot(agent, { force: true })
          const worst = worstAccount(snap)
          if (worst && worst.usedPercent >= 99.5) { exhausted = true; why = `RATE_LIMIT with ${describeWindow(worst)}` }
        }
        if (exhausted) {
          const state = agentState(agent)
          state.mode = 'stopped'
          const task = openTasks.get(agent)
          if (task) { task.guard.retryStopped = true; task.guard.stopped = true }
          logEvent('retry-stopped', agent, why)
          const isSubagent = agent?.session?.header?.origin === 'subagent'
          if (!isSubagent && blockGoal(agent, why) && task) task.guard.goalBlocked = true
          return undefined
        }
      }
    } catch (error) {
      ctx.logger?.warn?.('limit-guard: request-error check failed %s', String(error))
    }
    return next()
  }, { prepend: true })

  // ---------- lifecycle observers ----------

  ctx.on('agent/status', ({ agent, status }) => {
    if (status !== 'idle' || !openTasks.has(agent)) return
    if (goalContinues(agent)) return // the goal will start another round of the same task
    closeTask(agent).catch(error => ctx.logger?.warn?.('limit-guard: close task failed %s', String(error)))
  })

  ctx.on('agent/disposed', ({ agent }) => {
    if (openTasks.has(agent)) closeTask(agent, 'disposed').catch(() => {})
  })

  ctx.on('goal/changed', ({ agent, change }) => {
    try {
      if (!agent) return
      // A goal that stopped while the agent is idle (round cap, pause, completion)
      // ends the task the idle edge kept open for the next round.
      if (change?.operation !== 'resume' && agent.status === 'idle' && openTasks.has(agent) && !goalContinues(agent)) {
        closeTask(agent).catch(() => {})
      }
      if (change?.operation !== 'resume') return
      const state = perAgent.get(agent)
      if (state && state.mode !== 'normal') {
        state.mode = 'override'
        state.hardWarned = false
        logEvent('override', agent, 'goal resumed by the user')
      }
    } catch {}
  })

  ctx.on('session/event', (session, event) => {
    try {
      const sid = sessionIdOf(session)
      if (!sid) return
      if (event?.type === 'turn/end') {
        const kind = event.data?.reason?.kind
        if (typeof kind === 'string') sessionEnd.set(sid, kind)
        return
      }
      if (event?.type !== 'assistant/message') return
      const task = taskForSession(sid)
      if (!task) return
      const u = event.data?.usage
      if (u && typeof u === 'object') {
        task.tokens.input += Number(u.inputTokens) || 0
        task.tokens.cached += (Number(u.cacheReadTokens) || 0) + (Number(u.cacheWriteTokens) || 0)
        task.tokens.output += Number(u.outputTokens) || 0
        task.tokens.reasoning += Number(u.reasoningTokens) || 0
      }
      task.tokens.requests += 1
      const model = event.data?.message?.source?.model
      if (typeof model === 'string' && sid === task.sessionId) task.model = model
    } catch {}
  })

  // ---------- dashboard ----------

  function averages() {
    const acc = new Map()
    for (const t of store.history.slice(0, 20)) {
      for (const w of t.windows ?? []) {
        if (w.delta === null || w.rolled) continue
        const a = acc.get(w.key) ?? { key: w.key, sum: 0, n: 0 }
        a.sum += Math.max(0, w.delta); a.n += 1
        acc.set(w.key, a)
      }
    }
    return [...acc.values()].map(a => ({ key: a.key, avg: Math.round((a.sum / a.n) * 10) / 10, tasks: a.n }))
  }

  async function dashboard(force) {
    const out = []
    for (const id of Object.keys(sources)) {
      if (!sources[id].enabled()) { out.push({ id, enabled: false, providers: sources[id].providers(), windows: [] }); continue }
      const entry = await readSource(id, { force })
      const windows = (entry.reading?.windows ?? []).map(w => ({
        key: `${id}:${w.label}`,
        label: w.label,
        usedPercent: Math.round(w.usedPercent * 10) / 10,
        leftPercent: Math.round(Math.max(0, 100 - w.usedPercent) * 10) / 10,
        resetAt: w.resetAt,
        pace: computePace(samples.get(`${id}:${w.label}`) ?? []),
      }))
      out.push({ id, enabled: true, providers: sources[id].providers(), at: entry.at, error: entry.error, limited: !!entry.reading?.limited, windows })
    }
    const live = []
    const agents = ctx.get('agents')
    let list = []
    try { list = typeof agents?.list === 'function' ? [...agents.list()] : [] } catch {}
    for (const agent of list) {
      if (agent?.session?.header?.origin === 'subagent') continue
      const st = perAgent.get(agent)
      const task = openTasks.get(agent)
      if (!task && (!st || st.mode === 'normal') && agent.status !== 'running') continue
      const c = contextReading(agent)
      let spent = []
      if (task) {
        const nowSnap = { account: sourcesFor(agent).map(id => ({ source: id, ...(cache.get(id) ?? {}) })) }
        spent = flatWindows(nowSnap).map(end => {
          const start = task.start.windows.find(w => w.key === end.key)
          if (!start) return null
          const rolled = windowRolled(start, end)
          return { key: end.key, label: end.label, delta: Math.round((rolled ? end.usedPercent : end.usedPercent - start.usedPercent) * 10) / 10, rolled }
        }).filter(Boolean)
      }
      live.push({
        sessionId: sessionIdOf(agent.session) ?? '',
        cwd: String(cwdOf(agent) ?? ''),
        provider: providerOf(agent) ?? '',
        status: agent.status,
        mode: st?.mode ?? 'normal',
        handoffWritten: !!st?.handoffWritten,
        goalBlocked: !!st?.goalBlocked,
        context: c ? { usedPercent: Math.round(c.usedPercent * 10) / 10, usedTokens: c.usedTokens, windowTokens: c.windowTokens } : null,
        task: task ? { prompt: task.prompt, startedAt: task.startedAt, tokens: task.tokens, spent } : null,
      })
    }
    return {
      version: VERSION,
      uptimeSec: Math.round((Date.now() - startedAt) / 1000),
      settings: Object.fromEntries(Object.keys(TUNABLE).map(k => [k, eff(k)])),
      overridden: Object.keys(store.settings),
      handoffPath: config.handoffPath,
      sources: out,
      agents: live,
      averages: averages(),
      history: store.history.slice(0, 30),
      events: store.events.slice(0, 40),
    }
  }

  function releaseSession(sessionId) {
    const agents = ctx.get('agents')
    let list = []
    try { list = typeof agents?.list === 'function' ? [...agents.list()] : [] } catch {}
    for (const agent of list) {
      if (sessionIdOf(agent.session) !== sessionId) continue
      const st = perAgent.get(agent)
      if (st && st.mode !== 'normal') {
        st.mode = 'override'; st.hardWarned = false
        logEvent('override', agent, 'released from the dashboard')
        return true
      }
    }
    return false
  }

  function sendJson(res, status, body) {
    res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
    res.end(JSON.stringify(body))
  }

  async function readBody(req) {
    const chunks = []
    let size = 0
    for await (const c of req) {
      size += c.length
      if (size > 64 * 1024) throw new Error('body too large')
      chunks.push(c)
    }
    const raw = Buffer.concat(chunks).toString('utf8')
    return raw ? JSON.parse(raw) : {}
  }

  async function handle(req, res) {
    try {
      const url = new URL(String(req.url ?? '/'), 'http://local')
      const action = url.pathname.replace(/\/+$/, '').split('/api/')[1] ?? ''
      if (req.method === 'GET' && action === 'dashboard') {
        sendJson(res, 200, await dashboard(url.searchParams.get('refresh') === '1'))
        return
      }
      if (req.method === 'POST') {
        // JSON-only POSTs: a cross-site form cannot send this content type without a preflight.
        if (!String(req.headers?.['content-type'] ?? '').toLowerCase().startsWith('application/json')) {
          sendJson(res, 415, { ok: false, error: 'application/json required' })
          return
        }
        const body = await readBody(req)
        if (action === 'settings') {
          if (body?.reset === true) store.settings = {}
          else {
            const patch = sanitizeSettings({ [body?.key]: body?.value })
            if (Object.keys(patch).length === 0) { sendJson(res, 400, { ok: false, error: 'invalid setting' }); return }
            store.settings = { ...store.settings, ...patch }
            logEvent('config', undefined, `${body.key} = ${body.value}`)
          }
          scheduleSave()
          sendJson(res, 200, { ok: true, settings: Object.fromEntries(Object.keys(TUNABLE).map(k => [k, eff(k)])) })
          return
        }
        if (action === 'clear-history') {
          store.history = []; store.events = []
          saveNow()
          sendJson(res, 200, { ok: true })
          return
        }
        if (action === 'release') {
          sendJson(res, 200, { ok: releaseSession(String(body?.sessionId ?? '')) })
          return
        }
      }
      sendJson(res, 404, { ok: false, error: 'unknown limit-guard endpoint' })
    } catch (error) {
      sendJson(res, 500, { ok: false, error: String(error?.message ?? error) })
    }
  }

  ctx.inject(['webServer'], (ctx2) => {
    ctx2.effect(() => ctx2.webServer.register({ kind: 'prefix', path: '/limit-guard', handler: handle }), 'limit-guard: dashboard API')
  })

  // ---------- model tool ----------

  ctx.inject(['tools'], (ctx2) => {
    ctx2.tools.register(defineTool({
      name: 'limit_status',
      description: 'Show current usage limits for this session: context window fill and, for subscription providers, the 5-hour/weekly windows with reset times. Call it before starting a long task or a fan-out of subagents.',
      parameters: {},
      output: {
        schema: { type: 'string' },
        render: (_args, value) => [{ type: 'text', text: value }],
      },
      async execute(_args, exec) {
        const agent = exec?.agent
        const snap = await snapshot(agent, { force: true })
        const lines = [`provider: ${snap.provider ?? 'unknown'}`]
        if (snap.context) lines.push(`context: ${pct(snap.context.usedPercent)} (${snap.context.usedTokens}/${snap.context.windowTokens} tokens)`)
        else lines.push('context: unknown')
        if (snap.account.length === 0) lines.push('account limits: no source for this provider')
        for (const s of snap.account) {
          if (s.error) { lines.push(`${s.source}: unavailable (${s.error})`); continue }
          if (s.reading.windows.length === 0) lines.push(`${s.source}: no configured windows`)
          for (const w of s.reading.windows) {
            const pace = computePace(samples.get(`${s.source}:${w.label}`) ?? [])
            const paceText = pace ? `; pace ~${pace.perHour}%/h${pace.etaAt ? `, runs out ~${fmtTime(pace.etaAt)}` : ''}` : ''
            lines.push(describeWindow({ ...w, source: s.source }) + paceText)
          }
          if (s.reading.limited) lines.push(`${s.source}: LIMIT REACHED`)
        }
        const avg = averages()
        if (avg.length > 0) lines.push(`average task cost: ${avg.map(a => `${a.key} ${a.avg}% (${a.tasks} tasks)`).join(', ')}`)
        const last = store.history[0]
        if (last) lines.push(`previous task: ${last.windows.map(w => `${w.key} ${w.delta === null ? '?' : `+${w.delta}%`}${w.rolled ? ' (window reset)' : ''}`).join(', ') || 'no account windows'}`)
        const st = agent ? perAgent.get(agent) : undefined
        lines.push(`guard: ${eff('enabled') ? 'on' : 'OFF'}, state ${st?.mode ?? 'normal'}; warn at ${eff('warnPercent')}% (context ${eff('contextWarnPercent')}%), hard stop at ${eff('hardStopPercent')}%, ${eff('graceSteps')} grace steps`)
        return lines.join('\n')
      },
    }))
  })
}
