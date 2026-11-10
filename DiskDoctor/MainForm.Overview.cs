using DiskDoctor.Core;
using DiskDoctor.UI;

namespace DiskDoctor;

public sealed partial class MainForm
{
    ComboBox _drive;
    Button _btnScan, _btnStop;
    TreeView _tree;
    ListView _lvDir, _lvTop, _lvExt, _lvGroups;
    TextBox _dirInfo;
    UsageBar _usage;
    Label _summary;
    CancellationTokenSource _scanCts;
    DiskScanner _scanner;
    DirNode _root;
    readonly Dictionary<DirNode, TreeNode> _treeMap = new();
    DirNode _currentDir;
    const string ChooseFolder = "Выбрать папку…";

    sealed class FileRow { public string Path; public long Size; public DateTime Modified; public DirNode Dir; }

    TabPage BuildOverviewTab()
    {
        var page = new TabPage("Обзор диска");

        _drive = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Margin = new Padding(3, 5, 3, 3) };
        FillDrives();
        _drive.SelectedIndexChanged += (_, _) =>
        {
            if (_drive.SelectedItem as string == ChooseFolder)
            {
                using var dlg = new FolderBrowserDialog { Description = "Папка для анализа" };
                if (dlg.ShowDialog(this) == DialogResult.OK) { _drive.Items.Insert(_drive.Items.Count - 1, dlg.SelectedPath); _drive.SelectedIndex = _drive.Items.Count - 2; }
                else _drive.SelectedIndex = 0;
            }
        };
        _btnScan = Ui.Btn("▶ Сканировать", async (_, _) => await Scan());
        _btnStop = Ui.Btn("■ Стоп", (_, _) => _scanCts?.Cancel());
        _btnStop.Enabled = false;
        var btnUp = Ui.Btn("↑ Вверх", (_, _) => { if (_currentDir?.Parent != null) SelectDir(_currentDir.Parent); });
        var btnRefresh = Ui.Btn("⟳ Диски", (_, _) => FillDrives());
        var top = Ui.Bar(Ui.Lbl("Диск:"), _drive, btnRefresh, _btnScan, _btnStop, btnUp);

        _summary = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 26, Padding = new Padding(6, 6, 0, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        _usage = new UsageBar { Height = 0 };

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ShowLines = true, Font = new Font("Segoe UI", 9f) };
        _tree.BeforeExpand += (_, e) => PopulateTreeNode(e.Node);
        _tree.AfterSelect += (_, e) => { if (e.Node?.Tag is DirNode d) ShowDir(d); };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
        _tree.ContextMenuStrip = BuildPathMenu(() => _tree.SelectedNode?.Tag is DirNode d ? new List<object> { d } : new List<object>());

        _lvDir = Ui.List(false, ("Имя", 300, false), ("Размер", 95, true), ("% от папки", 120, false), ("Файлов", 80, true), ("Изменён", 110, false), ("Оценка", 150, false), ("Что это", 330, false));
        _lvDir.OwnerDraw = true;
        _lvDir.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _lvDir.DrawItem += (_, e) => e.DrawDefault = false;
        _lvDir.DrawSubItem += DrawPercentBar;
        _lvDir.MouseUp += (_, _) => _lvDir.Invalidate();
        _lvDir.SelectedIndexChanged += (_, _) => { ShowItemInfo(_lvDir); _lvDir.Invalidate(); };
        _lvDir.DoubleClick += (_, _) => { if (_lvDir.SelectedItems.Count == 1 && (_lvDir.SelectedItems[0].Tag as Row)?.Obj is DirNode d) SelectDir(d); };
        _lvDir.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Back && _currentDir?.Parent != null) SelectDir(_currentDir.Parent);
            if (e.KeyCode == Keys.Enter && _lvDir.SelectedItems.Count == 1 && (_lvDir.SelectedItems[0].Tag as Row)?.Obj is DirNode d) SelectDir(d);
            if (e.KeyCode == Keys.Delete) _ = DeleteSelected(SelectedObjs(_lvDir), e.Shift ? false : true);
        };
        _lvDir.ContextMenuStrip = BuildPathMenu(() => SelectedObjs(_lvDir));

        _dirInfo = Ui.InfoBox();

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        rightSplit.Panel1.Controls.Add(_lvDir);
        rightSplit.Panel2.Controls.Add(_dirInfo);
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(rightSplit);

        page.Controls.Add(split);
        page.Controls.Add(_usage);
        page.Controls.Add(_summary);
        page.Controls.Add(top);
        page.HandleCreated += (_, _) => { };
        Load += (_, _) =>
        {
            split.SplitterDistance = 420;
            rightSplit.SplitterDistance = Math.Max(100, rightSplit.Height - 110);
        };
        return page;
    }

    void FillDrives()
    {
        _drive.Items.Clear();
        int sel = 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : $" [{d.VolumeLabel}]";
                string sys = d.Name.Equals(Sys.SystemDrive, StringComparison.OrdinalIgnoreCase) ? " — системный" : "";
                double pct = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                if (sys != "") sel = _drive.Items.Count;
                _drive.Items.Add(new DriveItem(d.Name, $"{d.Name}{label}{sys}: свободно {Fmt.Size(d.TotalFreeSpace)} из {Fmt.Size(d.TotalSize)} (занято {pct:0}%)"));
            }
            catch { }
        }
        _drive.Items.Add(ChooseFolder);
        _drive.SelectedIndex = Math.Min(sel, _drive.Items.Count - 1);
    }

    sealed class DriveItem
    {
        public string Root, Text;
        public DriveItem(string r, string t) { Root = r; Text = t; }
        public override string ToString() => Text;
    }

    string SelectedRoot() => _drive.SelectedItem switch
    {
        DriveItem d => d.Root,
        string s when s != ChooseFolder => s,
        _ => null,
    };

    async Task Scan()
    {
        string root = SelectedRoot();
        if (root == null || !Directory.Exists(root)) return;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _scanner = new DiskScanner();
        _btnScan.Enabled = false; _btnStop.Enabled = true;
        _tree.Nodes.Clear(); _treeMap.Clear(); _lvDir.Items.Clear(); _lvTop.Items.Clear(); _lvExt.Items.Clear(); _lvGroups.Items.Clear();
        _usage.Segments.Clear(); _usage.Total = 0; _usage.Height = 0; _usage.Invalidate();
        _summary.Text = "Сканирование…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => SetStatus($"Сканирование: {Fmt.Count(_scanner.FilesScanned)} файлов, {Fmt.Size(_scanner.BytesScanned)} — {_scanner.CurrentPath}");
        timer.Start();
        BeginBusy("Сканирование…");
        Log.Write($"Сканирование {root}");
        try
        {
            _root = await Task.Run(() => _scanner.Scan(root, ct));
            var totals = await Task.Run(() => Classifier.Totals(_root));
            timer.Stop();
            ShowScanResult(root, totals, ct.IsCancellationRequested, sw.Elapsed);
        }
        catch (Exception ex) { Log.Write("Ошибка сканирования: " + ex.Message); }
        finally
        {
            timer.Stop(); timer.Dispose();
            _btnScan.Enabled = true; _btnStop.Enabled = false;
            EndBusy();
        }
    }

    void ShowScanResult(string root, Dictionary<Risk, long> totals, bool cancelled, TimeSpan elapsed)
    {
        long used = 0, total = 0, free = 0;
        bool isDrive = Path.GetPathRoot(root)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true;
        if (isDrive)
        {
            try { var di = new DriveInfo(root); total = di.TotalSize; free = di.TotalFreeSpace; used = total - free; } catch { }
        }
        long unaccounted = isDrive ? Math.Max(0, used - _root.Size) : 0;

        _summary.Text = isDrive
            ? $"{root}  занято {Fmt.Size(used)} из {Fmt.Size(total)}, свободно {Fmt.Size(free)}.  Найдено файлов: {Fmt.Count(_root.FileCount)} на {Fmt.Size(_root.Size)}" + (cancelled ? "  (сканирование прервано)" : "")
            : $"{root}: {Fmt.Size(_root.Size)}, файлов {Fmt.Count(_root.FileCount)}" + (cancelled ? "  (прервано)" : "");

        _usage.Segments = new()
        {
            ("Системное", totals[Risk.Critical], Classifier.RiskColor(Risk.Critical)),
            ("Программы", totals[Risk.Program], Color.FromArgb(240, 140, 60)),
            ("Осторожно (данные программ)", totals[Risk.Caution], Color.FromArgb(235, 190, 60)),
            ("Ваши файлы", totals[Risk.User], Color.FromArgb(90, 140, 220)),
            ("Можно удалить (кэш/мусор)", totals[Risk.Safe], Color.FromArgb(60, 170, 90)),
            ("Не классифицировано", totals[Risk.Unknown], Color.FromArgb(170, 170, 170)),
            ("Скрыто/недоступно (теневые копии, служебное)", unaccounted, Color.FromArgb(90, 90, 90)),
            ("Свободно", free, Color.White),
        };
        _usage.Total = _usage.Segments.Sum(s => s.size);
        _usage.Height = 60;
        _usage.Invalidate();

        var rootNode = MakeTreeNode(_root, _root.Size);
        rootNode.Text = $"{_root.Name}  —  {Fmt.Size(_root.Size)}";
        _tree.Nodes.Add(rootNode);
        PopulateTreeNode(rootNode);
        rootNode.Expand();
        _tree.SelectedNode = rootNode;

        FillTopFiles();
        FillTypes();
        string msg = $"Готово за {elapsed.TotalSeconds:0.0} с: {Fmt.Count(_scanner.FilesScanned)} файлов, {Fmt.Count(_scanner.DirsScanned)} папок" +
                     (_scanner.DeniedDirs > 0 ? $", нет доступа к {_scanner.DeniedDirs} папкам" + (Sys.IsAdmin ? "" : " (запустите от администратора)") : "") +
                     (unaccounted > 1L << 30 ? $". {Fmt.Size(unaccounted)} не видно при сканировании — это точки восстановления, служебные данные NTFS или недоступные папки." : "");
        SetStatus(msg);
        Log.Write(msg);
    }

    // ---------- дерево ----------
    TreeNode MakeTreeNode(DirNode d, long parentSize)
    {
        var cls = Classifier.ForNode(d);
        var tn = new TreeNode(NodeText(d, parentSize)) { Tag = d };
        if (cls.Risk != Risk.Unknown) tn.ForeColor = Classifier.RiskColor(cls.Risk);
        if (d.HasChildren) tn.Nodes.Add(new TreeNode("…"));
        _treeMap[d] = tn;
        return tn;
    }

    static string NodeText(DirNode d, long parentSize) =>
        $"{d.Name}  —  {Fmt.Size(d.Size)}" + (parentSize > 0 ? $"  ({100.0 * d.Size / parentSize:0.#}%)" : "") + (d.Denied ? "  🔒" : "") + (d.IsLink ? "  ↪" : "");

    void PopulateTreeNode(TreeNode tn)
    {
        if (tn.Tag is not DirNode d) return;
        if (tn.Nodes.Count == 1 && tn.Nodes[0].Tag == null)
        {
            tn.Nodes.Clear();
            if (d.Children == null) return;
            _tree.BeginUpdate();
            foreach (var c in d.Children.Take(300)) tn.Nodes.Add(MakeTreeNode(c, d.Size));
            if (d.Children.Count > 300) tn.Nodes.Add(new TreeNode($"… ещё {d.Children.Count - 300} папок (см. список справа)") { ForeColor = Color.Gray, Tag = "more" });
            _tree.EndUpdate();
        }
    }

    void SelectDir(DirNode d)
    {
        var chain = new Stack<DirNode>();
        for (var a = d; a != null; a = a.Parent) chain.Push(a);
        TreeNode tn = null;
        foreach (var n in chain)
        {
            if (tn != null) { PopulateTreeNode(tn); tn.Expand(); }
            if (!_treeMap.TryGetValue(n, out tn)) { ShowDir(d); return; }
        }
        _tree.SelectedNode = tn;
        tn?.EnsureVisible();
    }

    void ShowDir(DirNode d)
    {
        _currentDir = d;
        _lvDir.BeginUpdate();
        _lvDir.Items.Clear();
        _lvDir.ListViewItemSorter = null;
        if (d.Children != null)
            foreach (var c in d.Children.Take(2000))
            {
                var cls = Classifier.ForNode(c);
                var it = new ListViewItem(new[]
                {
                    "📁 " + c.Name + (c.IsLink ? "  ↪ ссылка" : "") + (c.Denied ? "  🔒 нет доступа" : ""),
                    Fmt.Size(c.Size), Fmt.Pct(c.Size, d.Size), Fmt.Count(c.FileCount), "",
                    Classifier.RiskName(cls.Risk), cls.Title,
                }) { Tag = new Row(c, c.Name, c.Size, c.Size, c.FileCount, DateTime.MinValue, (int)cls.Risk, cls.Title ?? "") };
                Ui.StyleRisk(it, cls.Risk, 5);
                _lvDir.Items.Add(it);
            }

        // файлы этой папки читаем с диска
        var files = new List<FileRow>();
        try
        {
            foreach (var f in new DirectoryInfo(d.FullPath).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                files.Add(new FileRow { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime, Dir = d });
        }
        catch { }
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var f in files.Take(1000))
        {
            string name = Path.GetFileName(f.Path);
            var cls = Classifier.ForFile(d, name);
            var it = new ListViewItem(new[]
            {
                "📄 " + name, Fmt.Size(f.Size), Fmt.Pct(f.Size, d.Size), "", f.Modified.ToString("dd.MM.yyyy"),
                Classifier.RiskName(cls.Risk), cls.Title,
            }) { Tag = new Row(f, name, f.Size, f.Size, 0L, f.Modified, (int)cls.Risk, cls.Title ?? "") };
            Ui.StyleRisk(it, cls.Risk, 5);
            _lvDir.Items.Add(it);
        }
        if (files.Count > 1000)
            _lvDir.Items.Add(new ListViewItem($"… ещё {files.Count - 1000} мелких файлов ({Fmt.Size(files.Skip(1000).Sum(f => f.Size))})") { ForeColor = Color.Gray });

        if (d.Parent == null && Path.GetPathRoot(d.FullPath)?.Equals(d.FullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var di = new DriveInfo(d.FullPath);
                long hidden = di.TotalSize - di.TotalFreeSpace - d.Size;
                if (hidden > 256L << 20)
                {
                    var it = new ListViewItem(new[] { "🛈 [Не видно при сканировании]", Fmt.Size(hidden), "", "", "", "Системное", "Теневые копии (точки восстановления), MFT, недоступные папки" }) { Tag = null };
                    it.ForeColor = Color.DimGray;
                    _lvDir.Items.Add(it);
                }
            }
            catch { }
        }
        _lvDir.EndUpdate();

        var own = Classifier.ForNode(d);
        _dirInfo.Text = $"{d.FullPath}\r\nРазмер: {Fmt.Size(d.Size)}, файлов: {Fmt.Count(d.FileCount)}" +
                        (own.Risk != Risk.Unknown ? $"\r\nОценка: {Classifier.RiskName(own.Risk)} — {own.Title}\r\n{own.Advice}" : "") +
                        (d.Denied ? "\r\n🔒 Нет доступа к этой папке — запустите программу от имени администратора." : "");
    }

    void ShowItemInfo(ListView lv)
    {
        if (lv.SelectedItems.Count == 0) return;
        if (lv.SelectedItems.Count > 1)
        {
            long sum = SelectedObjs(lv).Sum(o => o is DirNode d ? d.Size : o is FileRow f ? f.Size : 0);
            _dirInfo.Text = $"Выбрано: {lv.SelectedItems.Count}, всего {Fmt.Size(sum)}";
            return;
        }
        var o = (lv.SelectedItems[0].Tag as Row)?.Obj;
        string path = o is DirNode dn ? dn.FullPath : (o as FileRow)?.Path;
        if (path == null) { _dirInfo.Text = lv.SelectedItems[0].SubItems[^1].Text; return; }
        var cls = o is DirNode d2 ? Classifier.ForNode(d2) : Classifier.ForPath(path);
        long size = o is DirNode d3 ? d3.Size : ((FileRow)o).Size;
        _dirInfo.Text = $"{path}\r\nРазмер: {Fmt.Size(size)}\r\n" +
                        (cls.Risk == Risk.Unknown ? "Оценка: нет данных — это не системный объект, но и не известный кэш. Решайте по содержимому." :
                            $"Оценка: {Classifier.RiskName(cls.Risk)} — {cls.Title}{(cls.Inherited ? " (по родительской папке)" : "")}\r\n{cls.Advice}");
    }

    void DrawPercentBar(object sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 2 || e.Item.Tag is not Row row) { e.DrawDefault = true; return; }
        var parent = _currentDir;
        long size = row.Obj is DirNode d ? d.Size : row.Obj is FileRow f ? f.Size : 0;
        double frac = parent == null || parent.Size <= 0 ? 0 : (double)size / parent.Size;
        var g = e.Graphics;
        var back = e.Item.Selected ? SystemColors.Highlight : e.Item.BackColor;
        using (var b = new SolidBrush(back)) g.FillRectangle(b, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 3, e.Bounds.Y + 3, (int)((e.Bounds.Width - 6) * frac), e.Bounds.Height - 6);
        if (r.Width > 0) using (var b = new SolidBrush(Color.FromArgb(120, 70, 130, 200))) g.FillRectangle(b, r);
        TextRenderer.DrawText(g, e.SubItem.Text, e.Item.ListView.Font, e.Bounds, e.Item.Selected ? SystemColors.HighlightText : SystemColors.WindowText,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    static List<object> SelectedObjs(ListView lv) =>
        lv.SelectedItems.Cast<ListViewItem>().Select(i => (i.Tag as Row)?.Obj).Where(o => o != null).ToList();

    // ---------- контекстное меню ----------
    ContextMenuStrip BuildPathMenu(Func<List<object>> getSel)
    {
        var m = new ContextMenuStrip();
        string PathOf(object o) => o switch { DirNode d => d.FullPath, FileRow f => f.Path, FileEntry fe => fe.Path, _ => null };
        m.Items.Add("Открыть в Проводнике", null, (_, _) => { var p = getSel().Select(PathOf).FirstOrDefault(); if (p != null) Sys.OpenInExplorer(p); });
        m.Items.Add("Копировать путь", null, (_, _) => { var ps = getSel().Select(PathOf).Where(p => p != null).ToList(); if (ps.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, ps)); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Удалить в Корзину", null, async (_, _) => await DeleteSelected(getSel(), true));
        m.Items.Add("Удалить навсегда (Shift+Del)", null, async (_, _) => await DeleteSelected(getSel(), false));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Искать хвосты программы по имени этой папки", null, (_, _) =>
        {
            var p = getSel().Select(PathOf).FirstOrDefault();
            if (p == null) return;
            StartLeftoverSearch(Path.GetFileName(p.TrimEnd('\\')), "", null);
        });
        return m;
    }

    async Task DeleteSelected(List<object> objs, bool toRecycle)
    {
        var items = objs.Select(o => o switch
        {
            DirNode d => (path: d.FullPath, size: d.Size, cls: Classifier.ForNode(d), obj: o),
            FileRow f => (path: f.Path, size: f.Size, cls: Classifier.ForPath(f.Path), obj: o),
            FileEntry fe => (path: fe.Path, size: fe.Size, cls: Classifier.ForPath(fe.Path), obj: o),
            _ => (path: (string)null, size: 0L, cls: RuleMatch.None, obj: o),
        }).Where(x => x.path != null).ToList();
        if (items.Count == 0) return;
        if (items.Any(i => i.obj is DirNode d && d.Parent == null)) { Ui.Warn(this, "Нельзя удалить корень сканирования."); return; }

        var critical = items.Where(i => i.cls.Risk == Risk.Critical).ToList();
        if (critical.Count > 0)
        {
            Ui.Warn(this, "Удаление заблокировано — это системные объекты:\n\n" +
                string.Join("\n\n", critical.Take(5).Select(c => $"{c.path}\n→ {c.cls.Title}. {c.cls.Advice}")), "Системные файлы");
            return;
        }
        long total = items.Sum(i => i.size);
        string warn = "";
        if (items.Any(i => i.cls.Risk == Risk.Program))
            warn += "\n\n⚠ Среди выбранного есть папки установленных программ. Правильнее удалить программу на вкладке «Программы» — иначе в системе останутся «сломанные» записи (как у DaVinci Resolve).";
        if (items.Any(i => i.cls.Risk == Risk.Caution))
            warn += "\n\n⚠ Среди выбранного есть данные/настройки программ. Убедитесь, что они не нужны.";
        if (items.Any(i => i.cls.Risk == Risk.User))
            warn += "\n\n⚠ Это ваши файлы — проверьте, что они не нужны.";
        string list = string.Join("\n", items.Take(8).Select(i => $"• {i.path} ({Fmt.Size(i.size)})")) + (items.Count > 8 ? $"\n… и ещё {items.Count - 8}" : "");
        if (!Ui.Confirm(this, $"{(toRecycle ? "Переместить в Корзину" : "УДАЛИТЬ НАВСЕГДА")} {items.Count} {Plural(items.Count, "объект", "объекта", "объектов")} ({Fmt.Size(total)})?\n\n{list}{warn}",
                "Удаление", warn.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question)) return;

        var res = await Bg("Удаление…", () =>
        {
            var r = new DeleteResult();
            foreach (var i in items)
            {
                var one = FileOps.DeleteItem(i.path, toRecycle, false, false);
                r.Merge(one);
                Log.Write($"Удалено{(toRecycle ? " (в Корзину)" : "")}: {i.path} — {Fmt.Size(one.Freed)}{(one.Failed > 0 ? $", не удалось: {one.Failed}" : "")}");
            }
            return r;
        });

        // обновляем дерево
        foreach (var i in items)
        {
            bool gone = !File.Exists(i.path) && !Directory.Exists(i.path);
            if (i.obj is DirNode d)
            {
                if (gone)
                {
                    DiskScanner.RemoveNode(d);
                    if (_treeMap.TryGetValue(d, out var tn)) { tn.Remove(); _treeMap.Remove(d); }
                }
                else
                {
                    long before = d.Size; var (now, _) = FileOps.Measure(d.FullPath);
                    for (var a = d; a != null; a = a.Parent) a.Size -= before - now;
                }
            }
            else if (gone && i.obj is FileRow f) DiskScanner.SubtractFile(f.Dir, f.Size);
            else if (gone && i.obj is FileEntry fe && _root != null)
            {
                var dir = FindNode(Path.GetDirectoryName(fe.Path));
                if (dir != null) DiskScanner.SubtractFile(dir, fe.Size);
            }
        }
        foreach (var kv in _treeMap) kv.Value.Text = kv.Key.Parent == null ? $"{kv.Key.Name}  —  {Fmt.Size(kv.Key.Size)}" : NodeText(kv.Key, kv.Key.Parent.Size);
        if (_currentDir != null) ShowDir(_currentDir);
        foreach (ListViewItem it in _lvTop.Items.Cast<ListViewItem>().ToList())
            if ((it.Tag as Row)?.Obj is FileEntry fe2 && !File.Exists(fe2.Path)) it.Remove();

        string msg = $"Освобождено {Fmt.Size(res.Freed)}" + (res.Failed > 0 ? $". Не удалось удалить {res.Failed} (заняты другой программой или нет прав)." : ".");
        SetStatus(msg);
        if (res.Failed > 0) Ui.Warn(this, msg + "\n\n" + string.Join("\n", res.Errors.Take(10)));
    }

    DirNode FindNode(string path)
    {
        if (_root == null || path == null) return null;
        if (!path.StartsWith(_root.FullPath, StringComparison.OrdinalIgnoreCase)) return null;
        var cur = _root;
        foreach (var seg in path.Substring(_root.FullPath.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur.Children?.FirstOrDefault(c => c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return null;
        }
        return cur;
    }

    // ---------- крупные файлы ----------
    TabPage BuildTopFilesTab()
    {
        var page = new TabPage("Крупные файлы");
        _lvTop = Ui.List(false, ("Файл", 280, false), ("Размер", 95, true), ("Папка", 420, false), ("Изменён", 100, false), ("Оценка", 150, false), ("Что это / совет", 420, false));
        _lvTop.ContextMenuStrip = BuildPathMenu(() => SelectedObjs(_lvTop));
        _lvTop.DoubleClick += (_, _) => { if (SelectedObjs(_lvTop).FirstOrDefault() is FileEntry f) Sys.OpenInExplorer(f.Path); };
        _lvTop.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) _ = DeleteSelected(SelectedObjs(_lvTop), !e.Shift); };
        page.Controls.Add(_lvTop);
        page.Controls.Add(new Label { Text = "500 самых больших файлов на диске. Двойной щелчок — показать в Проводнике, правая кнопка — удалить.", Dock = DockStyle.Top, Height = 24, Padding = new Padding(4, 5, 0, 0) });
        return page;
    }

    void FillTopFiles()
    {
        _lvTop.BeginUpdate();
        _lvTop.Items.Clear();
        foreach (var f in _scanner.TopFiles())
        {
            var cls = Classifier.ForPath(f.Path);
            var it = new ListViewItem(new[]
            {
                Path.GetFileName(f.Path), Fmt.Size(f.Size), Path.GetDirectoryName(f.Path), f.Modified.ToString("dd.MM.yyyy"),
                Classifier.RiskName(cls.Risk), string.IsNullOrEmpty(cls.Title) ? "" : $"{cls.Title}. {cls.Advice}",
            }) { Tag = new Row(f, Path.GetFileName(f.Path), f.Size, Path.GetDirectoryName(f.Path), f.Modified, (int)cls.Risk, cls.Title ?? "") };
            Ui.StyleRisk(it, cls.Risk, 4);
            _lvTop.Items.Add(it);
        }
        _lvTop.EndUpdate();
    }

    // ---------- типы файлов ----------
    TabPage BuildTypesTab()
    {
        var page = new TabPage("Типы файлов");
        _lvGroups = Ui.List(false, ("Группа", 220, false), ("Размер", 100, true), ("Файлов", 90, true), ("%", 60, true));
        _lvExt = Ui.List(false, ("Расширение", 140, false), ("Группа", 200, false), ("Размер", 100, true), ("Файлов", 90, true), ("%", 60, true));
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(_lvGroups);
        split.Panel2.Controls.Add(_lvExt);
        page.Controls.Add(split);
        page.Controls.Add(new Label { Text = "Чем занято место: группы файлов (слева) и отдельные расширения (справа).", Dock = DockStyle.Top, Height = 24, Padding = new Padding(4, 5, 0, 0) });
        Load += (_, _) => split.SplitterDistance = 520;
        return page;
    }

    void FillTypes()
    {
        var exts = _scanner.Extensions();
        long total = Math.Max(1, exts.Sum(e => e.Size));
        _lvExt.BeginUpdate(); _lvExt.Items.Clear();
        foreach (var e in exts.Take(500))
            _lvExt.Items.Add(new ListViewItem(new[] { e.Ext, FileGroups.Of(e.Ext), Fmt.Size(e.Size), Fmt.Count(e.Count), Fmt.Pct(e.Size, total) })
                { Tag = new Row(e, e.Ext, FileGroups.Of(e.Ext), e.Size, e.Count, e.Size) });
        _lvExt.EndUpdate();
        _lvGroups.BeginUpdate(); _lvGroups.Items.Clear();
        foreach (var g in exts.GroupBy(e => FileGroups.Of(e.Ext)).Select(g => (name: g.Key, size: g.Sum(x => x.Size), count: g.Sum(x => x.Count))).OrderByDescending(g => g.size))
            _lvGroups.Items.Add(new ListViewItem(new[] { g.name, Fmt.Size(g.size), Fmt.Count(g.count), Fmt.Pct(g.size, total) }) { Tag = new Row(g.name, g.name, g.size, g.count, g.size) });
        _lvGroups.EndUpdate();
    }
}
