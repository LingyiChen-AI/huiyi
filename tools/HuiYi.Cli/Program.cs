using System.Diagnostics;
using System.Text;
using HuiYi;
using HuiYi.Audio;
using HuiYi.Speak;
using HuiYi.Speech;
using NAudio.Wave;

// Developer checks for the engines, without the UI:
//   huiyi-cli models                       download / verify the models
//   huiyi-cli transcribe <audio>           one decode of a whole file
//   huiyi-cli listen <audio> [target]      the live pipeline on a file (fast), with translation
//   huiyi-cli loopback <audio> [target]    play the file on the speakers and listen to the PC (real path)
//   huiyi-cli translate <text> <src> <dst>
//   huiyi-cli voices                       Windows voices and the virtual cable
//   huiyi-cli say <text> <lang>            speak with a Windows voice on the speakers
//   huiyi-cli micusers                     meeting apps using a mic now
Console.OutputEncoding = Encoding.UTF8;
Log.Echo = l => Console.Error.WriteLine("  · " + l);
var cmd = args.FirstOrDefault() ?? "help";
var models = new ModelStore();
string[] sys = Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray();

switch (cmd)
{
    case "models":
    {
        var last = -1;
        await models.Download(new Progress<(long Done, long Total)>(p =>
        {
            var pct = (int)(p.Done * 100 / p.Total);
            if (pct != last) { last = pct; Console.Write($"\r{pct}%"); }
        }), CancellationToken.None);
        Console.WriteLine($"\nready: {models.Ready}");
        break;
    }
    case "transcribe":
    {
        using var rec = new Recognizer(models) { Fix = GlossaryFix() };
        var samples = ReadAll(args[1]);
        var w = Stopwatch.StartNew();
        var r = rec.Recognize(samples);
        Console.WriteLine($"[{r.Lang}] {r.Text}");
        Console.WriteLine($"{samples.Length / 16000.0:F1} s audio decoded in {w.ElapsedMilliseconds} ms");
        break;
    }
    case "listen":
    case "loopback":
    case "mic":
    case "both":
    {
        var target = args.Length > 2 ? args[2] : "auto";
        var settings = new AppSettings { Target = target };
        var tmp = Path.Combine(Path.GetTempPath(), "huiyi-cli-sessions");
        var store = new SessionStore(tmp);
        var ai = new AiClient();
        var translator = new TranslationService(ai);
        var notes = new NotesEngine(ai, store);
        using var rec = new Recognizer(models) { Fix = GlossaryFix() };
        var listen = new ListenEngine(rec, models);
        using var prints = new Voiceprints(models);
        var tagger = new SpeakerTagger(() => prints);
        // As the app does; HUIYI_TURNS=0 leaves long stretches whole, for comparing.
        if (Environment.GetEnvironmentVariable("HUIYI_TURNS") != "0")
            listen.Turns = (s, heard) => new TurnFinder(prints.Embed, tagger.Nearest).Points(s, heard.SentenceEnds());
        var meeting = new MeetingController(settings, store, translator, notes, listen, () => sys, tagger);
        var w = Stopwatch.StartNew();
        string lastPartial = "";
        meeting.SubtitleChanged += s =>
        {
            if (s.Live && s.Source != lastPartial)
            {
                lastPartial = s.Source;
                Console.WriteLine($"{w.Elapsed.TotalSeconds,6:F1}s  ~ {Tail(s.Source)}  ⇒ {Tail(s.Translation)}");
            }
        };
        var done = new TaskCompletionSource();
        meeting.Error += e =>
        {
            Console.WriteLine("ERROR " + e);
            done.TrySetResult();
        };
        IAudioSource source;
        WaveOutEvent? player = null;
        if (cmd == "listen")
        {
            var fs = new FileSource(args[1], realTime: false);
            fs.Finished += () => done.TrySetResult();
            source = fs;
        }
        else if (cmd == "both")
        {
            // both <pc-audio> <mic-audio>: the app's real source, the PC and a microphone mixed. The
            // first file plays on the speakers (the other side), then the second goes into the
            // virtual cable, heard as a microphone (me), each starting while the other still plays.
            var cableIn = VirtualCable.Input() ?? throw new InvalidOperationException("VB-CABLE is not installed");
            source = new BothEnds(new SteadySource(new LoopbackSource()), new MicSource(VirtualCable.Output()!.ID));
            var speaker = new WaveOutEvent();
            speaker.Init(new MediaFoundationReader(args[1]));
            var mine = new NAudio.Wave.WasapiOut(cableIn, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
            var mineReader = new MediaFoundationReader(args[2]);
            mine.Init(mineReader);
            var left = 2;
            void Ended() { if (Interlocked.Decrement(ref left) == 0) Task.Delay(2500).ContinueWith(_ => done.TrySetResult()); }
            speaker.PlaybackStopped += (_, _) => Ended();
            mine.PlaybackStopped += (_, _) => Ended();
            meeting.Start(source);
            speaker.Play();
            // Mine starts 3 s before the speaker's file ends: a reply that overlaps a little.
            // HUIYI_BOTH_AT=<seconds>: start mine that far into the speaker's file instead — talking
            // over a video that keeps playing.
            var pcLength = new MediaFoundationReader(args[1]).TotalTime;
            await Task.Delay(double.TryParse(Environment.GetEnvironmentVariable("HUIYI_BOTH_AT"), out var at)
                ? TimeSpan.FromSeconds(at) : pcLength - TimeSpan.FromSeconds(3));
            mine.Play();
            await done.Task;
            speaker.Dispose();
            mine.Dispose();
            source = null!;
        }
        else if (cmd == "mic")
        {
            // The microphone path without a microphone: the file goes into CABLE Input and is
            // heard on CABLE Output, a capture device like any USB conference mic.
            var cableIn = VirtualCable.Input() ?? throw new InvalidOperationException("VB-CABLE is not installed");
            var cableOut = VirtualCable.Output()!;
            source = new MicSource(cableOut.ID);
            var wasapi = new NAudio.Wave.WasapiOut(cableIn, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
            wasapi.Init(new MediaFoundationReader(args[1]));
            wasapi.PlaybackStopped += (_, _) => Task.Delay(2500).ContinueWith(_ => done.TrySetResult());
            meeting.Start(source);
            wasapi.Play();
            await done.Task;
            wasapi.Dispose();
            source = null!;
        }
        else
        {
            source = new LoopbackSource();
            var reader = new MediaFoundationReader(args[1]);
            player = new WaveOutEvent();
            player.Init(reader);
            player.PlaybackStopped += (_, _) => Task.Delay(2500).ContinueWith(_ => done.TrySetResult());
        }
        if (source != null)
        {
            meeting.Start(source);
            player?.Play();
            await done.Task;
        }
        // Read faster than real time, the decoder is still behind: wait until it stops moving.
        double fed;
        do
        {
            fed = listen.FedSeconds;
            await Task.Delay(1500);
        } while (cmd == "listen" && listen.FedSeconds > fed);
        var id = meeting.SessionId!;
        // Let the last translations land.
        await Task.Delay(3000);
        Console.WriteLine("\n=== lines ===");
        var snap = store.Snapshot(id)!;
        var numbers = SpeakerLabels.Numbers(snap.Lines);
        var names = SpeakerLabels.Names(snap);
        foreach (var l in snap.Lines)
            Console.WriteLine($"{TranscriptExport.Clock(l.T)} {SpeakerLabels.Who(l, numbers, names),-6} (voice {l.Voice?.ToString() ?? "-"}) {l.Src}"
                              + (l.Fixes is { Count: > 0 } f ? "   [词库: " + string.Join(", ", f.Select(x => $"{x.From}→{x.To}")) + "]" : "")
                              + (l.Dst != null ? $"\n          {l.Dst}" : ""));
        Console.WriteLine($"speakers on show: {SpeakerLabels.ShownCount(snap.Lines)}");
        // HUIYI_REDO=1: the after-meeting check over the whole file, line by line beside the live voice.
        if (Environment.GetEnvironmentVariable("HUIYI_REDO") == "1" && cmd == "listen")
        {
            var whole = ReadAll(args[1]);
            var rw = Stopwatch.StartNew();
            var plan = MeetingRedo.Run(whole, snap.Lines, prints.Embed, rec.Recognize);
            Console.WriteLine($"\n=== after the meeting ({rw.Elapsed.TotalSeconds:F0} s for {whole.Length / 16000.0:F0} s of audio): {plan.Relabelled} lines relabelled, {plan.Split} split ===");
            foreach (var l in snap.Lines)
            {
                if (!plan.Lines.TryGetValue(l.Id, out var ps)) continue;
                Console.WriteLine($"{TranscriptExport.Clock(l.T)} live {l.Voice?.ToString() ?? "-"}  {l.Src}");
                foreach (var p in ps) Console.WriteLine($"        -> {p.Voice?.ToString() ?? "-"}  {p.Text}");
            }
        }
        meeting.Stop();
        player?.Dispose();
        try { Directory.Delete(tmp, true); } catch { }
        break;
    }
    case "redo":
    {
        // huiyi-cli redo <audio>: a meeting over the file the way the app runs one, recording kept,
        // then the after-meeting check applied to the record; prints the lines before and after.
        var settings = new AppSettings { Target = "zh-CN", TranslationOn = false };
        var store = new SessionStore(Path.Combine(Path.GetTempPath(), "huiyi-cli-redo"));
        Paths.Local = Path.Combine(Path.GetTempPath(), "huiyi-cli-redo-local");
        var ai = new AiClient();
        var notes = new NotesEngine(ai, store);
        using var rec = new Recognizer(models);
        var listen = new ListenEngine(rec, models);
        using var prints = new Voiceprints(models);
        var tagger = new SpeakerTagger(() => prints);
        listen.Turns = (s, heard) => new TurnFinder(prints.Embed, tagger.Nearest).Points(s, heard.SentenceEnds());
        var meeting = new MeetingController(settings, store, new TranslationService(ai), notes, listen, () => sys, tagger)
        {
            Redo = (audio, lines) => MeetingRedo.Run(audio, lines, prints.Embed, rec.Recognize),
        };
        var redone = new TaskCompletionSource<(int, int)>();
        meeting.Redone += (_, r, s) => redone.TrySetResult((r, s));
        var fs = new FileSource(args[1], realTime: false);
        var finished = new TaskCompletionSource();
        fs.Finished += () => finished.TrySetResult();
        meeting.Start(fs);
        var id = meeting.SessionId!;
        await finished.Task;
        double fed;
        do { fed = listen.FedSeconds; await Task.Delay(1500); } while (listen.FedSeconds > fed);
        void Print(string title)
        {
            var snap = store.Snapshot(id)!;
            Console.WriteLine($"\n=== {title}: {snap.Lines.Count} lines ===");
            foreach (var l in snap.Lines) Console.WriteLine($"{TranscriptExport.Clock(l.T)} voice {l.Voice?.ToString() ?? "-",-2} {l.Src}");
        }
        Print("live");
        var recording = Path.Combine(Paths.Recordings, id + ".wav");
        Console.WriteLine($"recording kept: {File.Exists(recording)} ({(File.Exists(recording) ? new FileInfo(recording).Length / 1024 : 0)} KB)");
        var w = Stopwatch.StartNew();
        meeting.Stop();
        var done = await Task.WhenAny(redone.Task, Task.Delay(TimeSpan.FromMinutes(10)));
        if (done != redone.Task) Console.WriteLine("\n(no change from the after-meeting check)");
        else Console.WriteLine($"\nafter-meeting check: {redone.Task.Result.Item1} relabelled, {redone.Task.Result.Item2} split, {w.Elapsed.TotalSeconds:F0} s");
        await Task.Delay(500);
        Print("after the meeting");
        Console.WriteLine($"recording deleted: {!File.Exists(recording)}");
        break;
    }
    case "translate":
    {
        var ai = new AiClient();
        var t = new TranslationService(ai);
        Console.WriteLine(await t.Translate(args[1], args[2], args[3], TranslationService.Kind.Sentence) ?? "(null)");
        break;
    }
    case "voices":
        foreach (var v in Voices.All) Console.WriteLine($"{v.Language,-8} {v.DisplayName}");
        Console.WriteLine($"VB-CABLE: {(VirtualCable.Installed ? "installed" : "not installed")}");
        Console.WriteLine($"system languages: {string.Join(", ", sys)} → mine {Languages.MyLanguage(sys)}");
        break;
    case "say":
    {
        var settings = new AppSettings();
        using var rec = new Recognizer(models);
        var speak = new SpeakEngine(settings, rec, new TranslationService(new AiClient()), () => null, () => sys);
        speak.Problem += p => Console.WriteLine("PROBLEM " + p);
        await speak.SayIntoRoom(args[1], args[2], intoRoom: false);
        break;
    }
    case "dialog":
    {
        // Two Windows voices taking turns, 0.8 s apart, like a meeting (speaker tests).
        var a = Voices.All.First(v => v.DisplayName.Contains("Huihui"));
        var b = Voices.All.First(v => v.DisplayName.Contains("Kangkang"));
        string[] script =
        [
            "大家好，我们今天主要讨论新版本的上线计划。", "好的，我先说一下测试这边的进度。",
            "目前还有几个问题没有解决，你那边怎么看？", "登录模块上周已经修好了，回归测试也跑完了。",
            "那支付模块呢，我记得还有一个超时的问题。", "超时的问题定位到了，是网关的配置不对。",
            "什么时候能改好？我们需要一个明确的时间。", "我估计周三之前可以改完，周四再测一轮。",
            "好，那上线时间就定在下周一，大家没意见吧。", "没意见，不过发布说明需要市场部一起看一下。",
            "这个我来联系市场部，今天下午就发给他们。", "好的，那我这边把测试报告整理好发到群里。",
            "还有其他问题吗？没有的话今天就到这里。", "没有了，谢谢大家，我们周四再同步一次进度。",
        ];
        using var writer = new WaveFileWriter(args[1], WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        for (var i = 0; i < script.Length; i++)
        {
            using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer { Voice = i % 2 == 0 ? a : b };
            using var stream = await synth.SynthesizeTextToStreamAsync(script[i]);
            var bytes = new byte[stream.Size];
            using (var dr = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
            {
                await dr.LoadAsync((uint)stream.Size);
                dr.ReadBytes(bytes);
            }
            using var wav = new WaveFileReader(new MemoryStream(bytes));
            var raw = new byte[wav.Length];
            var n = wav.Read(raw, 0, raw.Length);
            var mono = new Resampler(wav.WaveFormat.SampleRate).Process(Resampler.ToMonoFloat(raw, n, wav.WaveFormat));
            writer.WriteSamples(mono, 0, mono.Length);
            var gap = new float[(int)(0.8 * 16000)];
            writer.WriteSamples(gap, 0, gap.Length);
        }
        Console.WriteLine($"{script.Length} lines, {writer.TotalTime.TotalSeconds:F0} s -> {args[1]}");
        break;
    }
    case "clip":
    {
        // clip <audio> <from-minute> <minutes> <out.wav>: a stretch as 16 kHz mono WAV, for tests.
        var all = ReadAll(args[1]);
        var from = (int)(double.Parse(args[2]) * 60 * 16000);
        var len = (int)(double.Parse(args[3]) * 60 * 16000);
        var part = all.Skip(from).Take(len).ToArray();
        using var w = new WaveFileWriter(args[4], WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        w.WriteSamples(part, 0, part.Length);
        Console.WriteLine($"{part.Length / 16000.0:F0} s -> {args[4]}");
        break;
    }
    case "ai-models":
    {
        // ai-models <preset> [base url]: the chat models a platform offers (keys come from the app's own store).
        var p = ProviderPreset.Find(args[1]).Create();
        if (args.Length > 2) p.BaseUrl = args[2];
        try
        {
            var list = await AiClient.ListModels(p);
            Console.WriteLine($"{p.Name} ({p.BaseUrl}): {list.Count} chat models");
            Console.WriteLine(string.Join(", ", list.Take(40)));
        }
        catch (AiException e)
        {
            Console.WriteLine($"{p.Name} ({p.BaseUrl}): {e.Failure} (a key is needed to list models)");
        }
        break;
    }
    case "speakers":
        SpeakerEval.Run(args, models);
        break;
    case "micusers":
        foreach (var (k, v) in MicUsers.InCall()) Console.WriteLine($"{v} ({k})");
        Console.WriteLine("done");
        break;
    case "tokens":
    {
        // huiyi-cli tokens <audio> <from s> <seconds>: one clip, each piece with its time, and the sentence ends.
        var all = ReadAll(args[1]);
        var from = (int)(double.Parse(args[2]) * 16000);
        var clip = all[from..Math.Min(all.Length, from + (int)(double.Parse(args[3]) * 16000))];
        using var rec = new Recognizer(models);
        var r = rec.Recognize(clip);
        Console.WriteLine(r.Text);
        Console.WriteLine(string.Join(" ", (r.Tokens ?? []).Select((t, i) => $"{t}@{r.Times![i]:F2}")));
        Console.WriteLine("sentence ends: " + string.Join(", ", r.SentenceEnds().Select(x => x.ToString("F2"))));
        break;
    }
    case "suspects":
    {
        // suspects <glossary.json> <text file>: how many lines would go to the AI check, with examples.
        var g = new Glossary(args[1]);
        var lines = File.ReadAllLines(args[2]).Where(l => l.Trim().Length > 0).ToList();
        var hits = lines.Select(l => (Line: l, S: g.Suspects(l, null))).Where(x => x.S.Count > 0).ToList();
        Console.WriteLine($"{hits.Count} of {lines.Count} lines have suspects ({100.0 * hits.Count / Math.Max(1, lines.Count):F1} %)");
        foreach (var (line, s) in hits.Take(25))
            Console.WriteLine($"  {string.Join(", ", s.Select(x => $"{x.Heard}→{x.Term}"))}   | {line.Trim()[..Math.Min(60, line.Trim().Length)]}");
        break;
    }
    case "mics":
    {
        var current = DefaultDevices.DefaultMicId();
        foreach (var (id, name) in DefaultDevices.Mics()) Console.WriteLine($"{(id == current ? "*" : " ")} {name}  {id}");
        Console.WriteLine($"cable: {(VirtualCable.Installed ? VirtualCable.Output()?.FriendlyName : "none")}");
        var speaker = new NAudio.CoreAudioApi.MMDeviceEnumerator().GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
        Console.WriteLine($"speaker: {speaker.FriendlyName}  {speaker.ID}");
        break;
    }
    case "setmic":
        // Switches the default mic and reports what Windows says afterwards.
        Console.WriteLine(DefaultDevices.SetDefault(args[1]) ? "set" : "failed");
        Console.WriteLine($"default now: {DefaultDevices.DefaultMicId()}");
        break;
    default:
        Console.WriteLine("usage: models | transcribe <f> | listen <f> [target] | loopback <f> [target] | translate <text> <src> <dst> | voices | say <text> <lang> | micusers");
        break;
}

static string Tail(string s) => s.Length > 60 ? "…" + s[^60..] : s;

// HUIYI_GLOSSARY=<glossary.json>: recognition corrected against that glossary, as the app does.
static Func<string, (string, IReadOnlyList<TermFix>)>? GlossaryFix() =>
    Environment.GetEnvironmentVariable("HUIYI_GLOSSARY") is { Length: > 0 } g ? new Glossary(g).FixWithDetails : null;

static float[] ReadAll(string path)
{
    using var reader = new MediaFoundationReader(path);
    var resampler = new Resampler(reader.WaveFormat.SampleRate);
    var all = new List<float>();
    var buf = new byte[reader.WaveFormat.AverageBytesPerSecond];
    int n;
    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        all.AddRange(resampler.Process(Resampler.ToMonoFloat(buf, n, reader.WaveFormat)));
    return all.ToArray();
}
