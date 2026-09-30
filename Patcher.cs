// INSIDE mod patcher: installs .insidepatch mods (made with InsideDev) into your own copy of INSIDE.
// Modular: every mod only replaces the few Unity objects it changes, so any number of mods can be installed on the
// same game file, and each one can be switched on or off on its own.
//   - reads every *.insidepatch in the "mods" folder next to this exe (and next to the exe itself)
//   - finds the game (next to this exe, the Steam libraries, or a folder you give it)
//   - shows what is installed by looking at the game files themselves (no state file that can go out of date)
//   - keeps one untouched copy of each original file (<file>.insidepatch-backup) and rebuilds the file from it with
//   - only installs on the exact game version the mod was made for (SHA-256), otherwise changes nothing
// No game data is shipped beyond the changed objects themselves.
// Command line:  InsidePatcher.exe [list | install <n|name|all> | uninstall <n|name|all>] [--game <folder>] [--mods <folder>]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

static class InsidePatcher
{
    sealed class ObjPatch { public long pid; public uint origLen, newLen; public byte[] origSha, newSha, data; }
    sealed class FilePatch { public string rel; public long vanillaLen; public byte[] vanillaSha; public List<ObjPatch> objs = new List<ObjPatch>(); }
    sealed class Mod
    {
        public string name, desc, path, id;
        public List<FilePatch> files = new List<FilePatch>();
        public string status, detail; public ConsoleColor color;
        public bool installed, installable;
        public List<Mod> conflicts = new List<Mod>();
    }
    sealed class GameFile
    {
        public string rel, full, bak; public bool exists, hasBak;
        public byte[] cur, curSha, baseSha; public Dictionary<long, byte[]> curObjs;
        public bool foreignChanges;
    }

    static string game, modsDir;
    static List<Mod> mods = new List<Mod>();
    static Dictionary<string, GameFile> files = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);

    static int Main(string[] args)
    {
        try { Console.Title = "INSIDE mod patcher"; } catch { }
        try { return Run(args); }
        catch (Exception e) { Console.WriteLine(); Say(ConsoleColor.Red, "ERROR: " + e.Message); Pause(); return 1; }
    }

    static int Run(string[] args)
    {
        string here = AppDomain.CurrentDomain.BaseDirectory;
        var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
            else if (args[i] == "--mods" && i + 1 < args.Length) modsDir = args[++i];
            else pos.Add(args[i]);
        }
        if (modsDir == null) modsDir = Path.Combine(here, "mods");
        LoadMods(here);

        if (game == null) game = FindGame(here);
        bool interactive = pos.Count == 0;
        while (game == null || !File.Exists(Path.Combine(game, "INSIDE.exe")))
        {
            if (!interactive) { Console.WriteLine("INSIDE not found; pass --game <folder>"); return 1; }
            if (game != null) Console.WriteLine("INSIDE.exe not found in: " + game);
            Console.Write("Drag your INSIDE folder here (or type its path) and press Enter: ");
            var l = (Console.ReadLine() ?? "").Trim().Trim(new[] { '"' });
            if (l.Length == 0) return 1;
            game = l;
        }

        if (!interactive)
        {
            Scan();
            string cmd = pos[0].ToLowerInvariant();
            if (cmd == "list") { Print(false); return 0; }
            if ((cmd == "install" || cmd == "uninstall") && pos.Count > 1)
            {
                var sel = Select(string.Join(" ", pos.Skip(1).ToArray()));
                if (sel == null) return 1;
                bool ok = cmd == "install" ? Install(sel) : Uninstall(sel);
                Scan(); Print(false);
                return ok ? 0 : 2;
            }
            Console.WriteLine("usage: InsidePatcher.exe [list | install <n|name|all> | uninstall <n|name|all>] [--game <folder>] [--mods <folder>]");
            return 1;
        }

        while (true)
        {
            Scan();
            Console.Clear();
            Print(true);
            Console.WriteLine();
            Console.WriteLine("  <number>      switch that mod on/off (several: 1 3 4)");
            Console.WriteLine("  A             install all      U  uninstall all (original game files)");
            Console.WriteLine("  D <number>    details          R  refresh (after adding mods)    Q  quit");
            Console.Write("> ");
            var line = (Console.ReadLine() ?? "q").Trim();
            if (line.Length == 0) continue;
            var up = line.ToUpperInvariant();
            Console.WriteLine();
            if (up == "Q") return 0;
            if (up == "R") { LoadMods(here); continue; }
            if (up == "A") { Install(mods.ToList()); Pause(); continue; }
            if (up == "U") { Uninstall(mods.ToList()); Pause(); continue; }
            if (up.StartsWith("D"))
            {
                var sel = Select(line.Substring(1).Trim());
                if (sel != null) foreach (var m in sel) Details(m);
                Pause(); continue;
            }
            var pick = Select(line);
            if (pick == null) { Pause(); continue; }
            var on = pick.Where(m => !m.installed).ToList();
            var off = pick.Where(m => m.installed).ToList();
            if (off.Count > 0) Uninstall(off);
            if (on.Count > 0) { Scan(); Install(on); }
            Pause();
        }
    }

    // ---------------------------------------------------------------- mods
    static void LoadMods(string here)
    {
        mods.Clear();
        var paths = new List<string>();
        if (Directory.Exists(modsDir)) paths.AddRange(Directory.GetFiles(modsDir, "*.insidepatch"));
        paths.AddRange(Directory.GetFiles(here, "*.insidepatch"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(Path.GetFileName(p))) continue;
            try { mods.Add(Load(p)); }
            catch (Exception e) { Say(ConsoleColor.Yellow, "skipped " + Path.GetFileName(p) + ": " + e.Message); }
        }
        foreach (var a in mods) a.conflicts.Clear();
        for (int i = 0; i < mods.Count; i++)
            for (int j = i + 1; j < mods.Count; j++)
                if (Clash(mods[i], mods[j])) { mods[i].conflicts.Add(mods[j]); mods[j].conflicts.Add(mods[i]); }
    }

    static bool Clash(Mod a, Mod b)
    {
        foreach (var fa in a.files)
            foreach (var fb in b.files)
            {
                if (!fa.rel.Equals(fb.rel, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Same(fa.vanillaSha, fb.vanillaSha)) return true;
                foreach (var oa in fa.objs)
                    foreach (var ob in fb.objs)
                        if (oa.pid == ob.pid && !Same(oa.newSha, ob.newSha)) return true;
            }
        return false;
    }

    static Mod Load(string path)
    {
        var m = new Mod { path = path, id = Path.GetFileNameWithoutExtension(path) };
        using (var r = new BinaryReader(File.OpenRead(path)))
        {
            string magic = Encoding.ASCII.GetString(r.ReadBytes(12));
            if (magic == "INSIDEPATCH1") throw new Exception("old single-file format; get the new version of this mod");
            if (magic != "INSIDEPATCH2") throw new Exception("not an INSIDE mod file");
            m.name = Str(r); m.desc = Str(r);
            int fc = r.ReadInt32();
            for (int i = 0; i < fc; i++)
            {
                var f = new FilePatch { rel = Str(r), vanillaLen = r.ReadInt64(), vanillaSha = r.ReadBytes(32) };
                int oc = r.ReadInt32();
                for (int k = 0; k < oc; k++)
                {
                    var o = new ObjPatch { pid = r.ReadInt64(), origLen = r.ReadUInt32(), origSha = r.ReadBytes(32), newLen = r.ReadUInt32() };
                    o.data = r.ReadBytes((int)o.newLen);
                    if (o.data.Length != o.newLen) throw new Exception("file is cut short");
                    o.newSha = ShaBytes(o.data);
                    f.objs.Add(o);
                }
                m.files.Add(f);
            }
        }
        if (m.name.Length == 0) m.name = m.id;
        return m;
    }

    static List<Mod> Select(string s)
    {
        s = s.Trim();
        if (s.Equals("all", StringComparison.OrdinalIgnoreCase)) return mods.ToList();
        var res = new List<Mod>();
        foreach (var tok in s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int n;
            Mod m = int.TryParse(tok, out n) && n >= 1 && n <= mods.Count ? mods[n - 1]
                : mods.FirstOrDefault(x => x.id.Equals(tok, StringComparison.OrdinalIgnoreCase) || x.name.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (m == null) { Console.WriteLine("no mod '" + tok + "'"); return null; }
            if (!res.Contains(m)) res.Add(m);
        }
        return res.Count > 0 ? res : null;
    }

    // ---------------------------------------------------------------- status (read from the game files)
    static void Scan()
    {
        files.Clear();
        foreach (var m in mods)
            foreach (var f in m.files)
                if (!files.ContainsKey(f.rel)) files[f.rel] = ReadGameFile(f.rel);

        foreach (var m in mods)
        {
            int on = 0, off = 0, other = 0; bool missing = false, wrongVersion = false;
            foreach (var f in m.files)
            {
                var g = files[f.rel];
                if (!g.exists || g.curObjs == null) { missing = true; continue; }
                if (!Same(g.baseSha, f.vanillaSha)) wrongVersion = true;
                foreach (var o in f.objs)
                {
                    byte[] cur;
                    if (!g.curObjs.TryGetValue(o.pid, out cur)) { other++; continue; }
                    var h = ShaBytes(cur);
                    if (Same(h, o.newSha)) on++; else if (Same(h, o.origSha)) off++; else other++;
                }
            }
            int total = on + off + other;
            m.installed = !missing && total > 0 && on == total;
            m.installable = !missing && !wrongVersion;
            m.detail = null;
            if (missing) { m.status = "game file missing"; m.color = ConsoleColor.Red; }
            else if (m.installed) { m.status = "INSTALLED"; m.color = ConsoleColor.Green; }
            else if (on > 0) { m.status = "partly installed (" + on + "/" + total + ")"; m.color = ConsoleColor.Yellow; }
            else if (wrongVersion) { m.status = "different game version"; m.color = ConsoleColor.Red; m.detail = "made for another version of the game files (or they were changed by something else)"; }
            else if (other > 0 && !m.conflicts.Any(c => c.installed)) { m.status = "not installed (part of it is changed by something else)"; m.color = ConsoleColor.Yellow; }
            else { m.status = "not installed"; m.color = ConsoleColor.Gray; }
            var cOn = m.conflicts.Where(c => c.installed).ToList();
            if (!m.installed && cOn.Count > 0)
            {
                m.status += " - conflicts with " + string.Join(", ", cOn.Select(c => "#" + (mods.IndexOf(c) + 1)).ToArray());
                if (m.color == ConsoleColor.Gray) m.color = ConsoleColor.Yellow;
            }
        }
        // does a file hold changes that none of the listed mods explain? (a mod removed from the folder, another tool)
        foreach (var g in files.Values)
        {
            g.foreignChanges = false;
            if (!g.exists || g.curObjs == null || !g.hasBak) continue;
            var on = mods.Where(m => m.installed && m.files.Any(f => f.rel.Equals(g.rel, StringComparison.OrdinalIgnoreCase))).ToList();
            try { g.foreignChanges = !Same(ShaBytes(Rebuild(File.ReadAllBytes(g.bak), g.rel, on)), g.curSha); } catch { g.foreignChanges = true; }
        }
    }

    static GameFile ReadGameFile(string rel)
    {
        var g = new GameFile { rel = rel, full = Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar)) };
        g.bak = g.full + ".insidepatch-backup";
        g.exists = File.Exists(g.full);
        g.hasBak = File.Exists(g.bak);
        if (!g.exists) return g;
        g.cur = File.ReadAllBytes(g.full);
        g.curSha = ShaBytes(g.cur);
        g.baseSha = g.hasBak ? Sha(g.bak) : g.curSha;
        try { g.curObjs = Objects(g.cur); } catch { g.curObjs = null; }
        return g;
    }

    static void Print(bool header)
    {
        Console.WriteLine("INSIDE mod patcher");
        Console.WriteLine("Game: " + game);
        Console.WriteLine("Mods: " + modsDir);
        Console.WriteLine();
        if (mods.Count == 0) { Console.WriteLine("  No mods found. Put .insidepatch files in the mods folder and press R."); return; }
        int w = Math.Min(48, Math.Max(10, mods.Max(m => m.name.Length)));
        Console.WriteLine("  #   " + "Mod".PadRight(w) + "  Status");
        for (int i = 0; i < mods.Count; i++)
        {
            var m = mods[i];
            string n = m.name.Length > w ? m.name.Substring(0, w - 1) + "~" : m.name;
            Console.Write("  " + (i + 1).ToString().PadRight(3) + " " + n.PadRight(w) + "  ");
            Say(m.color, m.status);
        }
        int nOn = mods.Count(m => m.installed);
        Console.WriteLine();
        Console.WriteLine("  " + nOn + " of " + mods.Count + " installed");
        foreach (var g in files.Values.Where(g => g.foreignChanges))
            Say(ConsoleColor.Yellow, "  note: " + g.rel + " also has changes that none of these mods make (a mod no longer in the folder?).\n" +
                "        They are dropped the next time a mod for this file is switched on or off.");
    }

    static void Details(Mod m)
    {
        Console.WriteLine("#" + (mods.IndexOf(m) + 1) + "  " + m.name);
        Console.WriteLine("file:    " + Path.GetFileName(m.path));
        Console.WriteLine("status:  " + m.status);
        if (m.detail != null) Console.WriteLine("         " + m.detail);
        Console.WriteLine(Wrap(m.desc));
        foreach (var f in m.files) Console.WriteLine("changes: " + f.rel + " (" + f.objs.Count + " objects)");
        foreach (var c in m.conflicts) Console.WriteLine("conflicts with #" + (mods.IndexOf(c) + 1) + " " + c.name + " (both change the same object)");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- install / uninstall
    static bool Install(List<Mod> want)
    {
        bool ok = true;
        var take = new List<Mod>();
        foreach (var m in want)
        {
            if (m.installed) { Console.WriteLine("already installed: " + m.name); continue; }
            if (!m.installable) { Say(ConsoleColor.Red, "can't install " + m.name + ": " + m.status); ok = false; continue; }
            var c = m.conflicts.FirstOrDefault(x => x.installed || take.Contains(x));
            if (c != null) { Say(ConsoleColor.Yellow, "skipped " + m.name + ": conflicts with " + c.name + " (uninstall that one first)"); ok = false; continue; }
            take.Add(m);
        }
        if (take.Count == 0) return ok;
        var target = mods.Where(m => m.installed).Concat(take).ToList();
        return Apply(target, take.SelectMany(m => m.files.Select(f => f.rel))) && ok;
    }

    static bool Uninstall(List<Mod> drop)
    {
        var actually = drop.Where(m => m.installed || m.status.StartsWith("partly")).ToList();
        if (actually.Count == 0) { Console.WriteLine("nothing to uninstall"); return true; }
        var target = mods.Where(m => m.installed && !actually.Contains(m)).ToList();
        bool ok = Apply(target, actually.SelectMany(m => m.files.Select(f => f.rel)));
        Scan();
        foreach (var m in actually.Where(x => x.installed))
        {
            var by = mods.Where(o => o != m && o.installed && Covers(o, m)).Select(o => "#" + (mods.IndexOf(o) + 1) + " " + o.name).ToArray();
            Say(ConsoleColor.Yellow, m.name + " stays active" + (by.Length > 0 ? ": " + string.Join(", ", by) + " includes the same changes - switch that off too" : ""));
        }
        return ok;
    }

    // does mod a make every change of mod b?
    static bool Covers(Mod a, Mod b)
    {
        foreach (var fb in b.files)
            foreach (var ob in fb.objs)
                if (!a.files.Any(fa => fa.rel.Equals(fb.rel, StringComparison.OrdinalIgnoreCase) && fa.objs.Any(oa => oa.pid == ob.pid && Same(oa.newSha, ob.newSha)))) return false;
        return true;
    }

    // rebuilds every touched game file from its original with exactly the target mods
    static bool Apply(List<Mod> target, IEnumerable<string> touched)
    {
        bool ok = true;
        foreach (var rel in touched.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var g = files[rel];
            var on = target.Where(m => m.files.Any(f => f.rel.Equals(rel, StringComparison.OrdinalIgnoreCase))).ToList();
            try
            {
                if (on.Count == 0)
                {
                    if (g.hasBak)
                    {
                        File.Copy(g.bak, g.full, true); File.Delete(g.bak);
                        Say(ConsoleColor.Green, "restored original " + rel);
                    }
                    continue;
                }
                byte[] orig = g.hasBak ? File.ReadAllBytes(g.bak) : g.cur;
                var osha = ShaBytes(orig);
                foreach (var m in on)
                    if (!Same(osha, m.files.First(f => f.rel.Equals(rel, StringComparison.OrdinalIgnoreCase)).vanillaSha))
                        throw new Exception(m.name + " was made for a different version of " + rel);
                var res = Rebuild(orig, rel, on);
                // check: every object is exactly what the mods say, the rest untouched
                var ro = Objects(res); var oo = Objects(orig);
                var want = new Dictionary<long, byte[]>();
                foreach (var m in on) foreach (var f in m.files) if (f.rel.Equals(rel, StringComparison.OrdinalIgnoreCase)) foreach (var o in f.objs) want[o.pid] = o.data;
                if (ro.Count != oo.Count) throw new Exception("object count changed");
                foreach (var kv in oo)
                {
                    byte[] exp; if (!want.TryGetValue(kv.Key, out exp)) exp = kv.Value;
                    if (!Same(ro[kv.Key], exp)) throw new Exception("check failed on object " + kv.Key);
                }
                if (!g.hasBak) { File.Copy(g.full, g.bak); g.hasBak = true; }
                string tmp = g.full + ".insidepatch-tmp";
                File.WriteAllBytes(tmp, res);
                File.Copy(tmp, g.full, true); File.Delete(tmp);
                Say(ConsoleColor.Green, "patched " + rel + " with: " + string.Join(", ", on.Select(m => m.name).ToArray()));
            }
            catch (Exception e) { Say(ConsoleColor.Red, rel + ": " + e.Message + " (file left unchanged)"); ok = false; }
        }
        return ok;
    }

    // ---------------------------------------------------------------- Unity serialized files (5.0: format 14-16)
    struct Entry { public long pid; public int tablePos; public uint start, size; }

    static void Parse(byte[] b, out uint dataOffset, out List<Entry> list)
    {
        uint ver = BE(b, 8); dataOffset = BE(b, 12);
        if (ver < 14 || ver > 16 || b[16] != 0) throw new Exception("unsupported file format " + ver);
        int p = Array.IndexOf(b, (byte)0, 20) + 1;
        p += 4;
        if (b[p] != 0) throw new Exception("files with type trees are not supported");
        p += 1;
        int n = BitConverter.ToInt32(b, p); p += 4;
        for (int i = 0; i < n; i++)
        {
            int cid = BitConverter.ToInt32(b, p); p += 4;
            if (ver >= 16) p += 3;
            if (cid < 0) p += 16;
            p += 16;
        }
        n = BitConverter.ToInt32(b, p); p += 4;
        list = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            p = (p + 3) & ~3;
            list.Add(new Entry { pid = BitConverter.ToInt64(b, p), tablePos = p + 8, start = BitConverter.ToUInt32(b, p + 8), size = BitConverter.ToUInt32(b, p + 12) });
            p += 24 + (ver >= 15 ? 1 : 0);
        }
        if (p > dataOffset) throw new Exception("bad object table");
    }

    static Dictionary<long, byte[]> Objects(byte[] b)
    {
        uint d; List<Entry> list; Parse(b, out d, out list);
        var res = new Dictionary<long, byte[]>();
        foreach (var e in list)
        {
            var x = new byte[e.size];
            Buffer.BlockCopy(b, (int)(d + e.start), x, 0, (int)e.size);
            res[e.pid] = x;
        }
        return res;
    }

    // original file + replaced objects -> new file (objects kept in order, 8-byte aligned, table and size updated)
    static byte[] Rebuild(byte[] orig, string rel, List<Mod> on)
    {
        var repl = new Dictionary<long, byte[]>();
        foreach (var m in on)
            foreach (var f in m.files)
                if (f.rel.Equals(rel, StringComparison.OrdinalIgnoreCase))
                    foreach (var o in f.objs) repl[o.pid] = o.data;
        uint d; List<Entry> list; Parse(orig, out d, out list);
        var meta = new byte[d]; Buffer.BlockCopy(orig, 0, meta, 0, (int)d);
        var data = new MemoryStream(orig.Length + 65536);
        var sorted = list.OrderBy(e => e.start).ToList();
        foreach (var e in sorted)
        {
            byte[] body; bool r = repl.TryGetValue(e.pid, out body);
            while (data.Length % 8 != 0) data.WriteByte(0);
            uint at = (uint)data.Length;
            if (r) data.Write(body, 0, body.Length); else data.Write(orig, (int)(d + e.start), (int)e.size);
            uint sz = r ? (uint)body.Length : e.size;
            Array.Copy(BitConverter.GetBytes(at), 0, meta, e.tablePos, 4);
            Array.Copy(BitConverter.GetBytes(sz), 0, meta, e.tablePos + 4, 4);
        }
        var last = sorted[sorted.Count - 1];
        long tail = d + last.start + last.size;
        data.Write(orig, (int)tail, (int)(orig.Length - tail));
        var res = new byte[meta.Length + data.Length];
        Buffer.BlockCopy(meta, 0, res, 0, meta.Length);
        Buffer.BlockCopy(data.ToArray(), 0, res, meta.Length, (int)data.Length);
        uint len = (uint)res.Length;
        res[4] = (byte)(len >> 24); res[5] = (byte)(len >> 16); res[6] = (byte)(len >> 8); res[7] = (byte)len;
        return res;
    }

    // ---------------------------------------------------------------- finding the game
    static string FindGame(string here)
    {
        for (var d = new DirectoryInfo(here); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "INSIDE.exe"))) return d.FullName;
        var libs = new List<string>();
        string steam = null;
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) if (k != null) steam = k.GetValue("SteamPath") as string; }
        catch { }
        if (steam == null) steam = @"C:\Program Files (x86)\Steam";
        libs.Add(steam);
        try
        {
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (var line in File.ReadAllLines(vdf))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("\"path\"") && !(t.Length > 3 && t[0] == '"' && char.IsDigit(t[1]))) continue;
                    var parts = t.Split('"');
                    if (parts.Length >= 4 && parts[3].Contains(":")) libs.Add(parts[3].Replace(@"\\", @"\"));
                }
        }
        catch { }
        foreach (var l in libs)
        {
            var g = Path.Combine(l, @"steamapps\common\INSIDE");
            if (File.Exists(Path.Combine(g, "INSIDE.exe"))) return g;
        }
        return null;
    }

    // ---------------------------------------------------------------- helpers
    static string Str(BinaryReader r) { int n = r.ReadInt32(); return Encoding.UTF8.GetString(r.ReadBytes(n)); }
    static uint BE(byte[] b, int p) { return (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); }
    static byte[] Sha(string path) { using (var s = SHA256.Create()) using (var f = File.OpenRead(path)) return s.ComputeHash(f); }
    static byte[] ShaBytes(byte[] b) { using (var s = SHA256.Create()) return s.ComputeHash(b); }
    static bool Same(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static void Say(ConsoleColor c, string s) { var o = Console.ForegroundColor; Console.ForegroundColor = c; Console.WriteLine(s); Console.ForegroundColor = o; }
    static void Pause() { Console.WriteLine(); Console.Write("Press Enter to continue."); Console.ReadLine(); }
    static string Wrap(string s)
    {
        var sb = new StringBuilder(); int col = 0;
        foreach (var w in s.Split(' ')) { if (col + w.Length > 100) { sb.Append('\n'); col = 0; } sb.Append(w).Append(' '); col += w.Length + 1; }
        return sb.ToString();
    }
}
