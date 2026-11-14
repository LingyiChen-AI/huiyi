using HuiYi.Audio;
using HuiYi.Localization;
using NAudio.Wave;

namespace HuiYi.Speech;

public static class MediaImport
{
    public static async Task<Session> Recognize(string path, Recognizer recognizer, ModelStore models,
        bool distinguishVoices, IProgress<double>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double duration;
        using (var reader = new MediaFoundationReader(path)) duration = reader.TotalTime.TotalSeconds;
        if (duration <= 0) throw new InvalidOperationException(L.T("文件没有可识别的音轨"));
        using var prints = distinguishVoices ? new Voiceprints(models) : null;
        var tagger = new SpeakerTagger(() => prints);
        var result = new Session
        {
            Title = Path.GetFileNameWithoutExtension(path), Renamed = true,
            StartedAt = DateTime.UtcNow, HasAudio = false
        };
        result.EndedAt = result.StartedAt.AddSeconds(duration);
        using var source = new FileSource(path, realTime: false);
        using var engine = new ListenEngine(recognizer, models);
        string? failure = null;
        engine.Failed += message => { failure = message; source.Stop(); engine.CancelInput(); };
        source.Finished += engine.CompleteInput;
        engine.Audio += (_, _) => progress?.Report(Math.Min(0.99, engine.FedSeconds / duration));
        tagger.Merged += (from, into) =>
        {
            foreach (var line in result.Lines.Where(l => l.Voice == from)) line.Voice = into;
        };
        if (prints != null)
            engine.Turns = (samples, heard) => new TurnFinder(prints.Embed, tagger.Nearest).Points(samples, heard.SentenceEnds());
        engine.Final += segment =>
        {
            token.ThrowIfCancellationRequested();
            if (segment.Start >= duration) return;
            result.Lines.Add(new TranscriptLine
            {
                Id = Guid.NewGuid().ToString("N"), T = Math.Min(duration, segment.Start),
                End = Math.Min(duration, segment.End), Src = segment.Text, SrcLang = segment.Lang, Fixes = segment.Fixes?.ToList(),
                Voice = segment.Samples != null && SpeakerLabels.Substantive(segment.Text) ? tagger.Voice(segment.Samples) : null
            });
        };
        engine.Start(source);
        using var registration = token.Register(() => { source.Stop(); engine.CancelInput(); });
        await engine.Completion.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (failure != null) throw new InvalidOperationException(failure);
        if (result.Lines.Count == 0) throw new InvalidOperationException(L.T("文件中未识别到语音"));
        progress?.Report(1);
        return result;
    }
}
