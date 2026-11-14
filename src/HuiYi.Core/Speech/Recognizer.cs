using SherpaOnnx;

namespace HuiYi.Speech;

/// `Tokens` and `Times`: each recognized piece (a character, a word part, punctuation) and when it
/// starts, in seconds from the clip's start, for cutting a clip at its sentence ends.
public sealed record Recognized(string Text, string Lang, string[]? Tokens = null, float[]? Times = null)
{
    private static readonly char[] Enders = ['。', '？', '！', '?', '!', '.'];

    /// Where each sentence ends, in seconds from the clip's start: half-way between a sentence-ending
    /// mark and the next piece (a turn usually changes there). Empty without timestamps.
    public List<double> SentenceEnds()
    {
        var ends = new List<double>();
        if (Tokens == null || Times == null || Tokens.Length != Times.Length) return ends;
        for (var i = 0; i + 1 < Tokens.Length; i++)
        {
            var t = Tokens[i].Trim();
            if (t.Length == 0 || t.IndexOfAny(Enders) < 0) continue;
            // The next piece that is a word, not more punctuation.
            var j = i + 1;
            while (j < Tokens.Length && Tokens[j].Trim().All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c))) j++;
            if (j >= Tokens.Length) break;
            ends.Add((Times[i] + Times[j]) / 2);
        }
        return ends;
    }
}

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
        // Digital silence (a muted microphone, a gap filled on the PC's side) or a sliver of audio:
        // nothing to hear, and the decoder divides by zero on it.
        if (samples.Length < SampleRate / 10 || !samples.Any(v => Math.Abs(v) > 1e-5f)) return new Recognized("", "");
        lock (gate)
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            recognizer.Decode(stream);
            var r = stream.Result;
            var text = r.Text.Trim();
            return new Recognized(text, Languages.FromRecognizer(LangTag(r), text), r.Tokens, r.Timestamps);
        }
    }

    /// The result's language tag ("<|zh|>"), on sherpa-onnx versions that report it.
    private static string? LangTag(OfflineRecognizerResult r) =>
        r.GetType().GetProperty("Lang")?.GetValue(r) as string;

    // A decode still running on a listening thread finishes first: freeing the model under it crashes.
    public void Dispose() { lock (gate) recognizer.Dispose(); }
}
