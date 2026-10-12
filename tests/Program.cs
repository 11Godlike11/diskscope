using DiskScope;
using System.Text.Json;
using System.Diagnostics;
using System.Buffers.Binary;
using System.ComponentModel;

static class Tests
{
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
    static T Field<T>(object obj, string name) => JsonSerializer.SerializeToElement(obj, Program.JsonOptions).GetProperty(name).Deserialize<T>(Program.JsonOptions)!;
    static void Throws<T>(Action action, string message, string name, int? errorCode = null) where T : Exception
    {
        try { action(); }
        catch (T ex)
        {
            Check(ex.GetType() == typeof(T) && ex.Message.Contains(message, StringComparison.Ordinal) &&
                (errorCode == null || (ex is Win32Exception win32 ? win32.NativeErrorCode : ex.HResult & 0xffff) == errorCode), name);
            return;
        }
        throw new Exception("Expected failure: " + name);
    }
    static byte[] FileRecord(int size)
    {
        byte[] b = new byte[size]; const int off = 48; const ushort seq = 0xa55a;
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x454c4946);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), off);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(size / 512 + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), seq);
        for (int i = 1; i <= size / 512; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off + i * 2), (ushort)(0x1200 + i));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 512 - 2), seq);
        }
        return b;
    }
    static void TestFixup()
    {
        foreach (int size in new[] { 1024, 4096 })
        {
            byte[] b = FileRecord(size);
            Check(NtfsScanner.Fixup(b) && Enumerable.Range(1, size / 512).All(i => BitConverter.ToUInt16(b, i * 512 - 2) == 0x1200 + i), $"{size}-byte FILE record fixes every 512-byte boundary");
            b = FileRecord(size); b[size - 2] ^= 1;
            Check(!NtfsScanner.Fixup(b), $"{size}-byte FILE record rejects a mismatched sequence number");
            b = FileRecord(size); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(size / 512));
            Check(!NtfsScanner.Fixup(b), $"{size}-byte FILE record rejects an incomplete USA");
        }
    }
    static void TestBitmap()
    {
        byte[] bits = new byte[32]; bits[0] = 0x21;
        bits[1] |= 1; bits[10] |= 1; bits[31] |= 0x80;
        var segments = NtfsScanner.BitmapSegments(bits, 256, 1024);
        Check(segments.Count == 3 && segments[0] == new NtfsScanner.ReadSegment(0, 16 * 1024) && segments[1] == new NtfsScanner.ReadSegment(80 * 1024, 8 * 1024) && segments[2] == new NtfsScanner.ReadSegment(248 * 1024, 8 * 1024), "Bitmap merges adjacent blocks and small holes, skips large empty ranges");
        bits = new byte[2]; bits[0] = 0x21; bits[1] = 0x80;
        Check(NtfsScanner.BitmapSegments(bits, 16, 1024).Single().Length == 16 * 1024, "Bitmap includes a final partial read block");
        Throws<InvalidDataException>(() => NtfsScanner.BitmapSegments(new byte[1], 16, 1024), "Incomplete", "Truncated bitmap triggers full-read fallback");
        Throws<InvalidDataException>(() => NtfsScanner.BitmapSegments(new byte[2], 16, 1024), "root missing", "Empty or stale bitmap triggers full-read fallback");
    }
    static Snapshot Synthetic()
    {
        var s = new Snapshot { Root = "C:\\", RootId = 1, CreatedUtc = DateTime.UtcNow, TotalRecordSlots = 100, BitmapUsed = true, ReadSeconds = 1.2, ParseSeconds = .3, BuildSeconds = .4 };
        var b = new IndexBuilder();
        foreach (var e in new Entry[] {
            new() { Id = 1, Directory = true },
            new() { Id = 2, Directory = true, Names = [new(1, "dir", 1)] },
            new() { Id = 3, Logical = 100, Allocated = 4096, Names = [new(1, "z", 1), new(2, "a", 1), new(2, "A~1", 2)] },
            new() { Id = 4, Logical = 200, Allocated = 8192, Names = [new(2, "other", 1)] },
            new() { Id = 5, Directory = true, Names = [new(2, "sub", 1)] },
            new() { Id = 6, Logical = 50, Allocated = 2048, Names = [new(5, "nested", 1)] },
            new() { Id = 7, Logical = 9, Names = [new(999, "orphan", 1)] } }) b.Add(e);
        s.Build([b]); return s;
    }
    static void TestCompact(string root)
    {
        var buffer = new SegmentBuffer<int>(3); buffer.Append(new[] { 1, 2, 3, 4, 5 }); buffer.Add(6); buffer.Add(7); var values = new int[7]; buffer.CopyTo(values);
        Check(values.SequenceEqual(Enumerable.Range(1, 7)) && buffer.Count == 7, "Segment buffers preserve records across append/copy boundaries");
        var extension = new Snapshot { Root = "C:\\", RootId = 1 }; var extBuilder = new IndexBuilder();
        extBuilder.Add(new() { Id = 1, Directory = true }); extBuilder.Add(new() { Id = 10, Logical = 1, Allocated = 1024, Names = [new(1, "DOS~1", 2)] });
        extBuilder.Extensions.Add((10, new() { Id = 11, Logical = 2, Allocated = 2048, Names = [new(1, "long name", 1)] })); extension.Build([extBuilder]);
        Check(extension.Get(10).Allocated == 3072 && extension.Get(10).Logical == 3 && extension.PathOf(10) == "C:\\long name" && extension.Get(10).Links == 1, "Extension records merge into base IDs and retain DOS/long namespace selection");
        foreach (string backend in new[] { "ntfs-mft", "ntfs-mft-ioctl" })
        {
            var ordered = new Snapshot { Root = "C:\\", RootId = 1, Backend = backend }; var baseBuilder = new IndexBuilder(); var otherBuilder = new IndexBuilder();
            baseBuilder.Add(new() { Id = 1, Directory = true }); baseBuilder.Add(new() { Id = 2, Names = [new(1, "x", 1)] }); baseBuilder.Add(new() { Id = 3, Names = [new(1, "y", 1)] });
            baseBuilder.Extensions.Add((2, new() { Id = 20, Names = [new(1, "a", 1)] })); otherBuilder.Extensions.Add((2, new() { Id = 10, Names = [new(1, "b", 1)] })); ordered.Build([baseBuilder, otherBuilder]);
            Check(ordered.Get(2).Names.Select(n => n.Name).SequenceEqual(backend == "ntfs-mft" ? new[] { "x", "b", "a" } : new[] { "x", "a", "b" }), $"{backend} preserves extension-name order across parser workers");
            Check(ordered.Get(1).Children.SequenceEqual(backend == "ntfs-mft" ? new ulong[] { 2, 3 } : new ulong[] { 3, 2 }), $"{backend} preserves child traversal order and Largest tie behavior");
        }
        var many = new Snapshot { Root = "C:\\", RootId = 1 }; var manyBuilder = new IndexBuilder();
        manyBuilder.Add(new() { Id = 1, Directory = true }); manyBuilder.Add(new() { Id = 2, Allocated = 4096, Names = Enumerable.Range(0, 32).Select(i => new LinkName(1, $"link{i:00}", 1)).ToList() }); many.Build([manyBuilder]);
        Check(many.ListChildren(1).Count() == 32 && many.Resolve("C:\\link31") == 2 && many.Get(1).TotalAllocated == 4096, "Name deduplication handles more unique hard-link names than records");
        Check(many.RemoveName("C:\\link00", 2) && many.ListChildren(1).Count() == 31 && many.Get(1).ChildCount == 1 && many.PathOf(2) == "C:\\link01", "Same-parent canonical transfer never duplicates CSR children");
        var unresolved = new Snapshot { Root = "C:\\", RootId = 1 }; var orphanBuilder = new IndexBuilder();
        orphanBuilder.Add(new() { Id = 1, Directory = true }); orphanBuilder.Add(new() { Id = 2, Directory = true, Names = [new(999, "orphan", 1)] });
        orphanBuilder.Add(new() { Id = 3, Allocated = 4096, Names = [new(1, "a", 1), new(2, "z", 1)] }); unresolved.Build([orphanBuilder]);
        Check(unresolved.RemoveName("C:\\a", 3) && unresolved.Unresolved == 2 && unresolved.Get(1).Files == 0 && unresolved.Get(1).TotalAllocated == 0 && unresolved.PathOf(3) == "[unresolved]", "Canonical transfer into an unresolved subtree updates reachability and root totals");
        var s = Synthetic();
        Check(s.Resolve("C:\\DIR\\A") == 3 && s.Resolve("C:\\z") == 3 && s.PathOf(3) == "C:\\dir\\a", "Compact index preserves case-insensitive lookup, aliases and canonical paths");
        Check(s.Get(1).Files == 3 && s.Get(1).Directories == 2 && s.Get(1).TotalAllocated == 14336 && s.Unresolved == 1, "Compact CSR rollup preserves directory and unresolved accounting");
        Check(s.ListChildren(1).Count() == 2 && s.ListChildren(1).Single(e => e.Alias).Name == "z", "Compact children include aliases without duplicate physical accounting");
        string file = Path.Combine(root, "compact.dscope");
        foreach (byte codec in new byte[] { 0, 1, 2 })
        {
            s.Save(file, codec); var copy = Snapshot.Load(file);
            Check(copy.Resolve("C:\\z") == 3 && copy.Get(1).TotalAllocated == 14336 && copy.PathOf(6) == "C:\\dir\\sub\\nested" && copy.Unresolved == 1 && copy.SaveSeconds > 0 && copy.ReadSeconds == 1.2, $"v3 block snapshot roundtrip codec {codec} preserves fields and timings");
        }
        string cache = Path.Combine(root, "compact-state"); Directory.CreateDirectory(cache); s.Save(Path.Combine(cache, "C.dscope"));
        using (var service = new Service(cache))
        {
            var largest = service.Largest("C:\\", 2, 0, null, null);
            Check(Field<long>(largest, "matching_files") == 3 && Field<object[]>(largest, "items").Length == 2 && JsonSerializer.SerializeToElement(largest, Program.JsonOptions).GetProperty("items")[0].GetProperty("name").GetString() == "other", "Largest traverses compact CSR and preserves ranking");
            var browse = service.Browse("C:\\dir", 1, 0, "allocated");
            Check(Field<long>(browse, "omitted_allocated_bytes") == 6144 && Field<int>(browse, "next_offset") == 1 && Field<int>(browse, "total_children") == 3, "Browse preserves sorting, pagination and omitted allocation");
        }
        s = Synthetic(); Check(s.RemoveName("C:\\z", 3) && s.Get(1).TotalAllocated == 14336 && s.Get(3).Links == 1 && !s.ListChildren(1).Any(x => x.Alias), "Deleting a noncanonical hard link keeps physical totals");
        Throws<FileNotFoundException>(() => s.Resolve("C:\\z"), "absent", "Deleted alias no longer resolves");
        Check(s.RemoveName("C:\\dir\\a", 3) && s.Get(1).TotalAllocated == 10240 && s.Get(2).TotalLogical == 250 && s.Get(1).Files == 2 && s.Count == 6, "Deleting the final link subtracts allocation, logical size and files along ancestors");
        s = Synthetic();
        Check(s.RemoveName("C:\\dir\\a", 3) && s.Get(2).TotalAllocated == 10240 && s.Get(1).TotalAllocated == 14336 && s.PathOf(3) == "C:\\z" && s.Resolve("C:\\z") == 3, "Deleting the canonical link transfers ownership to a remaining name");
        s.Save(file); s = Snapshot.Load(file);
        Check(s.AdjustedAfterCleanup && s.CleanupAdjustments == 1 && s.Get(3).Links == 1 && s.ListChildren(1).Any(x => !x.Alias && x.Name == "z"), "Adjusted v3 snapshot persists transferred canonical children and flags");
        Check(s.RemoveName("C:\\z", 3) && s.RemoveName("C:\\dir\\sub\\nested", 6) && s.Get(1).TotalAllocated == 8192 && s.Get(2).Files == 1 && s.Get(5).Files == 0 && s.Get(1).Directories == 2, "Full hard-link deletion and nested file deletion retain empty directories and totals");
        Throws<FileNotFoundException>(() => s.Resolve("C:\\dir\\sub\\nested"), "absent", "Deleted canonical child no longer resolves");
        string old = Path.Combine(root, "old.dscope");
        using (var fs = File.Create(old)) using (var zip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest)) using (var w = new BinaryWriter(zip)) w.Write("DiskScope-2");
        Check(!Snapshot.FormatCurrent(old), "v2 snapshots are marked noncurrent");
        Throws<InvalidDataException>(() => Snapshot.Load(old), "disk_scan", "v2 load reports a clear rescan requirement");
    }
    static void TestAgentOutput(string root)
    {
        var s = new Snapshot { Root = @"C:\", RootId = 1, CreatedUtc = DateTime.UtcNow };
        var b = new IndexBuilder();
        foreach (var e in new Entry[] {
            new() { Id = 1, Directory = true },
            new() { Id = 2, Directory = true, Names = [new(1, "dir", 1)] },
            new() { Id = 3, Allocated = 4 * 1024 * 1024, Logical = 4 * 1024 * 1024, Names = [new(2, "movie.MP4", 1), new(1, "z-alias.mp4", 1)] },
            new() { Id = 4, Allocated = 2 * 1024 * 1024, Logical = 128 * 1024 * 1024, Names = [new(2, "sparse.img", 1)] },
            new() { Id = 5, Allocated = 1024 * 1024, Logical = 2 * 1024 * 1024, Names = [new(2, "README", 1)] },
            new() { Id = 6, Allocated = 1024 * 1024, Logical = 1024 * 1024, Reparse = true, Names = [new(2, "link.png", 1)] },
            new() { Id = 7, Allocated = 16 * 1024 * 1024, Logical = 17 * 1024 * 1024 + 1, Names = [new(2, "lowdiff.cs", 1)] },
            new() { Id = 8, Allocated = 1024 * 1024, Logical = 1024 * 1024, Names = [new(2, "cache.tmp", 1)] },
            new() { Id = 9, Allocated = 2 * 1024 * 1024, Logical = 2 * 1024 * 1024, Names = [new(2, "small.mp4", 1)] } }) b.Add(e);
        s.Build([b]);
        string cache = Path.Combine(root, "agent-state"); Directory.CreateDirectory(cache); string file = Path.Combine(cache, "C.dscope"); s.Save(file);
        using (var fs = File.OpenRead(file)) using (var r = new BinaryReader(fs)) Check(r.ReadString() == Snapshot.Magic && r.ReadByte() == 1, "Default atomic v3 snapshot uses ZLib flag");
        Check(Snapshot.Load(file).Get(1).TotalAllocated == s.Get(1).TotalAllocated && !Directory.EnumerateFiles(cache, "*.tmp").Any(), "Default ZLib roundtrip and atomic temporary cleanup");
        using var service = new Service(cache);
        var browse = JsonSerializer.SerializeToElement(service.Browse(@"C:\dir", 20, 0, "name"), Program.JsonOptions);
        var rows = browse.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Check(browse.TryGetProperty("snapshot", out var stamp) && !browse.TryGetProperty("scan", out _) && stamp.EnumerateObject().Count() == 3 && !browse.TryGetProperty("next_action", out _) && !browse.TryGetProperty("next_offset", out _), "Compact response has short snapshot and no generic guidance");
        var normal = rows["small.mp4"];
        Check(normal.EnumerateObject().Count() == 6 && !normal.TryGetProperty("files", out _) && !normal.TryGetProperty("id", out _) && !normal.TryGetProperty("cleanup_hint", out _), "Ordinary compact file contains only six required fields");
        Check(rows["sparse.img"].GetProperty("logical_bytes").GetInt64() == 128 * 1024 * 1024 && !rows["README"].TryGetProperty("logical_bytes", out _) && !rows["lowdiff.cs"].TryGetProperty("logical_bytes", out _), "Logical size requires both strict 1 MiB and 10 percent differences");
        Check(rows["movie.MP4"].GetProperty("hard_links").GetInt32() == 2 && !normal.TryGetProperty("hard_links", out _) && rows["link.png"].GetProperty("reparse_point").GetBoolean() && !normal.TryGetProperty("reparse_point", out _), "Nondefault hard-link and reparse flags are conditional");
        Check(rows["movie.MP4"].GetProperty("cleanup_hint").GetString()!.StartsWith("hard-linked") && browse.GetProperty("directory").GetProperty("files").GetInt64() == 7 && browse.GetProperty("directory").GetProperty("expandable").GetBoolean(), "Special cleanup hint and directory-only counts are preserved");
        Check(normal.GetProperty("share").GetDouble() == 7.4 && Service.Share(1, 3) == 33.3 && Service.Share(1, 0) == 0, "Shares use parent allocation, one decimal and zero-safe division");
        var alias = JsonSerializer.SerializeToElement(service.Browse(@"C:\", 20, 0, "name"), Program.JsonOptions).GetProperty("items")[1];
        Check(alias.GetProperty("hard_link_alias").GetBoolean() && alias.GetProperty("allocated_bytes").GetInt64() == 0 && alias.GetProperty("physical_bytes").GetInt64() == 4 * 1024 * 1024 && alias.GetProperty("canonical_path").GetString() == @"C:\dir\movie.MP4", "Compact alias exposes physical bytes and canonical owner without duplicate allocation");
        var full = JsonSerializer.SerializeToElement(service.Browse(@"C:\dir", 20, 0, "name", true), Program.JsonOptions);
        var fullRow = full.GetProperty("items")[0];
        Check(full.TryGetProperty("scan", out var info) && info.TryGetProperty("accounting", out _) && !full.TryGetProperty("snapshot", out _) && fullRow.TryGetProperty("id", out _) && fullRow.TryGetProperty("logical", out _) && fullRow.TryGetProperty("child_count", out _) && fullRow.TryGetProperty("hard_link_alias", out _) && full.TryGetProperty("next_action", out _), "Verbose retains full 1.0.x browse fields and guidance");
        var detail = JsonSerializer.SerializeToElement(service.Detail(@"C:\dir\small.mp4", true), Program.JsonOptions);
        Check(detail.TryGetProperty("scan", out _) && detail.TryGetProperty("note", out _) && detail.GetProperty("item").TryGetProperty("logical_bytes", out _), "Verbose detail retains compatibility metadata");
        var largest = JsonSerializer.SerializeToElement(service.Largest(@"C:\dir", 1, 0, null, null, true), Program.JsonOptions);
        Check(largest.TryGetProperty("scan", out _) && largest.GetProperty("items")[0].TryGetProperty("id", out _), "Verbose largest retains compatibility fields");
        var types = JsonSerializer.SerializeToElement(service.Types(@"C:\", 50), Program.JsonOptions);
        var ext = types.GetProperty("extensions").EnumerateArray().ToDictionary(x => x.GetProperty("extension").GetString()!);
        Check(types.GetProperty("files").GetInt64() == 7 && ext[".mp4"].GetProperty("files").GetInt64() == 2 && ext[".mp4"].GetProperty("allocated_bytes").GetInt64() == 6 * 1024 * 1024, "Types merges extension case and counts canonical hard links once");
        Check(ext["(no extension)"].GetProperty("files").GetInt64() == 1 && ext[".mp4"].GetProperty("example_path").GetString() == @"C:\dir\movie.MP4" && ext[".mp4"].GetProperty("share").GetDouble() == 22.2, "Types includes no extension, largest example and subtree share");
        Check(types.GetProperty("categories").EnumerateArray().Select(x => x.GetProperty("category").GetString()).Order().SequenceEqual(new[] { "code", "disk_image", "image", "other", "temp_cache", "video" }.Order()) && types.GetProperty("categories").EnumerateArray().Sum(x => x.GetProperty("allocated_bytes").GetInt64()) == s.Get(1).TotalAllocated, "Types category totals preserve physical allocation");
        foreach (var mapping in new[] { (".mp3", "audio"), (".zip", "archive"), (".msi", "installer"), (".pdf", "document"), (".db", "database"), (".log", "log"), (".dll", "executable/library"), (".unknown", "other"), (".ts", "code") })
            Check(FileTypes.Category(mapping.Item1) == mapping.Item2, $"Category mapping {mapping.Item1}");
        Check(Field<object[]>(service.Types(@"C:\", 1), "extensions").Length == 1 && Field<object[]>(service.Types(@"C:\dir\small.mp4"), "extensions").Length == 1, "Types limits output and supports file scope");
        Check(JsonSerializer.SerializeToElement(service.Types(@"C:\", verbose: true), Program.JsonOptions).TryGetProperty("scan", out _), "Types supports verbose snapshot metadata");
    }
    static void TestWait()
    {
        var jobs = new Jobs(); using var finish = new ManualResetEventSlim();
        var started = jobs.Start("test", update => { update(new("waiting", 1, 2)); finish.Wait(); return new { ok = true }; });
        string id = Field<string>(started, "job_id");
        var zero = Stopwatch.StartNew(); Check(Field<string>(jobs.WaitStatus(id, 0).GetAwaiter().GetResult(), "state") == "running" && zero.Elapsed.TotalSeconds < .5, "Zero wait returns immediately");
        var progress = new List<CleanupProgress>(); var watch = Stopwatch.StartNew(); var wait = jobs.WaitStatus(id, 30, progress.Add);
        Thread.Sleep(1100); finish.Set();
        Check(Field<string>(wait.GetAwaiter().GetResult(), "state") == "complete" && watch.Elapsed.TotalSeconds < 3 && progress.Count >= 1 && progress.Any(p => p.Processed == 1), "Waiting emits task progress and completes early");
        watch.Restart(); jobs.WaitStatus(id, 30).GetAwaiter().GetResult(); Check(watch.Elapsed.TotalSeconds < .5, "Completed jobs do not consume requested wait");
        Throws<ArgumentException>(() => jobs.WaitStatus(id, 31).GetAwaiter().GetResult(), "0..30", "Wait upper bound enforced");
        Throws<ArgumentException>(() => jobs.WaitStatus(id, -1).GetAwaiter().GetResult(), "0..30", "Wait lower bound enforced");
        Throws<ArgumentException>(() => jobs.WaitStatus("missing", 1).GetAwaiter().GetResult(), "Unknown job_id", "Unknown waiting job rejected");
        using var blocked = new ManualResetEventSlim(); var next = jobs.Start("timeout", _ => { blocked.Wait(); return new { }; });
        watch.Restart(); var timed = jobs.WaitStatus(Field<string>(next, "job_id"), 1).GetAwaiter().GetResult();
        Check(Field<string>(timed, "state") == "running" && watch.Elapsed.TotalSeconds >= .9 && watch.Elapsed.TotalSeconds < 2, "Wait timeout returns still-running state");
        blocked.Set(); jobs.Drain();
        var views = jobs.Start("views", _ => new JobResult(new { value = "compact" }, new { value = "full" })); jobs.Drain();
        string viewsId = Field<string>(views, "job_id");
        Check(Field<JsonElement>(jobs.Status(viewsId), "result").GetProperty("value").GetString() == "compact" && Field<JsonElement>(jobs.Status(viewsId, true), "result").GetProperty("value").GetString() == "full", "Job status selects compact or full stored results without retaining an index");
        Check(Field<JsonElement>(jobs.WaitStatus(viewsId, 30, verbose: true).GetAwaiter().GetResult(), "result").GetProperty("value").GetString() == "full", "Waiting job status honors verbose result selection");
    }
    static void TestCleanupAdjustment(string root)
    {
        string fixture = Path.Combine(root, "adjust"); Directory.CreateDirectory(fixture);
        string first = Path.Combine(fixture, "first.dat"), second = Path.Combine(fixture, "second.dat"); File.WriteAllBytes(first, new byte[8193]);
        using (var process = Process.Start(new ProcessStartInfo("fsutil", $"hardlink create \"{second}\" \"{first}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!) { process.WaitForExit(); Check(process.ExitCode == 0, "Incremental cleanup hard-link fixture created"); }
        long fixtureAllocation = ReadEntry(first).Allocated;
        string volumeRoot = Path.GetPathRoot(fixture)!; var s = new Snapshot { Root = volumeRoot, CreatedUtc = DateTime.UtcNow };
        var builder = new IndexBuilder();
        string? path = fixture;
        while (path != null)
        {
            using var h = Native.Open(path); var info = Native.Info(h); var entry = ReadEntry(path); builder.Add(entry);
            if (path.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase)) { s.RootId = entry.Id; s.VolumeSerial = info.Serial; break; }
            path = Path.GetDirectoryName(path);
        }
        builder.Add(ReadEntry(first)); s.Build([builder]); string cache = Path.Combine(root, "adjust-state"); Directory.CreateDirectory(cache); string file = Path.Combine(cache, volumeRoot[0] + ".dscope"); s.Save(file);
        using var service = new Service(cache); var cleaner = new Cleanup(service); service.Get(first);
        long before = service.Get(first).Get(s.RootId).TotalAllocated;
        var plan = cleaner.Plan([first], 10); var result = cleaner.Execute(Field<string>(plan, "plan_id"), Field<string>(plan, "confirm_token"));
        Check(Field<string>(result, "next_action").Contains("Adjusted 1") && service.Get(second).Get(s.RootId).TotalAllocated == before && Snapshot.Load(file).CleanupAdjustments == 1, "Executor adjusts and saves a matching loaded snapshot after partial link deletion");
        plan = cleaner.Plan([second], 10); result = cleaner.Execute(Field<string>(plan, "plan_id"), Field<string>(plan, "confirm_token"));
        Check(Field<string>(result, "next_action").Contains("Adjusted 1") && service.Get(fixture).Get(s.RootId).TotalAllocated == before - fixtureAllocation && Snapshot.Load(file).CleanupAdjustments == 2, "Executor saves final hard-link allocation deduction without changing safety checks");
        // Re-index only new owned fixtures to exercise stale snapshot and external link-count changes.
        string stale = Path.Combine(fixture, "stale.dat"), changed = Path.Combine(fixture, "changed.dat"), external = Path.Combine(fixture, "external.dat");
        File.WriteAllBytes(stale, new byte[4097]); File.WriteAllBytes(changed, new byte[4097]);
        builder = new IndexBuilder(); path = fixture;
        while (path != null) { builder.Add(ReadEntry(path)); if (path.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase)) break; path = Path.GetDirectoryName(path); }
        builder.Add(ReadEntry(stale)); builder.Add(ReadEntry(changed)); s = new Snapshot { Root = volumeRoot, RootId = s.RootId, CreatedUtc = DateTime.UtcNow, VolumeSerial = s.VolumeSerial }; s.Build([builder]); s.Save(file); service.Release(); service.Get(stale);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(1)); plan = cleaner.Plan([stale], 10); result = cleaner.Execute(Field<string>(plan, "plan_id"), Field<string>(plan, "confirm_token"));
        Check(Field<string>(result, "next_action").Contains("1 names require a rescan") && service.Get(fixture).Contains(ReadEntry(changed).Id), "Cleanup refuses to adjust a cached snapshot replaced on disk");
        service.Release(); service.Get(changed);
        using (var process = Process.Start(new ProcessStartInfo("fsutil", $"hardlink create \"{external}\" \"{changed}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!) { process.WaitForExit(); Check(process.ExitCode == 0, "External hard-link change fixture created"); }
        plan = cleaner.Plan([changed], 10); result = cleaner.Execute(Field<string>(plan, "plan_id"), Field<string>(plan, "confirm_token"));
        Check(Field<string>(result, "next_action").Contains("1 names require a rescan") && File.Exists(external) && !service.Get(fixture).AdjustedAfterCleanup, "Link-count drift skips index deductions and preserves the external link");
        string skip = Path.Combine(fixture, "skip.txt"); File.WriteAllText(skip, "fixture"); plan = cleaner.Plan([skip], 10); service.Release(); result = cleaner.Execute(Field<string>(plan, "plan_id"), Field<string>(plan, "confirm_token"));
        Check(Field<string>(result, "next_action").Contains("1 names require a rescan"), "Cleanup with an unloaded index requests rescan");
    }
    static Entry ReadEntry(string path)
    {
        using var f = Native.Open(path); var info = Native.Info(f);
        using var vol = Native.Open(@"\\.\" + Path.GetPathRoot(path)![..2], 0x80000000);
        var v = Native.Ioctl(vol, 0x90064, null, 128);
        int rs = BitConverter.ToInt32(v, 48), cluster = BitConverter.ToInt32(v, 44);
        return NtfsScanner.Parse(NtfsScanner.Record(vol, info.Identity, rs), info.Identity, cluster);
    }
    static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass a new, nonexistent test directory; tests only delete their own fixtures");
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Test directory already exists; choose a new fixture path");
        Directory.CreateDirectory(root);
        TestFixup(); TestBitmap(); TestCompact(root); TestAgentOutput(root); TestWait(); TestCleanupAdjustment(root);
        string cache = Path.Combine(root, "state"); using var service = new Service(cache); var cleaner = new Cleanup(service);
        string fixture = Path.Combine(root, "fixture"); Directory.CreateDirectory(fixture);
        string regular = Path.Combine(fixture, "大 文件.dat"); File.WriteAllBytes(regular, new byte[1048577]);
        var e = ReadEntry(regular); using (var h = Native.Open(regular))
        {
            Check(e.Logical == Native.Info(h).Size, "MFT logical size matches live Unicode file");
            Check(e.Allocated == Native.Allocation(h), "MFT physical allocation matches live file");
        }
        File.WriteAllBytes(regular + ":agent-test", new byte[8192]);
        var ads = ReadEntry(regular); Check(ads.Logical == 1048577 + 8192 && ads.Allocated > e.Allocated, "Alternate data streams counted");
        File.WriteAllText(Path.Combine(fixture, "tiny.txt"), "tiny"); var tiny = ReadEntry(Path.Combine(fixture, "tiny.txt")); Check(tiny.Logical == 4 && tiny.Allocated == 0, "Resident data consumes no standalone clusters");
        string link = Path.Combine(fixture, "hardlink.dat");
        var linkProc = Process.Start(new ProcessStartInfo("fsutil", $"hardlink create \"{link}\" \"{regular}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!; linkProc.WaitForExit();
        var linked = ReadEntry(link); Console.WriteLine($"linkExit={linkProc.ExitCode} id={linked.Id:x16} expected={ads.Id:x16} links={linked.Links} names={linked.Names.Count}");
        Check(linkProc.ExitCode == 0 && linked.Id == ads.Id && linked.Links == 2, "Hard link identity and count");
        var hardPlan = cleaner.Plan([link], 100); Check(Field<long>(hardPlan, "estimated_reclaimable_bytes") == 0, "Single hard-link removal estimates zero reclaimed storage");
        string sparse = Path.Combine(fixture, "sparse.bin"); File.WriteAllBytes(sparse, []);
        var sp = Process.Start(new ProcessStartInfo("fsutil", $"sparse setflag \"{sparse}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!; sp.WaitForExit();
        using (var fs = File.OpenWrite(sparse)) fs.SetLength(128L * 1024 * 1024);
        var se = ReadEntry(sparse); Check(se.Logical == 128L * 1024 * 1024 && se.Allocated == 0, "Sparse logical size is not physical usage");
        Throws<InvalidOperationException>(() => cleaner.Plan(["C:\\"], 10), "Drive-root cleanup is not allowed", "Drive root deletion blocked");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Throws<InvalidOperationException>(() => cleaner.Plan([windows], 10), "Protected system, application, or tool path", "Windows deletion blocked");
        string protectedFile = Path.Combine(windows, "explorer.exe");
        Throws<InvalidOperationException>(() => cleaner.Plan([@"\\?\" + protectedFile], 10), "Protected system, application, or tool path", "Extended-prefix Windows file selection refused");
        Throws<ArgumentException>(() => cleaner.Plan([@"\\.\" + protectedFile], 10), "A local drive-letter path is required", "Device-prefix Windows file selection refused");
        Throws<ArgumentException>(() => cleaner.Plan([@"\\localhost\" + protectedFile[0] + "$" + protectedFile[2..]], 10), "A local drive-letter path is required", "UNC Windows file selection refused");
        Throws<InvalidOperationException>(() => cleaner.Plan(["F:\\Program Files"], 10), "Protected system, application, or tool path", "Application directory protected on a secondary drive");
        Throws<InvalidOperationException>(() => cleaner.Plan(["C:\\Users"], 10), "Whole user-profile cleanup is refused", "Whole Users directory protected");
        Throws<InvalidOperationException>(() => cleaner.Plan(["C:\\Users\\OtherUser"], 10), "Whole user-profile cleanup is refused", "Whole secondary profile protected");
        Throws<ArgumentException>(() => Service.Normalize(@"\\server\share\file"), "A local drive-letter path is required", "UNC selections refused");
        Throws<ArgumentException>(() => Service.Normalize(regular + ":agent-test"), "A local drive-letter path is required", "Alternate-stream selection refused");
        Throws<InvalidOperationException>(() => cleaner.Plan([fixture], 1), "Selection exceeds max_files", "Plan file-count limit enforced");
        var ancestors = cleaner.LockAncestors(regular);
        try
        {
            Throws<Win32Exception>(() => { using var h = Native.Open(Native.LongPath(fixture), 0x10000); }, "Cannot open", "Ancestor lock denies DELETE access used for rename/replacement", 32);
            Throws<IOException>(() => Directory.Move(fixture, fixture + "-moved"), "", "Ancestor lock blocks parent-directory rename", 32);
        }
        finally { foreach (var h in ancestors) h.Dispose(); }
        Directory.Move(fixture, fixture + "-moved"); Directory.Move(fixture + "-moved", fixture);
        Check(Directory.Exists(fixture), "Ordinary user directory can be renamed after ancestor locks close");
        using (var h = Native.Open(Native.LongPath(regular)))
        {
            string actual = Native.FinalPath(h);
            Cleanup.CheckPathMatches(regular.ToUpperInvariant(), @"\\?\" + actual);
            Check(true, "Handle path comparison accepts DOS prefix and case differences");
            Throws<InvalidOperationException>(() => Cleanup.CheckPathMatches(link, actual), "Handle path does not match expected path", "Handle path comparison rejects a different hard-link name with the same identity");
        }
        string junction = Path.Combine(root, "junction"); var jp = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{fixture}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!; jp.WaitForExit();
        Check(jp.ExitCode == 0, "Junction fixture created");
        Throws<InvalidOperationException>(() => cleaner.Plan([junction], 100), "Plan contains a junction/symlink", "Junction selection refused");
        Throws<InvalidOperationException>(() => cleaner.Plan([Path.Combine(junction, "tiny.txt")], 100), "Cleanup through a reparse-point ancestor is refused", "Junction ancestor refused");
        string changing = Path.Combine(root, "changing.txt"); File.WriteAllText(changing, "old");
        var stale = cleaner.Plan([changing], 10); File.WriteAllText(changing, "new contents");
        Throws<InvalidOperationException>(() => cleaner.Execute(Field<string>(stale, "plan_id"), Field<string>(stale, "confirm_token")), "File changed since preview", "Stale preview refused before deletion"); Check(File.Exists(changing), "Stale file retained");
        string delete = Path.Combine(root, "delete.txt"); File.WriteAllText(delete, "delete test fixture only");
        var plan = cleaner.Plan([delete], 10); string id = Field<string>(plan, "plan_id"), token = Field<string>(plan, "confirm_token");
        Throws<InvalidOperationException>(() => cleaner.Execute(id, "wrong-token"), "confirm_token does not match the reviewed plan", "Wrong plan token refused");
        var progress = new List<CleanupProgress>();
        var result = cleaner.Execute(id, token, progress.Add); Check(Field<int>(result, "deleted_files") == 1 && !File.Exists(delete), "Exact fixture file deletion works");
        Check(progress.Any(p => p.Stage == "preflight") && progress.Any(p => p.Stage == "deleting") && progress.Last().DeletedFiles == 1, "Cleanup progress distinguishes preflight and durable deletions");
        var auditStatus = cleaner.ExecutionStatus(id);
        Check(Field<string>(auditStatus, "state") == "complete" && Field<int>(auditStatus, "deleted_files") == 1, "Durable completion audit is readable after execution");
        Throws<IOException>(() => cleaner.Execute(id, token), "", "Plan replay blocked by exclusive audit creation", 80);
        string readOnly = Path.Combine(root, "readonly.txt"); File.WriteAllText(readOnly, "read-only fixture"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var readOnlyPlan = cleaner.Plan([readOnly], 10);
        Check(Field<int>(readOnlyPlan, "file_count") == 1 && (File.GetAttributes(readOnly) & FileAttributes.ReadOnly) != 0, "Read-only file is planned without clearing its attribute");
        var readOnlyResult = cleaner.Execute(Field<string>(readOnlyPlan, "plan_id"), Field<string>(readOnlyPlan, "confirm_token"));
        Check(Field<int>(readOnlyResult, "deleted_files") == 1 && Field<object[]>(readOnlyResult, "failures").Length == 0 && !File.Exists(readOnly), "Read-only fixture deletion succeeds");
        string selection = Path.Combine(root, "selection"); Directory.CreateDirectory(selection); File.WriteAllText(Path.Combine(selection, "before.txt"), "before");
        var folderPlan = cleaner.Plan([selection], 100); File.WriteAllText(Path.Combine(selection, "after.txt"), "after");
        cleaner.Execute(Field<string>(folderPlan, "plan_id"), Field<string>(folderPlan, "confirm_token"));
        Check(File.Exists(Path.Combine(selection, "after.txt")) && Directory.Exists(selection), "New files and directories retained");
        var allLinks = cleaner.Plan([link, regular], 100);
        Check(Field<long>(allLinks, "estimated_reclaimable_bytes") > 0, "Removing all hard-link names counts allocation once");
        var allLinksResult = cleaner.Execute(Field<string>(allLinks, "plan_id"), Field<string>(allLinks, "confirm_token"));
        Check(Field<int>(allLinksResult, "deleted_files") == 2 && !File.Exists(link) && !File.Exists(regular), "Own hard-link count changes do not invalidate later planned deletion");
        var snap = new Snapshot { Root = "C:\\", RootId = 1, CreatedUtc = DateTime.UtcNow };
        var r = new Entry { Id = 1, Directory = true }; var d = new Entry { Id = 2, Directory = true, Names = [new(1, "dir", 1)] };
        var hard = new Entry { Id = 3, Links = 2, Logical = 100, Allocated = 4096, Names = [new(1, "z", 1), new(2, "a", 1)] };
        var builder = new IndexBuilder(); foreach (var entry in new[] { r, d, hard }) builder.Add(entry); snap.Build([builder]);
        Check(snap.Get(1).TotalAllocated == 4096 && snap.Get(1).Files == 1, "Hard-linked allocation counted once across directory rollup");
        string snapshotFile = Path.Combine(root, "roundtrip.dscope"); snap.Save(snapshotFile); var restored = Snapshot.Load(snapshotFile);
        Check(restored.Get(1).TotalAllocated == 4096 && restored.Resolve("C:\\dir\\a") == 3 && restored.Resolve("C:\\z") == 3, "Snapshot roundtrip and hard-link alias resolution");
        Check(restored.ListChildren(1).Any(x => x.Alias && x.Name == "z"), "Hard-link aliases appear in directory listing without duplicate allocation");
        Directory.CreateDirectory(cache); snap.Save(Path.Combine(cache, "C.dscope"));
        service.Get("C:\\dir"); Check(service.LoadedCount == 1, "Snapshot is cached after query");
        service.Release(); Check(service.LoadedCount == 0 && File.Exists(Path.Combine(cache, "C.dscope")), "Explicit release preserves disk snapshot");
        service.Get("C:\\dir"); service.TrimIdle(DateTime.UtcNow.AddSeconds(service.IdleSeconds + 1));
        Check(service.LoadedCount == 0, "Idle indexes are evicted");
        service.Get("C:\\dir"); snap.Root = "D:\\"; snap.Save(Path.Combine(cache, "D.dscope"));
        service.Get("D:\\dir"); Check(service.LoadedCount == 1, "Loading another volume evicts the previous index");
        var jobs = new Jobs(); using var finish = new ManualResetEventSlim();
        var started = jobs.Start("test", update => { update(new("waiting", 1, 2)); finish.Wait(); return new { ok = true }; });
        string jobId = Field<string>(started, "job_id");
        Check(Field<string>(jobs.Status(jobId), "state") == "running", "Background operation returns before completion");
        Throws<InvalidOperationException>(() => jobs.Start("overlap", _ => new { }), "An operation is already running", "Conflicting background operation blocked");
        finish.Set(); jobs.Drain(); Check(Field<string>(jobs.Status(jobId), "state") == "complete", "Background job completes with queryable result");
        var failedJob = jobs.Start("fail", _ => throw new InvalidOperationException("fixture failure")); jobs.Drain();
        Check(Field<string>(jobs.Status(Field<string>(failedJob, "job_id")), "state") == "failed", "Background failures remain queryable");
        Console.WriteLine($"{checks} checks passed"); return 0;
    }
}
