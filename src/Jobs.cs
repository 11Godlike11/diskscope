using System.Collections.Concurrent;
using System.Text.Json;

namespace DiskScope;

record JobResult(object Compact, object Verbose);

sealed class BackgroundJob
{
    public readonly string Id = Guid.NewGuid().ToString("N");
    public readonly string Kind;
    public readonly DateTime CreatedUtc = DateTime.UtcNow;
    public volatile string State = "running";
    public volatile CleanupProgress Progress = new("starting", 0, 0);
    public object? Result;
    public string? Error;
    public Task? Task;
    public BackgroundJob(string kind) => Kind = kind;
}

sealed class Jobs
{
    readonly ConcurrentDictionary<string, BackgroundJob> jobs = new();
    readonly object gate = new();
    public object Start(string kind, Func<Action<CleanupProgress>, object> operation, bool verbose = false)
    {
        lock (gate)
        {
            if (jobs.Values.Any(j => j.State == "running")) throw new InvalidOperationException("An operation is already running; poll disk_job_status before starting another");
            foreach (var old in jobs.Values.Where(j => j.State != "running").OrderByDescending(j => j.CreatedUtc).Skip(31)) jobs.TryRemove(old.Id, out _);
            var job = new BackgroundJob(kind); jobs[job.Id] = job;
            job.Task = Task.Run(() =>
            {
                try
                {
                    job.Result = operation(progress => job.Progress = progress);
                    var result = JsonSerializer.SerializeToElement(job.Result is JobResult views ? views.Compact : job.Result, Program.JsonOptions);
                    if (result.TryGetProperty("failures", out var failures) && failures.GetArrayLength() > 0)
                    { job.Error = "Cleanup stopped on a file error; inspect result and audit before proceeding"; job.State = "failed"; }
                    else job.State = "complete";
                }
                catch (Exception ex) { job.Error = ex.Message; job.State = "failed"; }
            });
            return new { job_id = job.Id, kind, state = "running", next_action = verbose ? "Poll disk_job_status with job_id. Keep this server alive until completion. A preview job must complete and be reviewed before executing its plan." : "Use disk_job_status with wait_seconds: 30." };
        }
    }
    public object Status(string id, bool verbose = false)
    {
        if (!jobs.TryGetValue(id, out var job)) throw new ArgumentException("Unknown job_id in this server process");
        return Status(job, verbose);
    }
    static object Status(BackgroundJob job, bool verbose)
    {
        string state = job.State;
        return new { job_id = job.Id, kind = job.Kind, state, created_utc = job.CreatedUtc, progress = job.Progress,
            error = state == "running" ? null : job.Error, result = state == "running" ? null : job.Result is JobResult views ? verbose ? views.Verbose : views.Compact : job.Result };
    }
    internal static void ValidateWait(int seconds)
    {
        if (seconds < 0 || seconds > 30) throw new ArgumentException("wait_seconds must be 0..30");
    }
    public async Task<object> WaitStatus(string id, int seconds, Action<CleanupProgress>? progress = null, bool verbose = false)
    {
        ValidateWait(seconds);
        BackgroundJob job;
        lock (gate) { if (!jobs.TryGetValue(id, out job!)) throw new ArgumentException("Unknown job_id in this server process"); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (seconds > 0 && job.State == "running") progress?.Invoke(job.Progress);
        while (job.State == "running" && watch.Elapsed.TotalSeconds < seconds)
        {
            var remaining = TimeSpan.FromSeconds(seconds) - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.WhenAny(job.Task!, Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1)));
            if (job.State == "running" && watch.Elapsed.TotalSeconds < seconds) progress?.Invoke(job.Progress);
        }
        return Status(job, verbose);
    }
    public void Drain()
    {
        // Closing stdin is not a request to abandon a deletion already accepted by this process.
        Task[] tasks;
        lock (gate) tasks = jobs.Values.Select(j => j.Task).OfType<Task>().ToArray();
        Task.WaitAll(tasks);
    }
}
