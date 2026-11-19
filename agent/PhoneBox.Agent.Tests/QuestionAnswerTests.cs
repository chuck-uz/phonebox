using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Answering Claude Code's AskUserQuestion from the phone.</summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with the other approval tests
public sealed class QuestionAnswerTests : IDisposable
{
    private const string Input = """
    {"questions":[
      {"question":"Which layout?","header":"Layout","multiSelect":false,
       "options":[{"label":"Timeline","description":"Day as a strip"},{"label":"List"}]},
      {"question":"Which extras?","header":"Extras","multiSelect":true,
       "options":[{"label":"Weather"},{"label":"Clock"},{"label":"Notes"}]}]}
    """;

    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-question-").FullName;
    private readonly PushStore _events;

    public QuestionAnswerTests()
    {
        _events = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        _events.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static IReadOnlyList<ApprovalQuestion> Qs() => ClaudeHook.Questions(JsonDocument.Parse(Input).RootElement)!;

    [Fact]
    public void Questions_keep_text_header_choices_and_multi_select()
    {
        var qs = Qs();
        Assert.Equal(2, qs.Count);
        Assert.Equal("Which layout?", qs[0].Question);
        Assert.Equal("Layout", qs[0].Header);
        Assert.False(qs[0].MultiSelect);
        Assert.Equal(["Timeline", "List"], qs[0].Options.Select(o => o.Label));
        Assert.True(qs[1].MultiSelect);
    }

    [Theory]
    [InlineData("""{"Which layout?":"Timeline","Which extras?":"Weather, Notes"}""", true)]
    [InlineData("""{"Which layout?":"Timeline"}""", false)]                                     // a question left out
    [InlineData("""{"Which layout?":"Timeline, List","Which extras?":"Clock"}""", false)]       // two for a single choice
    [InlineData("""{"Which layout?":"Grid","Which extras?":"Clock"}""", false)]                 // not one of the choices
    [InlineData("""{"Which layout?":"List","Which extras?":"Clock, Clock"}""", false)]          // the same choice twice
    public void Answers_must_pick_existing_choices_for_every_question(string json, bool valid) =>
        Assert.Equal(valid, ApprovalStore.ValidAnswers(Qs(), JsonSerializer.Deserialize<Dictionary<string, string>>(json)));

    [Fact]
    public void A_choice_with_a_comma_in_its_label_is_one_pick()
    {
        IReadOnlyList<ApprovalQuestion> qs =
        [
            new("Keep it?", null, false, [new("Yes, keep it", null), new("No", null)]),
            new("Which?", null, true, [new("A, B", null), new("C", null)]),
        ];
        Assert.True(ApprovalStore.ValidAnswers(qs, new Dictionary<string, string> { ["Keep it?"] = "Yes, keep it", ["Which?"] = "A, B, C" }));
        Assert.True(ApprovalStore.ValidAnswers(qs, new Dictionary<string, string> { ["Keep it?"] = "No", ["Which?"] = "A, B" }));
        Assert.False(ApprovalStore.ValidAnswers(qs, new Dictionary<string, string> { ["Keep it?"] = "Yes, No", ["Which?"] = "C" }));
    }

    [Fact]
    public async Task An_answer_from_the_phone_reaches_the_waiting_hook()
    {
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var a = new Approval(ApprovalStore.NewId(), "Claude Code", "s", "p", "Claude has a question", "", null, now, now + 60_000, "question", Qs());
        var waiting = store.AskForAnswersAsync(a, CancellationToken.None);
        var answers = new Dictionary<string, string> { ["Which layout?"] = "Timeline", ["Which extras?"] = "Weather, Notes" };

        Assert.False(store.Decide(a.Id, "answer", new Dictionary<string, string> { ["Which layout?"] = "Grid" }));
        Assert.True(store.Decide(a.Id, "answer", answers));

        var (decision, got) = await waiting;
        Assert.Equal("answer", decision);
        Assert.Equal(answers, got);
    }

    [Fact]
    public async Task A_permission_prompt_cannot_be_answered_as_a_question()
    {
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var a = new Approval(ApprovalStore.NewId(), "Claude Code", "s", "p", "Run a command", "ls", null, now, now + 60_000);
        var waiting = store.AskAsync(a, CancellationToken.None);
        Assert.False(store.Decide(a.Id, "answer", new Dictionary<string, string> { ["x"] = "y" }));
        Assert.True(store.Decide(a.Id, "deny"));
        Assert.Equal("deny", await waiting);
    }

    [Fact]
    public void The_hook_output_allows_with_the_original_questions_and_the_answers()
    {
        var input = JsonDocument.Parse(Input).RootElement;
        var output = ClaudeHook.AnswerOutput(input, new Dictionary<string, string> { ["Which layout?"] = "List", ["Which extras?"] = "Clock" });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
        var d = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PermissionRequest", d.GetProperty("hookEventName").GetString());
        var dec = d.GetProperty("decision");
        Assert.Equal("allow", dec.GetProperty("behavior").GetString());
        var updated = dec.GetProperty("updatedInput");
        Assert.Equal(2, updated.GetProperty("questions").GetArrayLength());
        Assert.Equal("List", updated.GetProperty("answers").GetProperty("Which layout?").GetString());
        Assert.Equal("Clock", updated.GetProperty("answers").GetProperty("Which extras?").GetString());
    }
}
