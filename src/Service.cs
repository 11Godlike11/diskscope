using System.Diagnostics;

namespace DiskScope;

sealed class Service : IDisposable
{
    public string DataDir { get; }
    public int IdleSeconds { get; }
    readonly object cacheGate = new();
    readonly Dictionary<string, (Snapshot Snapshot, DateTime Accessed)> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer idleTimer;
    public Service(string dataDir, int idleSeconds = 120)
    {
        if (idleSeconds < 1 || idleSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(idleSeconds), "Idle timeout must be 1..86400 seconds");
        DataDir = System.IO.Path.GetFullPath(dataDir); IdleSeconds = idleSeconds;
        idleTimer = new Timer(_ => TrimIdle(DateTime.UtcNow), null, TimeSpan.FromSeconds(Math.Min(15, idleSeconds)), TimeSpan.FromSeconds(Math.Min(15, idleSeconds)));
    }
    public void Dispose() { idleTimer.Dispose(); Release(); }
    public int TrimIdle(DateTime utc)
    {
        int removed;
        lock (cacheGate)
        {
            var expired = loaded.Where(x => (utc - x.Value.Accessed).TotalSeconds >= IdleSeconds).Select(x => x.Key).ToArray();
            foreach (var root in expired) loaded.Remove(root);
            removed = expired.Length;
        }
        if (removed > 0) CollectReleased();
        return removed;
    }
    static void CollectReleased()
    {
        // Release our managed indexes only. Do not flush the host's system cache or other applications.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
    public object Release(string? path = null)
    {
        string? root = path == null ? null : System.IO.Path.GetPathRoot(Normalize(path));
        int removed;
        lock (cacheGate)
        {
            removed = root == null ? loaded.Count : loaded.ContainsKey(root) ? 1 : 0;
            if (root == null) loaded.Clear(); else loaded.Remove(root);
        }
        if (removed > 0) CollectReleased();
        return new { released_snapshots = removed, idle_seconds = IdleSeconds, snapshots_on_disk_preserved = true, note = "Releases this process's managed index cache. OS filesystem cache is managed by Windows; other processes are untouched." };
    }
    internal int LoadedCount { get { lock (cacheGate) return loaded.Count; } }
    internal string AdjustAfterCleanup(IEnumerable<PlannedFile> files)
    {
        int adjusted = 0, skipped = 0; var changed = new HashSet<Snapshot>();
        var removed = new Dictionary<(uint Serial, ulong Id), uint>();
        lock (cacheGate)
        {
            try
            {
                foreach (var f in files)
                {
                    var identity = (f.Serial, f.Id); uint before = removed.GetValueOrDefault(identity); removed[identity] = before + 1;
                    string root = System.IO.Path.GetPathRoot(f.Path)!;
                    if (!loaded.TryGetValue(root, out var cached)) { skipped++; continue; }
                    var s = cached.Snapshot; var disk = new FileInfo(SnapshotFile(root));
                    if (!disk.Exists || disk.LastWriteTimeUtc.Ticks != s.WrittenTicks || disk.Length != s.WrittenLength || s.VolumeSerial != f.Serial) { skipped++; continue; }
                    lock (s)
                    {
                        if (s.Contains(f.Id) && s.Get(f.Id).Links == f.Links - before && s.RemoveName(f.Path, f.Id)) { adjusted++; changed.Add(s); } else skipped++;
                    }
                }
                foreach (var s in changed) lock (s) s.Save(SnapshotFile(s.Root));
            }
            catch (Exception ex)
            {
                // Deletions already happened and are durably audited. A failed adjustment must not be replayed.
                loaded.Clear(); return $"Index adjustment failed: {ex.Message}; rescan the affected volume. Empty directories remain.";
            }
        }
        return $"Adjusted {adjusted} deleted names in matching loaded snapshots; {skipped} names require a rescan (index not loaded, stale, or name absent). disk_volumes reports current free space. Empty directories remain.";
    }
    void Remember(Snapshot snapshot)
    {
        lock (cacheGate) { loaded.Clear(); loaded[snapshot.Root] = (snapshot, DateTime.UtcNow); }
    }
    public static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\")) path = path[4..];
        if (path.Length == 1 && char.IsLetter(path[0])) path += @":\";
        else if (path.Length == 2 && path[1] == ':') path += "\\";
        path = System.IO.Path.GetFullPath(path);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':'))
            throw new ArgumentException("A local drive-letter path is required; UNC paths and alternate-stream selections are unsupported");
        return path;
    }
    public static string Human(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double n = bytes; int i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
    string SnapshotFile(string root) => System.IO.Path.Combine(DataDir, root[0] + ".dscope");
    public Snapshot Get(string path)
    {
        string root = System.IO.Path.GetPathRoot(Normalize(path))!;
        lock (cacheGate)
        {
            if (loaded.TryGetValue(root, out var cached)) { loaded[root] = (cached.Snapshot, DateTime.UtcNow); return cached.Snapshot; }
            string file = SnapshotFile(root);
            if (!File.Exists(file)) throw new InvalidOperationException($"No snapshot for {root}; call disk_scan first");
            bool evicted = loaded.Count > 0; loaded.Clear();
            if (evicted) CollectReleased();
            var s = Snapshot.Load(file); loaded[root] = (s, DateTime.UtcNow); return s;
        }
    }
    public object Volumes() => DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new
    {
        path = d.Name, filesystem = d.DriveFormat, label = d.VolumeLabel,
        total_bytes = d.TotalSize, free_bytes = d.AvailableFreeSpace, free = Human(d.AvailableFreeSpace),
        snapshot_exists = File.Exists(SnapshotFile(d.Name)),
        snapshot_written_utc = File.Exists(SnapshotFile(d.Name)) ? (DateTime?)File.GetLastWriteTimeUtc(SnapshotFile(d.Name)) : null,
        snapshot_format_current = File.Exists(SnapshotFile(d.Name)) && Snapshot.FormatCurrent(SnapshotFile(d.Name)),
        scan = VolumeInfo(d.Name),
        fast_scan_available = d.DriveFormat == "NTFS"
    }).ToArray();
    object? VolumeInfo(string root)
    {
        string file = SnapshotFile(root);
        try { return File.Exists(file) && Snapshot.FormatCurrent(file) ? Info(Snapshot.Metadata(file)) : null; }
        catch (IOException) { return null; }
    }
    public object Scan(string path, Action<long,long>? progress = null, bool verbose = false, bool both = false)
    {
        Release();
        var s = NtfsScanner.Scan(Normalize(path), progress); Directory.CreateDirectory(DataDir);
        s.Save(SnapshotFile(s.Root)); Remember(s);
        var info = Info(s); var result = new { scan = info, overview = Browse(s.Root, 20, 0, "allocated", verbose) };
        return both ? new JobResult(result, new { scan = info, overview = Browse(s.Root, 20, 0, "allocated", true) }) : result;
    }
    object Info(Snapshot s) => new
    {
        root = s.Root, backend = s.Backend, scanned_utc = s.CreatedUtc, scan_seconds = Math.Round(s.Seconds, 3),
        indexed_records = s.Count, raw_records = s.RawRecords, invalid_records = s.InvalidRecords, unresolved_records = s.Unresolved,
        total_record_slots = s.TotalRecordSlots, bitmap_used = s.BitmapUsed, raw_records_meaning = "Record slots actually read/enumerated, including empty slots in merged read segments; total_record_slots is the full valid MFT size.",
        timings = new { read = Math.Round(s.ReadSeconds, 3), parse = Math.Round(s.ParseSeconds, 3), build = Math.Round(s.BuildSeconds, 3), save = Math.Round(s.SaveSeconds, 3) },
        adjusted_after_cleanup = s.AdjustedAfterCleanup, cleanup_adjustments = s.CleanupAdjustments,
        live_snapshot = false, accounting = "Physical extents counted once per MFT record; hard links have one canonical owner. Includes ADS and nonresident metadata. Live volume can change while scanning."
    };
    object FullRow(Snapshot s, IndexedEntry e, string? displayedPath = null, bool alias = false) => new
    {
        id = e.Id.ToString("x16"), name = e.Id == s.RootId ? s.Root : displayedPath == null ? e.Name : System.IO.Path.GetFileName(displayedPath.TrimEnd('\\')), path = displayedPath ?? s.PathOf(e.Id),
        kind = e.Directory ? "directory" : "file", allocated_bytes = alias ? 0 : e.TotalAllocated, allocated = Human(alias ? 0 : e.TotalAllocated),
        logical_bytes = e.TotalLogical, logical = Human(e.TotalLogical), files = e.Files, directories = e.Directories,
        child_count = e.ChildCount, expandable = e.Directory && e.ChildCount > 0, reparse_point = e.Reparse, hard_links = e.Links,
        hard_link_alias = alias, canonical_path = alias ? s.PathOf(e.Id) : null, physical_bytes = alias ? (long?)e.TotalAllocated : null,
        cleanup_hint = Hint(displayedPath ?? s.PathOf(e.Id), e)
    };
    internal static double Share(long bytes, long total) => total <= 0 ? 0 : Math.Round(100d * bytes / total, 1);
    object Stamp(Snapshot s) => new { root = s.Root, scanned_utc = s.CreatedUtc, age_minutes = Math.Round(Math.Max(0, (DateTime.UtcNow - s.CreatedUtc).TotalMinutes), 1) };
    Dictionary<string, object?> Response(Snapshot s, bool verbose) => new() { [verbose ? "scan" : "snapshot"] = verbose ? Info(s) : Stamp(s) };
    internal object Row(Snapshot s, IndexedEntry e, string? displayedPath = null, bool alias = false, bool verbose = false)
    {
        if (verbose) return FullRow(s, e, displayedPath, alias);
        string path = displayedPath ?? s.PathOf(e.Id); long allocated = alias ? 0 : e.TotalAllocated;
        int parent = s.Nodes[e.Index].Parent;
        long parentBytes = parent < 0 ? e.TotalAllocated : new IndexedEntry(s, parent).TotalAllocated;
        var row = new Dictionary<string, object?> {
            ["name"] = e.Id == s.RootId ? s.Root : displayedPath == null ? e.Name : System.IO.Path.GetFileName(path.TrimEnd('\\')),
            ["path"] = path, ["kind"] = e.Directory ? "directory" : "file",
            ["allocated_bytes"] = allocated, ["allocated"] = Human(allocated), ["share"] = Share(allocated, parentBytes)
        };
        if (e.Directory) { row["files"] = e.Files; row["expandable"] = e.ChildCount > 0; }
        long difference = Math.Abs(e.TotalLogical - e.TotalAllocated);
        if (difference > 1024 * 1024 && difference > e.TotalAllocated * .1) row["logical_bytes"] = e.TotalLogical;
        if (e.Links > 1) row["hard_links"] = e.Links;
        if (e.Reparse) row["reparse_point"] = true;
        if (alias) { row["hard_link_alias"] = true; row["canonical_path"] = s.PathOf(e.Id); row["physical_bytes"] = e.TotalAllocated; }
        string hint = Hint(path, e); if (hint != "review contents and ownership") row["cleanup_hint"] = hint;
        return row;
    }
    static string Hint(string path, IndexedEntry e)
    {
        if (e.Reparse) return "reparse-point: do not follow for cleanup";
        if (e.Links > 1 && !e.Directory) return "hard-linked: removing one name may free no space";
        string p = path.ToLowerInvariant();
        if (p.Contains("\\windows\\") || p.EndsWith("\\windows") || p.Contains("\\program files")) return "system/application: use servicing or uninstall";
        if (p.Contains("\\cache\\") || p.Contains("\\caches\\") || p.Contains("\\temp\\") || p.EndsWith("\\temp")) return "possible-cache: verify owner and active use";
        return "review contents and ownership";
    }
    static IEnumerable<IndexedEntry> Sort(IEnumerable<IndexedEntry> entries, string sort) => sort switch
    {
        "allocated" => entries.OrderByDescending(e => e.TotalAllocated).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id),
        "logical" => entries.OrderByDescending(e => e.TotalLogical).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id),
        "name" => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id),
        _ => throw new ArgumentException("sort must be allocated, logical or name")
    };
    public object Browse(string path, int limit, int offset, string sort, bool verbose = false)
    {
        limit = Math.Clamp(limit, 1, 100); if (offset < 0) throw new ArgumentException("offset cannot be negative");
        path = Normalize(path); var s = Get(path); lock (s)
        {
            var e = s.Get(s.Resolve(path));
            if (!e.Directory) throw new ArgumentException("Use disk_detail for a file");
            var listed = s.ListChildren(e.Id);
            var sorted = (sort switch
            {
                "allocated" => listed.OrderByDescending(x => x.Alias ? 0 : x.Entry.TotalAllocated).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                "logical" => listed.OrderByDescending(x => x.Entry.TotalLogical).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                "name" => listed.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                _ => throw new ArgumentException("sort must be allocated, logical or name")
            }).ThenBy(x => x.Entry.Id).ToArray();
            var page = sorted.Skip(offset).Take(limit).ToArray(); int next = offset + page.Length;
            var response = Response(s, verbose);
            response["directory"] = Row(s, e, path, verbose: verbose); response["sort"] = sort; response["offset"] = offset; response["limit"] = limit;
            response["total_children"] = sorted.Length;
            response["items"] = page.Select(c => Row(s, c.Entry, System.IO.Path.Combine(path, c.Name), c.Alias, verbose)).ToArray();
            response["has_more"] = next < sorted.Length; if (next < sorted.Length) response["next_offset"] = next;
            response["omitted_children"] = Math.Max(0, sorted.Length - next);
            response["omitted_allocated_bytes"] = sorted.Skip(next).Sum(c => c.Alias ? 0 : c.Entry.TotalAllocated);
            if (verbose) response["next_action"] = "Expand an item.path with disk_browse; request further pages using next_offset. Do not infer deletability from size.";
            else if (next < sorted.Length) response["next_action"] = $"Continue with offset {next}.";
            return response;
        }
    }
    public object Detail(string path, bool verbose = false)
    {
        path = Normalize(path); var s = Get(path); lock (s)
        {
            var e = s.Get(s.Resolve(path));
            object live;
            try
            {
                using var h = Native.Open(Native.LongPath(path)); var i = Native.Info(h);
                live = new { exists = true, file_id = i.Identity.ToString("x16"), logical_bytes = i.Size, allocated_bytes = Native.Allocation(h), hard_links = i.Links, last_write_utc = DateTime.FromFileTimeUtc(i.Write), reparse_point = (i.Attributes & 0x400) != 0, matches_snapshot_identity = i.Identity == e.Id };
            }
            catch (Exception ex) { live = new { exists_or_accessible = false, error = ex.Message }; }
            var response = Response(s, verbose);
            bool alias = !path.TrimEnd('\\').Equals(s.PathOf(e.Id).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            response["item"] = Row(s, e, path, !verbose && alias, verbose); response["live"] = live;
            response["hard_link_paths"] = e.Names.Count == 0 ? new[] { s.PathOf(e.Id) } : e.Names.Where(n => n.Namespace != 2).Select(n => s.Contains(n.Parent) ? System.IO.Path.Combine(s.PathOf(n.Parent), n.Name) : "[unresolved] " + n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            if (verbose) response["note"] = "Canonical directory totals count each physical record once. Aliases are available in detail/path lookup. Directory junction targets are not traversed. No file contents are read.";
            return response;
        }
    }
    public object Types(string path, int limit = 15, bool verbose = false)
    {
        limit = Math.Clamp(limit, 1, 50); path = Normalize(path); var s = Get(path); lock (s)
        {
            var root = s.Get(s.Resolve(path)); long bytes = root.TotalAllocated;
            var totals = FileTypes.Aggregate(s, root.Index);
            var categories = new Dictionary<string, TypeTotal>();
            var extensions = totals.Select(x => (Extension: x.Key.ToString(), Total: x.Value)).ToArray();
            foreach (var x in extensions)
            {
                string category = FileTypes.Category(x.Extension);
                if (!categories.TryGetValue(category, out var total)) categories.Add(category, total = new());
                total.Allocated += x.Total.Allocated; total.Files += x.Total.Files;
            }
            var response = Response(s, verbose); response["scope"] = path; response["allocated_bytes"] = bytes;
            response["files"] = extensions.Sum(x => x.Total.Files); response["total_extensions"] = totals.Count;
            response["extensions"] = extensions.OrderByDescending(x => x.Total.Allocated).ThenBy(x => x.Extension, StringComparer.Ordinal).Take(limit).Select(x => new {
                extension = x.Extension, allocated_bytes = x.Total.Allocated, allocated = Human(x.Total.Allocated), files = x.Total.Files,
                share = Share(x.Total.Allocated, bytes), example_path = s.PathOf(s.Nodes[x.Total.Largest].Id)
            }).ToArray();
            response["categories"] = categories.OrderByDescending(x => x.Value.Allocated).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => new {
                category = x.Key, allocated_bytes = x.Value.Allocated, allocated = Human(x.Value.Allocated), files = x.Value.Files, share = Share(x.Value.Allocated, bytes)
            }).ToArray();
            return response;
        }
    }
    public object Largest(string path, int limit, long minBytes, string? name, string? extension, bool verbose = false)
    {
        limit = Math.Clamp(limit, 1, 100); path = Normalize(path); var s = Get(path); lock (s)
        {
            ulong id = s.Resolve(path);
            var stack = new Stack<int>(); stack.Push(s.IndexOf(id)); var results = new PriorityQueue<IndexedEntry, long>(); long matches = 0;
            while (stack.TryPop(out var current))
            {
                var e = new IndexedEntry(s, current);
                if (e.Directory) { foreach (int c in s.ChildNodes(current)) stack.Push(c); continue; }
                if (e.Allocated < minBytes || (name != null && !e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) || (extension != null && !System.IO.Path.GetExtension(e.Name).Equals(extension.StartsWith('.') ? extension : "." + extension, StringComparison.OrdinalIgnoreCase))) continue;
                matches++; results.Enqueue(e, e.Allocated); if (results.Count > limit) results.Dequeue();
            }
            var response = Response(s, verbose); response["scope"] = path; response["matching_files"] = matches; response["returned"] = results.Count;
            response["items"] = results.UnorderedItems.Select(x => x.Element).OrderByDescending(e => e.Allocated).Select(e => Row(s, e, verbose: verbose)).ToArray();
            return response;
        }
    }
}
