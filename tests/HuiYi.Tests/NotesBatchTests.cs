using HuiYi;

namespace HuiYi.Tests;

public class NotesBatchTests
{
    [Fact]
    public async Task LongSingleLineIsCompleteAndRequestsAreBounded()
    {
        var source = string.Concat(Enumerable.Range(0, 10000).Select(i => $"唯一{i:D5}😀"));
        var lines = new List<TranscriptLine> { new() { Id = "a", T = 1234, Src = source } };
        var prompts = new List<string>();
        var active = 0;
        var peak = 0;
        var result = await NotesBatch.Generate(lines, "English", null, NoteTemplates.BuiltIn[0], DateTime.UtcNow,
            async p =>
            {
                var count = Interlocked.Increment(ref active);
                lock (prompts) { peak = Math.Max(peak, count); prompts.Add(p); }
                await Task.Delay(10);
                Interlocked.Decrement(ref active);
                return "{\"summary\":\"Meeting summary\"}";
            });
        Assert.NotNull(result);
        Assert.Equal(2, peak);
        Assert.All(prompts, p => Assert.True(p.Length <= NotesBatch.MaxPromptChars));
        var extracted = prompts.Where(p => !p.Contains("Ordered partial notes (JSON)")).Select(p =>
        {
            var start = p.IndexOf("[L1 ", StringComparison.Ordinal);
            start = p.IndexOf("] ", start, StringComparison.Ordinal) + 2;
            var end = p.IndexOf("\n\nThis meeting ran", start, StringComparison.Ordinal);
            return p[start..end];
        });
        Assert.Equal(source, string.Concat(extracted.OrderBy(x => source.IndexOf(x, StringComparison.Ordinal))));
    }
    [Fact]
    public async Task HierarchyKeepsOrderEvidenceDateAndEveryTemplateSection()
    {
        var lines = Enumerable.Range(0, 18).Select(i => new TranscriptLine
        {
            Id = i.ToString(), Voice = 4, T = 600 + i * 300, Src = $"marker{i:D2} speaker words " + new string('x', 9000)
        }).ToList();
        var template = NoteTemplates.Find("weekly", []);
        var start = new DateTime(2026, 11, 4, 14, 0, 0, DateTimeKind.Local);
        var merges = 0;
        var result = await NotesBatch.Generate(lines, "English", new Dictionary<int, string> { [1] = "Alice" }, template, start,
            async prompt =>
            {
                Assert.True(prompt.Length <= NotesBatch.MaxPromptChars);
                Assert.Contains("2026年11月4日（周三）14:00", prompt);
                foreach (var section in template.Sections) Assert.Contains($"\"{section.Key}\": [", prompt);
                List<int> ids;
                if (prompt.Contains("Ordered partial notes (JSON)"))
                {
                    Interlocked.Increment(ref merges);
                    ids = System.Text.RegularExpressions.Regex.Matches(prompt, "order-(\\d+(?:,\\d+)*)")
                        .SelectMany(m => m.Groups[1].Value.Split(',').Select(int.Parse)).ToList();
                    Assert.Equal(ids.OrderBy(i => i), ids);
                    Assert.Contains("never rebase times", prompt);
                }
                else
                {
                    Assert.Contains("Alice]", prompt);
                    ids = System.Text.RegularExpressions.Regex.Matches(prompt, "marker(\\d+)")
                        .Select(m => int.Parse(m.Groups[1].Value)).ToList();
                    // Finish earlier chunks later to exercise result ordering independently of completion order.
                    await Task.Delay(ids[0] % 2 == 0 ? 20 : 1);
                }
                return "{\"summary\":\"Meeting order-" + string.Join(",", ids) +
                    "\",\"todos\":[{\"text\":\"Deliver report\",\"owner\":\"Alice\",\"due\":\"11月6日\",\"evidence\":[\"L18\"]}]}";
            });
        Assert.True(merges >= 3);
        Assert.Equal("Meeting order-" + string.Join(",", Enumerable.Range(0, 18)), result.Summary);
        Assert.Equal(lines[17].T, result.Todos[0].T);
        Assert.Equal("Alice", result.Todos[0].Owner);
        Assert.Equal(template.Sections.Select(s => s.Key), result.Sections!.Select(s => s.Key));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("language")]
    [InlineData("timeout")]
    public async Task RetriesUnusableResultsAndTransientFailures(string failure)
    {
        var calls = 0;
        var result = await NotesBatch.Generate([new() { Src = "A decision" }], "English", null,
            NoteTemplates.BuiltIn[0], DateTime.UtcNow, _ =>
            {
                if (++calls == 1)
                {
                    if (failure == "timeout") throw new AiException(AiFailure.Timeout);
                    return Task.FromResult(failure switch
                    {
                        "language" => "{\"summary\":\"这是完全中文的会议纪要内容\"}",
                        "oversized" => "{\"summary\":\"" + new string('x', 7000) + "\"}",
                        _ => "invalid"
                    });
                }
                return Task.FromResult("{\"summary\":\"Valid meeting summary\"}");
            });
        Assert.Equal(2, calls);
        Assert.Equal("Valid meeting summary", result.Summary);
    }

    [Theory]
    [InlineData(AiFailure.InvalidKey)]
    [InlineData(AiFailure.NoKey)]
    [InlineData(AiFailure.RateLimited)]
    public async Task DoesNotMultiplyAuthenticationOrClientRateLimitRetries(AiFailure failure)
    {
        var calls = 0;
        var exception = await Assert.ThrowsAsync<AiException>(() => NotesBatch.Generate([new() { Src = "Decision" }],
            "English", null, NoteTemplates.BuiltIn[0], DateTime.UtcNow, _ =>
            {
                calls++;
                throw new AiException(failure);
            }));
        Assert.Equal(failure, exception.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedTemplateFailsBeforeSending()
    {
        var template = new NoteTemplate { Focus = new string('x', 25000) };
        var calls = 0;
        await Assert.ThrowsAsync<AiException>(() => NotesBatch.Generate([new() { Src = "Decision" }],
            "English", null, template, DateTime.UtcNow, _ => { calls++; return Task.FromResult("{}"); }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NearLimitRepliesRemainBoundedAtEveryMergeLevel()
    {
        var merges = 0;
        await NotesBatch.Generate([new() { Src = new string('x', 350000) }], "English", null,
            NoteTemplates.BuiltIn[0], DateTime.UtcNow, prompt =>
            {
                Assert.True(prompt.Length <= NotesBatch.MaxPromptChars);
                if (prompt.Contains("Ordered partial notes (JSON)")) Interlocked.Increment(ref merges);
                return Task.FromResult("{\"summary\":\"" + new string('x', 5900) + "\"}");
            });
        Assert.True(merges >= 10);
    }

    [Fact]
    public async Task EveryBuiltInTemplateFitsAndRetainsItsSections()
    {
        foreach (var template in NoteTemplates.BuiltIn)
        {
            var result = await NotesBatch.Generate([new() { Src = "A meeting decision" }], "English", null,
                template, DateTime.UtcNow, prompt =>
                {
                    Assert.True(prompt.Length <= NotesBatch.MaxPromptChars);
                    foreach (var section in template.Sections) Assert.Contains($"\"{section.Key}\": [", prompt);
                    return Task.FromResult("{\"summary\":\"Meeting summary\"}");
                });
            Assert.Equal(template.Sections.Select(s => s.Key), result.Sections!.Select(s => s.Key));
        }
    }

    [Fact]
    public async Task OversizedSpeakerNameFailsBeforeSending()
    {
        var lines = Enumerable.Range(0, 2).Select(i => new TranscriptLine
        {
            Id = i.ToString(), Voice = 1, Src = "Speaker says something"
        }).ToList();
        var calls = 0;
        await Assert.ThrowsAsync<AiException>(() => NotesBatch.Generate(lines, "English",
            new Dictionary<int, string> { [1] = new string('x', 25000) }, NoteTemplates.BuiltIn[0], DateTime.UtcNow,
            _ => { calls++; return Task.FromResult("{}"); }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RootCanReturnLongerNotesWhileIntermediateRepliesRetryWithinBudget()
    {
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var rootCalls = 0;
        var result = await NotesBatch.Generate([new() { Src = string.Concat(Enumerable.Range(0, 12000).Select(i => $"{i:D5}")) }], "English", null,
            NoteTemplates.BuiltIn[0], DateTime.UtcNow, prompt =>
            {
                var attempt = attempts.AddOrUpdate(prompt, 1, (_, count) => count + 1);
                return Task.FromResult("{\"summary\":\"" + new string('x', attempt == 1 ? 7000 : 500) + prompt.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}");
            }, finalChat: prompt =>
            {
                rootCalls++;
                Assert.True(prompt.Length <= NotesBatch.MaxPromptChars);
                return Task.FromResult("{\"summary\":\"" + new string('x', 16000) + "\"}");
            });
        Assert.Equal(16000, result.Summary.Length);
        Assert.Equal(1, rootCalls);
        Assert.All(attempts.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task FailedLeafStopsOtherWorkerIncludingPendingRetries()
    {
        var calls = 0;
        var firstCalls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<AiException>(() => NotesBatch.Generate([new() { Src = new string('x', 300000) }],
            "English", null, NoteTemplates.BuiltIn[0], DateTime.UtcNow, async _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 2)
                {
                    await release.Task;
                    await Task.Delay(50);
                    return "invalid";
                }
                if (Interlocked.Increment(ref firstCalls) == 2) release.TrySetResult();
                return "invalid";
            }));
        Assert.Equal(3, calls);
    }

}

[CollectionDefinition("Notes batch integration", DisableParallelization = true)]
public class NotesBatchCollection { }

[Collection("Notes batch integration")]
public sealed class NotesBatchIntegrationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "huiyi-notes-" + Guid.NewGuid().ToString("N"));
    private readonly string roaming = Paths.Roaming;
    private readonly System.Net.HttpListener listener = new();
    private readonly AiClient client;
    private readonly List<string> prompts = [];
    private Func<string, string> reply = _ => "{\"summary\":\"Meeting summary\"}";
    private readonly List<DateTime> starts = [];
    private readonly List<int> tokenLimits = [];
    private int active;
    private int peak;
    private int responseDelay;
    private const string Good = "{\"summary\":\"Meeting summary\",\"todos\":[{\"text\":\"Deliver report\",\"evidence\":[\"L2\"]}]}";

    public NotesBatchIntegrationTests()
    {
        Paths.Roaming = Path.Combine(folder, "keys");
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var provider = new AiProvider { Preset = "custom", BaseUrl = $"http://localhost:{port}/v1", Model = "test" };
        AIKey.Save(provider.Id, "test-key");
        client = new AiClient { Provider = () => provider };
        client.Refresh();
        _ = Serve();
    }

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            System.Net.HttpListenerContext context;
            try { context = await listener.GetContextAsync(); } catch { return; }
            _ = Respond(context);
        }
    }

    private async Task Respond(System.Net.HttpListenerContext context)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync())!;
        var prompt = body["messages"]![0]!["content"]!.GetValue<string>();
        var current = Interlocked.Increment(ref active);
        lock (prompts)
        {
            prompts.Add(prompt);
            starts.Add(DateTime.UtcNow);
            tokenLimits.Add(body["max_tokens"]!.GetValue<int>());
            peak = Math.Max(peak, current);
        }
        await Task.Delay(responseDelay);
        var content = reply(prompt);
        var response = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content }, finish_reason = "stop" } }
        });
        var bytes = System.Text.Encoding.UTF8.GetBytes(response);
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        Interlocked.Decrement(ref active);
        context.Response.Close();
    }

    public void Dispose()
    {
        listener.Stop();
        Paths.Roaming = roaming;
        // SessionStore's delayed writer may still be draining, keep its temporary folder isolated.
    }

    [Fact]
    public async Task RealClientOverlapsTwoRequestsWhilePreservingStartGate()
    {
        var store = new SessionStore(Path.Combine(folder, "sessions"));
        var session = store.Begin();
        session.Lines = [new() { Id = "a", Src = new string('中', 80000) }];
        var engine = new NotesEngine(client, store) { Language = () => "English" };
        responseDelay = 600;
        reply = _ => Good;
        await engine.Update(session.Id, true);
        Assert.Equal(NotesEngine.State.Idle, engine.Status);
        Assert.Equal(2, peak);
        Assert.True(starts.Count >= 5);
        for (var i = 1; i < starts.Count; i++)
            Assert.True((starts[i] - starts[i - 1]).TotalMilliseconds >= 180);
        Assert.Equal(6000, tokenLimits[^1]);
        Assert.All(tokenLimits.Take(tokenLimits.Count - 1), n => Assert.Equal(2000, n));
        Assert.All(prompts.Take(prompts.Count - 1), p => Assert.Contains("under 4500 characters", p));
    }

    [Fact]
    public async Task OversizedTemplatePreservesOriginalNotesWithoutRequest()
    {
        var store = new SessionStore(Path.Combine(folder, "sessions"));
        var session = store.Begin();
        session.Lines = [new() { Src = new string('x', 30000) }];
        var old = new MeetingNotes { Summary = "Original summary" };
        session.Notes = old;
        var engine = new NotesEngine(client, store)
        {
            Language = () => "English",
            Template = _ => new NoteTemplate { Focus = new string('x', 25000) }
        };
        engine.Show(session.Id);
        await engine.Update(session.Id, true);
        Assert.Equal(NotesEngine.State.Failed, engine.Status);
        Assert.Same(old, engine.Notes);
        Assert.Same(old, store.Get(session.Id)!.Notes);
        Assert.Empty(prompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedChunkKeepsStoredAndShownNotes(bool final)
    {
        var store = new SessionStore(Path.Combine(folder, "sessions"));
        var session = store.Begin();
        session.Lines = [new() { Id = "a", Src = new string('x', 25000) },
            new() { Id = "b", T = 60, Src = "FAIL_THIS_CHUNK" + new string('y', 20000) }];
        var old = new MeetingNotes { Summary = "Original summary", Covered = 0 };
        session.Notes = old;
        var engine = new NotesEngine(client, store) { Language = () => "English" };
        engine.Show(session.Id);
        reply = p => p.Contains("FAIL_THIS_CHUNK") ? "invalid" : Good;
        await engine.Update(session.Id, final);
        Assert.Equal(NotesEngine.State.Failed, engine.Status);
        Assert.Same(old, engine.Notes);
        Assert.Same(old, store.Get(session.Id)!.Notes);
        Assert.Equal(0, old.Covered);
    }

    [Fact]
    public async Task MergeFailureKeepsPreviousNotesAfterSuccessfulChunks()
    {
        var store = new SessionStore(Path.Combine(folder, "sessions"));
        var session = store.Begin();
        session.Lines = [new() { Id = "a", Src = new string('x', 50000) }];
        var old = new MeetingNotes { Summary = "Original summary" };
        session.Notes = old;
        var engine = new NotesEngine(client, store) { Language = () => "English" };
        engine.Show(session.Id);
        reply = p => p.Contains("Ordered partial notes (JSON)") ? "invalid" : Good;
        await engine.Update(session.Id, true);
        Assert.Equal(NotesEngine.State.Failed, engine.Status);
        Assert.Same(old, engine.Notes);
        Assert.Same(old, store.Get(session.Id)!.Notes);
        Assert.Contains(prompts, p => p.Contains("Ordered partial notes (JSON)"));
    }

    [Fact]
    public async Task OversizedLiveAccumulationRebuildsAllLinesWithinBudget()
    {
        var store = new SessionStore(Path.Combine(folder, "sessions"));
        var session = store.Begin();
        session.Lines = [new() { Id = "a", Src = "Earlier decision", T = 300 }, new() { Id = "b", Src = "New decision", T = 900 }];
        session.Notes = new MeetingNotes { Summary = new string('x', 40000), Covered = 1 };
        var engine = new NotesEngine(client, store) { Language = () => "English" };
        engine.Show(session.Id);
        reply = _ => Good;
        await engine.Update(session.Id, false);
        Assert.Equal(NotesEngine.State.Idle, engine.Status);
        Assert.All(prompts, p => Assert.True(p.Length <= NotesBatch.MaxPromptChars));
        Assert.Contains("Earlier decision", prompts[0]);
        Assert.Equal(900, engine.Notes.Todos[0].T);
        Assert.Equal(2, engine.Notes.Covered);
        Assert.False(engine.Notes.Final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortMeetingsKeepOriginalSingleRequest(bool final)
    {
        var store = new SessionStore(Path.Combine(folder, "sessions"));
        var session = store.Begin();
        session.Lines = [new() { Id = "a", Src = "Earlier", T = 30 }, new() { Id = "b", Src = "Later", T = 90 }];
        var old = new MeetingNotes { Summary = "Previous summary", Covered = 1 };
        session.Notes = old;
        var expected = NotesPrompt.Build(final ? new MeetingNotes() : old, session.Lines, final ? 0 : 1,
            "English", final, template: NoteTemplates.BuiltIn[0], meetingStart: session.StartedAt);
        var engine = new NotesEngine(client, store) { Language = () => "English" };
        engine.Show(session.Id);
        reply = _ => Good;
        await engine.Update(session.Id, final);
        Assert.Equal(expected, Assert.Single(prompts));
        Assert.Equal(NotesEngine.State.Idle, engine.Status);
        Assert.Equal(final, engine.Notes.Final);
    }
}

