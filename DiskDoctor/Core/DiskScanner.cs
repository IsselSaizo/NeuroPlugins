using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace DiskDoctor.Core;

public sealed class DirNode
{
    public string Name;
    public DirNode Parent;
    public List<DirNode> Children;
    public long Size;        // всего, включая вложенные
    public long OwnFiles;    // размер файлов непосредственно в этой папке
    public long FileCount;   // всего файлов
    public bool Denied;      // нет доступа
    public bool IsLink;      // точка повторной обработки (junction/symlink) — не сканируется
    public RuleMatch CachedClass;

    public string FullPath => Parent == null ? Name : Path.Combine(Parent.FullPath, Name);

    public bool HasChildren => Children != null && Children.Count > 0;
}

public sealed class FileEntry
{
    public string Path;
    public long Size;
    public DateTime Modified;
}

public sealed class ExtStat
{
    public string Ext;
    public long Size;
    public long Count;
}

public sealed class DiskScanner
{
    public long FilesScanned;
    public long BytesScanned;
    public long DirsScanned;
    public long DeniedDirs;
    public volatile string CurrentPath = "";

    const int TopN = 500;
    readonly PriorityQueue<FileEntry, long> _top = new();
    readonly object _topGate = new();
    long _topMin;
    readonly ConcurrentDictionary<string, ExtStat> _ext = new(StringComparer.OrdinalIgnoreCase);
    CancellationToken _ct;

    const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    readonly struct Ent
    {
        public readonly string Name; public readonly bool Dir; public readonly long Len; public readonly FileAttributes Attr; public readonly DateTime Mod;
        public Ent(string n, bool d, long l, FileAttributes a, DateTime m) { Name = n; Dir = d; Len = l; Attr = a; Mod = m; }
    }

    static readonly EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    public DirNode Scan(string root, CancellationToken ct)
    {
        _ct = ct;
        var node = new DirNode { Name = root };
        ScanDir(node, root, 0);
        return node;
    }

    public List<FileEntry> TopFiles()
    {
        lock (_topGate) return _top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
    }

    public List<ExtStat> Extensions() => _ext.Values.OrderByDescending(e => e.Size).ToList();

    void ScanDir(DirNode node, string path, int depth)
    {
        if (_ct.IsCancellationRequested) return;
        CurrentPath = path;
        Interlocked.Increment(ref DirsScanned);
        var subdirs = new List<DirNode>();
        long own = 0, count = 0;
        try
        {
            var en = new FileSystemEnumerable<Ent>(path,
                (ref FileSystemEntry e) => new Ent(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes, e.LastWriteTimeUtc.UtcDateTime),
                Opts);
            foreach (var e in en)
            {
                if (e.Dir)
                {
                    var child = new DirNode { Name = e.Name, Parent = node };
                    if ((e.Attr & FileAttributes.ReparsePoint) != 0) child.IsLink = true;
                    subdirs.Add(child);
                }
                else
                {
                    long len = e.Len;
                    // облачные файлы-заглушки (OneDrive и т.п.) места на диске не занимают
                    if ((e.Attr & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) len = 0;
                    own += len; count++;
                    AddExt(e.Name, len);
                    if (len > _topMin || _top.Count < TopN) AddTop(path, e.Name, len, e.Mod);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
        {
            node.Denied = true;
            Interlocked.Increment(ref DeniedDirs);
        }

        Interlocked.Add(ref FilesScanned, count);
        Interlocked.Add(ref BytesScanned, own);
        node.OwnFiles = own;

        var toScan = subdirs.Where(d => !d.IsLink).ToList();
        if (depth < 3 && toScan.Count > 1)
            Parallel.ForEach(toScan, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = CancellationToken.None },
                d => ScanDir(d, Path.Combine(path, d.Name), depth + 1));
        else
            foreach (var d in toScan) ScanDir(d, Path.Combine(path, d.Name), depth + 1);

        long size = own, files = count;
        foreach (var d in subdirs) { size += d.Size; files += d.FileCount; }
        node.Size = size; node.FileCount = files;
        subdirs.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Children = subdirs.Count > 0 ? subdirs : null;
    }

    void AddExt(string name, long len)
    {
        int dot = name.LastIndexOf('.');
        string ext = dot > 0 && name.Length - dot <= 12 ? name.Substring(dot).ToLowerInvariant() : "(без расширения)";
        var st = _ext.GetOrAdd(ext, k => new ExtStat { Ext = k });
        Interlocked.Add(ref st.Size, len);
        Interlocked.Increment(ref st.Count);
    }

    void AddTop(string dir, string name, long len, DateTime mod)
    {
        lock (_topGate)
        {
            if (_top.Count < TopN) _top.Enqueue(new FileEntry { Path = Path.Combine(dir, name), Size = len, Modified = mod.ToLocalTime() }, len);
            else if (len > _topMin) { _top.DequeueEnqueue(new FileEntry { Path = Path.Combine(dir, name), Size = len, Modified = mod.ToLocalTime() }, len); }
            if (_top.Count >= TopN) _top.TryPeek(out _, out _topMin);
        }
    }

    /// <summary>Удалить узел из дерева и вычесть его размер у предков.</summary>
    public static void RemoveNode(DirNode n)
    {
        var p = n.Parent;
        if (p == null) return;
        p.Children?.Remove(n);
        for (var a = p; a != null; a = a.Parent) { a.Size -= n.Size; a.FileCount -= n.FileCount; }
    }

    public static void SubtractFile(DirNode dir, long size)
    {
        dir.OwnFiles -= size;
        for (var a = dir; a != null; a = a.Parent) { a.Size -= size; a.FileCount--; }
    }
}

public static class FileGroups
{
    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
    static FileGroups()
    {
        void Add(string g, string exts) { foreach (var e in exts.Split(' ')) Map["." + e] = g; }
        Add("Видео", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts mts m2ts vob 3gp braw r3d mxf prores");
        Add("Аудио", "mp3 wav flac aac ogg m4a wma opus aiff ape");
        Add("Изображения", "jpg jpeg png gif bmp tif tiff webp heic raw cr2 nef arw dng psd svg ico");
        Add("Архивы", "zip rar 7z tar gz bz2 xz zst cab tgz");
        Add("Образы дисков / VM", "iso img vhd vhdx vmdk vdi wim esd qcow2");
        Add("Программы / библиотеки", "exe dll sys msi msp msu ocx so");
        Add("Документы", "pdf doc docx xls xlsx ppt pptx odt ods txt rtf csv epub djvu fb2");
        Add("Модели ИИ / данные", "safetensors ckpt pt pth bin gguf onnx h5 pb npy npz parquet");
        Add("Игровые ресурсы", "pak vpk bsa ba2 forge upk uasset ucas utoc");
        Add("Базы данных / кэш", "db sqlite sqlite3 ldb edb dat idx mdf ldf");
        Add("Временные / логи / дампы", "tmp temp log etl dmp mdmp hdmp old bak chk");
    }
    public static string Of(string ext) => Map.TryGetValue(ext ?? "", out var g) ? g : "Прочее";
}
