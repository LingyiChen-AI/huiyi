namespace HuiYi.Tests;

public sealed class SessionImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "huiyi-import-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImportedRecordingKeepsFullDurationAndDoesNotReplaceLiveMeeting()
    {
        var store = new SessionStore(root);
        var live = store.Begin();
        var start = DateTime.UtcNow;
        var imported = new Session
        {
            Title = "Local video", StartedAt = start, EndedAt = start.AddHours(3), Renamed = true,
            Lines = [new() { Id = "line", T = 10790, End = 10800, Src = "Last words" }]
        };
        var id = store.Import(imported);
        imported.Lines.Clear();
        Assert.Equal(live.Id, store.LiveId);
        Assert.Null(store.GroupOf(id));
        var loaded = new SessionStore(root).Get(id)!;
        Assert.Equal(TimeSpan.FromHours(3), loaded.Duration);
        Assert.Single(loaded.Lines);
        Assert.Equal(10800d, loaded.Lines[0].End);
    }

    [Fact]
    public void InvalidImportDoesNotCreateRecord()
    {
        var store = new SessionStore(root);
        Assert.Throws<ArgumentException>(() => store.Import(new Session()));
        Assert.Empty(store.All);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Theory]
    [InlineData("file:///C:/private.wav")]
    [InlineData("--exec anything")]
    [InlineData("javascript:alert(1)")]
    public async Task LinkImportRejectsNonWebInputBeforeStartingTools(string input)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => HuiYi.Speech.VideoDownload.Download(input, root, CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV123", "HTTP Error 412: Precondition Failed", true)]
    [InlineData("https://b23.tv/abc", "Request is blocked by server (412)", true)]
    [InlineData("https://www.bilibili.com/video/BV123", "HTTP Error 404: Not Found", false)]
    [InlineData("https://bilibili.com.example.com/video", "HTTP Error 412", false)]
    [InlineData("https://example.com/video", "HTTP Error 412", false)]
    public void OnlyBilibiliRejectionRetriesWithoutProxy(string url, string error, bool expected) =>
        Assert.Equal(expected, HuiYi.Speech.VideoDownload.ShouldRetryDirect(new Uri(url), error));

    [Fact]
    public void DownloadFailureShowsStatusWithoutLeakingPrivateUrl()
    {
        var message = HuiYi.Speech.VideoDownload.FailureMessage("ERROR: https://private.example/?token=secret HTTP Error 412: Precondition Failed");
        Assert.Contains("412", message);
        Assert.DoesNotContain("private.example", message);
        Assert.DoesNotContain("secret", message);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
