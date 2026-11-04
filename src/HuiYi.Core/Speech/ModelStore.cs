using System.Security.Cryptography;

namespace HuiYi.Speech;

/// The on-device models: SenseVoice (speech recognition for zh/en/ja/ko/yue, k2-fsa, Apache-2.0)
/// and Silero VAD (MIT). Downloaded on first use, never bundled, each checked against a pinned
/// SHA-256 before use. Hugging Face first, then its mirror for networks that can't reach it.
public sealed class ModelStore
{
    public sealed record ModelFile(string RelativePath, string[] Urls, long Size, string Sha256);

    private const string SenseVoice = "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/";

    public static readonly ModelFile[] Files =
    [
        new("sense-voice/model.int8.onnx",
            [$"https://huggingface.co/{SenseVoice}model.int8.onnx", $"https://hf-mirror.com/{SenseVoice}model.int8.onnx"],
            239_233_841, "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51"),
        new("sense-voice/tokens.txt",
            [$"https://huggingface.co/{SenseVoice}tokens.txt", $"https://hf-mirror.com/{SenseVoice}tokens.txt"],
            315_894, "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc"),
        // Silero VAD from Hugging Face, so the mirror serves the very same file.
        new("silero_vad.onnx",
            ["https://huggingface.co/csukuangfj/vad/resolve/main/silero_vad.onnx",
             "https://hf-mirror.com/csukuangfj/vad/resolve/main/silero_vad.onnx"],
            1_807_522, "a35ebf52fd3ce5f1469b2a36158dba761bc47b973ea3382b3186ca15b1f5af28"),
        // Voiceprints for telling speakers apart: 3D-Speaker CAM++, trained on ~200k Chinese speakers
        // (Apache-2.0). Cicada's WeSpeaker model (English VoxCeleb) put two Chinese hosts of a
        // Bilibili interview into one voice at every threshold; CAM++ told them apart (huiyi-cli speakers).
        new(SpeakerFile,
            [$"https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/{SpeakerFile}",
             $"https://hf-mirror.com/csukuangfj/speaker-embedding-models/resolve/main/{SpeakerFile}"],
            28_281_138, "f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11"),
    ];

    private const string SpeakerFile = "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx";

    public string Root { get; }
    public ModelStore(string? root = null) => Root = root ?? Paths.Models;

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public string SenseVoiceModel => PathOf("sense-voice/model.int8.onnx");
    public string SenseVoiceTokens => PathOf("sense-voice/tokens.txt");
    public string Vad => PathOf("silero_vad.onnx");
    public string Speaker => PathOf(SpeakerFile);

    public long TotalBytes => Files.Sum(f => f.Size);

    /// Present with the right size; the hash is checked once after download and remembered.
    public bool Ready => Files.All(f => new FileInfo(PathOf(f.RelativePath)) is { Exists: true } i && i.Length == f.Size
                                        && File.Exists(PathOf(f.RelativePath) + ".ok"));

    /// Downloads what is missing. `progress` gets (bytes done, bytes total).
    public async Task Download(IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HuiYi/0.1");
        long doneBefore = 0;
        foreach (var f in Files)
        {
            var target = PathOf(f.RelativePath);
            if (new FileInfo(target) is { Exists: true } info && info.Length == f.Size && File.Exists(target + ".ok"))
            {
                doneBefore += f.Size;
                progress?.Report((doneBefore, TotalBytes));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // A file already there (put by hand, or from before) only needs its hash checked.
            if (File.Exists(target) && new FileInfo(target).Length == f.Size && await Sha256Of(target, ct) == f.Sha256)
            {
                await File.WriteAllTextAsync(target + ".ok", f.Sha256, ct);
                doneBefore += f.Size;
                progress?.Report((doneBefore, TotalBytes));
                continue;
            }
            Exception? last = null;
            var ok = false;
            foreach (var url in f.Urls)
            {
                try
                {
                    var part = target + ".part";
                    using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var dst = File.Create(part);
                        var buffer = new byte[1 << 16];
                        long got = 0;
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                            got += n;
                            progress?.Report((doneBefore + got, TotalBytes));
                        }
                    }
                    var hash = await Sha256Of(part, ct);
                    if (hash != f.Sha256) throw new InvalidDataException($"checksum mismatch for {f.RelativePath}");
                    File.Move(part, target, true);
                    await File.WriteAllTextAsync(target + ".ok", f.Sha256, ct);
                    ok = true;
                    Log.Debug($"models: {f.RelativePath} downloaded from {new Uri(url).Host}");
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    last = e;
                    Log.Debug($"models: {f.RelativePath} from {new Uri(url).Host} failed: {e.Message}");
                }
            }
            if (!ok) throw new IOException($"下载 {f.RelativePath} 失败", last);
            doneBefore += f.Size;
        }
    }

    private static async Task<string> Sha256Of(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }
}
