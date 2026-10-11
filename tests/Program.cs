using DiskScope;
using System.Text.Json;
using System.Diagnostics;

static class Tests
{
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
    static T Field<T>(object obj, string name) => JsonSerializer.SerializeToElement(obj, Program.JsonOptions).GetProperty(name).Deserialize<T>(Program.JsonOptions)!;
    static void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Expected failure: " + name); }
    static Entry ReadEntry(string path)
    {
        using var f = Native.Open(path); var info = Native.Info(f);
        using var vol = Native.Open(@"\\.\" + Path.GetPathRoot(path)![..2], 0x80000000);
        var v = Native.Ioctl(vol, 0x90064, null, 128);
        int rs = BitConverter.ToInt32(v, 48), sector = BitConverter.ToInt32(v, 40), cluster = BitConverter.ToInt32(v, 44);
        return NtfsScanner.Parse(NtfsScanner.Record(vol, info.Identity, rs, sector), info.Identity, cluster);
    }
    static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass a new, nonexistent test directory; tests only delete their own fixtures");
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Test directory already exists; choose a new fixture path");
        Directory.CreateDirectory(root);
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
        Throws(() => cleaner.Plan(["C:\\"], 10), "Drive root deletion blocked");
        Throws(() => cleaner.Plan([Environment.GetFolderPath(Environment.SpecialFolder.Windows)], 10), "Windows deletion blocked");
        Throws(() => cleaner.Plan(["F:\\Program Files"], 10), "Application directory protected on a secondary drive");
        Throws(() => cleaner.Plan(["C:\\Users"], 10), "Whole Users directory protected");
        Throws(() => cleaner.Plan(["C:\\Users\\OtherUser"], 10), "Whole secondary profile protected");
        Throws(() => Service.Normalize(@"\\server\share\file"), "UNC selections refused");
        Throws(() => Service.Normalize(regular + ":agent-test"), "Alternate-stream selection refused");
        Throws(() => cleaner.Plan([fixture], 1), "Plan file-count limit enforced");
        string junction = Path.Combine(root, "junction"); var jp = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{fixture}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!; jp.WaitForExit();
        Check(jp.ExitCode == 0, "Junction fixture created");
        Throws(() => cleaner.Plan([junction], 100), "Junction selection refused");
        Throws(() => cleaner.Plan([Path.Combine(junction, "tiny.txt")], 100), "Junction ancestor refused");
        string changing = Path.Combine(root, "changing.txt"); File.WriteAllText(changing, "old");
        var stale = cleaner.Plan([changing], 10); File.WriteAllText(changing, "new contents");
        Throws(() => cleaner.Execute(Field<string>(stale, "plan_id"), Field<string>(stale, "confirm_token")), "Stale preview refused before deletion"); Check(File.Exists(changing), "Stale file retained");
        string delete = Path.Combine(root, "delete.txt"); File.WriteAllText(delete, "delete test fixture only");
        var plan = cleaner.Plan([delete], 10); string id = Field<string>(plan, "plan_id"), token = Field<string>(plan, "confirm_token");
        Throws(() => cleaner.Execute(id, "wrong-token"), "Wrong plan token refused");
        var progress = new List<CleanupProgress>();
        var result = cleaner.Execute(id, token, progress.Add); Check(Field<int>(result, "deleted_files") == 1 && !File.Exists(delete), "Exact fixture file deletion works");
        Check(progress.Any(p => p.Stage == "preflight") && progress.Any(p => p.Stage == "deleting") && progress.Last().DeletedFiles == 1, "Cleanup progress distinguishes preflight and durable deletions");
        var auditStatus = cleaner.ExecutionStatus(id);
        Check(Field<string>(auditStatus, "state") == "complete" && Field<int>(auditStatus, "deleted_files") == 1, "Durable completion audit is readable after execution");
        Throws(() => cleaner.Execute(id, token), "Plan replay blocked");
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
        snap.Entries = new() { [1] = r, [2] = d, [3] = hard }; snap.Build();
        Check(r.TotalAllocated == 4096 && r.Files == 1, "Hard-linked allocation counted once across directory rollup");
        string snapshotFile = Path.Combine(root, "roundtrip.dscope"); snap.Save(snapshotFile); var restored = Snapshot.Load(snapshotFile);
        Check(restored.Entries[1].TotalAllocated == 4096 && restored.Resolve("C:\\dir\\a") == 3 && restored.Resolve("C:\\z") == 3, "Snapshot roundtrip and hard-link alias resolution");
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
        Throws(() => jobs.Start("overlap", _ => new { }), "Conflicting background operation blocked");
        finish.Set(); jobs.Drain(); Check(Field<string>(jobs.Status(jobId), "state") == "complete", "Background job completes with queryable result");
        var failedJob = jobs.Start("fail", _ => throw new InvalidOperationException("fixture failure")); jobs.Drain();
        Check(Field<string>(jobs.Status(Field<string>(failedJob, "job_id")), "state") == "failed", "Background failures remain queryable");
        Console.WriteLine($"{checks} checks passed"); return 0;
    }
}
