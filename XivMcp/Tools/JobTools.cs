using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Starting and controlling background jobs (queues of tool calls run by the plugin; see <see cref="JobManager"/>).</summary>
internal static class JobTools
{
    private const string StepsSchema = """
        "steps": {
          "type": "array",
          "description": "Tool calls in order: [{ \"id\": optional step id, \"tool\": tool name, \"args\": { ... }, \"note\": optional }]. A string argument \"{{stepId.path}}\" is replaced by that value from the result of an earlier step (e.g. \"{{plan.list.id}}\").",
          "items": {
            "type": "object",
            "properties": { "id": { "type": "string" }, "tool": { "type": "string" }, "args": { "type": "object" }, "note": { "type": "string" } },
            "required": ["tool"]
          }
        }
        """;

    public static IEnumerable<McpTool> Create(Func<JobManager> manager, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "start_job",
            Description = "Starts a background job: a queue of tool calls XIV MCP runs one after another inside the game, however long they take " +
                          "(e.g. gather_until for hours, then prepare_craft_plan, then run_crafting_list), independent of this conversation or " +
                          "connection. Poll it with get_job. If a step fails or is stopped, the job becomes 'pending' and waits: fix it with " +
                          "update_job (retry, skip, append or replace the remaining steps) and resume_job. While a step runs, other state-changing " +
                          "tool calls are refused so they can't collide with the job (reading is fine). Each step still needs its own permission.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Short name shown in game (e.g. \"Tacos scrip farm\")." },
                    {{StepsSchema}}
                  },
                  "required": ["name", "steps"]
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                var name = args.String("name") ?? throw new ToolException("'name' is required.");
                var job = manager().Start(name, ParseSteps(args.Node("steps")), client());
                return Task.FromResult<object?>(new { started = JobManager.Describe(job), poll = $"get_job id={job.Id}" });
            },
        };

        yield return new McpTool
        {
            Name = "list_jobs",
            Description = "Background jobs (newest first) with state — queued, running, paused, pending (waiting for the agent after a failed or " +
                          "stopped step), completed, failed, cancelled — progress and the current step. Finished jobs are kept for 7 days.",
            InputSchema = """{ "type": "object", "properties": { "include_finished": { "type": "boolean", "description": "Default true." } } }""",
            Handler = (args, _) =>
            {
                var all = args.Bool("include_finished", true);
                return Task.FromResult<object?>(manager().All().Where(j => all || !j.Finished).Select(JobManager.Describe).ToList());
            },
        };

        yield return new McpTool
        {
            Name = "get_job",
            Description = "One background job in detail: every step with its state, arguments, result (or error), timing and attempts, plus the job log.",
            InputSchema = """{ "type": "object", "properties": { "id": { "type": "string", "description": "Job id (or name)." }, "log_lines": { "type": "integer", "description": "Default 30." } }, "required": ["id"] }""",
            Handler = (args, _) =>
            {
                var job = manager().Get(args.String("id") ?? throw new ToolException("'id' is required."));
                var lines = args.Int("log_lines", 30, 0, 200);
                return Task.FromResult<object?>(new
                {
                    job = JobManager.Describe(job),
                    steps = job.Steps.Select(s => new
                    {
                        s.Id, s.Tool, s.Note,
                        state = s.State.ToString().ToLowerInvariant(),
                        args = s.Args,
                        result = s.Result,
                        error = s.Error,
                        started = s.StartedUtc,
                        finished = s.FinishedUtc,
                        attempts = s.Attempts,
                    }).ToList(),
                    log = job.Log.TakeLast(lines).ToList(),
                });
            },
        };

        yield return new McpTool
        {
            Name = "update_job",
            Description = "Changes a job that is pending or paused: action=retry (run the current step again), skip (move past it), append (add " +
                          "steps at the end) or replace_remaining (swap all steps that haven't run yet). resume=true continues right away.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string" },
                    "action": { "type": "string", "enum": ["retry", "skip", "append", "replace_remaining"] },
                    {{StepsSchema}},
                    "resume": { "type": "boolean", "description": "Continue the job after the change (default false)." }
                  },
                  "required": ["id", "action"]
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                var steps = args.Node("steps") is null ? null : ParseSteps(args.Node("steps"));
                var job = manager().Update(args.String("id") ?? throw new ToolException("'id' is required."), args.String("action") ?? "", steps, args.Bool("resume", false));
                return Task.FromResult<object?>(JobManager.Describe(job));
            },
        };

        yield return new McpTool
        {
            Name = "wait",
            Description = "Waits a number of seconds, or until a time (UTC, ISO 8601) — e.g. a job step that waits for ventures or voyages to return " +
                          "before the next step. Stops early when the job is paused or cancelled.",
            InputSchema = """
                { "type": "object", "properties": { "seconds": { "type": "integer" }, "until": { "type": "string", "description": "UTC time, e.g. 2026-10-04T08:00:00Z." } } }
                """,
            Handler = async (args, ct) =>
            {
                TimeSpan delay;
                if (args.String("until") is { } until)
                {
                    if (!DateTime.TryParse(until, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
                        throw new ToolException("'until' must be an ISO 8601 time.");
                    delay = when - DateTime.UtcNow;
                }
                else delay = TimeSpan.FromSeconds(args.Int("seconds", 0, 0, 7 * 24 * 3600));
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                return new { waited = Math.Round(Math.Max(0, delay.TotalSeconds)), now = DateTime.UtcNow };
            },
        };

        foreach (var (toolName, verb, description) in new[]
                 {
                     ("pause_job", "pause", "Pauses a job: the running step is stopped (it runs again on resume) and nothing else starts."),
                     ("resume_job", "resume", "Continues a paused or pending job (after update_job for a failed step)."),
                     ("cancel_job", "cancel", "Cancels a job for good; the running step is stopped."),
                 })
        {
            yield return new McpTool
            {
                Name = toolName,
                Description = description,
                InputSchema = """{ "type": "object", "properties": { "id": { "type": "string", "description": "Job id (or name)." } }, "required": ["id"] }""",
                ReadOnly = false,
                Handler = (args, _) =>
                {
                    var id = args.String("id") ?? throw new ToolException("'id' is required.");
                    var m = manager();
                    switch (verb)
                    {
                        case "pause": m.Pause(id, "Paused by the agent."); break;
                        case "resume": m.Resume(id); break;
                        default: m.Cancel(id); break;
                    }
                    return Task.FromResult<object?>(JobManager.Describe(m.Get(id)));
                },
            };
        }
    }

    private static List<JobManager.Step> ParseSteps(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0) throw new ToolException("'steps' must be a non-empty array.");
        return array.OfType<JsonObject>().Select(s => new JobManager.Step
        {
            Id = s["id"]?.ToString() ?? "",
            Tool = s["tool"]?.ToString() ?? throw new ToolException("Each step needs 'tool'."),
            Args = s["args"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : [],
            Note = s["note"]?.ToString(),
        }).ToList();
    }
}
