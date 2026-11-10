using System.Collections;
using DiskDoctor.Core;

namespace DiskDoctor.UI;

/// <summary>Данные строки ListView: объект + ключи сортировки по колонкам.</summary>
public sealed class Row
{
    public object Obj;
    public IComparable[] Keys;
    public Row(object obj, params IComparable[] keys) { Obj = obj; Keys = keys; }
}

public static class Ui
{
    public static ListView List(bool checkboxes, params (string text, int width, bool right)[] cols)
    {
        var lv = new DoubleBufferedListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            CheckBoxes = checkboxes,
            Dock = DockStyle.Fill,
            GridLines = false,
        };
        foreach (var c in cols)
            lv.Columns.Add(c.text, c.width, c.right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        EnableSorting(lv);
        return lv;
    }

    public static void EnableSorting(ListView lv)
    {
        int sortCol = -1; bool asc = true;
        lv.ColumnClick += (_, e) =>
        {
            if (e.Column == sortCol) asc = !asc; else { sortCol = e.Column; asc = e.Column == 0; }
            lv.ListViewItemSorter = new RowComparer(e.Column, asc);
            lv.Sort();
        };
    }

    sealed class RowComparer : IComparer
    {
        readonly int _col; readonly bool _asc;
        public RowComparer(int col, bool asc) { _col = col; _asc = asc; }
        public int Compare(object x, object y)
        {
            var a = (x as ListViewItem)?.Tag as Row; var b = (y as ListViewItem)?.Tag as Row;
            // служебные строки (без Row) — всегда внизу
            if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
            var ka = _col < a.Keys.Length ? a.Keys[_col] : null;
            var kb = _col < b.Keys.Length ? b.Keys[_col] : null;
            int r;
            if (ka == null || kb == null) r = ka == null ? (kb == null ? 0 : -1) : 1;
            else if (ka is string sa && kb is string sb) r = string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
            else r = ka.CompareTo(kb);
            return _asc ? r : -r;
        }
    }

    public static Button Btn(string text, EventHandler click, string tip = null, ToolTip tt = null)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(3) };
        b.Click += click;
        if (tip != null && tt != null) tt.SetToolTip(b, tip);
        return b;
    }

    public static FlowLayoutPanel Bar(params Control[] controls)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
        p.Controls.AddRange(controls);
        return p;
    }

    public static TextBox InfoBox() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = SystemColors.Info, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f),
    };

    public static Label Lbl(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3),
        Font = bold ? new Font("Segoe UI", 9f, FontStyle.Bold) : null,
    };

    public static void StyleRisk(ListViewItem it, Risk r, int riskColumn)
    {
        it.UseItemStyleForSubItems = false;
        var back = Classifier.RiskBack(r);
        it.BackColor = back;
        foreach (ListViewItem.ListViewSubItem s in it.SubItems) s.BackColor = back;
        if (riskColumn >= 0 && riskColumn < it.SubItems.Count)
        {
            it.SubItems[riskColumn].ForeColor = Classifier.RiskColor(r);
            it.SubItems[riskColumn].Font = new Font(it.ListView?.Font ?? SystemFonts.DefaultFont, FontStyle.Bold);
        }
    }

    public static bool Confirm(IWin32Window owner, string text, string title = "Подтверждение", MessageBoxIcon icon = MessageBoxIcon.Question) =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static void Info(IWin32Window owner, string text, string title = "DiskDoctor") =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(IWin32Window owner, string text, string title = "DiskDoctor") =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

public class DoubleBufferedListView : ListView
{
    public DoubleBufferedListView() { DoubleBuffered = true; }
}

/// <summary>Полоса «на что потрачен диск» с разбиением по критичности.</summary>
public sealed class UsageBar : Control
{
    public List<(string label, long size, Color color)> Segments = new();
    public long Total;

    public UsageBar()
    {
        DoubleBuffered = true;
        Height = 64;
        Dock = DockStyle.Top;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Total <= 0 || Segments.Count == 0) return;
        var bar = new Rectangle(6, 4, Width - 12, 22);
        float x = bar.X;
        foreach (var s in Segments)
        {
            float w = (float)s.size / Total * bar.Width;
            if (w <= 0) continue;
            using var br = new SolidBrush(s.color);
            g.FillRectangle(br, x, bar.Y, w, bar.Height);
            x += w;
        }
        g.DrawRectangle(Pens.Gray, bar);
        // легенда
        float lx = 6, ly = bar.Bottom + 6;
        foreach (var s in Segments)
        {
            if (s.size <= 0) continue;
            string text = $"{s.label}: {Fmt.Size(s.size)}";
            var sz = g.MeasureString(text, Font);
            if (lx + sz.Width + 22 > Width) { lx = 6; ly += sz.Height + 2; }
            using (var br = new SolidBrush(s.color)) g.FillRectangle(br, lx, ly + 3, 12, 12);
            g.DrawRectangle(Pens.Gray, lx, ly + 3, 12, 12);
            TextRenderer.DrawText(g, text, Font, new Point((int)lx + 16, (int)ly + 1), ForeColor);
            lx += sz.Width + 30;
        }
        int needed = (int)(ly + 22);
        if (needed != Height) BeginInvoke(() => Height = needed);
    }
}
