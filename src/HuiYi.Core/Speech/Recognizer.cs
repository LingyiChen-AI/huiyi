using SherpaOnnx;

namespace HuiYi.Speech;

public sealed record Recognized(string Text, string Lang);

/// SenseVoice through sherpa-onnx: one model for Chinese, English, Japanese, Korean and Cantonese,
/// with the spoken language in the result. Not streaming, but a 10 s clip decodes in ~0.1 s on a
/// desktop CPU, so partial text is just the clip so far decoded again. Thread-safe (one decode at a
/// time); listening and speak-back share it.
public sealed class Recognizer : IDisposable
{
    public const int SampleRate = 16000;
    private readonly OfflineRecognizer recognizer;
    private readonly object gate = new();

    public Recognizer(ModelStore models, int threads = 4)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.SenseVoice.Model = models.SenseVoiceModel;
        config.ModelConfig.SenseVoice.Language = "auto";
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = models.SenseVoiceTokens;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        recognizer = new OfflineRecognizer(config);
    }

    public Recognized Recognize(float[] samples)
    {
        lock (gate)
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            recognizer.Decode(stream);
            var r = stream.Result;
            var text = r.Text.Trim();
            return new Recognized(text, Languages.FromRecognizer(LangTag(r), text));
        }
    }

    /// The result's language tag ("<|zh|>"), on sherpa-onnx versions that report it.
    private static string? LangTag(OfflineRecognizerResult r) =>
        r.GetType().GetProperty("Lang")?.GetValue(r) as string;

    public void Dispose() => recognizer.Dispose();
}
