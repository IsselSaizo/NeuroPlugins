// dsh-limit-guard — Web client half.
// 1. Settings → "Limit Guard" page: live account windows (left / used / reset /
//    pace), the current and previous task cost in %, history, guard state per
//    session, runtime switches and thresholds. Data comes from the Host half's
//    package-private JSON API (/limit-guard/api/*).
// 2. A "handoff summary" button before the composer's send button. It is shown
//    only in sessions that already have at least one turn, and sends a prompt that
//    makes the agent write .agent/handoff/STATUS.md for the next chat. Any text
//    already typed in the composer is appended as an extra note.
window.__ModuleLoader__.load({
  id: 'dsh-limit-guard',
  factory(require) {
    const React = require('react')
    const h = React.createElement

    const HANDOFF_PATH = '.agent/handoff/STATUS.md'
    const API = '/limit-guard/api'
    const VERSION = '0.3.0'

    const DICTS = {
      en: {
        button: 'Handoff summary',
        tooltip: 'Write a summary for the next chats (.agent/handoff/STATUS.md)',
        busy: 'Wait until the composer is free',
        prompt: [
          'Make a handoff summary for the next chats.',
          '',
          'Stop and do not start new work. Finish or revert only an edit that is half-applied, so the project is consistent.',
          'Call `limit_status` once (if available) to record the current limits.',
          `Then write \`${HANDOFF_PATH}\` in the session working directory (create folders, overwrite an older file) in the language of this conversation, with sections:`,
          '- Goal — the task in 1-3 sentences as the user stated it;',
          '- Done — what is finished and verified;',
          '- In progress — what is partially done and its exact state;',
          '- Next steps — numbered, starting with the very next action;',
          '- Files touched — paths with one line each;',
          '- Decisions and context — what a new chat must know and must not redo;',
          '- Open questions / risks;',
          '- Limits — values and reset times from limit_status.',
          '',
          'Keep it compact: a new agent must be able to continue from this file alone. Then reply with a 3-5 line summary and the line: "To continue, open a new chat and write: continue from .agent/handoff/STATUS.md".',
        ].join('\n'),
        note: 'Additional note from the user:',
        nav: 'Limit Guard',
        tagline: 'context · subscriptions · handoff',
        up: 'up {n} min',
        loading: 'Loading limits…',
        loadError: 'Limit Guard API is not reachable: {e}. Restart DSH after updating the plugin.',
        refresh: 'Refresh',
        on: 'ON', off: 'OFF',
        hintOn: 'Enabled — click to turn off', hintOff: 'Disabled — click to turn on',
        tGuard: 'Guard', tGoals: 'Block goals at limit', tRetry: 'Stop futile retries',
        thWarn: 'Wrap up at, %', thHard: 'Hard stop at, %', thCtx: 'Context warn, %', thGrace: 'Steps for handoff',
        resetDefaults: 'Defaults',
        windows: 'Account limits',
        noSources: 'No account sources are configured.',
        srcOff: 'disabled', srcErr: 'unavailable: {e}', srcNoWin: 'no configured windows',
        left: 'left', used: 'used {p}', resets: 'resets {t} (in {d})', resetsUnknown: 'reset time unknown',
        pace: 'pace ~{p}%/h', runsOut: '→ runs out ~{t}', enough: '→ lasts until the reset', paceNone: 'pace: not enough data yet',
        limited: 'LIMIT REACHED',
        avgTask: 'avg task ~{p}% → about {n} more tasks like this',
        current: 'Current tasks',
        noCurrent: 'No task is running.',
        previous: 'Previous task',
        noPrevious: 'No finished tasks yet. A task starts with your message and ends when the agent goes idle.',
        spent: 'spent', spentSoFar: 'spent so far',
        rolled: 'window reset during the task',
        tokens: '{i} in · {o} out · {r} requests',
        duration: '{d}',
        history: 'Task history',
        colWhen: 'When', colTask: 'Task', colTime: 'Time', colSpent: 'Spent', colTokens: 'Tokens', colEnd: 'Result',
        clear: 'Clear history',
        events: 'Guard events',
        noEvents: 'No events yet.',
        release: 'Release',
        releaseHint: 'Let this session continue automatic steps (as if the user wrote “continue”)',
        modes: { normal: 'normal', override: 'user continues', handoff: 'writing handoff', stopped: 'stopped' },
        ends: { completed: 'done', blocked: 'stopped by guard', error: 'error', aborted: 'cancelled', 'max-tokens': 'max tokens', disposed: 'closed' },
        kinds: { warn: 'wrap-up', 'hard-warn': 'hard wrap-up', 'nearly-out': 'nearly out', override: 'override', handoff: 'handoff written', stop: 'stopped', reject: 'step refused', 'goal-blocked': 'goal blocked', 'retry-stopped': 'retry stopped', recovered: 'recovered', context: 'context', config: 'settings' },
        context: 'context {p}',
        footer: 'How it works: at the wrap-up threshold the agent finishes the current edit, writes {path} and ends the turn. After that the guard refuses automatic steps (goal rounds, continuations) and marks an active goal as blocked, so it does not loop into the exhausted quota. Your own messages always go through: above the threshold the agent only gets a short “limit is low” note. Retries of requests that failed because the quota is gone are suppressed. Spent % is measured on the shared account, so tasks running in parallel share the delta.',
      },
      ru: {
        button: 'Саммари для следующих чатов',
        tooltip: 'Сделать саммари для следующих чатов (.agent/handoff/STATUS.md)',
        busy: 'Дождитесь, пока поле ввода освободится',
        prompt: [
          'Сделай саммари для следующих чатов.',
          '',
          'Остановись и не начинай новую работу. Доведи или откати только наполовину применённую правку, чтобы проект был в целостном состоянии.',
          'Один раз вызови `limit_status` (если он есть), чтобы записать текущие лимиты.',
          `Затем запиши \`${HANDOFF_PATH}\` в рабочей папке сессии (создай папки, перезапиши старый файл) на языке этого разговора, с разделами:`,
          '- Цель — задача в 1-3 предложениях, как её поставил пользователь;',
          '- Сделано — что закончено и проверено;',
          '- В процессе — что сделано частично и в каком точно состоянии;',
          '- Следующие шаги — нумерованный список, начиная с самого ближайшего действия;',
          '- Затронутые файлы — пути, по строке на каждый;',
          '- Решения и контекст — что новый чат должен знать и не переделывать;',
          '- Открытые вопросы / риски;',
          '- Лимиты — значения и время сброса из limit_status.',
          '',
          'Пиши компактно: новый агент должен суметь продолжить только по этому файлу. Затем ответь саммари в 3-5 строк и строкой: «Чтобы продолжить, откройте новый чат и напишите: продолжи по .agent/handoff/STATUS.md».',
        ].join('\n'),
        note: 'Дополнительная заметка пользователя:',
        nav: 'Сторож лимитов',
        tagline: 'контекст · подписки · handoff',
        up: 'работает {n} мин',
        loading: 'Загружаю лимиты…',
        loadError: 'API сторожа лимитов недоступно: {e}. После обновления плагина перезапустите DSH.',
        refresh: 'Обновить',
        on: 'ВКЛ', off: 'ВЫКЛ',
        hintOn: 'Включено — нажмите, чтобы выключить', hintOff: 'Выключено — нажмите, чтобы включить',
        tGuard: 'Сторож', tGoals: 'Блокировать goal у лимита', tRetry: 'Не ретраить при исчерпании',
        thWarn: 'Сворачиваться на, %', thHard: 'Жёсткий стоп на, %', thCtx: 'Контекст, %', thGrace: 'Шагов на handoff',
        resetDefaults: 'По умолчанию',
        windows: 'Лимиты аккаунта',
        noSources: 'Источники лимитов аккаунта не настроены.',
        srcOff: 'выключен', srcErr: 'недоступен: {e}', srcNoWin: 'нет настроенных окон',
        left: 'осталось', used: 'использовано {p}', resets: 'сброс {t} (через {d})', resetsUnknown: 'время сброса неизвестно',
        pace: 'темп ~{p}%/ч', runsOut: '→ кончится ~{t}', enough: '→ хватит до сброса', paceNone: 'темп: пока мало данных',
        limited: 'ЛИМИТ ИСЧЕРПАН',
        avgTask: 'средняя задача ~{p}% → хватит примерно на {n} таких',
        current: 'Текущие задачи',
        noCurrent: 'Сейчас ничего не выполняется.',
        previous: 'Предыдущая задача',
        noPrevious: 'Завершённых задач пока нет. Задача начинается с вашего сообщения и заканчивается, когда агент освободится.',
        spent: 'потрачено', spentSoFar: 'потрачено пока',
        rolled: 'окно сбросилось во время задачи',
        tokens: '{i} вход · {o} выход · {r} запросов',
        duration: '{d}',
        history: 'История задач',
        colWhen: 'Когда', colTask: 'Задача', colTime: 'Время', colSpent: 'Потрачено', colTokens: 'Токены', colEnd: 'Итог',
        clear: 'Очистить историю',
        events: 'События сторожа',
        noEvents: 'Событий пока нет.',
        release: 'Снять стоп',
        releaseHint: 'Разрешить этой сессии автоматические шаги (как будто вы написали «продолжай»)',
        modes: { normal: 'норма', override: 'продолжает по вашему слову', handoff: 'пишет handoff', stopped: 'остановлена' },
        ends: { completed: 'готово', blocked: 'остановлено сторожем', error: 'ошибка', aborted: 'отменено', 'max-tokens': 'лимит токенов ответа', disposed: 'закрыто' },
        kinds: { warn: 'сворачивание', 'hard-warn': 'жёсткое сворачивание', 'nearly-out': 'почти ноль', override: 'продолжение', handoff: 'handoff записан', stop: 'стоп', reject: 'шаг отклонён', 'goal-blocked': 'goal заблокирован', 'retry-stopped': 'ретрай остановлен', recovered: 'лимит восстановился', context: 'контекст', config: 'настройки' },
        context: 'контекст {p}',
        footer: 'Как это работает: на пороге сворачивания агент доводит текущую правку, пишет {path} и завершает ход. После этого сторож отклоняет автоматические шаги (раунды goal, продолжения) и переводит активный goal в blocked, чтобы он не крутился по кругу в исчерпанный лимит. Ваши сообщения проходят всегда: выше порога агент получает лишь короткую пометку «лимит на исходе». Повторы запросов, упавших из-за исчерпанного лимита, отключаются. «Потрачено» меряется по общему аккаунту, поэтому параллельные задачи делят одну дельту.',
      },
      zh: {
        button: '交接摘要',
        tooltip: '为后续对话生成交接摘要（.agent/handoff/STATUS.md）',
        busy: '请等待输入框空闲',
        prompt: null,
        note: '用户补充说明：',
      },
    }

    const CSS = `
.dlg-handoff-btn{display:inline-flex;align-items:center;justify-content:center;width:28px;height:28px;padding:0;margin:0;border:0;border-radius:8px;background:transparent;color:var(--dsw-alias-label-secondary);cursor:pointer;transition:background-color .12s,color .12s}
.dlg-handoff-btn:hover:not(:disabled){background:var(--dsw-alias-bg-layer-2);color:var(--dsw-alias-label-primary)}
.dlg-handoff-btn:focus-visible{outline:2px solid var(--dsw-alias-brand-primary);outline-offset:1px}
.dlg-handoff-btn:disabled{opacity:.45;cursor:default}
.dlg-handoff-btn svg{display:block}
.lg-wrap{display:flex;flex-direction:column;gap:14px;font-size:13px;color:var(--dsw-alias-label-primary,#1f2937)}
.lg-row{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.lg-dim{color:var(--dsw-alias-label-secondary,#6b7280)}
.lg-s{font-size:11px;line-height:1.4;margin-top:3px}
.lg-card{background:var(--dsw-alias-bg-layer-2,color-mix(in srgb,var(--dsw-alias-bg-base,#f7f7f8) 82%,transparent));border:1px solid var(--dsw-alias-border-l1,#e5e7eb);border-radius:10px;padding:12px 14px;min-width:0}
.lg-title{font-size:12px;font-weight:600;color:var(--dsw-alias-label-secondary,#6b7280);letter-spacing:.02em;text-transform:uppercase}
.lg-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:10px}
.lg-big{font-size:21px;font-weight:650;margin-top:4px;white-space:nowrap}
.lg-big small{font-size:12px;font-weight:500;margin-left:6px;color:var(--dsw-alias-label-secondary,#6b7280)}
.lg-track{height:8px;border-radius:4px;background:var(--dsw-alias-border-l1,#e5e7eb);overflow:hidden;margin-top:8px;position:relative}
.lg-fill{height:100%;border-radius:4px}
.lg-mark{position:absolute;top:-2px;bottom:-2px;width:2px;background:var(--dsw-alias-label-secondary,#6b7280);opacity:.6}
.lg-ok{color:var(--dsw-alias-state-success-primary,#15803d)}
.lg-warn{color:var(--dsw-alias-state-warn-primary,#b45309)}
.lg-bad{color:var(--dsw-alias-state-error-primary,#b91c1c)}
.lg-btn{border:1px solid var(--dsw-alias-border-l2,#d1d5db);background:transparent;color:inherit;border-radius:8px;padding:4px 12px;font-size:12px;cursor:pointer;display:inline-flex;align-items:center;gap:6px}
.lg-btn:hover{border-color:var(--dsw-alias-label-secondary,#9ca3af)}
.lg-btn.lg-on{border-color:var(--dsw-alias-state-success-primary,#16a34a);color:var(--dsw-alias-state-success-primary,#16a34a)}
.lg-dot{width:7px;height:7px;border-radius:50%;display:inline-block}
.lg-num{display:inline-flex;align-items:center;gap:6px;font-size:12px}
.lg-num input{width:56px;padding:3px 6px;border-radius:6px;border:1px solid var(--dsw-alias-border-l2,#d1d5db);background:transparent;color:inherit;font-size:12px}
.lg-num input.lg-over{border-color:var(--dsw-alias-brand-primary,#2563eb)}
.lg-table{width:100%;border-collapse:collapse;font-size:12px;margin-top:8px;table-layout:auto}
.lg-table th{text-align:left;font-weight:600;padding:4px 6px;color:var(--dsw-alias-label-secondary,#6b7280)}
.lg-table td{padding:4px 6px;border-top:1px solid var(--dsw-alias-border-l1,#eceef1);vertical-align:top}
.lg-table td.lg-clip{max-width:260px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.lg-badge{display:inline-block;padding:1px 7px;border-radius:999px;font-size:10.5px;font-weight:600;border:1px solid currentColor;white-space:nowrap}
.lg-chip{display:inline-block;padding:1px 6px;margin:1px 4px 1px 0;border-radius:6px;font-size:11px;background:var(--dsw-alias-bg-layer-1,rgba(127,127,127,.12));white-space:nowrap}
.lg-spentbig{font-size:19px;font-weight:650}
`

    let styled = false
    function ensureStyles() {
      if (styled || typeof document === 'undefined') return
      const el = document.createElement('style')
      el.setAttribute('data-limit-guard', '')
      el.textContent = CSS
      document.head.appendChild(el)
      styled = true
    }

    function Icon() {
      return h('svg', { viewBox: '0 0 24 24', width: 18, height: 18, fill: 'none', stroke: 'currentColor', strokeWidth: 1.8, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true },
        h('path', { d: 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h5' }),
        h('path', { d: 'M14 3v5h5' }),
        h('path', { d: 'M19 8v4' }),
        h('path', { d: 'M8.5 12.5h5M8.5 16h3' }),
        h('path', { d: 'M15.5 18h6M19 15.5l2.5 2.5-2.5 2.5' }))
    }

    async function apiGet(action) {
      const r = await fetch(`${API}/${action}`, { headers: { accept: 'application/json' }, cache: 'no-store' })
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      return r.json()
    }
    async function apiPost(action, body) {
      const r = await fetch(`${API}/${action}`, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json' }, body: JSON.stringify(body || {}) })
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      return r.json()
    }

    return {
      inject: ['slots', 'locale'],
      apply(ctx) {
        ensureStyles()
        // DSH ships only zh/en UI languages. A Russian-speaking user on the English UI
        // (browser language ru) still gets Russian text; an explicit 'ru' language pack wins too.
        const browserRu = typeof navigator !== 'undefined' && /^ru\b/i.test(navigator.language || '')
        const langOf = () => {
          let active = 'en'
          try { active = String(ctx.locale.getLocale().active || 'en') } catch (e) {}
          return active.startsWith('ru') || (browserRu && !active.startsWith('zh')) ? 'ru' : active.startsWith('zh') ? 'zh' : 'en'
        }
        const t = (key, vars) => {
          const lang = langOf()
          let value = DICTS[lang][key]
          if (value == null) value = DICTS.en[key]
          if (typeof value === 'string' && vars) value = value.replace(/\{(\w+)\}/g, (m, k) => (vars[k] !== undefined ? String(vars[k]) : m))
          return value == null ? key : value
        }
        const subscribe = (fn) => { try { return ctx.locale.subscribe(fn) } catch (e) { return () => {} } }
        const snapshot = () => { try { return ctx.locale.getSnapshot() } catch (e) { return null } }

        // ---------- formatting ----------
        const loc = () => (langOf() === 'ru' ? 'ru-RU' : langOf() === 'zh' ? 'zh-CN' : 'en-US')
        function fmtClock(ms) {
          if (!ms) return '—'
          const d = new Date(ms)
          const sameDay = new Date().toDateString() === d.toDateString()
          return sameDay
            ? d.toLocaleTimeString(loc(), { hour: '2-digit', minute: '2-digit', hour12: false })
            : d.toLocaleString(loc(), { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false })
        }
        function fmtDur(ms) {
          if (!Number.isFinite(ms) || ms <= 0) return '0'
          const ru = langOf() === 'ru'
          const s = Math.round(ms / 1000)
          if (s < 60) return ru ? `${s} с` : `${s}s`
          const m = Math.round(s / 60)
          if (m < 60) return ru ? `${m} мин` : `${m} min`
          const hh = Math.floor(m / 60)
          if (hh < 48) return ru ? `${hh} ч ${m % 60} мин` : `${hh}h ${m % 60}m`
          return ru ? `${Math.floor(hh / 24)} д ${hh % 24} ч` : `${Math.floor(hh / 24)}d ${hh % 24}h`
        }
        function fmtTok(n) {
          n = Number(n) || 0
          if (n >= 1e6) return (n / 1e6).toFixed(2) + 'M'
          if (n >= 1e3) return (n / 1e3).toFixed(1) + 'k'
          return String(Math.round(n))
        }
        const fmtPct = (p) => `${Math.round(Number(p) * 10) / 10}%`
        const tone = (used, s) => (used >= s.hardStopPercent ? 'lg-bad' : used >= s.warnPercent ? 'lg-warn' : 'lg-ok')
        const toneColor = { 'lg-ok': 'var(--dsw-alias-state-success-primary,#16a34a)', 'lg-warn': 'var(--dsw-alias-state-warn-primary,#d97706)', 'lg-bad': 'var(--dsw-alias-state-error-primary,#dc2626)' }
        const shortKey = (key) => String(key).replace(/^[^:]+:/, '')

        // ---------- data ----------
        function useDashboard(intervalMs) {
          const [data, setData] = React.useState(null)
          const [error, setError] = React.useState('')
          const load = React.useCallback(async (refresh) => {
            try {
              const d = await apiGet(`dashboard${refresh ? '?refresh=1' : ''}`)
              setData(d); setError('')
            } catch (e) { setError(String(e && e.message || e)) }
          }, [])
          React.useEffect(() => {
            let alive = true
            const tick = () => { if (alive) load(false) }
            tick()
            const timer = setInterval(tick, intervalMs)
            return () => { alive = false; clearInterval(timer) }
          }, [load, intervalMs])
          return { data, error, load }
        }

        // ---------- pieces ----------
        function Toggle(props) {
          const on = !!props.on
          return h('button', {
            type: 'button',
            className: 'lg-btn' + (on ? ' lg-on' : ''),
            title: on ? t('hintOn') : t('hintOff'),
            onClick: async () => { try { await apiPost('settings', { key: props.k, value: !on }); props.onChange() } catch (e) { console.error(e) } },
          },
          h('span', { className: 'lg-dot', style: { background: on ? 'var(--dsw-alias-state-success-primary,#16a34a)' : 'currentColor', opacity: on ? 1 : 0.4 } }),
          `${props.label}: ${on ? t('on') : t('off')}`)
        }

        function NumField(props) {
          const [text, setText] = React.useState(String(props.value))
          React.useEffect(() => { setText(String(props.value)) }, [props.value])
          const commit = async () => {
            const v = Number(String(text).replace(',', '.'))
            if (!Number.isFinite(v) || v === props.value) { setText(String(props.value)); return }
            try { await apiPost('settings', { key: props.k, value: props.integer ? Math.round(v) : v }); props.onChange() } catch (e) { setText(String(props.value)) }
          }
          return h('label', { className: 'lg-num' },
            h('span', { className: 'lg-dim' }, props.label),
            h('input', {
              className: props.overridden ? 'lg-over' : '',
              value: text, inputMode: 'decimal',
              onChange: (e) => setText(e.target.value),
              onBlur: commit,
              onKeyDown: (e) => { if (e.key === 'Enter') e.currentTarget.blur() },
            }))
        }

        function Bar(props) {
          const used = Math.max(0, Math.min(100, Number(props.used) || 0))
          const cls = tone(used, props.s)
          return h('div', { className: 'lg-track' },
            h('div', { className: 'lg-fill', style: { width: `${used}%`, background: toneColor[cls] } }),
            h('div', { className: 'lg-mark', style: { left: `${Math.min(100, props.s.warnPercent)}%` }, title: t('thWarn') }),
            props.s.hardStopPercent <= 100 ? h('div', { className: 'lg-mark', style: { left: `${props.s.hardStopPercent}%` }, title: t('thHard') }) : null)
        }

        function WindowCard(props) {
          const { src, w, s, avg } = props
          const cls = tone(w.usedPercent, s)
          const reset = w.resetAt ? t('resets', { t: fmtClock(w.resetAt), d: fmtDur(w.resetAt - Date.now()) }) : t('resetsUnknown')
          let pace = t('paceNone')
          if (w.pace) pace = `${t('pace', { p: w.pace.perHour })} ${w.pace.etaAt ? t('runsOut', { t: fmtClock(w.pace.etaAt) }) : t('enough')}`
          return h('div', { className: 'lg-card' },
            h('div', { className: 'lg-title' }, `${src.id} · ${w.label}`),
            h('div', { className: 'lg-big ' + cls }, fmtPct(w.leftPercent), h('small', null, t('left'))),
            h(Bar, { used: w.usedPercent, s }),
            h('div', { className: 'lg-s lg-dim' }, `${t('used', { p: fmtPct(w.usedPercent) })} · ${reset}`),
            h('div', { className: 'lg-s ' + (w.pace && w.pace.etaAt ? 'lg-warn' : 'lg-dim') }, pace),
            avg && avg.avg > 0 ? h('div', { className: 'lg-s lg-dim' }, t('avgTask', { p: avg.avg, n: Math.floor(w.leftPercent / avg.avg) })) : null)
        }

        function SpentChips(props) {
          const list = props.windows || []
          if (list.length === 0) return h('span', { className: 'lg-dim' }, '—')
          return h(React.Fragment, null, list.map((w) => h('span', {
            key: w.key, className: 'lg-chip', title: w.rolled ? t('rolled') : (w.start != null ? `${fmtPct(w.start)} → ${fmtPct(w.end)}` : ''),
          }, `${shortKey(w.key)}: ${w.delta == null ? '?' : (w.delta >= 0 ? '+' : '') + fmtPct(w.delta)}${w.rolled ? ' ↻' : ''}`)))
        }

        function EndBadge(props) {
          const ends = t('ends')
          const kind = props.kind || 'completed'
          const color = kind === 'completed' ? toneColor['lg-ok'] : kind === 'blocked' ? toneColor['lg-warn'] : kind === 'error' ? toneColor['lg-bad'] : 'var(--dsw-alias-label-secondary,#6b7280)'
          return h('span', { className: 'lg-badge', style: { color } }, (ends && ends[kind]) || kind)
        }

        function PreviousTask(props) {
          const task = props.task
          if (!task) return h('div', { className: 'lg-card' }, h('div', { className: 'lg-title' }, t('previous')), h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } }, t('noPrevious')))
          const main = (task.windows || []).filter((w) => w.delta != null)
          return h('div', { className: 'lg-card' },
            h('div', { className: 'lg-row', style: { justifyContent: 'space-between' } },
              h('div', { className: 'lg-title' }, t('previous')),
              h(EndBadge, { kind: task.guard && task.guard.stopped && task.end === 'completed' ? 'blocked' : task.end })),
            h('div', { style: { marginTop: 6, fontWeight: 550 } }, task.prompt || '—'),
            h('div', { className: 'lg-row', style: { marginTop: 8, gap: 16 } },
              main.length === 0 ? h('span', { className: 'lg-dim' }, '—') : main.map((w) => h('div', { key: w.key },
                h('div', { className: 'lg-s lg-dim' }, `${shortKey(w.key)} · ${t('spent')}`),
                h('div', { className: 'lg-spentbig ' + (w.delta >= 5 ? 'lg-warn' : '') }, `${w.delta >= 0 ? '' : '−'}${fmtPct(Math.abs(w.delta))}${w.rolled ? ' ↻' : ''}`),
                h('div', { className: 'lg-s lg-dim' }, w.start != null ? `${fmtPct(w.start)} → ${fmtPct(w.end)}` : '')))),
            h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } },
              `${fmtClock(task.startedAt)} · ${fmtDur(task.endedAt - task.startedAt)} · ${t('tokens', { i: fmtTok((task.tokens.input || 0) + (task.tokens.cached || 0)), o: fmtTok(task.tokens.output), r: task.tokens.requests })}${task.model ? ' · ' + task.model : ''}${task.context && task.context.end != null ? ' · ' + t('context', { p: fmtPct(task.context.end) }) : ''}`))
        }

        function CurrentTasks(props) {
          const agents = props.agents || []
          return h('div', { className: 'lg-card' },
            h('div', { className: 'lg-title' }, t('current')),
            agents.length === 0 ? h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } }, t('noCurrent')) :
              agents.map((a) => {
                const modes = t('modes')
                const modeColor = a.mode === 'stopped' ? toneColor['lg-bad'] : a.mode === 'handoff' ? toneColor['lg-warn'] : a.mode === 'override' ? 'var(--dsw-alias-brand-primary,#2563eb)' : 'var(--dsw-alias-label-secondary,#6b7280)'
                return h('div', { key: a.sessionId, style: { marginTop: 10 } },
                  h('div', { className: 'lg-row' },
                    h('span', { className: 'lg-badge', style: { color: modeColor } }, (modes && modes[a.mode]) || a.mode),
                    h('span', { style: { fontWeight: 550, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 420 } }, a.task ? a.task.prompt || '—' : a.cwd),
                    h('span', { style: { flex: 1 } }),
                    a.mode === 'stopped' || a.mode === 'handoff' ? h('button', {
                      type: 'button', className: 'lg-btn', title: t('releaseHint'),
                      onClick: async () => { try { await apiPost('release', { sessionId: a.sessionId }); props.onChange() } catch (e) { console.error(e) } },
                    }, t('release')) : null),
                  h('div', { className: 'lg-s lg-dim' },
                    a.task ? h(React.Fragment, null, `${t('spentSoFar')}: `, h(SpentChips, { windows: a.task.spent }), ` · ${fmtDur(Date.now() - a.task.startedAt)} · ${t('tokens', { i: fmtTok((a.task.tokens.input || 0) + (a.task.tokens.cached || 0)), o: fmtTok(a.task.tokens.output), r: a.task.tokens.requests })}`) : null,
                    a.context ? ` · ${t('context', { p: fmtPct(a.context.usedPercent) })}` : '',
                    a.cwd ? ` · ${a.cwd}` : ''))
              }))
        }

        function Dashboard() {
          React.useSyncExternalStore(subscribe, snapshot)
          const { data: d, error, load } = useDashboard(5000)
          const refresh = () => load(false)
          if (!d) return h('div', { className: 'lg-dim' }, error ? t('loadError', { e: error }) : t('loading'))
          const s = d.settings
          const over = new Set(d.overridden || [])
          const avgByKey = {}
          for (const a of d.averages || []) avgByKey[a.key] = a
          const enabledSources = (d.sources || []).filter((x) => x.enabled)
          const kinds = t('kinds')
          return h('div', { className: 'lg-wrap' },
            h('div', { className: 'lg-row' },
              h('span', { style: { fontWeight: 650, fontSize: 15 } }, `Limit Guard v${d.version || VERSION}`),
              h('span', { className: 'lg-dim' }, t('tagline')),
              h('span', { className: 'lg-dim' }, t('up', { n: Math.round((d.uptimeSec || 0) / 60) })),
              h('span', { style: { flex: 1 } }),
              h('button', { type: 'button', className: 'lg-btn', onClick: () => load(true) }, t('refresh'))),
            h('div', { className: 'lg-row' },
              h(Toggle, { k: 'enabled', label: t('tGuard'), on: s.enabled, onChange: refresh }),
              h(Toggle, { k: 'blockGoals', label: t('tGoals'), on: s.blockGoals, onChange: refresh }),
              h(Toggle, { k: 'stopRetryOnQuota', label: t('tRetry'), on: s.stopRetryOnQuota, onChange: refresh })),
            h('div', { className: 'lg-row', style: { gap: 14 } },
              h(NumField, { k: 'warnPercent', label: t('thWarn'), value: s.warnPercent, overridden: over.has('warnPercent'), onChange: refresh }),
              h(NumField, { k: 'hardStopPercent', label: t('thHard'), value: s.hardStopPercent, overridden: over.has('hardStopPercent'), onChange: refresh }),
              h(NumField, { k: 'contextWarnPercent', label: t('thCtx'), value: s.contextWarnPercent, overridden: over.has('contextWarnPercent'), onChange: refresh }),
              h(NumField, { k: 'graceSteps', label: t('thGrace'), value: s.graceSteps, integer: true, overridden: over.has('graceSteps'), onChange: refresh }),
              over.size > 0 ? h('button', { type: 'button', className: 'lg-btn', onClick: async () => { try { await apiPost('settings', { reset: true }); refresh() } catch (e) { console.error(e) } } }, t('resetDefaults')) : null),
            error ? h('div', { className: 'lg-s lg-bad' }, t('loadError', { e: error })) : null,
            h('div', { className: 'lg-title' }, t('windows')),
            enabledSources.length === 0 ? h('div', { className: 'lg-dim' }, t('noSources')) :
              h('div', { className: 'lg-grid' }, enabledSources.flatMap((src) => {
                if (src.error) return [h('div', { key: src.id, className: 'lg-card' }, h('div', { className: 'lg-title' }, src.id), h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } }, t('srcErr', { e: src.error })), h('div', { className: 'lg-s lg-dim' }, (src.providers || []).join(', ')))]
                if (!src.windows || src.windows.length === 0) return [h('div', { key: src.id, className: 'lg-card' }, h('div', { className: 'lg-title' }, src.id), h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } }, src.limited ? t('limited') : t('srcNoWin')))]
                return src.windows.map((w) => h(WindowCard, { key: w.key, src, w, s, avg: avgByKey[w.key] }))
              })),
            h('div', { style: { display: 'grid', gridTemplateColumns: 'minmax(0,1fr) minmax(0,1fr)', gap: 10 } },
              h(PreviousTask, { task: (d.history || [])[0] }),
              h(CurrentTasks, { agents: d.agents, onChange: refresh })),
            h('div', { className: 'lg-card' },
              h('div', { className: 'lg-row', style: { justifyContent: 'space-between' } },
                h('div', { className: 'lg-title' }, t('history')),
                (d.history || []).length > 0 || (d.events || []).length > 0 ? h('button', { type: 'button', className: 'lg-btn', onClick: async () => { try { await apiPost('clear-history', {}); refresh() } catch (e) { console.error(e) } } }, t('clear')) : null),
              (d.history || []).length === 0 ? h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } }, t('noPrevious')) :
                h('table', { className: 'lg-table' },
                  h('thead', null, h('tr', null, h('th', null, t('colWhen')), h('th', null, t('colTask')), h('th', null, t('colTime')), h('th', null, t('colSpent')), h('th', null, t('colTokens')), h('th', null, t('colEnd')))),
                  h('tbody', null, d.history.map((task) => h('tr', { key: task.id + task.startedAt },
                    h('td', { className: 'lg-dim', style: { whiteSpace: 'nowrap' } }, fmtClock(task.startedAt)),
                    h('td', { className: 'lg-clip', title: `${task.prompt || ''}\n${task.cwd || ''}` }, task.prompt || '—'),
                    h('td', { className: 'lg-dim', style: { whiteSpace: 'nowrap' } }, fmtDur(task.endedAt - task.startedAt)),
                    h('td', null, h(SpentChips, { windows: task.windows })),
                    h('td', { className: 'lg-dim', style: { whiteSpace: 'nowrap' } }, `${fmtTok((task.tokens.input || 0) + (task.tokens.cached || 0))} / ${fmtTok(task.tokens.output)}`),
                    h('td', null, h(EndBadge, { kind: task.guard && task.guard.stopped && task.end === 'completed' ? 'blocked' : task.end }))))))),
            h('div', { className: 'lg-card' },
              h('div', { className: 'lg-title' }, t('events')),
              (d.events || []).length === 0 ? h('div', { className: 'lg-s lg-dim', style: { marginTop: 6 } }, t('noEvents')) :
                h('table', { className: 'lg-table' }, h('tbody', null, d.events.map((ev, i) => h('tr', { key: i },
                  h('td', { className: 'lg-dim', style: { whiteSpace: 'nowrap' } }, fmtClock(ev.ts)),
                  h('td', null, h('span', { className: 'lg-badge', style: { color: /reject|stop|blocked|nearly/.test(ev.kind) ? toneColor['lg-bad'] : /warn|handoff/.test(ev.kind) ? toneColor['lg-warn'] : 'var(--dsw-alias-label-secondary,#6b7280)' } }, (kinds && kinds[ev.kind]) || ev.kind)),
                  h('td', { className: 'lg-dim' }, ev.session || ''),
                  h('td', { className: 'lg-clip', title: ev.detail }, ev.detail)))))),
            h('div', { className: 'lg-s lg-dim' }, t('footer', { path: d.handoffPath || HANDOFF_PATH })))
        }

        function HandoffButton(props) {
          React.useSyncExternalStore(subscribe, snapshot)
          const turns = props.useProjection ? props.useProjection('sessionStats', (v) => (v && v.turns) || 0) : 0
          const blank = props.useSession ? props.useSession((s) => !!(s && s.blank)) : true
          const draft = props.useInput ? props.useInput((s) => (s && s.draft) || '') : ''
          const phase = props.useInput ? props.useInput((s) => (s && s.phase) || 'plain') : 'plain'
          // setDraft reaches the editor asynchronously: submit once the published draft
          // shows the new text (or after a short fallback delay).
          const pending = React.useRef(null)
          const actions = props.inputActions
          const norm = (s) => String(s).replace(/\r/g, '').trim()
          const fire = React.useCallback(() => {
            if (!pending.current) return
            clearTimeout(pending.current.timer)
            pending.current = null
            actions && actions.submit()
          }, [actions])
          React.useEffect(() => {
            if (pending.current && norm(draft) === pending.current.text) fire()
          }, [draft, fire])
          React.useEffect(() => () => { if (pending.current) clearTimeout(pending.current.timer) }, [])
          const used = turns > 0 || !blank
          if (!used || !actions) return null
          const busy = phase !== 'plain' || pending.current !== null
          const onClick = () => {
            const extra = draft.trim()
            const text = extra ? `${t('prompt')}\n\n${t('note')}\n${extra}` : t('prompt')
            pending.current = { text: norm(text), timer: setTimeout(fire, 800) }
            actions.setDraft(text)
          }
          const title = busy ? t('busy') : t('tooltip')
          return h('button', { type: 'button', className: 'dlg-handoff-btn', onClick, disabled: busy, title, 'aria-label': t('button') }, h(Icon))
        }

        ctx.slots.inject('settings.section', () => ctx.slots.register({
          name: 'settings.section',
          id: 'limit-guard',
          order: 440,
          label: () => t('nav'),
        }, () => h(Dashboard)))

        ctx.slots.inject('conversation.input.right', () => ctx.slots.register({
          name: 'conversation.input.right',
          id: 'limit-guard-handoff',
          order: -10,
          label: () => t('button'),
        }, HandoffButton))
      },
    }
  },
})
