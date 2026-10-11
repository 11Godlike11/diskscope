using System.Collections.Concurrent;
using System.Text.Json;

namespace DiskScope;

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
    public object Start(string kind, Func<Action<CleanupProgress>, object> operation)
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
                    var result = JsonSerializer.SerializeToElement(job.Result, Program.JsonOptions);
                    if (result.TryGetProperty("failures", out var failures) && failures.GetArrayLength() > 0)
                    { job.Error = "Cleanup stopped on a file error; inspect result and audit before proceeding"; job.State = "failed"; }
                    else job.State = "complete";
                }
                catch (Exception ex) { job.Error = ex.Message; job.State = "failed"; }
            });
            return new { job_id = job.Id, kind, state = "running", next_action = "Poll disk_job_status with job_id. Keep this server alive until completion. A preview job must complete and be reviewed before executing its plan." };
        }
    }
    public object Status(string id)
    {
        if (!jobs.TryGetValue(id, out var job)) throw new ArgumentException("Unknown job_id in this server process");
        string state = job.State;
        return new { job_id = id, kind = job.Kind, state, created_utc = job.CreatedUtc, progress = job.Progress,
            error = state == "running" ? null : job.Error, result = state == "running" ? null : job.Result };
    }
    public void Drain()
    {
        // Closing stdin is not a request to abandon a deletion already accepted by this process.
        Task[] tasks;
        lock (gate) tasks = jobs.Values.Select(j => j.Task).OfType<Task>().ToArray();
        Task.WaitAll(tasks);
    }
}
