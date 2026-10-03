using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Background jobs: a queue of tool calls the plugin runs one after another, for as long as they take (gathering for hours, a whole
/// crafting list, ...), independent of any MCP client connection. Agents start jobs, poll them, and fix them up when a step fails:
/// a failed or stopped step puts the job into "pending" (waiting for the agent: retry, skip, change steps or cancel).
/// Later steps can use earlier results: a string argument "{{stepId.path.to.value}}" is replaced by that value from the result of the
/// step with that id. Jobs are kept in pluginConfigs/XivMcp/jobs.json; after a plugin reload a running job comes back paused.
/// Only one job step runs at a time (the game has one character).
/// </summary>
internal sealed class JobManager : IDisposable, ICache
{
    public enum JobState { Queued, Running, Paused, Pending, Completed, Failed, Cancelled }
    public enum StepState { Queued, Running, Done, Failed, Interrupted, Skipped }

    public sealed class Step
    {
        public string Id { get; set; } = "";
        public string Tool { get; set; } = "";
        public JsonObject Args { get; set; } = [];
        public string? Note { get; set; }
        public StepState State { get; set; } = StepState.Queued;
        public JsonNode? Result { get; set; }
        public string? Error { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class Job
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Client { get; set; }
        public JobState State { get; set; } = JobState.Queued;
        public string? Reason { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<Step> Steps { get; set; } = [];
        public List<string> Log { get; set; } = [];

        public Step? Current => Steps.FirstOrDefault(s => s.State is StepState.Queued or StepState.Running or StepState.Interrupted or StepState.Failed);
        public bool Finished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
    }

    /// <summary>Tools that may not be steps (job control itself).</summary>
    public static readonly HashSet<string> ControlTools = ["start_job", "list_jobs", "get_job", "update_job", "pause_job", "resume_job", "cancel_job"];

    private readonly Dictionary<string, McpTool> tools;
    private readonly string file = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "jobs.json");
    private readonly List<Job> jobs;
    private readonly Lock sync = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim wake = new(0);
    private CancellationTokenSource? stepCts;
    private string? runningJobId;
    private long version;

    public static JobManager? Instance { get; private set; }

    public JobManager(IEnumerable<McpTool> tools)
    {
        this.tools = tools.ToDictionary(t => t.Name);
        try { jobs = File.Exists(file) ? JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(file), JsonOptions) ?? [] : []; }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] jobs.json unreadable, starting empty: {ex.Message}"); jobs = []; }
        // Whatever was running when the plugin stopped resumes only when someone says so.
        foreach (var j in jobs.Where(j => j.State is JobState.Running or JobState.Queued))
        {
            j.State = JobState.Paused;
            j.Reason = "Paused because the plugin was reloaded; resume_job to continue.";
            foreach (var s in j.Steps.Where(s => s.State == StepState.Running)) s.State = StepState.Interrupted;
        }
        Instance = this;
        _ = Task.Run(RunLoop);
    }

    public void Dispose()
    {
        shutdown.Cancel();
        stepCts?.Cancel();
        lock (sync)
        {
            foreach (var j in jobs.Where(j => j.State == JobState.Running))
            {
                j.State = JobState.Paused;
                j.Reason = "Paused because the plugin was unloaded; resume_job to continue.";
            }
            Save();
        }
        Instance = null;
    }

    /// <summary>True while a job step is executing (other state-changing tool calls are refused then).</summary>
    public bool StepRunning
    {
        get { lock (sync) return runningJobId is not null; }
    }

    public string? RunningJobName
    {
        get { lock (sync) return runningJobId is { } id ? jobs.FirstOrDefault(j => j.Id == id)?.Name : null; }
    }

    // ------------------------------------------------------------------ control

    public Job Start(string name, List<Step> steps, string? client)
    {
        Validate(steps);
        var job = new Job { Id = NewId(), Name = name, Client = client, Steps = steps };
        lock (sync)
        {
            jobs.Add(job);
            AddLog(job, $"Created with {steps.Count} step(s).");
            Changed();
        }
        wake.Release();
        return job;
    }

    private List<Job>? snapshot;
    private long snapshotVersion = -1;

    /// <summary>Copies of all jobs (newest first), safe to read while the runner works; rebuilt only when something changed.</summary>
    public List<Job> All()
    {
        lock (sync)
        {
            var v = Interlocked.Read(ref version);
            if (snapshot is null || snapshotVersion != v)
            {
                snapshot = JsonSerializer.Deserialize<List<Job>>(JsonSerializer.Serialize(jobs.OrderByDescending(j => j.CreatedUtc).ToList(), JsonOptions), JsonOptions) ?? [];
                snapshotVersion = v;
            }
            return snapshot;
        }
    }

    public Job Get(string id)
    {
        string jobId;
        lock (sync) jobId = Find(id).Id; // throws if unknown
        return All().First(j => j.Id == jobId);
    }

    public void Pause(string id, string reason = "Paused.")
    {
        lock (sync)
        {
            var job = Find(id);
            if (job.Finished) throw new ToolException($"Job {job.Id} has already {job.State.ToString().ToLowerInvariant()}.");
            job.State = JobState.Paused;
            job.Reason = reason;
            AddLog(job, reason);
            if (runningJobId == job.Id) stepCts?.Cancel();
            Changed();
        }
    }

    public void Resume(string id)
    {
        lock (sync)
        {
            var job = Find(id);
            if (job.Finished) throw new ToolException($"Job {job.Id} has already {job.State.ToString().ToLowerInvariant()}.");
            if (job.Current is { State: StepState.Failed }) throw new ToolException("The current step failed; use update_job with action retry or skip (or change the steps) first.");
            job.State = JobState.Queued;
            job.Reason = null;
            AddLog(job, "Resumed.");
            Changed();
        }
        wake.Release();
    }

    public void Cancel(string id)
    {
        lock (sync)
        {
            var job = Find(id);
            if (job.Finished) return;
            job.State = JobState.Cancelled;
            job.Reason = "Cancelled.";
            AddLog(job, "Cancelled.");
            if (runningJobId == job.Id) stepCts?.Cancel();
            Changed();
        }
    }

    /// <summary>Changes a job that is not running a step right now: retry / skip the current step, append or replace the remaining steps.</summary>
    public Job Update(string id, string action, List<Step>? steps, bool resume)
    {
        lock (sync)
        {
            var job = Find(id);
            if (job.Finished) throw new ToolException($"Job {job.Id} has already {job.State.ToString().ToLowerInvariant()}.");
            if (runningJobId == job.Id) throw new ToolException("A step of this job is running; pause_job first.");
            var current = job.Current;
            switch (action)
            {
                case "retry":
                    if (current is null) throw new ToolException("No step to retry.");
                    current.State = StepState.Queued; current.Error = null;
                    AddLog(job, $"Step {current.Id} ({current.Tool}) will be retried.");
                    break;
                case "skip":
                    if (current is null) throw new ToolException("No step to skip.");
                    current.State = StepState.Skipped; current.FinishedUtc = DateTime.UtcNow;
                    AddLog(job, $"Skipped step {current.Id} ({current.Tool}).");
                    break;
                case "append":
                    Validate(steps ?? throw new ToolException("append needs 'steps'."), job);
                    job.Steps.AddRange(steps);
                    AddLog(job, $"Appended {steps.Count} step(s).");
                    break;
                case "replace_remaining":
                    Validate(steps ?? throw new ToolException("replace_remaining needs 'steps'."), job);
                    job.Steps.RemoveAll(s => s.State is StepState.Queued or StepState.Failed or StepState.Interrupted);
                    job.Steps.AddRange(steps);
                    AddLog(job, $"Replaced the remaining steps with {steps.Count} step(s).");
                    break;
                default:
                    throw new ToolException("action must be retry, skip, append or replace_remaining.");
            }
            if (resume) { job.State = JobState.Queued; job.Reason = null; }
            else if (job.State != JobState.Paused) { job.State = JobState.Pending; job.Reason ??= "Changed; resume_job to continue."; }
            if (job.Current is null && job.State == JobState.Queued) Complete(job);
            Changed();
            if (resume) wake.Release();
            return job;
        }
    }

    // ------------------------------------------------------------------ running

    private async Task RunLoop()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { await wake.WaitAsync(TimeSpan.FromSeconds(5), shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            Job? job;
            Step? step;
            JsonObject args;
            lock (sync)
            {
                job = jobs.Where(j => j.State is JobState.Queued or JobState.Running).OrderBy(j => j.CreatedUtc).FirstOrDefault();
                if (job is null) continue;
                step = job.Current;
                if (step is null) { Complete(job); Changed(); continue; }
                try { args = Resolve(job, step.Args); }
                catch (ToolException ex) { Fail(job, step, ex.Message); Changed(); continue; }
                job.State = JobState.Running;
                job.Reason = null;
                step.State = StepState.Running;
                step.StartedUtc = DateTime.UtcNow;
                step.Attempts++;
                runningJobId = job.Id;
                stepCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                AddLog(job, $"Step {step.Id}: {step.Tool} started.");
                Changed();
            }

            object? result = null;
            string? error = null;
            var interrupted = false;
            try
            {
                result = await tools[step.Tool].Handler(new ToolArgs(args), stepCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { interrupted = true; }
            catch (ToolException ex) { error = ex.Message; }
            catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; Svc.Log.Error(ex, $"[MCP] Job step {step.Tool} failed"); }

            lock (sync)
            {
                runningJobId = null;
                step.FinishedUtc = DateTime.UtcNow;
                if (interrupted)
                {
                    step.State = StepState.Interrupted;
                    AddLog(job, $"Step {step.Id} interrupted ({job.State.ToString().ToLowerInvariant()}).");
                    if (job.State == JobState.Running) { job.State = JobState.Paused; job.Reason = "Interrupted."; }
                }
                else if (error is not null)
                {
                    Fail(job, step, error);
                }
                else
                {
                    step.State = StepState.Done;
                    step.Error = null;
                    step.Result = ToNode(result);
                    AddLog(job, $"Step {step.Id}: {step.Tool} done.");
                    if (job.Current is null) Complete(job);
                    else if (job.State == JobState.Running) job.State = JobState.Queued;
                }
                Changed();
            }
            wake.Release();
        }
    }

    private void Fail(Job job, Step step, string error)
    {
        step.State = StepState.Failed;
        step.Error = error;
        job.State = JobState.Pending;
        job.Reason = $"Step {step.Id} ({step.Tool}) failed: {error} — waiting for retry, skip, new steps or cancel (update_job).";
        AddLog(job, $"Step {step.Id} failed: {error}");
        Svc.Log.Information($"[MCP] Job '{job.Name}' pending: step {step.Id} ({step.Tool}) failed");
    }

    private void Complete(Job job)
    {
        job.State = JobState.Completed;
        job.Reason = null;
        AddLog(job, "Completed.");
    }

    /// <summary>Replaces "{{stepId.path}}" string arguments with values from earlier step results.</summary>
    private static JsonObject Resolve(Job job, JsonObject args)
    {
        var copy = (JsonObject)args.DeepClone();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList())
                    {
                        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && Placeholder.Match(s) is { Success: true } m)
                            o[key] = Lookup(job, m.Groups[1].Value, m.Groups[2].Value);
                        else Walk(o[key]);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Placeholder.Match(s) is { Success: true } m)
                            a[i] = Lookup(job, m.Groups[1].Value, m.Groups[2].Value);
                        else Walk(a[i]);
                    }
                    break;
            }
        }
        Walk(copy);
        return copy;
    }

    private static readonly Regex Placeholder = new(@"^\{\{([A-Za-z0-9_-]+)\.([^}]+)\}\}$");

    private static JsonNode? Lookup(Job job, string stepId, string path)
    {
        var step = job.Steps.FirstOrDefault(s => s.Id == stepId) ?? throw new ToolException($"No step '{stepId}' for {{{{{stepId}.{path}}}}}.");
        if (step.State != StepState.Done || step.Result is null) throw new ToolException($"Step '{stepId}' has no result yet for {{{{{stepId}.{path}}}}}.");
        JsonNode? node = step.Result;
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o.FirstOrDefault(kv => kv.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) throw new ToolException($"Step '{stepId}' result has no '{path}'.");
        }
        return node.DeepClone();
    }

    private void Validate(List<Step> steps, Job? job = null)
    {
        if (steps.Count == 0) throw new ToolException("A job needs at least one step.");
        var used = new HashSet<string>((job?.Steps ?? []).Select(s => s.Id));
        var n = used.Count;
        foreach (var s in steps)
        {
            if (!tools.ContainsKey(s.Tool)) throw new ToolException($"Unknown tool '{s.Tool}' in a step.");
            if (ControlTools.Contains(s.Tool)) throw new ToolException($"'{s.Tool}' can't be a job step.");
            if (!tools[s.Tool].IsAvailable) throw new ToolException($"'{s.Tool}' is not available right now (the plugin it needs is not loaded).");
            if (string.IsNullOrWhiteSpace(s.Id)) s.Id = $"s{++n}";
            if (!used.Add(s.Id)) throw new ToolException($"Duplicate step id '{s.Id}'.");
        }
    }

    private Job Find(string id) =>
        jobs.FirstOrDefault(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? jobs.Where(j => j.Name.Equals(id, StringComparison.OrdinalIgnoreCase) && !j.Finished).OrderByDescending(j => j.CreatedUtc).FirstOrDefault()
        ?? jobs.Where(j => j.Name.Equals(id, StringComparison.OrdinalIgnoreCase)).OrderByDescending(j => j.CreatedUtc).FirstOrDefault()
        ?? throw new ToolException($"No job '{id}'. Use list_jobs.");

    private static void AddLog(Job job, string line)
    {
        job.Log.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");
        if (job.Log.Count > 200) job.Log.RemoveRange(0, job.Log.Count - 200);
        job.UpdatedUtc = DateTime.UtcNow;
    }

    private void Changed()
    {
        // Keep finished jobs for a while, then drop them.
        jobs.RemoveAll(j => j.Finished && DateTime.UtcNow - j.UpdatedUtc > TimeSpan.FromDays(7));
        Save();
        Interlocked.Increment(ref version);
        Updated?.Invoke(this);
    }

    private void Save()
    {
        try { File.WriteAllText(file, JsonSerializer.Serialize(jobs, JsonOptions)); }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not save jobs: {ex.Message}"); }
    }

    private static JsonNode? ToNode(object? result)
    {
        if (result is null) return null;
        if (result is ToolResultWithImages withImages) result = withImages.Data;
        if (result is string s) return JsonValue.Create(s);
        var node = JsonSerializer.SerializeToNode(result, JsonOptions);
        // Keep results compact in the job file.
        return node is not null && node.ToJsonString().Length > 20000 ? JsonValue.Create(node.ToJsonString()[..20000] + "…") : node;
    }

    private static string NewId() => Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------ ICache (xiv://cache/jobs, so clients can subscribe)

    public string Id => "jobs";
    public string Title => "Background jobs";
    public string Description => "Jobs started by agents: their steps, state (queued, running, paused, pending, completed, failed, cancelled) and log.";
    public long Version => Interlocked.Read(ref version);
    public event Action<ICache>? Updated;

    public IEnumerable<CacheEntryStatus> Entries()
    {
        lock (sync)
            return jobs.Where(j => !j.Finished)
                .Select(j => new CacheEntryStatus("jobs", $"{j.Name} ({j.State.ToString().ToLowerInvariant()})", j.UpdatedUtc, "updates live", true)).ToList();
    }

    public object Read()
    {
        lock (sync) return jobs.Select(Describe).ToList();
    }

    public static object Describe(Job j) => new
    {
        id = j.Id,
        name = j.Name,
        state = j.State.ToString().ToLowerInvariant(),
        reason = j.Reason,
        progress = $"{j.Steps.Count(s => s.State is StepState.Done or StepState.Skipped)}/{j.Steps.Count}",
        currentStep = j.Current is { } c ? new { c.Id, c.Tool, state = c.State.ToString().ToLowerInvariant(), since = c.StartedUtc, c.Error } : null,
        created = j.CreatedUtc,
        updated = j.UpdatedUtc,
    };
}
