using System.Collections.Concurrent;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Tool">The tool Claude Code wants to use (Bash, Edit, Write, WebFetch, an MCP tool…).</param>
/// <param name="Detail">What it wants to do: the command, the file path, the URL…</param>
/// <param name="Kind">"permission" (Allow / Deny) or "question" (Claude is asking something; it is answered in the terminal).</param>
public sealed record Approval(string Id, string Source, string Session, string Project, string Tool, string Detail,
    string? Description, long CreatedAt, long ExpiresAt, string Kind = "permission", IReadOnlyList<ApprovalQuestion>? Questions = null);

/// <summary>One AskUserQuestion question with its choices, so the phone can answer it.</summary>
public sealed record ApprovalQuestion(string Question, string? Header, bool MultiSelect, IReadOnlyList<ApprovalOption> Options);
public sealed record ApprovalOption(string Label, string? Description);

/// <summary>
/// Permission prompts waiting for an answer from the phone. A Claude Code hook posts the request and waits; the
/// phone shows Allow / Deny and posts the decision back. If no phone is around, or nobody answers in time, the request
/// ends without a decision and Claude Code asks in the terminal as usual.
/// </summary>
public sealed class ApprovalStore(PushStore events, ILogger<ApprovalStore> log)
{
    private readonly TimeProvider _clock = TimeProvider.System;
    internal ApprovalStore(PushStore events, ILogger<ApprovalStore> log, TimeProvider clock) : this(events, log) => _clock = clock;

    private sealed record Pending(Approval Approval, TaskCompletionSource<string?> Decision)
    {
        /// <summary>The phone's answers to a question (question text → chosen label, several joined by ", ").</summary>
        public IReadOnlyDictionary<string, string>? Answers { get; set; }
    }
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    /// <summary>When the phone last polled the agent; approvals are only offered to a phone that is actually there.</summary>
    public static long PhoneLastSeen;

    public static bool PhoneConnected => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Interlocked.Read(ref PhoneLastSeen) < 5000;

    /// <summary>The requests the phone can still answer; one past its expiry is left out even before it is cleaned up.</summary>
    public IReadOnlyList<Approval> Snapshot()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        return _pending.Values.Select(p => p.Approval).Where(a => a.ExpiresAt > now).OrderBy(a => a.CreatedAt).ToList();
    }

    /// <summary>Shows the request on the phone and waits for "allow" / "deny"; null = no answer (fall back to the terminal).</summary>
    public async Task<string?> AskAsync(Approval a, CancellationToken aborted) => (await AskForAnswersAsync(a, aborted)).Decision;

    /// <summary>Like <see cref="AskAsync"/>, plus the answers when the phone answered a question ("answer").</summary>
    public async Task<(string? Decision, IReadOnlyDictionary<string, string>? Answers)> AskForAnswersAsync(Approval a, CancellationToken aborted)
    {
        if (!PhoneConnected || a.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return (null, null);
        var pending = new Pending(a, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[a.Id] = pending;
        events.Emit("approval", JsonSerializer.SerializeToElement(new { id = a.Id }));
        log.LogInformation("approval {Id}: {Tool} in {Project} sent to the phone", a.Id, a.Tool, a.Project);
        try
        {
            // exactly the time left: Decide refuses an answer from the expiry on, so waiting longer only delays the terminal
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, a.ExpiresAt - _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            // The hook process goes away when the user answers in the terminal; that cancels the request here.
            await Task.WhenAny(pending.Decision.Task, Task.Delay(timeout, aborted));
            // The decision task is the one outcome: a timeout or cancel closes it with null, so a Decide that loses
            // the race reports false; a Decide that won before this line is returned even though the wait ended.
            pending.Decision.TrySetResult(null);
            var decision = await pending.Decision.Task;
            return (decision, decision == "answer" ? pending.Answers : null);
        }
        catch (OperationCanceledException) { pending.Decision.TrySetResult(null); return (null, null); }
        finally
        {
            _pending.TryRemove(a.Id, out _);
            events.Emit("approval-done", JsonSerializer.SerializeToElement(new { id = a.Id }));
        }
    }

    public bool Decide(string id, string decision, IReadOnlyDictionary<string, string>? answers = null)
    {
        if (decision is not ("allow" or "deny" or "skip" or "answer") || !_pending.TryGetValue(id, out var p)) return false;
        if (p.Approval.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return false;
        if (decision == "answer")
        {
            // only for a question, and only with a valid choice for every question asked
            if (p.Approval.Kind != "question" || p.Approval.Questions is not { Count: > 0 } qs || !ValidAnswers(qs, answers)) return false;
            p.Answers = answers;
        }
        // accepted only if it wins against the timeout and the cancel (see AskAsync), and only then logged as such
        if (!p.Decision.TrySetResult(decision)) return false;
        log.LogInformation("approval {Id}: {Decision} from the phone", id, decision);
        return true;
    }

    /// <summary>Every question answered with labels that exist; one label unless the question allows several.</summary>
    internal static bool ValidAnswers(IReadOnlyList<ApprovalQuestion> qs, IReadOnlyDictionary<string, string>? answers)
    {
        if (answers is null || answers.Count != qs.Count) return false;
        foreach (var q in qs)
        {
            if (!answers.TryGetValue(q.Question, out var v) || string.IsNullOrWhiteSpace(v)) return false;
            var labels = q.Options.Select(o => o.Label).ToList();
            // a label may itself contain ", " ("Yes, keep it"): a whole label is one pick, never split
            if (labels.Contains(v)) continue;
            if (!q.MultiSelect || SplitLabels(v, labels) is not { } picked || picked.Distinct().Count() != picked.Count) return false;
        }
        return true;
    }

    /// <summary>The labels a multi-choice answer is made of, joined by ", ", or null when it is not made of them only.</summary>
    private static List<string>? SplitLabels(string v, IReadOnlyList<string> labels)
    {
        if (v.Length == 0) return [];
        foreach (var l in labels.OrderByDescending(l => l.Length))
        {
            if (v == l) return [l];
            if (v.StartsWith(l + ", ", StringComparison.Ordinal) && SplitLabels(v[(l.Length + 2)..], labels) is { } rest) return [l, .. rest];
        }
        return null;
    }

    public static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}
