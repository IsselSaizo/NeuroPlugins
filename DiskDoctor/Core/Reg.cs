using System.Text;
using Microsoft.Win32;

namespace DiskDoctor.Core;

/// <summary>Ссылка на раздел или значение реестра (всегда 64-битное представление).</summary>
public sealed class RegRef
{
    public RegistryHive Hive;
    public string SubKey;
    public string ValueName;            // null — весь раздел
    public bool DeleteKeyIfEmpty;       // для значений: удалить раздел, если опустел

    public RegRef(RegistryHive hive, string subKey, string valueName = null, bool deleteKeyIfEmpty = false)
    { Hive = hive; SubKey = subKey; ValueName = valueName; DeleteKeyIfEmpty = deleteKeyIfEmpty; }

    public string HiveName => Reg.HiveName(Hive);
    public string KeyPath => HiveName + "\\" + SubKey;
    public override string ToString() => ValueName == null ? KeyPath : $"{KeyPath} → «{ValueName}»";

    public bool Exists()
    {
        using var k = Reg.Open(Hive, SubKey);
        if (k == null) return false;
        return ValueName == null || k.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase);
    }
}

public static class Reg
{
    public static RegistryKey Base(RegistryHive h) => RegistryKey.OpenBaseKey(h, RegistryView.Registry64);

    public static RegistryKey Open(RegistryHive h, string sub, bool writable = false)
    {
        try { using var b = Base(h); return b.OpenSubKey(sub, writable); } catch { return null; }
    }

    public static string HiveName(RegistryHive h) => h switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        _ => h.ToString(),
    };

    public static string[] SubKeys(RegistryHive h, string sub)
    {
        using var k = Open(h, sub);
        try { return k?.GetSubKeyNames() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    public static string Str(RegistryKey k, string name)
    {
        try { return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString(); } catch { return null; }
    }

    /// <summary>SID-ы загруженных профилей пользователей (HKEY_USERS).</summary>
    public static IEnumerable<string> UserSids() =>
        SubKeys(RegistryHive.Users, "").Where(s => (s.StartsWith("S-1-5-21-") || s == "S-1-5-18") && !s.EndsWith("_Classes"));

    // ---------- GUID MSI ----------
    /// <summary>{CBEDD781-6A78-42C2-B45C-FA854C525548} → 187DDEBC87A62C244BC5AF58C4255584</summary>
    public static string PackGuid(string guid)
    {
        string g = guid.Trim('{', '}').Replace("-", "").ToUpperInvariant();
        if (g.Length != 32) return null;
        var sb = new StringBuilder(32);
        sb.Append(Rev(g.Substring(0, 8))).Append(Rev(g.Substring(8, 4))).Append(Rev(g.Substring(12, 4)));
        for (int i = 16; i < 32; i += 2) { sb.Append(g[i + 1]).Append(g[i]); }
        return sb.ToString();
    }

    public static string UnpackGuid(string packed)
    {
        if (packed == null || packed.Length != 32) return null;
        string p = packed.ToUpperInvariant();
        var sb = new StringBuilder();
        for (int i = 16; i < 32; i += 2) sb.Append(p[i + 1]).Append(p[i]);
        string tail = sb.ToString();
        return "{" + Rev(p.Substring(0, 8)) + "-" + Rev(p.Substring(8, 4)) + "-" + Rev(p.Substring(12, 4)) + "-" + tail.Substring(0, 4) + "-" + tail.Substring(4) + "}";
    }

    static string Rev(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }

    public static bool IsGuid(string s) => s != null && s.Length == 38 && s[0] == '{' && Guid.TryParse(s, out _);

    // ---------- Удаление ----------
    public static bool Delete(RegRef r, out string error)
    {
        error = null;
        try
        {
            using var b = Base(r.Hive);
            if (r.ValueName == null)
            {
                b.DeleteSubKeyTree(r.SubKey, false);
                return true;
            }
            using (var k = b.OpenSubKey(r.SubKey, true))
            {
                if (k == null) return true;
                k.DeleteValue(r.ValueName, false);
                if (r.DeleteKeyIfEmpty && k.ValueCount == 0 && k.SubKeyCount == 0)
                {
                    k.Close();
                    b.DeleteSubKey(r.SubKey, false);
                }
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

/// <summary>Пишет .reg-файл с резервной копией удаляемых разделов/значений (UTF-16, как regedit).</summary>
public sealed class RegBackup : IDisposable
{
    readonly StreamWriter _w;
    readonly HashSet<string> _keysWritten = new(StringComparer.OrdinalIgnoreCase);
    public string FilePath { get; }

    public RegBackup(string file)
    {
        FilePath = file;
        _w = new StreamWriter(file, false, new UnicodeEncoding(false, true));
        _w.Write("Windows Registry Editor Version 5.00\r\n\r\n");
    }

    public void Add(RegRef r)
    {
        try
        {
            using var b = Reg.Base(r.Hive);
            using var k = b.OpenSubKey(r.SubKey);
            if (k == null) return;
            if (r.ValueName == null) ExportKey(k, r.KeyPath);
            else
            {
                _w.Write($"[{r.KeyPath}]\r\n");
                WriteValue(k, r.ValueName);
                _w.Write("\r\n");
            }
        }
        catch (Exception ex) { _w.Write($"; не удалось сохранить {r}: {ex.Message}\r\n\r\n"); }
    }

    void ExportKey(RegistryKey k, string path)
    {
        if (!_keysWritten.Add(path)) return;
        _w.Write($"[{path}]\r\n");
        foreach (var v in k.GetValueNames()) WriteValue(k, v);
        _w.Write("\r\n");
        foreach (var s in k.GetSubKeyNames())
        {
            try { using var sk = k.OpenSubKey(s); if (sk != null) ExportKey(sk, path + "\\" + s); } catch { }
        }
    }

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "");
    static string Hex(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2")));

    void WriteValue(RegistryKey k, string name)
    {
        string n = string.IsNullOrEmpty(name) ? "@" : "\"" + Esc(name) + "\"";
        RegistryValueKind kind;
        object val;
        try { kind = k.GetValueKind(name); val = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        catch { return; }
        string data = kind switch
        {
            RegistryValueKind.String => "\"" + Esc(val?.ToString() ?? "") + "\"",
            RegistryValueKind.DWord => "dword:" + unchecked((uint)(int)val).ToString("x8"),
            RegistryValueKind.QWord => "hex(b):" + Hex(BitConverter.GetBytes(unchecked((ulong)(long)val))),
            RegistryValueKind.ExpandString => "hex(2):" + Hex(Encoding.Unicode.GetBytes((val?.ToString() ?? "") + "\0")),
            RegistryValueKind.MultiString => "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Concat(((string[])val).Select(x => x + "\0")) + "\0")),
            RegistryValueKind.Binary => "hex:" + Hex((byte[])val ?? Array.Empty<byte>()),
            _ => "hex(0):" + Hex(val as byte[] ?? Array.Empty<byte>()),
        };
        _w.Write($"{n}={data}\r\n");
    }

    public void Dispose() { _w.Flush(); _w.Dispose(); }
}
