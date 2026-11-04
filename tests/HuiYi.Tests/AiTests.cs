using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using HuiYi;

namespace HuiYi.Tests;

/// The AI client against a local stand-in for an OpenAI-compatible service: what it sends to each
/// kind of platform, and how it reads what comes back.
public sealed class AiTests : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly string baseUrl;
    private readonly List<(string Path, string? Auth, JsonObject? Body)> seen = [];
    private Func<string, (int Status, string Body)> reply = _ => (200, "{}");

    public AiTests()
    {
        // Keys go to a temporary folder, never the user's.
        Paths.Roaming = Path.Combine(Path.GetTempPath(), "huiyi-ai-test-" + Guid.NewGuid().ToString("N"));
        var port = FreePort();
        baseUrl = $"http://localhost:{port}/v1";
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(Serve);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            var text = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            lock (seen) seen.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Authorization"], text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null));
            var (status, body) = reply(ctx.Request.Url!.AbsolutePath);
            ctx.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        listener.Stop();
        try { Directory.Delete(Paths.Roaming, true); } catch { }
    }

    private AiClient Client(string preset, string model = "m-1")
    {
        var p = new AiProvider { Preset = preset, Name = preset, BaseUrl = baseUrl, Model = model };
        AIKey.Save(p.Id, "sk-test-123");
        var c = new AiClient { Provider = () => p };
        c.Refresh();
        return c;
    }

    private static string Completion(string content) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop",
        }) }.ToJsonString();

    [Theory]
    [InlineData("ark", "thinking")]
    [InlineData("zhipu", "thinking")]
    [InlineData("ling", "thinking")]
    [InlineData("bailian", "enable_thinking")]
    [InlineData("deepseek", null)]
    [InlineData("ucloud", null)]
    [InlineData("custom", null)]
    public async Task SendsOnlyThePlatformsOwnThinkingSwitch(string preset, string? param)
    {
        reply = _ => (200, Completion("你好"));
        var c = Client(preset, "doubao-x");
        Assert.Equal("你好", await c.Chat("system text", "Translate.", AiClient.Priority.Translate));
        var (path, auth, body) = seen.Last();
        Assert.Equal("/v1/chat/completions", path);
        Assert.Equal("Bearer sk-test-123", auth);
        Assert.Equal("doubao-x", body!["model"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("system text", body["messages"]![0]!["content"]!.GetValue<string>());
        foreach (var other in new[] { "thinking", "enable_thinking" })
            Assert.Equal(other == param, body.ContainsKey(other));
    }

    [Fact]
    public async Task CheckReportsAWrongKeyAndAWrongModel()
    {
        var c = Client("deepseek");
        reply = _ => (401, "{}");
        await c.Check();
        Assert.Equal(AiClient.KeyState.Invalid, c.State);
        c.Refresh();
        reply = _ => (404, "{\"error\":\"model not found\"}");
        await c.Check();
        Assert.Equal(AiClient.KeyState.Unreachable, c.State);
        Assert.Contains("模型名", c.CheckError);
        c.Refresh();
        reply = _ => (200, Completion("hi"));
        await c.Check();
        Assert.Equal(AiClient.KeyState.Ok, c.State);
    }

    [Fact]
    public async Task ListsChatModelsOnly()
    {
        reply = _ => (200, """{"data":[{"id":"qwen3.7-plus"},{"id":"doubao-seedream-5-0"},{"id":"glm-5.3-flash"},{"id":"bge-embedding"},{"id":"qwen-image-3.0"}]}""");
        var c = Client("ucloud");
        var list = await AiClient.ListModels(c.Current!);
        Assert.Equal(["glm-5.3-flash", "qwen3.7-plus"], list);
        Assert.Equal("/v1/models", seen.Last().Path);
    }

    [Fact]
    public async Task ReasoningIsDroppedFromReplies()
    {
        reply = _ => (200, Completion("<think>让我想想……</think>\n\nHello"));
        Assert.Equal("Hello", await Client("custom").Chat("x", "Translate.", AiClient.Priority.Translate));
    }

    [Fact]
    public void NoServiceMeansNoAi()
    {
        var c = new AiClient();
        Assert.False(c.HasKey);
        Assert.Equal(AiClient.KeyState.None, c.State);
        // Ollama runs locally without a key.
        var p = ProviderPreset.Find("ollama").Create();
        p.Model = "qwen3:8b";
        var o = new AiClient { Provider = () => p };
        o.Refresh();
        Assert.True(o.HasKey);
    }

    [Fact]
    public void PresetsAreWellFormed()
    {
        Assert.Equal(ProviderPreset.All.Length, ProviderPreset.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in ProviderPreset.All.Where(p => p.Id != "custom"))
        {
            Assert.True(Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http"), p.Id);
            Assert.False(p.BaseUrl.EndsWith('/'), p.Id);
        }
        Assert.Equal("custom", ProviderPreset.Find("nope").Id);
        Assert.Equal("https://api.modelverse.cn/v1", ProviderPreset.Find("ucloud").BaseUrl);
    }

    [Fact]
    public void KeysAreKeptPerServiceAndEncrypted()
    {
        AIKey.Save("a", "sk-aaa");
        AIKey.Save("b", "sk-bbb");
        Assert.Equal("sk-aaa", AIKey.Read("a"));
        Assert.Equal("sk-bbb", AIKey.Read("b"));
        Assert.DoesNotContain("sk-aaa", File.ReadAllText(Path.Combine(Paths.Roaming, "keys", "a.key")));
        AIKey.Remove("a");
        Assert.Null(AIKey.Read("a"));
    }
}
