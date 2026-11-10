using System.Diagnostics;
using DiskDoctor.Core;
using DiskDoctor.UI;

namespace DiskDoctor;

public sealed partial class MainForm : Form
{
    readonly ToolTip _tt = new() { AutoPopDelay = 20000 };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 160 };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    int _busy;

    public MainForm(string[] args)
    {
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        Text = "DiskDoctor — анализ и очистка диска" + (Sys.IsAdmin ? " (администратор)" : "");
        Width = 1400; Height = 900;
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Shield;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Icon; } catch { }

        var status = new StatusStrip();
        status.Items.Add(_status);
        status.Items.Add(_progress);

        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildTopFilesTab());
        _tabs.TabPages.Add(BuildTypesTab());
        _tabs.TabPages.Add(BuildJunkTab());
        _tabs.TabPages.Add(BuildProgramsTab());
        _tabs.TabPages.Add(BuildLeftoversTab());
        var logPage = new TabPage("Журнал");
        logPage.Controls.Add(_log);
        logPage.Controls.Add(Ui.Bar(Ui.Btn("Открыть файл журнала", (_, _) => Sys.ShellOpen(Log.FilePath)), Ui.Btn("Папка резервных копий", (_, _) =>
        {
            string d = Path.Combine(AppDir.Data, "Backups"); Directory.CreateDirectory(d); Sys.OpenInExplorer(d);
        })));
        _tabs.TabPages.Add(logPage);

        Controls.Add(_tabs);
        if (!Sys.IsAdmin) Controls.Add(BuildAdminBanner());
        Controls.Add(status);

        Log.Line += line => { if (IsHandleCreated) BeginInvoke(() => _log.AppendText(line + Environment.NewLine)); };
        _tabs.SelectedIndexChanged += OnTabChanged;
        Shown += (_, _) =>
        {
            Log.Write($"DiskDoctor запущен. Администратор: {(Sys.IsAdmin ? "да" : "нет")}. Данные: {AppDir.Data}");
            SetStatus("Выберите диск и нажмите «Сканировать». Или откройте вкладки «Очистка мусора» и «Программы».");
            // параметры командной строки: --scan <диск или папка>, --tab <номер вкладки>
            string scan = Arg("--scan")?.Trim('"');
            if (scan != null && scan.Length == 2 && scan[1] == ':') scan += "\\";
            if (scan != null && Directory.Exists(scan))
            {
                int idx = _drive.Items.Cast<object>().ToList().FindIndex(o => o is DriveItem d && d.Root.Equals(scan, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) { _drive.Items.Insert(_drive.Items.Count - 1, scan); idx = _drive.Items.Count - 2; }
                _drive.SelectedIndex = idx;
                _ = Scan();
            }
            if (int.TryParse(Arg("--tab"), out int tab) && tab >= 0 && tab < _tabs.TabCount) _tabs.SelectedIndex = tab;
            // --find "Название программы" — сразу искать её хвосты
            string find = Arg("--find");
            if (!string.IsNullOrWhiteSpace(find)) StartLeftoverSearch(find, Arg("--publisher") ?? "", null);
        };
    }

    Control BuildAdminBanner()
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.FromArgb(255, 243, 205), Padding = new Padding(6) };
        p.Controls.Add(new Label
        {
            Text = "⚠ Программа запущена без прав администратора: системные папки не просканируются, а очистка системы и удаление программ будут ограничены.",
            AutoSize = true, Margin = new Padding(3, 8, 10, 3),
        });
        p.Controls.Add(Ui.Btn("Перезапустить с правами администратора", (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated") { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch { }
        }));
        return p;
    }

    void OnTabChanged(object sender, EventArgs e)
    {
        var page = _tabs.SelectedTab;
        if (page == _junkPage && !_junkAnalyzedOnce) _ = AnalyzeJunk();
        if (page == _progPage && _programs == null) _ = LoadPrograms();
    }

    // ---------- общий статус ----------
    void SetStatus(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(s)); return; }
        _status.Text = s;
    }

    void BeginBusy(string s)
    {
        _busy++;
        _progress.Visible = true;
        SetStatus(s);
    }

    void EndBusy(string s = null)
    {
        _busy = Math.Max(0, _busy - 1);
        if (_busy == 0) _progress.Visible = false;
        if (s != null) SetStatus(s);
    }

    /// <summary>Выполнить работу в фоне с индикатором.</summary>
    async Task<T> Bg<T>(string status, Func<T> work)
    {
        BeginBusy(status);
        try { return await Task.Run(work); }
        finally { EndBusy(); }
    }

    static string Plural(long n, string one, string few, string many)
    {
        long m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
        return many;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        base.OnFormClosing(e);
    }
}
