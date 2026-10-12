using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;

namespace DiskScope;

readonly record struct LinkName(ulong Parent, string Name, byte Namespace);
struct Entry
{
    public Entry() { }
    public ulong Id;
    public bool Directory, Reparse;
    public long Logical, Allocated, Modified;
    public int Links;
    internal LinkName? FirstName;
    internal List<LinkName>? ExtraNames;
    public List<LinkName> Names
    {
        get { if (ExtraNames == null) { ExtraNames = []; if (FirstName != null) ExtraNames.Add(FirstName.Value); } return ExtraNames; }
        set { ExtraNames = value; FirstName = value.Count == 0 ? null : value[0]; }
    }
    internal void AddName(LinkName name)
    {
        if (FirstName == null) FirstName = name;
        else if (ExtraNames == null) { if (name != FirstName) ExtraNames = [FirstName.Value, name]; }
        else if (!ExtraNames.Contains(name)) ExtraNames.Add(name);
    }
    internal int CountLinks()
    {
        if (ExtraNames == null) return 1;
        int count = 0;
        for (int i = 0; i < ExtraNames.Count; i++)
        {
            var n = ExtraNames[i]; if (n.Namespace == 2) continue;
            bool duplicate = false;
            for (int j = 0; j < i; j++) if (ExtraNames[j].Namespace != 2 && ExtraNames[j].Parent == n.Parent && ExtraNames[j].Name == n.Name) { duplicate = true; break; }
            if (!duplicate) count++;
        }
        return Math.Max(1, count);
    }
}

// ParentReference is only used during construction. The same eight bytes become two int indexes.
[StructLayout(LayoutKind.Explicit, Size = 41)]
struct IndexNode
{
    [FieldOffset(0)] public ulong Id;
    [FieldOffset(8)] public ulong ParentReference;
    [FieldOffset(8)] public int Parent;
    [FieldOffset(12)] public int DirectoryIndex;
    [FieldOffset(16)] public long Logical;
    [FieldOffset(24)] public long Allocated;
    [FieldOffset(32)] public int NameOffset;
    [FieldOffset(36)] public ushort NameLength;
    [FieldOffset(38)] public ushort Links;
    [FieldOffset(40)] public byte Flags;
}
[StructLayout(LayoutKind.Sequential)]
struct DirectoryTotal { public long Logical, Allocated, Files, Directories; }
readonly record struct Run(long Vcn, long Lcn, long Clusters);
[StructLayout(LayoutKind.Sequential, Pack = 1)]
readonly record struct StoredName(ulong Parent, int Offset, ushort Length, byte Namespace);
readonly record struct AliasName(int Index, int Offset, ushort Length);
record ListedEntry(IndexedEntry Entry, string Name, bool Alias);
readonly struct IndexedEntry(Snapshot snapshot, int index)
{
    public int Index => index;
    public ulong Id => snapshot.Nodes[index].Id;
    public ulong Parent => snapshot.Nodes[index].Parent < 0 ? 0 : snapshot.Nodes[snapshot.Nodes[index].Parent].Id;
    public string Name => snapshot.Name(index);
    public bool Directory => (snapshot.Nodes[index].Flags & 1) != 0;
    public bool Reparse => (snapshot.Nodes[index].Flags & 2) != 0;
    public long Logical => snapshot.Nodes[index].Logical;
    public long Allocated => snapshot.Nodes[index].Allocated;
    public int Links => snapshot.Nodes[index].Links;
    public long TotalLogical => Directory ? snapshot.Totals[snapshot.Nodes[index].DirectoryIndex].Logical : Logical;
    public long TotalAllocated => Directory ? snapshot.Totals[snapshot.Nodes[index].DirectoryIndex].Allocated : Allocated;
    public long Files => Directory ? snapshot.Totals[snapshot.Nodes[index].DirectoryIndex].Files : 1;
    public long Directories => Directory ? snapshot.Totals[snapshot.Nodes[index].DirectoryIndex].Directories : 0;
    public int ChildCount => snapshot.ChildCount(index);
    public IEnumerable<ulong> Children => snapshot.Children(index);
    public IReadOnlyList<LinkName> Names => snapshot.LinkNames(Id);
}

sealed class SegmentBuffer<T>(int size) where T : unmanaged
{
    readonly int blockSize = size;
    readonly List<T[]> blocks = [];
    public int Count { get; private set; }
    public void Add(T value)
    {
        int offset = Count % blockSize; if (offset == 0) blocks.Add(new T[blockSize]); blocks[^1][offset] = value; Count++;
    }
    public void Append(ReadOnlySpan<T> values)
    {
        while (!values.IsEmpty)
        {
            int offset = Count % blockSize; if (offset == 0) blocks.Add(new T[blockSize]); int n = Math.Min(blockSize - offset, values.Length);
            values[..n].CopyTo(blocks[^1].AsSpan(offset)); Count += n; values = values[n..];
        }
    }
    public void CopyTo(Span<T> destination)
    {
        int pos = 0; foreach (var block in blocks) { int n = Math.Min(block.Length, Count - pos); block.AsSpan(0, n).CopyTo(destination[pos..]); pos += n; }
    }
    public void Clear() { blocks.Clear(); Count = 0; }
    public struct Enumerator(SegmentBuffer<T> buffer)
    {
        int position = -1;
        public T Current => buffer.blocks[position / buffer.blockSize][position % buffer.blockSize];
        public bool MoveNext() => ++position < buffer.Count;
    }
    public Enumerator GetEnumerator() => new(this);
}

sealed class IndexBuilder
{
    internal readonly SegmentBuffer<IndexNode> Nodes = new(8192);
    internal readonly SegmentBuffer<char> Names = new(65536);
    internal readonly Dictionary<ulong, List<LinkName>> MultipleNames = [];
    internal readonly List<(ulong Base, Entry Data)> Extensions = [];
    internal long Raw, Invalid;
    internal double ParseSeconds;
    internal void Add(Entry e)
    {
        var name = e.FirstName;
        Nodes.Add(new() { Id = e.Id, ParentReference = name?.Parent ?? 0, Logical = e.Logical, Allocated = e.Allocated,
            NameOffset = Names.Count, NameLength = checked((ushort)(name?.Name.Length ?? 0)), Links = checked((ushort)e.CountLinks()), Flags = (byte)((e.Directory ? 1 : 0) | (e.Reparse ? 2 : 0) | ((name?.Namespace ?? 0) << 4)) });
        if (name != null) Names.Append(name.Value.Name.AsSpan());
        if (e.ExtraNames != null) MultipleNames.Add(e.Id, e.ExtraNames);
    }
    internal static void Append(List<char> target, ReadOnlySpan<char> value)
    {
        int start = target.Count; target.EnsureCapacity(checked(start + value.Length)); CollectionsMarshal.SetCount(target, start + value.Length);
        value.CopyTo(CollectionsMarshal.AsSpan(target)[start..]);
    }
}

sealed class Snapshot
{
    internal IndexNode[] Nodes = [];
    internal char[] NamePool = [];
    internal int[] ChildOffsets = [], ChildIndexes = [];
    internal DirectoryTotal[] Totals = [];
    internal Dictionary<ulong, List<StoredName>> HardNames = [];
    readonly Dictionary<int, List<AliasName>> aliases = [];
    readonly Dictionary<int, List<int>> movedChildren = [];
    public string Root = "", Backend = "ntfs-mft";
    public DateTime CreatedUtc;
    public double Seconds, ReadSeconds, ParseSeconds, BuildSeconds, SaveSeconds;
    public long RawRecords, TotalRecordSlots, InvalidRecords, Unresolved;
    public bool BitmapUsed, AdjustedAfterCleanup;
    public int CleanupAdjustments;
    public ulong RootId;
    internal int RootIndex, Count;
    int[]? ordinalLookup;
    internal long WrittenTicks, WrittenLength;
    internal uint VolumeSerial;
    internal static ulong Ordinal(ulong id) => id & 0xffffffffffffUL;
    internal int IndexOf(ulong id)
    {
        ulong ordinal = Ordinal(id);
        if (ordinalLookup != null) { int i = ordinal < (ulong)ordinalLookup.Length ? ordinalLookup[(int)ordinal] : -1; return i >= 0 && Nodes[i].Id == id && (Nodes[i].Flags & 4) == 0 ? i : -1; }
        int lo = 0, hi = Nodes.Length - 1;
        while (lo <= hi) { int mid = lo + (hi - lo) / 2; ulong value = Ordinal(Nodes[mid].Id); if (value < ordinal) lo = mid + 1; else if (value > ordinal) hi = mid - 1; else return Nodes[mid].Id == id && (Nodes[mid].Flags & 4) == 0 ? mid : -1; }
        return -1;
    }
    public IndexedEntry Get(ulong id) { int i = IndexOf(id); if (i < 0) throw new KeyNotFoundException(); return new(this, i); }
    internal string Name(int index) => new(NamePool, Nodes[index].NameOffset, Nodes[index].NameLength);
    internal bool Contains(ulong id) => IndexOf(id) >= 0;
    internal int ChildCount(int index)
    {
        int count = 0; for (int p = ChildOffsets[index]; p < ChildOffsets[index + 1]; p++) if (Nodes[ChildIndexes[p]].Parent == index && (Nodes[ChildIndexes[p]].Flags & 4) == 0) count++; if (movedChildren.TryGetValue(index, out var moved)) foreach (int c in moved) if (Nodes[c].Parent == index && (Nodes[c].Flags & 4) == 0) count++; return count;
    }
    internal struct ChildEnumerator(Snapshot snapshot, int parent)
    {
        int position = snapshot.ChildOffsets[parent] - 1, moved = -1;
        public int Current { get; private set; }
        public ChildEnumerator GetEnumerator() => this;
        public bool MoveNext()
        {
            while (++position < snapshot.ChildOffsets[parent + 1])
            { int c = snapshot.ChildIndexes[position]; if (snapshot.Nodes[c].Parent == parent && (snapshot.Nodes[c].Flags & 4) == 0) { Current = c; return true; } }
            if (snapshot.movedChildren.TryGetValue(parent, out var extra))
                while (++moved < extra.Count) { int c = extra[moved]; if (snapshot.Nodes[c].Parent == parent && (snapshot.Nodes[c].Flags & 4) == 0) { Current = c; return true; } }
            return false;
        }
    }
    internal ChildEnumerator ChildNodes(int index) => new(this, index);
    internal IEnumerable<ulong> Children(int index)
    {
        for (int p = ChildOffsets[index]; p < ChildOffsets[index + 1]; p++) { int c = ChildIndexes[p]; if (Nodes[c].Parent == index && (Nodes[c].Flags & 4) == 0) yield return Nodes[c].Id; }
        if (movedChildren.TryGetValue(index, out var moved)) foreach (int c in moved) if (Nodes[c].Parent == index && (Nodes[c].Flags & 4) == 0) yield return Nodes[c].Id;
    }
    internal void Build(IReadOnlyList<IndexBuilder> builders)
    {
        var watch = Stopwatch.StartNew(); int count = 0, chars = 0;
        foreach (var b in builders) { count = checked(count + b.Nodes.Count); chars = checked(chars + b.Names.Count); }
        Nodes = new IndexNode[count]; NamePool = new char[chars]; int pos = 0, cp = 0;
        var multiple = new Dictionary<ulong, List<LinkName>>();
        foreach (var b in builders)
        {
            foreach (var node in b.Nodes) { var n = node; n.NameOffset += cp; Nodes[pos++] = n; }
            b.Names.CopyTo(NamePool.AsSpan(cp)); cp += b.Names.Count;
            foreach (var item in b.MultipleNames) multiple.Add(item.Key, item.Value);
            b.Nodes.Clear(); b.Names.Clear(); b.MultipleNames.Clear();
        }
        Array.Sort(Nodes, (a, b) => Ordinal(a.Id).CompareTo(Ordinal(b.Id)));
        if (Nodes.Length > 0 && Ordinal(Nodes[^1].Id) < 64_000_000)
        {
            ordinalLookup = new int[checked((int)Ordinal(Nodes[^1].Id) + 1)]; Array.Fill(ordinalLookup, -1);
            for (int i = 0; i < Nodes.Length; i++) ordinalLookup[(int)Ordinal(Nodes[i].Id)] = i;
        }
        RootIndex = IndexOf(RootId); if (RootIndex < 0) throw new InvalidDataException("MFT root directory missing"); Count = count;
        var appended = new List<char>();
        var extensions = new List<(ulong Base, Entry Data)>();
        foreach (var b in builders) { extensions.AddRange(b.Extensions); b.Extensions.Clear(); }
        extensions.Sort((a, b) => Backend == "ntfs-mft-ioctl" ? Ordinal(b.Data.Id).CompareTo(Ordinal(a.Data.Id)) : Ordinal(a.Data.Id).CompareTo(Ordinal(b.Data.Id)));
        foreach (var (baseId, e) in extensions)
        {
            int i = IndexOf(baseId); if (i < 0) continue;
            Nodes[i].Logical += e.Logical; Nodes[i].Allocated += e.Allocated; if (e.Reparse) Nodes[i].Flags |= 2;
            if (!multiple.TryGetValue(baseId, out var names)) multiple[baseId] = names = Nodes[i].NameLength == 0 ? [] : [new(Nodes[i].ParentReference, Name(i), (byte)(Nodes[i].Flags >> 4))];
            if (e.ExtraNames != null) names.AddRange(e.ExtraNames); else if (e.FirstName != null) names.Add(e.FirstName.Value);
        }
        // Choose exactly the previous case-insensitive name/parent ordering, with DOS fallback.
        for (int i = 0; i < Nodes.Length; i++)
        {
            ulong parent = Nodes[i].ParentReference;
            if (multiple.TryGetValue(Nodes[i].Id, out var names))
            {
                LinkName? chosen = null; bool hasLong = false; int links = 0;
                for (int j = 0; j < names.Count; j++)
                {
                    var n = names[j]; if (n.Namespace == 2) continue; hasLong = true; bool duplicate = false;
                    for (int k = 0; k < j; k++) if (names[k].Namespace != 2 && names[k].Parent == n.Parent && names[k].Name == n.Name) { duplicate = true; break; }
                    if (!duplicate) links++;
                }
                foreach (var n in names)
                {
                    if (hasLong && n.Namespace == 2) continue; int pi = IndexOf(n.Parent);
                    if (pi < 0 || (Nodes[pi].Flags & 1) == 0) continue;
                    int order = chosen == null ? -1 : StringComparer.OrdinalIgnoreCase.Compare(n.Name, chosen.Value.Name);
                    if (chosen == null || (hasLong && (order < 0 || (order == 0 && n.Parent < chosen.Value.Parent)))) chosen = n;
                }
                Nodes[i].Links = checked((ushort)Math.Max(1, links));
                if (chosen != null)
                {
                    parent = chosen.Value.Parent;
                    if (!NamePool.AsSpan(Nodes[i].NameOffset, Nodes[i].NameLength).SequenceEqual(chosen.Value.Name))
                    { Nodes[i].NameOffset = NamePool.Length + appended.Count; Nodes[i].NameLength = (ushort)chosen.Value.Name.Length; IndexBuilder.Append(appended, chosen.Value.Name); }
                }
                else parent = 0;
                if (links > 1)
                {
                    var stored = new List<StoredName>(names.Count);
                    foreach (var n in names) { stored.Add(new(n.Parent, NamePool.Length + appended.Count, (ushort)n.Name.Length, n.Namespace)); IndexBuilder.Append(appended, n.Name); }
                    HardNames.Add(Nodes[i].Id, stored);
                }
            }
            int p = IndexOf(parent); Nodes[i].Parent = i == RootIndex || p < 0 || (Nodes[p].Flags & 1) == 0 ? -1 : p;
            Nodes[i].DirectoryIndex = -1;
        }
        if (appended.Count > 0) { int old = NamePool.Length; Array.Resize(ref NamePool, old + appended.Count); CollectionsMarshal.AsSpan(appended).CopyTo(NamePool.AsSpan(old)); }
        CompactNames(); BuildChildren(); Rollup(); BuildAliases(); ordinalLookup = null; BuildSeconds = watch.Elapsed.TotalSeconds;
    }
    internal IReadOnlyList<LinkName> LinkNames(ulong id)
    {
        if (!HardNames.TryGetValue(id, out var names)) return Array.Empty<LinkName>();
        var result = new LinkName[names.Count]; for (int i = 0; i < names.Count; i++) { var n = names[i]; result[i] = new(n.Parent, new string(NamePool, n.Offset, n.Length), n.Namespace); } return result;
    }
    void CompactNames()
    {
        var old = NamePool; var pool = new char[old.Length]; int used = 0;
        long namesCount = Nodes.Length; foreach (var names in HardNames.Values) namesCount += names.Count;
        int capacity = 1; while (capacity < namesCount * 2) capacity = checked(capacity * 2);
        var offsets = new int[capacity]; Array.Fill(offsets, -1); var lengths = new ushort[capacity]; int mask = capacity - 1;
        int Store(int offset, ushort length)
        {
            var name = old.AsSpan(offset, length); uint hash = 2166136261; foreach (char c in name) hash = (hash ^ c) * 16777619;
            int slot = (int)(hash & (uint)mask);
            while (offsets[slot] >= 0) { if (lengths[slot] == length && pool.AsSpan(offsets[slot], length).SequenceEqual(name)) return offsets[slot]; slot = (slot + 1) & mask; }
            int start = used; name.CopyTo(pool.AsSpan(used)); used += length; offsets[slot] = start; lengths[slot] = length; return start;
        }
        for (int i = 0; i < Nodes.Length; i++) Nodes[i].NameOffset = Store(Nodes[i].NameOffset, Nodes[i].NameLength);
        foreach (var names in HardNames.Values) for (int i = 0; i < names.Count; i++) { var n = names[i]; names[i] = n with { Offset = Store(n.Offset, n.Length) }; }
        Array.Resize(ref pool, used); NamePool = pool;
    }
    internal void BuildChildren()
    {
        movedChildren.Clear();
        ChildOffsets = new int[Nodes.Length + 1];
        for (int i = 0; i < Nodes.Length; i++) if (i != RootIndex && Nodes[i].Parent >= 0 && (Nodes[i].Flags & 4) == 0) ChildOffsets[Nodes[i].Parent + 1]++;
        for (int i = 1; i < ChildOffsets.Length; i++) ChildOffsets[i] += ChildOffsets[i - 1];
        ChildIndexes = new int[ChildOffsets[^1]]; var cursor = (int[])ChildOffsets.Clone();
        bool descending = Backend == "ntfs-mft-ioctl";
        for (int j = 0; j < Nodes.Length; j++)
        {
            int i = descending ? Nodes.Length - 1 - j : j;
            if (i != RootIndex && Nodes[i].Parent >= 0 && (Nodes[i].Flags & 4) == 0) ChildIndexes[cursor[Nodes[i].Parent]++] = i;
        }
    }
    void Rollup()
    {
        int dirs = 0; for (int i = 0; i < Nodes.Length; i++) if ((Nodes[i].Flags & 1) != 0) Nodes[i].DirectoryIndex = dirs++;
        Totals = new DirectoryTotal[dirs];
        for (int i = 0; i < Nodes.Length; i++) if ((Nodes[i].Flags & 1) != 0) Totals[Nodes[i].DirectoryIndex] = new() { Logical = Nodes[i].Logical, Allocated = Nodes[i].Allocated };
        var seen = new bool[Nodes.Length]; var visit = new int[Nodes.Length]; int count = 0; var stack = new Stack<int>(); stack.Push(RootIndex);
        while (stack.TryPop(out int i))
        {
            if (seen[i]) continue; seen[i] = true; visit[count++] = i;
            for (int p = ChildOffsets[i]; p < ChildOffsets[i + 1]; p++) stack.Push(ChildIndexes[p]);
        }
        for (int j = count - 1; j > 0; j--)
        {
            int i = visit[j], pi = Nodes[i].Parent; ref var p = ref Totals[Nodes[pi].DirectoryIndex];
            var e = new IndexedEntry(this, i); p.Logical += e.TotalLogical; p.Allocated += e.TotalAllocated; p.Files += e.Files; p.Directories += e.Directories + (e.Directory ? 1 : 0);
        }
        Unresolved = Nodes.Length - count;
    }
    internal void BuildAliases()
    {
        aliases.Clear();
        foreach (var item in HardNames)
            foreach (var n in item.Value)
            {
                if (n.Namespace == 2) continue; int p = IndexOf(n.Parent), i = IndexOf(item.Key); if (p < 0 || i < 0) continue;
                if (!aliases.TryGetValue(p, out var list)) aliases[p] = list = [];
                bool duplicate = false;
                foreach (var a in list) if (a.Index == i && NamePool.AsSpan(a.Offset, a.Length).SequenceEqual(NamePool.AsSpan(n.Offset, n.Length))) { duplicate = true; break; }
                if (!duplicate) list.Add(new(i, n.Offset, n.Length));
            }
    }
    public string PathOf(ulong id)
    {
        int i = IndexOf(id); var names = new List<string>(); int remaining = Nodes.Length;
        while (i != RootIndex) { if (i < 0 || remaining-- == 0) return "[unresolved]"; names.Add(Name(i)); i = Nodes[i].Parent; }
        names.Reverse(); return Root + string.Join('\\', names);
    }
    public IEnumerable<ListedEntry> ListChildren(ulong id)
    {
        int i = IndexOf(id); if (i < 0) throw new KeyNotFoundException();
        foreach (ulong c in Children(i)) { var e = Get(c); yield return new(e, e.Name, false); }
        if (aliases.TryGetValue(i, out var list)) foreach (var a in list) if ((Nodes[a.Index].Flags & 4) == 0 && (Nodes[a.Index].Parent != i || !NamePool.AsSpan(Nodes[a.Index].NameOffset, Nodes[a.Index].NameLength).SequenceEqual(NamePool.AsSpan(a.Offset, a.Length)))) yield return new(new(this, a.Index), new string(NamePool, a.Offset, a.Length), true);
    }
    public ulong Resolve(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path is outside this scan");
        int i = RootIndex;
        foreach (string part in path[Root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int found = -1;
            for (int p = ChildOffsets[i]; p < ChildOffsets[i + 1]; p++)
            {
                int c = ChildIndexes[p]; if (Nodes[c].Parent == i && (Nodes[c].Flags & 4) == 0 && NamePool.AsSpan(Nodes[c].NameOffset, Nodes[c].NameLength).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = c; break; }
            }
            if (found < 0 && movedChildren.TryGetValue(i, out var moved)) foreach (int c in moved) if (Nodes[c].Parent == i && (Nodes[c].Flags & 4) == 0 && NamePool.AsSpan(Nodes[c].NameOffset, Nodes[c].NameLength).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = c; break; }
            if (found < 0 && aliases.TryGetValue(i, out var list)) foreach (var a in list) if ((Nodes[a.Index].Flags & 4) == 0 && NamePool.AsSpan(a.Offset, a.Length).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = a.Index; break; }
            if (found < 0) throw new FileNotFoundException("Path absent from snapshot; rescan if it changed", path); i = found;
        }
        return Nodes[i].Id;
    }
    // Deletion never changes the guarded executor. Only names actually reported deleted reach here.
    internal bool RemoveName(string path, ulong id)
    {
        int i = IndexOf(id); if (i < 0 || (Nodes[i].Flags & 1) != 0) return false;
        try { if (Resolve(path) != id) return false; } catch (FileNotFoundException) { return false; }
        int parent = Nodes[i].Parent; bool canonical = path.Equals(PathOf(id), StringComparison.OrdinalIgnoreCase);
        if (HardNames.TryGetValue(id, out var names))
        {
            int pi = IndexOf(Resolve(System.IO.Path.GetDirectoryName(path)!)); string name = System.IO.Path.GetFileName(path);
            names.RemoveAll(n => n.Parent == Nodes[pi].Id && (NamePool.AsSpan(n.Offset, n.Length).Equals(name, StringComparison.OrdinalIgnoreCase) || (canonical && n.Namespace == 2)));
            var remaining = names.Where(n => n.Namespace != 2).DistinctBy(n => (n.Parent, new string(NamePool, n.Offset, n.Length))).ToList();
            if (remaining.Count > 0)
            {
                Nodes[i].Links = checked((ushort)remaining.Count);
                if (canonical)
                {
                    remaining.Sort((a, b) => { int c = NamePool.AsSpan(a.Offset, a.Length).CompareTo(NamePool.AsSpan(b.Offset, b.Length), StringComparison.OrdinalIgnoreCase); return c != 0 ? c : a.Parent.CompareTo(b.Parent); });
                    var chosen = remaining.Find(n => IndexOf(n.Parent) >= 0 && (Nodes[IndexOf(n.Parent)].Flags & 1) != 0);
                    bool reachable = Reachable(i);
                    AdjustParents(parent, -Nodes[i].Logical, -Nodes[i].Allocated, -1);
                    Nodes[i].Parent = chosen.Parent == 0 ? -1 : IndexOf(chosen.Parent); Nodes[i].NameOffset = chosen.Offset; Nodes[i].NameLength = chosen.Length;
                    bool nowReachable = Reachable(i); if (reachable != nowReachable) Unresolved += nowReachable ? -1 : 1;
                    AdjustParents(Nodes[i].Parent, Nodes[i].Logical, Nodes[i].Allocated, 1);
                    int newParent = Nodes[i].Parent;
                    if (newParent >= 0 && !ChildIndexes.AsSpan(ChildOffsets[newParent], ChildOffsets[newParent + 1] - ChildOffsets[newParent]).Contains(i))
                    {
                        if (!movedChildren.TryGetValue(newParent, out var moved)) movedChildren[newParent] = moved = [];
                        if (!moved.Contains(i)) moved.Add(i);
                    }
                }
                RemoveAlias(pi, i, name); if (remaining.Count == 1) HardNames.Remove(id); AdjustedAfterCleanup = true; CleanupAdjustments++; return true;
            }
            HardNames.Remove(id);
        }
        AdjustParents(parent, -Nodes[i].Logical, -Nodes[i].Allocated, -1); Nodes[i].Flags |= 4; Count--;
        if (!Reachable(i)) Unresolved--;
        AdjustedAfterCleanup = true; CleanupAdjustments++; return true;
    }
    void RemoveAlias(int parent, int index, string name)
    {
        if (aliases.TryGetValue(parent, out var list)) list.RemoveAll(a => a.Index == index && NamePool.AsSpan(a.Offset, a.Length).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    bool Reachable(int i) { int remaining = Nodes.Length; while (i >= 0 && i != RootIndex && remaining-- > 0) i = Nodes[i].Parent; return i == RootIndex; }
    void AdjustParents(int i, long logical, long allocated, long files)
    {
        if (!Reachable(i)) return;
        int remaining = Nodes.Length;
        while (i >= 0 && remaining-- > 0) { ref var t = ref Totals[Nodes[i].DirectoryIndex]; t.Logical += logical; t.Allocated += allocated; t.Files += files; i = Nodes[i].Parent; }
    }
    internal const string Magic = "DiskScope-3";
    internal static bool FormatCurrent(string file)
    {
        try { using var fs = File.OpenRead(file); using var r = new BinaryReader(fs, Encoding.UTF8); return r.ReadString() == Magic; } catch (IOException) { return false; }
    }
    internal static void WriteArray<T>(Stream stream, T[] values) where T : unmanaged => WriteArray(stream, values.AsSpan());
    internal static void WriteArray<T>(Stream stream, Span<T> values) where T : unmanaged => stream.Write(MemoryMarshal.AsBytes(values));
    internal static T[] ReadArray<T>(Stream stream, int count) where T : unmanaged
    {
        if (count < 0 || (long)count * Unsafe.SizeOf<T>() > 2_000_000_000) throw new InvalidDataException("Invalid snapshot array length");
        var values = new T[count]; stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan())); return values;
    }
    public void Save(string file) => Save(file, 1);
    internal void Save(string file, byte codec)
    {
        var watch = Stopwatch.StartNew(); string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (movedChildren.Count > 0) BuildChildren();
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                using var header = new BinaryWriter(fs, Encoding.UTF8, true);
                header.Write(Magic); header.Write(codec); header.Write(Root); header.Write(Backend); header.Write(CreatedUtc.Ticks); header.Write(Seconds); header.Write(RawRecords); header.Write(InvalidRecords); header.Write(RootId); header.Write(Unresolved);
                header.Write(TotalRecordSlots); header.Write(BitmapUsed); header.Write(ReadSeconds); header.Write(ParseSeconds); header.Write(BuildSeconds);
                long saveOffset = fs.Position; header.Write(0d); header.Write(AdjustedAfterCleanup); header.Write(CleanupAdjustments); header.Write(VolumeSerial);
                header.Write(Count); header.Write(Nodes.Length); header.Write(NamePool.Length); header.Write(ChildIndexes.Length); header.Write(Totals.Length); header.Flush();
                Stream payload = codec switch { 0 => fs, 1 => new ZLibStream(fs, CompressionLevel.Fastest, true), 2 => new BrotliStream(fs, CompressionLevel.Fastest, true), _ => throw new InvalidDataException("Invalid snapshot codec") };
                try
                {
                    WriteArray(payload, Nodes); WriteArray(payload, NamePool); WriteArray(payload, ChildOffsets); WriteArray(payload, ChildIndexes); WriteArray(payload, Totals);
                    using var w = new BinaryWriter(payload, Encoding.UTF8, true);
                    w.Write(HardNames.Count); foreach (var item in HardNames) { w.Write(item.Key); w.Write(item.Value.Count); WriteArray(payload, CollectionsMarshal.AsSpan(item.Value)); }
                    w.Flush();
                }
                finally { if (codec != 0) payload.Dispose(); }
                fs.Flush(); SaveSeconds = watch.Elapsed.TotalSeconds; fs.Position = saveOffset; header.Write(SaveSeconds); header.Flush();
            }
            File.Move(temp, file, true); WrittenTicks = File.GetLastWriteTimeUtc(file).Ticks; WrittenLength = new FileInfo(file).Length;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static Snapshot Metadata(string file)
    {
        using var fs = File.OpenRead(file); using var r = new BinaryReader(fs, Encoding.UTF8);
        return ReadHeader(r, out _);
    }
    static Snapshot ReadHeader(BinaryReader r, out byte codec)
    {
        if (r.BaseStream.ReadByte() == 0x1f) throw new InvalidDataException("快照格式已过期，请重新 disk_scan"); r.BaseStream.Position = 0;
        if (r.ReadString() != Magic) throw new InvalidDataException("快照格式已过期，请重新 disk_scan");
        codec = r.ReadByte();
        if (codec > 2) throw new InvalidDataException("Invalid snapshot codec");
        return new Snapshot { Root = r.ReadString(), Backend = r.ReadString(), CreatedUtc = new(r.ReadInt64(), DateTimeKind.Utc), Seconds = r.ReadDouble(), RawRecords = r.ReadInt64(), InvalidRecords = r.ReadInt64(), RootId = r.ReadUInt64(), Unresolved = r.ReadInt64(),
            TotalRecordSlots = r.ReadInt64(), BitmapUsed = r.ReadBoolean(), ReadSeconds = r.ReadDouble(), ParseSeconds = r.ReadDouble(), BuildSeconds = r.ReadDouble(), SaveSeconds = r.ReadDouble(), AdjustedAfterCleanup = r.ReadBoolean(), CleanupAdjustments = r.ReadInt32(), VolumeSerial = r.ReadUInt32(), Count = r.ReadInt32() };
    }
    public static Snapshot Load(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var header = new BinaryReader(fs, Encoding.UTF8, true);
        var r = header; var s = ReadHeader(r, out byte codec);
        int count = r.ReadInt32(), chars = r.ReadInt32(), children = r.ReadInt32(), dirs = r.ReadInt32();
        using Stream source = codec switch { 0 => fs, 1 => new ZLibStream(fs, CompressionMode.Decompress, true), 2 => new BrotliStream(fs, CompressionMode.Decompress, true), _ => throw new InvalidDataException("Invalid snapshot codec") };
        using var body = new BinaryReader(source, Encoding.UTF8, true);
        s.Nodes = ReadArray<IndexNode>(source, count); s.NamePool = ReadArray<char>(source, chars); s.ChildOffsets = ReadArray<int>(source, checked(count + 1)); s.ChildIndexes = ReadArray<int>(source, children); s.Totals = ReadArray<DirectoryTotal>(source, dirs);
        int hard = body.ReadInt32(); if (hard < 0 || hard > count) throw new InvalidDataException("Invalid hard-link table");
        for (int i = 0; i < hard; i++) { ulong id = body.ReadUInt64(); int names = body.ReadInt32(); if (names < 0 || names > 65536) throw new InvalidDataException("Invalid hard-link names"); var list = new List<StoredName>(names); CollectionsMarshal.SetCount(list, names); source.ReadExactly(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(list))); s.HardNames.Add(id, list); }
        s.RootIndex = s.IndexOf(s.RootId); if (s.RootIndex < 0) throw new InvalidDataException("MFT root missing"); s.BuildAliases(); s.WrittenTicks = File.GetLastWriteTimeUtc(file).Ticks; s.WrittenLength = fs.Length; return s;
    }
}
