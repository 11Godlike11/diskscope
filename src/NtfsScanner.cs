using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace DiskScope;

record LinkName(ulong Parent, string Name, byte Namespace);
sealed class Entry
{
    internal static readonly List<ulong> EmptyChildren = [];
    public ulong Id, Parent;
    public string Name = "";
    public bool Directory, Reparse;
    public long Logical, Allocated, Modified;
    public int Links;
    public List<LinkName> Names = [];
    public long TotalLogical, TotalAllocated, Files, Directories;
    public List<ulong> Children = EmptyChildren;
}
record Run(long Vcn, long Lcn, long Clusters);
record ListedEntry(Entry Entry, string Name, bool Alias);

sealed class Snapshot
{
    public string Root = "", Backend = "ntfs-mft";
    public DateTime CreatedUtc;
    public double Seconds;
    public long RawRecords, InvalidRecords, Unresolved;
    public Dictionary<ulong, Entry> Entries = [];
    public ulong RootId;
    public Dictionary<ulong, Dictionary<string, ulong>> ChildMaps = [];
    Dictionary<ulong, List<(string Name, ulong Id)>> AliasByParent = [];
    void BuildAliases()
    {
        AliasByParent.Clear();
        foreach (var e in Entries.Values)
            if (e.Links > 1)
                foreach (var n in e.Names.Where(n => n.Namespace != 2))
                {
                    if (!AliasByParent.TryGetValue(n.Parent, out var list)) AliasByParent[n.Parent] = list = [];
                    list.Add((n.Name, e.Id));
                }
        ChildMaps.Clear();
    }
    public void Build()
    {
        foreach (var e in Entries.Values)
        {
            e.Children = e.Directory ? [] : Entry.EmptyChildren; e.TotalLogical = e.Logical; e.TotalAllocated = e.Allocated;
            e.Files = e.Directory ? 0 : 1; e.Directories = 0;
            e.Links = Math.Max(1, e.Names.Where(n => n.Namespace != 2).Select(n => (n.Parent, n.Name)).Distinct().Count());
        }
        Unresolved = 0;
        foreach (var e in Entries.Values)
        {
            if (e.Id == RootId) continue;
            LinkName? chosen;
            if (e.Names.Count == 1) chosen = Entries.TryGetValue(e.Names[0].Parent, out var p) && p.Directory ? e.Names[0] : null;
            else
            {
                var names = e.Names.Where(n => n.Namespace != 2).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Parent).ToList();
                if (names.Count == 0) names = e.Names;
                chosen = names.FirstOrDefault(n => Entries.TryGetValue(n.Parent, out var p) && p.Directory);
            }
            if (chosen == null) continue;
            e.Parent = chosen.Parent; e.Name = chosen.Name;
            Entries[e.Parent].Children.Add(e.Id);
        }
        // One canonical path per record: hard-linked physical storage is counted once.
        var visit = new List<ulong>(Entries.Count); var stack = new Stack<ulong>(); var seen = new HashSet<ulong>(); stack.Push(RootId);
        while (stack.TryPop(out ulong id))
        {
            if (!seen.Add(id)) { Unresolved++; continue; }
            visit.Add(id); foreach (var c in Entries[id].Children) stack.Push(c);
        }
        for (int j = visit.Count - 1; j > 0; j--)
        {
            var e = Entries[visit[j]]; var p = Entries[e.Parent];
            p.TotalLogical += e.TotalLogical; p.TotalAllocated += e.TotalAllocated;
            p.Files += e.Files; p.Directories += e.Directories + (e.Directory ? 1 : 0);
        }
        Unresolved = Entries.Count - seen.Count;
        BuildAliases();
    }
    public string PathOf(ulong id)
    {
        var names = new List<string>(); var seen = new HashSet<ulong>();
        while (id != RootId)
        {
            if (!seen.Add(id) || !Entries.TryGetValue(id, out var e)) return "[unresolved]";
            names.Add(e.Name); id = e.Parent;
        }
        names.Reverse(); return Root + string.Join('\\', names);
    }
    public IEnumerable<ListedEntry> ListChildren(ulong id)
    {
        foreach (var c in Entries[id].Children) yield return new(Entries[c], Entries[c].Name, false);
        if (AliasByParent.TryGetValue(id, out var aliases))
            foreach (var a in aliases.Distinct())
            {
                var e = Entries[a.Id];
                if (e.Parent != id || e.Name != a.Name) yield return new(e, a.Name, true);
            }
    }
    public ulong Resolve(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path is outside this scan");
        ulong id = RootId;
        foreach (string part in path[Root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ChildMaps.TryGetValue(id, out var map))
            {
                map = new(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Entries[id].Children) map.TryAdd(Entries[c].Name, c);
                // Expose other hard-link paths too, without adding their storage to directory totals.
                if (AliasByParent.TryGetValue(id, out var aliases)) foreach (var a in aliases) map.TryAdd(a.Name, a.Id);
                ChildMaps[id] = map;
            }
            if (!map.TryGetValue(part, out id)) throw new FileNotFoundException("Path absent from snapshot; rescan if it changed", path);
        }
        return id;
    }
    public void Save(string file)
    {
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var compression = new GZipStream(fs, CompressionLevel.Fastest, true))
        using (var w = new BinaryWriter(compression, Encoding.UTF8))
        {
            w.Write("DiskScope-2"); w.Write(Root); w.Write(Backend); w.Write(CreatedUtc.Ticks); w.Write(Seconds); w.Write(RawRecords); w.Write(InvalidRecords); w.Write(RootId); w.Write(Unresolved); w.Write(Entries.Count);
            foreach (var e in Entries.Values)
            {
                w.Write(e.Id); w.Write(e.Parent); w.Write(e.Name); w.Write(e.Directory); w.Write(e.Reparse); w.Write(e.Logical); w.Write(e.Allocated); w.Write(e.Modified); w.Write(e.Links);
                w.Write(e.TotalLogical); w.Write(e.TotalAllocated); w.Write(e.Files); w.Write(e.Directories);
                w.Write(e.Links > 1 ? e.Names.Count : 0);
                if (e.Links > 1) foreach (var n in e.Names) { w.Write(n.Parent); w.Write(n.Name); w.Write(n.Namespace); }
            }
            w.Flush();
        }
        File.Move(temp, file, true);
    }
    public static Snapshot Load(string file)
    {
        using var fs = File.OpenRead(file);
        int magic = fs.ReadByte(); fs.Position = 0;
        using Stream source = magic == 0x1f ? new GZipStream(fs, CompressionMode.Decompress, true) : fs;
        using var r = new BinaryReader(source, Encoding.UTF8);
        string version = r.ReadString();
        if (version is not ("DiskScope-1" or "DiskScope-2")) throw new InvalidDataException("Unknown snapshot version");
        var s = new Snapshot { Root = r.ReadString(), Backend = r.ReadString(), CreatedUtc = new(r.ReadInt64(), DateTimeKind.Utc), Seconds = r.ReadDouble(), RawRecords = r.ReadInt64(), InvalidRecords = r.ReadInt64(), RootId = r.ReadUInt64() };
        if (version == "DiskScope-2") s.Unresolved = r.ReadInt64();
        int count = r.ReadInt32();
        s.Entries = new(count);
        for (int i = 0; i < count; i++)
        {
            var e = new Entry { Id = r.ReadUInt64() };
            if (version == "DiskScope-2") { e.Parent = r.ReadUInt64(); e.Name = r.ReadString(); }
            e.Directory = r.ReadBoolean(); e.Reparse = r.ReadBoolean(); e.Logical = r.ReadInt64(); e.Allocated = r.ReadInt64(); e.Modified = r.ReadInt64(); e.Links = r.ReadInt32();
            if (version == "DiskScope-2") { e.TotalLogical = r.ReadInt64(); e.TotalAllocated = r.ReadInt64(); e.Files = r.ReadInt64(); e.Directories = r.ReadInt64(); }
            int names = r.ReadInt32(); for (int n = 0; n < names; n++) e.Names.Add(new(r.ReadUInt64(), r.ReadString(), r.ReadByte()));
            s.Entries.Add(e.Id, e);
        }
        if (version == "DiskScope-1") s.Build();
        else
        {
            foreach (var e in s.Entries.Values) if (e.Directory) e.Children = [];
            foreach (var e in s.Entries.Values) if (e.Id != s.RootId && s.Entries.TryGetValue(e.Parent, out var p) && p.Directory) p.Children.Add(e.Id);
            s.BuildAliases();
        }
        return s;
    }
}

static class NtfsScanner
{
    static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    static ulong U64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b[p..]);
    static long I64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b[p..]);
    internal static bool Fixup(Span<byte> b, bool cooked = false)
    {
        if (b.Length < 48 || U32(b, 0) != 0x454c4946) return false;
        int off = U16(b, 4), count = U16(b, 6);
        if (off < 8 || off + count * 2 > b.Length || count < 2 || (count - 1) * 512 != b.Length) return false;
        ushort seq = U16(b, off);
        for (int i = 1; i < count; i++)
        {
            int end = 512 * i - 2; // NTFS update-sequence stride is independent of the volume's sector size.
            if (U16(b, end) != seq) { if (cooked) continue; return false; }
            b[end] = b[off + 2 * i]; b[end + 1] = b[off + 2 * i + 1];
        }
        return true;
    }
    static List<(int Offset, int Length)> Attributes(ReadOnlySpan<byte> b)
    {
        var result = new List<(int, int)>(); int off = U16(b, 20); int used = Math.Min((int)U32(b, 24), b.Length);
        while (off >= 48 && off + 16 <= used && U32(b, off) != uint.MaxValue)
        {
            int len = (int)U32(b, off + 4); if (len < 24 || off + len > used) throw new InvalidDataException("Invalid MFT attribute bounds");
            result.Add((off, len)); off += len;
        }
        return result;
    }
    static List<Run> Runs(ReadOnlySpan<byte> a)
    {
        long vcn = I64(a, 16), lcn = 0; int p = U16(a, 32); var runs = new List<Run>();
        while (p < a.Length && a[p] != 0)
        {
            int nl = a[p] & 15, no = a[p++] >> 4;
            if (nl < 1 || nl > 8 || no > 8 || p + nl + no > a.Length) throw new InvalidDataException("Invalid MFT runlist");
            ulong len = 0; for (int i = 0; i < nl; i++) len |= (ulong)a[p++] << (i * 8);
            long delta = 0; for (int i = 0; i < no; i++) delta |= (long)a[p++] << (i * 8);
            if (no > 0 && no < 8 && (delta & (1L << (no * 8 - 1))) != 0) delta |= -1L << (no * 8);
            if (no > 0) lcn += delta;
            if (len == 0 || len > long.MaxValue) throw new InvalidDataException("Invalid extent length");
            runs.Add(new(vcn, no == 0 ? -1 : lcn, (long)len)); vcn += (long)len;
        }
        return runs;
    }
    internal static byte[] Record(SafeFileHandle volume, ulong id, int recordSize)
    {
        var output = Native.Ioctl(volume, 0x90068, BitConverter.GetBytes(id & 0xffffffffffffUL), recordSize + 16);
        if (output.Length < recordSize + 12 || (U64(output, 0) & 0xffffffffffffUL) != (id & 0xffffffffffffUL)) throw new InvalidDataException("MFT extension record unavailable");
        byte[] b = output[12..(12 + recordSize)];
        if (!Fixup(b, true)) throw new InvalidDataException("Invalid MFT bootstrap record");
        return b;
    }
    public static Snapshot Scan(string root, Action<long, long>? progress = null)
    {
        root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(root))!;
        if (root.Length != 3) throw new ArgumentException("NTFS scanning requires a local drive letter");
        var watch = Stopwatch.StartNew(); var s = new Snapshot { Root = root, CreatedUtc = DateTime.UtcNow };
        using var volume = Native.Open(@"\\.\" + root[..2], 0x80000000);
        byte[] v = Native.Ioctl(volume, 0x90064, null, 128);
        int sector = (int)U32(v, 40), cluster = (int)U32(v, 44), rs = (int)U32(v, 48); long valid = I64(v, 56);
        if (rs < 512 || rs > 65536 || cluster < sector || valid <= 0) throw new InvalidDataException("Unsupported NTFS geometry");
        // Some storage/filter stacks reject raw ReadFile while supporting NTFS control codes.
        try { Native.ReadAt(volume, I64(v, 64) * cluster, new byte[cluster], cluster); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1 or 5 or 50 or 87)
        {
            return ScanControlRecords(volume, s, valid, rs, cluster, watch, progress);
        }
        byte[] rec0 = Record(volume, 0, rs); var mftRuns = new List<Run>(); var attrLists = new List<byte[]>();
        void Bootstrap(byte[] b)
        {
            foreach (var (off, len) in Attributes(b))
            {
                var a = b.AsSpan(off, len); uint type = U32(a, 0);
                if (type == 0x80 && a[9] == 0 && a[8] == 1) mftRuns.AddRange(Runs(a));
                if (type != 0x20) continue;
                if (a[8] == 0) attrLists.Add(a.Slice(U16(a, 20), (int)U32(a, 16)).ToArray());
                else
                {
                    long size = I64(a, 48); if (size > 16 * 1024 * 1024) throw new InvalidDataException("MFT attribute list too large");
                    byte[] list = new byte[(int)size]; int dest = 0;
                    foreach (var r in Runs(a))
                    {
                        int n = (int)Math.Min(size - dest, r.Clusters * cluster); if (n <= 0) break;
                        if (r.Lcn < 0) throw new InvalidDataException("Sparse MFT attribute list");
                        int aligned = ((n + sector - 1) / sector) * sector; byte[] chunk = new byte[aligned];
                        Native.ReadAt(volume, r.Lcn * cluster, chunk, aligned); Array.Copy(chunk, 0, list, dest, n); dest += n;
                    }
                    if (dest != size) throw new InvalidDataException("Incomplete MFT attribute list");
                    attrLists.Add(list);
                }
            }
        }
        Bootstrap(rec0); var extraIds = new HashSet<ulong>();
        foreach (var list in attrLists.ToArray())
        {
            int p = 0;
            while (p + 26 <= list.Length)
            {
                int len = U16(list, p + 4); if (len < 26 || p + len > list.Length) break;
                if (U32(list, p) == 0x80 && list[p + 6] == 0)
                {
                    ulong id = U64(list, p + 16) & 0xffffffffffffUL;
                    if (id != 0 && extraIds.Add(id)) Bootstrap(Record(volume, id, rs));
                }
                p += len;
            }
        }
        mftRuns = mftRuns.Distinct().OrderBy(r => r.Vcn).ToList();
        long expectedVcn = 0; foreach (var r in mftRuns) { if (r.Vcn != expectedVcn || r.Lcn < 0) throw new InvalidDataException("Incomplete MFT extent map"); expectedVcn += r.Clusters; }
        if (expectedVcn * cluster < valid) throw new InvalidDataException("MFT extents do not cover valid data");
        var extensions = new List<(ulong Base, Entry Data)>(); byte[] buf = new byte[4 * 1024 * 1024]; long processed = 0;
        foreach (var r in mftRuns)
        {
            long bytes = Math.Min(r.Clusters * cluster, valid - r.Vcn * cluster);
            for (long pos = 0; pos < bytes;)
            {
                int n = (int)Math.Min(buf.Length, bytes - pos); n -= n % rs; if (n == 0) break;
                Native.ReadAt(volume, r.Lcn * cluster + pos, buf, n);
                for (int p = 0; p < n; p += rs)
                {
                    s.RawRecords++; var b = buf.AsSpan(p, rs); if (U32(b, 0) != 0x454c4946 || (U16(b, 22) & 1) == 0) continue;
                    if (!Fixup(b)) { s.InvalidRecords++; continue; }
                    ulong ordinal = (ulong)((r.Vcn * cluster + pos + p) / rs); ulong id = ordinal | ((ulong)U16(b, 16) << 48);
                    try
                    {
                        var e = Parse(b, id, cluster); ulong baseId = U64(b, 32);
                        if (baseId != 0) extensions.Add((baseId, e)); else s.Entries[id] = e;
                        if (ordinal == 5) s.RootId = id;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException) { s.InvalidRecords++; }
                }
                pos += n; processed += n; progress?.Invoke(processed / rs, valid / rs);
            }
        }
        foreach (var (baseId, e) in extensions)
            if (s.Entries.TryGetValue(baseId, out var b))
            {
                b.Names.AddRange(e.Names); b.Logical += e.Logical; b.Allocated += e.Allocated; b.Reparse |= e.Reparse;
            }
        if (!s.Entries.ContainsKey(s.RootId)) throw new InvalidDataException("MFT root directory missing");
        s.Build(); s.Seconds = watch.Elapsed.TotalSeconds; return s;
    }
    static Snapshot ScanControlRecords(SafeFileHandle volume, Snapshot s, long valid, int rs, int cluster, Stopwatch watch, Action<long,long>? progress)
    {
        s.Backend = "ntfs-mft-ioctl";
        byte[] input = new byte[8], output = new byte[rs + 16]; ulong requested = (ulong)(valid / rs - 1);
        var extensions = new List<(ulong Base, Entry Data)>();
        while (true)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input, requested);
            if (!Native.DeviceIoControl(volume, 0x90068, input, 8, output, output.Length, out int n, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), $"MFT record query failed at {requested}");
            if (n < rs + 12) throw new InvalidDataException("Short MFT record response");
            ulong ordinal = U64(output, 0) & 0xffffffffffffUL;
            if (ordinal > requested) throw new InvalidDataException("MFT enumeration did not advance");
            var b = output.AsSpan(12, rs); s.RawRecords++;
            if (!Fixup(b, true)) s.InvalidRecords++;
            else
            {
                try
                {
                    ulong id = ordinal | ((ulong)U16(b, 16) << 48); var e = Parse(b, id, cluster); ulong baseId = U64(b, 32);
                    if (baseId != 0) extensions.Add((baseId, e)); else s.Entries[id] = e;
                    if (ordinal == 5) s.RootId = id;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException) { s.InvalidRecords++; }
            }
            if (s.RawRecords % 8192 == 0 || ordinal == 0) progress?.Invoke(valid / rs - (long)ordinal, valid / rs);
            if (ordinal == 0) break; requested = ordinal - 1;
        }
        foreach (var (baseId, e) in extensions)
            if (s.Entries.TryGetValue(baseId, out var b)) { b.Names.AddRange(e.Names); b.Logical += e.Logical; b.Allocated += e.Allocated; b.Reparse |= e.Reparse; }
        if (!s.Entries.ContainsKey(s.RootId)) throw new InvalidDataException("MFT root missing");
        s.Build(); s.Seconds = watch.Elapsed.TotalSeconds; return s;
    }
    internal static Entry Parse(ReadOnlySpan<byte> b, ulong id, int cluster)
    {
        var e = new Entry { Id = id, Directory = (U16(b, 22) & 2) != 0, Links = U16(b, 18) };
        foreach (var (off, len) in Attributes(b))
        {
            var a = b.Slice(off, len); uint type = U32(a, 0); bool resident = a[8] == 0;
            if (resident)
            {
                int vo = U16(a, 20), vl = (int)U32(a, 16); if (vo + vl > len) throw new InvalidDataException("Resident bounds"); var value = a.Slice(vo, vl);
                if (type == 0x30 && vl >= 66)
                {
                    int nl = value[64]; if (66 + nl * 2 > vl) throw new InvalidDataException("Filename bounds");
                    e.Names.Add(new(U64(value, 0), Encoding.Unicode.GetString(value.Slice(66, nl * 2)), value[65]));
                }
                if (type == 0x10 && vl >= 36) { e.Modified = I64(value, 8); e.Reparse = (U32(value, 32) & 0x400) != 0; }
                if (type == 0x80) e.Logical += vl; // resident content occupies the MFT, no separate clusters
            }
            else
            {
                if (type == 0x80 && I64(a, 16) == 0) e.Logical += I64(a, 48);
                // Count real runs, including named streams and directory indexes; sparse runs cost zero.
                foreach (var r in Runs(a)) if (r.Lcn >= 0) e.Allocated = checked(e.Allocated + r.Clusters * cluster);
            }
        }
        e.Names = e.Names.Distinct().ToList();
        // Header reference count includes separate DOS short-name attributes, which aren't extra hard links.
        e.Links = Math.Max(1, e.Names.Where(n => n.Namespace != 2).Select(n => (n.Parent, n.Name)).Distinct().Count());
        return e;
    }
}
