// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2: which share mode did a process open a file with? Non-destructive probe, USE ON COPIES ONLY.
// An existing handle's share flags decide what access later openers may ask for:
//   opener asks DELETE access      -> only succeeds if the existing handle has FILE_SHARE_DELETE
//   opener asks FILE_WRITE_DATA    -> only succeeds if the existing handle has FILE_SHARE_WRITE
//   opener asks FILE_READ_DATA     -> only succeeds if the existing handle has FILE_SHARE_READ
// The probe opens with OPEN_EXISTING and all share flags, never deletes, never writes, closes immediately.
// Restart Manager is used to confirm WHICH process holds the file.
using System.Runtime.InteropServices;
using System.Text.Json;

namespace S2Tool;

public static partial class ShareProbe
{
    const uint DELETE = 0x00010000, FILE_READ_DATA = 0x1, FILE_WRITE_DATA = 0x2;
    const uint SHARE_ALL = 7, OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint sa, uint disp, uint flags, nint tmpl);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    public static string Probe(string path, uint access)
    {
        var h = CreateFile(path, access, SHARE_ALL, 0, OPEN_EXISTING, 0x80, 0);
        if (h == -1) return Marshal.GetLastWin32Error() switch { 32 => "SHARING_VIOLATION", 2 => "NOT_FOUND", 5 => "ACCESS_DENIED", var e => $"ERR{e}" };
        CloseHandle(h);
        return "ok";
    }

    // --- Restart Manager: who holds this file? ---
    [StructLayout(LayoutKind.Sequential)] struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType; public uint AppStatus; public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint h, int flags, string key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint h);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint h, uint nFiles, string[] files, uint nApps, nint apps, uint nSvc, string[]? svc);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint h, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, out uint reasons);

    public static int[] Holders(string path)
    {
        var key = new string('x', 32);
        if (RmStartSession(out var h, 0, key) != 0) return [];
        try
        {
            if (RmRegisterResources(h, 1, [path], 0, 0, 0, null) != 0) return [];
            uint need = 0, count = 0;
            RmGetList(h, out need, ref count, null, out _);
            if (need == 0) return [];
            var info = new RM_PROCESS_INFO[need]; count = need;
            if (RmGetList(h, out _, ref count, info, out _) != 0) return [];
            return info.Take((int)count).Select(i => i.Process.dwProcessId).ToArray();
        }
        finally { RmEndSession(h); }
    }


    /// <summary>Control experiment: hold a scratch file with known share modes in THIS process and check the probe classifies them correctly.</summary>
    public static int SelfTest(string[] a)
    {
        var dir = a[1];
        Directory.CreateDirectory(dir);
        var rows = new List<object>(); bool allOk = true;
        foreach (var (share, name) in new[] { (FileShare.Read, "Read"), (FileShare.ReadWrite, "ReadWrite"), (FileShare.Read | FileShare.Delete, "Read|Delete"), (FileShare.ReadWrite | FileShare.Delete, "ReadWrite|Delete") })
        {
            var path = Path.Combine(dir, "probe-control-" + name.Replace('|', '_') + ".bin");
            File.WriteAllBytes(path, new byte[16]);
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            var d = Probe(path, DELETE); var w = Probe(path, FILE_WRITE_DATA); var r = Probe(path, FILE_READ_DATA);
            bool expD = share.HasFlag(FileShare.Delete), expW = share.HasFlag(FileShare.Write);
            bool ok = (d == "ok") == expD && (w == "ok") == expW && r == "ok";
            allOk &= ok;
            rows.Add(new { holderShare = name, deleteAccess = d, writeAccess = w, readAccess = r, matchesExpectation = ok, holders = Holders(path) });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { probeSelfTestPassed = allOk, rows }, new JsonSerializerOptions { WriteIndented = true }));
        return allOk ? 0 : 1;
    }
    /// <summary>probe <folder> [--pid P] [--pattern chunk-stream*]: for every file, report holders + the three access probes.</summary>
    public static int Run(string[] a)
    {
        var folder = a[1];
        int? pid = Program.Opt(a, "--pid") is { } p ? int.Parse(p) : null;
        var rows = new List<object>();
        int held = 0, noDelete = 0, noWrite = 0, noRead = 0, total = 0;
        foreach (var f in Directory.EnumerateFiles(folder).OrderBy(x => x))
        {
            total++;
            var holders = Holders(f);
            bool byPid = pid is null ? holders.Length > 0 : holders.Contains(pid.Value);
            if (!byPid) continue;
            held++;
            var d = Probe(f, DELETE); var w = Probe(f, FILE_WRITE_DATA); var r = Probe(f, FILE_READ_DATA);
            if (d != "ok") noDelete++; if (w != "ok") noWrite++; if (r != "ok") noRead++;
            rows.Add(new { file = Path.GetFileName(f), holders, deleteAccess = d, writeAccess = w, readAccess = r });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { folder = "<copy>", filesChecked = total, heldByTarget = held, deniedDelete = noDelete, deniedWrite = noWrite, deniedRead = noRead, rows },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
