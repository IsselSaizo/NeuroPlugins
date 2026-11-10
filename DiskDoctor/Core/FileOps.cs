namespace DiskDoctor.Core;

public sealed class DeleteResult
{
    public long Freed;
    public long FilesDeleted;
    public long Failed;
    public long ScheduledOnReboot;
    public List<string> Errors = new();

    public void Merge(DeleteResult o)
    {
        Freed += o.Freed; FilesDeleted += o.FilesDeleted; Failed += o.Failed; ScheduledOnReboot += o.ScheduledOnReboot;
        if (Errors.Count < 200) Errors.AddRange(o.Errors.Take(200 - Errors.Count));
    }
}

public static class FileOps
{
    /// <summary>Размер папки (рекурсивно, без перехода по ссылкам).</summary>
    public static (long size, long files) Measure(string path, int minAgeHours = 0, CancellationToken ct = default)
    {
        long size = 0, files = 0;
        DateTime limit = DateTime.UtcNow.AddHours(-minAgeHours);
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (minAgeHours == 0 || fi.LastWriteTimeUtc < limit) return (fi.Length, 1);
                return (0, 0);
            }
            if (!Directory.Exists(path)) return (0, 0);
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                string d = stack.Pop();
                IEnumerable<FileSystemInfo> items;
                try { items = new DirectoryInfo(d).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }); }
                catch { continue; }
                try
                {
                    foreach (var it in items)
                    {
                        if (it is DirectoryInfo di)
                        {
                            if ((di.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(di.FullName);
                        }
                        else if (it is FileInfo f)
                        {
                            if (minAgeHours > 0 && f.LastWriteTimeUtc >= limit) continue;
                            size += f.Length; files++;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return (size, files);
    }

    /// <summary>
    /// Удалить содержимое папки (или саму папку, если deleteSelf). Занятые файлы пропускаются
    /// (или планируются к удалению при перезагрузке, если scheduleLocked).
    /// </summary>
    public static DeleteResult DeleteTree(string path, bool deleteSelf, int minAgeHours = 0, bool scheduleLocked = false, CancellationToken ct = default)
    {
        var r = new DeleteResult();
        DateTime limit = DateTime.UtcNow.AddHours(-minAgeHours);
        if (File.Exists(path)) { DeleteFile(new FileInfo(path), r, minAgeHours, limit, scheduleLocked); return r; }
        if (!Directory.Exists(path)) return r;
        DeleteDirContents(new DirectoryInfo(path), r, minAgeHours, limit, scheduleLocked, ct);
        if (deleteSelf) TryRemoveDir(path, r, scheduleLocked);
        return r;
    }

    static void DeleteDirContents(DirectoryInfo dir, DeleteResult r, int minAge, DateTime limit, bool sched, CancellationToken ct)
    {
        List<FileSystemInfo> items;
        try { items = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
        catch (Exception ex) { r.Failed++; AddErr(r, dir.FullName + ": " + ex.Message); return; }
        foreach (var it in items)
        {
            if (ct.IsCancellationRequested) return;
            if (it is DirectoryInfo sub)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // ссылку удаляем саму по себе, не заходя внутрь
                    try { sub.Delete(false); } catch (Exception ex) { r.Failed++; AddErr(r, sub.FullName + ": " + ex.Message); }
                    continue;
                }
                DeleteDirContents(sub, r, minAge, limit, sched, ct);
                TryRemoveDir(sub.FullName, r, sched);
            }
            else if (it is FileInfo f) DeleteFile(f, r, minAge, limit, sched);
        }
    }

    static void TryRemoveDir(string path, DeleteResult r, bool sched)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return;
            if (di.EnumerateFileSystemInfos().Any()) return; // что-то осталось (занято/свежее)
            if ((di.Attributes & FileAttributes.ReadOnly) != 0) di.Attributes &= ~FileAttributes.ReadOnly;
            di.Delete(false);
        }
        catch
        {
            if (sched && Native.MoveFileEx(path, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) r.ScheduledOnReboot++;
        }
    }

    static void DeleteFile(FileInfo f, DeleteResult r, int minAge, DateTime limit, bool sched)
    {
        long len = 0;
        try
        {
            if (minAge > 0 && f.LastWriteTimeUtc >= limit) return;
            len = f.Length;
            if ((f.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                f.Attributes = FileAttributes.Normal;
            f.Delete();
            r.Freed += len; r.FilesDeleted++;
        }
        catch (Exception ex)
        {
            if (sched && Native.MoveFileEx(f.FullName, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) { r.ScheduledOnReboot++; return; }
            r.Failed++;
            AddErr(r, f.FullName + ": " + ex.Message);
        }
    }

    static void AddErr(DeleteResult r, string e) { if (r.Errors.Count < 200) r.Errors.Add(e); }

    /// <summary>Стать владельцем и выдать полный доступ Администраторам (для папок с защитой TrustedInstaller).</summary>
    public static void TakeOwnership(string path)
    {
        Log.Write($"Получение прав на {path}…");
        Sys.Run("takeown.exe", $"/F \"{path}\" /A /R", 1_800_000);
        Sys.Run("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /T /C /Q", 1_800_000);
    }

    /// <summary>Удаление «как пользователь»: в Корзину либо навсегда, с повторной попыткой после получения прав.</summary>
    public static DeleteResult DeleteItem(string path, bool toRecycleBin, bool forceOwnership, bool scheduleLocked)
    {
        var r = new DeleteResult();
        var (size, files) = Measure(path);
        if (toRecycleBin)
        {
            if (Native.SendToRecycleBin(path)) { r.Freed = size; r.FilesDeleted = files; return r; }
            Log.Write($"В Корзину переместить не удалось ({path}) — удаляю напрямую.");
        }
        bool isDir = Directory.Exists(path);
        r = DeleteTree(path, true, 0, false);
        if ((Directory.Exists(path) || File.Exists(path)) && forceOwnership && Sys.IsAdmin)
        {
            TakeOwnership(path);
            var r2 = DeleteTree(path, true, 0, scheduleLocked);
            r2.Freed += r.Freed; r2.FilesDeleted += r.FilesDeleted;
            r = r2;
        }
        else if (scheduleLocked && (Directory.Exists(path) || File.Exists(path)))
        {
            var r2 = DeleteTree(path, true, 0, true);
            r.ScheduledOnReboot += r2.ScheduledOnReboot; r.Freed += r2.Freed; r.FilesDeleted += r2.FilesDeleted;
            r.Failed = r2.Failed;
        }
        _ = isDir;
        return r;
    }
}
