using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskScope;

record PlannedFile(string Path, ulong Id, uint Serial, long Size, long WriteTime, long Allocated, uint Links);
record CleanupPlan(string PlanId, string ConfirmToken, DateTime CreatedUtc, DateTime ExpiresUtc, string[] SelectedPaths, PlannedFile[] Files);
record CleanupProgress(string Stage, int Processed, int Total, int DeletedFiles = 0);

sealed class Cleanup(Service service)
{
    string PlansDir => System.IO.Path.Combine(service.DataDir, "plans");
    static bool Within(string p, string parent) => p.Equals(parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || p.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    void CheckAllowed(string path)
    {
        string root = System.IO.Path.GetPathRoot(path)!;
        if (path.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Drive-root cleanup is not allowed; select a specific subdirectory or files");
        string[] denied = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            root + "Windows", root + "Program Files", root + "Program Files (x86)", root + "WindowsApps", root + "ProgramData",
            root + "System Volume Information", root + "$Recycle.Bin", root + "Recovery", root + "Boot", AppContext.BaseDirectory, service.DataDir];
        if (denied.Where(x => !string.IsNullOrEmpty(x)).Any(x => Within(path, x))) throw new InvalidOperationException("Protected system, application, or tool path; use its servicing/uninstall interface");
        if (path.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Select a specific folder within the user profile");
        if (path.TrimEnd('\\').Equals(root + "Users", StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetDirectoryName(path.TrimEnd('\\'))?.Equals(root + "Users", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Whole user-profile cleanup is refused; select a specific subfolder");
        if (System.IO.Path.GetDirectoryName(path)?.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true &&
            (System.IO.Path.GetFileName(path).StartsWith('$') || new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys", "bootmgr" }.Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Protected volume file");
    }
    // Keep every ancestor pinned against rename/delete; reject junctions and symlinks at each level.
    internal List<SafeFileHandle> LockAncestors(string path)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            string? current = System.IO.Path.GetDirectoryName(path);
            while (current != null)
            {
                var h = Native.Open(Native.LongPath(current), 0x1 | 0x80, 3); handles.Add(h); // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, no delete sharing
                if ((Native.Info(h).Attributes & 0x400) != 0) throw new InvalidOperationException("Cleanup through a reparse-point ancestor is refused");
                current = System.IO.Path.GetDirectoryName(current);
            }
            return handles;
        }
        catch { foreach (var h in handles) h.Dispose(); throw; }
    }
    internal static void CheckPathMatches(string expected, string actual)
    {
        if (expected.StartsWith(@"\\?\")) expected = expected[4..];
        if (actual.StartsWith(@"\\?\")) actual = actual[4..];
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Handle path does not match expected path: {expected}");
    }
    void CheckHandlePath(string expected, SafeFileHandle h)
    {
        string actual = Native.FinalPath(h); CheckAllowed(actual); CheckPathMatches(expected, actual);
    }
    PlannedFile Inspect(string path)
    {
        CheckAllowed(path); var ancestors = LockAncestors(path);
        try
        {
            using var h = Native.Open(Native.LongPath(path), 0, 1); var i = Native.Info(h);
            CheckHandlePath(path, h);
            if ((i.Attributes & (0x400 | 0x10)) != 0) throw new InvalidOperationException("Cleanup accepts regular files only; no reparse points");
            return new(path, i.Identity, i.Serial, i.Size, i.Write, Native.Allocation(h), i.Links);
        }
        finally { foreach (var h in ancestors) h.Dispose(); }
    }
    public object Plan(string[] paths, int maxFiles, Action<CleanupProgress>? progress = null)
    {
        if (paths.Length == 0 || paths.Length > 100) throw new ArgumentException("Select 1..100 explicit paths");
        maxFiles = Math.Clamp(maxFiles, 1, 100000); string[] selected = paths.Select(Service.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        progress?.Invoke(new("enumerating", 0, 0));
        foreach (string path in selected)
        {
            CheckAllowed(path); var ancestors = LockAncestors(path);
            try
            {
                var pending = new Stack<string>(); pending.Push(path);
                while (pending.TryPop(out string? p))
                {
                    CheckAllowed(p); var attr = File.GetAttributes(Native.LongPath(p));
                    if ((attr & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"Plan contains a junction/symlink: {p}. Select files outside it");
                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        foreach (string child in Directory.EnumerateFileSystemEntries(Native.LongPath(p))) pending.Push(child.StartsWith(@"\\?\") ? child[4..] : child);
                    }
                    else { candidates.Add(p); if (candidates.Count > maxFiles) throw new InvalidOperationException("Selection exceeds max_files; narrow the scope"); }
                }
            }
            finally { foreach (var h in ancestors) h.Dispose(); }
        }
        int inspected = 0;
        var files = candidates.Order(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var file = Inspect(path); inspected++;
            if (inspected % 64 == 0 || inspected == candidates.Count) progress?.Invoke(new("inspecting", inspected, candidates.Count));
            return file;
        }).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No regular files selected");
        string id = Guid.NewGuid().ToString("N"), token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var plan = new CleanupPlan(id, token, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), selected, files);
        Directory.CreateDirectory(PlansDir); string file = System.IO.Path.Combine(PlansDir, id + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(plan, Program.JsonOptions));
        return Summary(plan, 0, 20);
    }
    CleanupPlan Load(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid plan_id");
        return JsonSerializer.Deserialize<CleanupPlan>(File.ReadAllText(System.IO.Path.Combine(PlansDir, id + ".json")), Program.JsonOptions) ?? throw new InvalidDataException("Invalid plan");
    }
    object Summary(CleanupPlan p, int offset, int limit) => new
    {
        plan_id = p.PlanId, confirm_token = p.ConfirmToken, selected_paths = p.SelectedPaths, created_utc = p.CreatedUtc, expires_utc = p.ExpiresUtc,
        mode = "permanent", file_count = p.Files.Length, logical_bytes = p.Files.Sum(f => f.Size),
        estimated_reclaimable_bytes = p.Files.GroupBy(f => (f.Serial, f.Id)).Where(g => g.Count() == g.First().Links).Sum(g => g.First().Allocated),
        hard_link_files = p.Files.Count(f => f.Links > 1), offset, limit,
        items = p.Files.Skip(offset).Take(limit).Select(f => new { path = f.Path, logical_bytes = f.Size, allocated_bytes = f.Allocated, hard_links = f.Links }).ToArray(),
        next_offset = offset + limit < p.Files.Length ? (int?)(offset + limit) : null,
        note = "Preview only. Execution permanently deletes exactly these files, leaves directories/new files intact, and checks identity/size/write-time/link-count. Only execute within explicitly authorized cleanup scope. Hard-link savings are conservatively zero."
    };
    public object Show(string id, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentException("Negative offset"); return Summary(Load(id), offset, Math.Clamp(limit, 1, 100));
    }
    static void CheckSame(PlannedFile f, Native.FileInfoNative i, long allocated, uint expectedLinks)
    {
        if (i.Identity != f.Id || i.Serial != f.Serial || i.Size != f.Size || i.Write != f.WriteTime || i.Links != expectedLinks || allocated != f.Allocated || (i.Attributes & (0x400 | 0x10)) != 0)
            throw new InvalidOperationException($"File changed since preview: {f.Path}; create a new plan");
    }
    public object Execute(string id, string token, Action<CleanupProgress>? progress = null)
    {
        var p = Load(id);
        if (token != p.ConfirmToken) throw new InvalidOperationException("confirm_token does not match the reviewed plan");
        if (DateTime.UtcNow > p.ExpiresUtc) throw new InvalidOperationException("Plan expired; create a new plan");
        string audit = System.IO.Path.Combine(PlansDir, id + ".execution.jsonl");
        // Exclusive create prevents replay or two processes executing the same plan.
        using var log = new FileStream(audit, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(log) { AutoFlush = true };
        void Record(object o) { writer.WriteLine(JsonSerializer.Serialize(o, Program.JsonOptions)); writer.Flush(); log.Flush(true); }
        Record(new { state = "started", plan_id = id, utc = DateTime.UtcNow });
        try
        {
            // Full preflight catches ordinary stale files before any deletion.
            int checkedFiles = 0; progress?.Invoke(new("preflight", 0, p.Files.Length));
            foreach (var f in p.Files)
            {
                var live = Inspect(f.Path); if (live != f) throw new InvalidOperationException($"File changed since preview: {f.Path}");
                checkedFiles++; if (checkedFiles % 64 == 0 || checkedFiles == p.Files.Length) progress?.Invoke(new("preflight", checkedFiles, p.Files.Length));
            }
        }
        catch (Exception ex) { Record(new { state = "preflight_failed", error = ex.Message }); throw; }
        int deleted = 0; long estimated = 0; var failures = new List<object>();
        var removedLinks = new Dictionary<(uint, ulong), uint>();
        progress?.Invoke(new("deleting", 0, p.Files.Length));
        foreach (var f in p.Files)
        {
            List<SafeFileHandle>? ancestors = null;
            try
            {
                CheckAllowed(f.Path); ancestors = LockAncestors(f.Path);
                using var h = Native.Open(Native.LongPath(f.Path), 0x10000 | 0x80, 1); // DELETE | FILE_READ_ATTRIBUTES, deny writes and renames
                CheckHandlePath(f.Path, h);
                var i = Native.Info(h); var identity = (f.Serial, f.Id);
                uint removed = removedLinks.GetValueOrDefault(identity);
                CheckSame(f, i, Native.Allocation(h), f.Links - removed);
                Record(new { state = "delete_intent", path = f.Path, file_id = f.Id.ToString("x16") });
                if (!Native.SetFileInformationByHandle(h, 21, BitConverter.GetBytes(0x1u | 0x2u | 0x10u), 4)) // FileDispositionInfoEx: DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
                {
                    int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (error is not (87 or 50 or 1)) throw new Win32Exception(error);
                    if (!Native.SetFileInformationByHandle(h, 4, BitConverter.GetBytes(1), 4)) throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                h.Dispose(); deleted++; removedLinks[identity] = removed + 1; if (i.Links == 1) estimated += f.Allocated;
                Record(new { state = "deleted", path = f.Path });
                if (deleted % 32 == 0 || deleted == p.Files.Length) progress?.Invoke(new("deleting", deleted, p.Files.Length, deleted));
            }
            catch (Exception ex) { failures.Add(new { path = f.Path, error = ex.Message }); Record(new { state = "failed", path = f.Path, error = ex.Message }); break; }
            finally { if (ancestors != null) foreach (var h in ancestors) h.Dispose(); }
        }
        Record(new { state = "finished", deleted, failed = failures.Count });
        progress?.Invoke(new("finished", deleted + failures.Count, p.Files.Length, deleted));
        string next = service.AdjustAfterCleanup(p.Files.Take(deleted));
        return new { plan_id = id, deleted_files = deleted, estimated_freed_bytes = estimated, failures, stopped_on_first_error = failures.Count > 0, unattempted_files = p.Files.Length - deleted - failures.Count, audit_path = audit, next_action = next };
    }
    public object ExecutionStatus(string id)
    {
        var plan = Load(id); string audit = System.IO.Path.Combine(PlansDir, id + ".execution.jsonl");
        if (!File.Exists(audit)) return new { plan_id = id, state = "not_started", file_count = plan.Files.Length };
        int deleted = 0; string state = "interrupted_or_running"; string? error = null;
        using var stream = new FileStream(audit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line); var row = doc.RootElement;
                string? eventState = row.GetProperty("state").GetString();
                if (eventState == "deleted") deleted++;
                if (eventState is "failed" or "preflight_failed") { state = "failed"; error = row.GetProperty("error").GetString(); }
                if (eventState == "finished") state = row.GetProperty("failed").GetInt32() == 0 ? "complete" : "failed";
            }
            catch (JsonException) { /* A concurrent writer may still be completing the last line. */ }
        }
        return new { plan_id = id, state, deleted_files = deleted, file_count = plan.Files.Length, error, audit_path = audit, note = "An unfinished audit cannot distinguish a running process from interruption. Never replay an attempted plan; inspect remaining files and create a new plan." };
    }
}
