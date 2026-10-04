using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XivMcp.Diagnostics;

public enum SelfTestStatus { Passed, Failed, Skipped }

/// <summary>A live check: returns a detail (or null for "ok"), throws <see cref="SelfTestSkip"/> when it doesn't apply, anything else fails it.</summary>
public sealed record SelfTestCheck(string Name, Func<CancellationToken, Task<string?>> Run, TimeSpan? Timeout = null);

public sealed record SelfTestResult(string Name, SelfTestStatus Status, string Detail, TimeSpan Duration);

/// <summary>Thrown by a check that doesn't apply right now (not logged in, plugin not installed, …).</summary>
public sealed class SelfTestSkip(string reason) : Exception(reason);

/// <summary>Runs checks one after another, each with a timeout; a failing check never stops the others.</summary>
public static class SelfTestRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static async Task<List<SelfTestResult>> RunAsync(IEnumerable<SelfTestCheck> checks, CancellationToken ct)
    {
        var results = new List<SelfTestResult>();
        foreach (var check in checks)
        {
            ct.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var timeout = check.Timeout ?? DefaultTimeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            SelfTestResult result;
            try
            {
                var detail = await check.Run(cts.Token).WaitAsync(timeout, ct).ConfigureAwait(false);
                result = new SelfTestResult(check.Name, SelfTestStatus.Passed, string.IsNullOrWhiteSpace(detail) ? "ok" : detail, watch.Elapsed);
            }
            catch (SelfTestSkip skip) { result = new SelfTestResult(check.Name, SelfTestStatus.Skipped, skip.Message, watch.Elapsed); }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                result = new SelfTestResult(check.Name, SelfTestStatus.Failed, $"timed out after {timeout.TotalSeconds:0.#} s", watch.Elapsed);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                result = new SelfTestResult(check.Name, SelfTestStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", watch.Elapsed);
            }
            results.Add(result);
        }
        return results;
    }

    public static string Summarize(IReadOnlyCollection<SelfTestResult> results) =>
        $"{results.Count(r => r.Status == SelfTestStatus.Passed)} passed, {results.Count(r => r.Status == SelfTestStatus.Failed)} failed, " +
        $"{results.Count(r => r.Status == SelfTestStatus.Skipped)} skipped";
}
