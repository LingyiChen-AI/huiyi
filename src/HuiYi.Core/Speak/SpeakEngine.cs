using HuiYi.Audio;
using HuiYi.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace HuiYi.Speak;

/// The virtual microphone: VB-Audio Virtual Cable. What we play into "CABLE Input" comes out of
/// "CABLE Output", which the user picks as the microphone in Zoom / Teams / 腾讯会议. Never bundled
/// (it is someone else's driver): the user installs it from vb-audio.com.
public static class VirtualCable
{
    public const string Download = "https://vb-audio.com/Cable/";

    public static MMDevice? Input()
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public static bool Installed => Input() != null;
}

/// Windows' own voices (Settings › Time & language › Speech). Picks one for a language code.
public static class Voices
{
    public static IReadOnlyList<VoiceInformation> All => SpeechSynthesizer.AllVoices;

    public static bool Fits(VoiceInformation v, string code)
    {
        var lang = v.Language; // "zh-CN", "en-US"…
        return code switch
        {
            "zh-CN" => lang.Equals("zh-CN", StringComparison.OrdinalIgnoreCase),
            "zh-TW" => lang is "zh-TW" or "zh-HK",
            "yue" => lang is "zh-HK",
            _ => lang.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase) || lang.Equals(code, StringComparison.OrdinalIgnoreCase),
        };
    }

    public static VoiceInformation? For(string code, string? preferredId)
    {
        var all = All;
        return all.FirstOrDefault(v => v.Id == preferredId && Fits(v, code)) ?? all.FirstOrDefault(v => Fits(v, code));
    }
}

/// Hold the key and speak: mic → SenseVoice → translation → a Windows voice → the virtual mic.
/// Never speaks untranslated text into the room.
public sealed class SpeakEngine
{
    public enum State { Idle, Recording, Working, Speaking }

    public State Status { get; private set; }
    public event Action? StateChanged;
    /// A user-facing problem ("没装虚拟麦克风"…).
    public event Action<string>? Problem;
    /// What was said and what was spoken into the room.
    public event Action<string, string, string, string>? Spoke; // src, srcLang, dst, dstLang

    private readonly AppSettings settings;
    private readonly Recognizer recognizer;
    private readonly TranslationService translator;
    private readonly Func<string?> theirLanguage;
    private readonly Func<IReadOnlyList<string>> systemLanguages;
    private MicSource? mic;
    private readonly List<float> recorded = [];
    private readonly object gate = new();
    private DateTime pressedAt;

    private readonly LingClient? ling;

    public SpeakEngine(AppSettings settings, Recognizer recognizer, TranslationService translator,
        Func<string?> theirLanguage, Func<IReadOnlyList<string>> systemLanguages, LingClient? ling = null)
    {
        this.ling = ling;
        this.settings = settings;
        this.recognizer = recognizer;
        this.translator = translator;
        this.theirLanguage = theirLanguage;
        this.systemLanguages = systemLanguages;
    }

    private void Set(State s)
    {
        Status = s;
        StateChanged?.Invoke();
    }

    /// The language spoken into the room.
    public string Target()
    {
        if (settings.SpeakTarget != "auto") return settings.SpeakTarget;
        var mine = Languages.MyLanguage(systemLanguages());
        var theirs = theirLanguage();
        if (theirs != null && !Languages.SameLanguage(theirs, mine)) return theirs == "yue" ? "zh-TW" : theirs;
        return mine.StartsWith("zh") ? "en" : "zh-CN";
    }

    public void Press()
    {
        if (Status != State.Idle) return;
        if (!VirtualCable.Installed)
        {
            Problem?.Invoke("还没装虚拟麦克风（VB-CABLE），先在设置里安装");
            return;
        }
        lock (gate) recorded.Clear();
        pressedAt = DateTime.Now;
        mic = new MicSource();
        mic.Samples += s => { lock (gate) recorded.AddRange(s); };
        mic.Failed += m => Problem?.Invoke(m);
        mic.Start();
        Set(State.Recording);
    }

    public void Release()
    {
        if (Status != State.Recording) return;
        mic?.Dispose();
        mic = null;
        float[] audio;
        lock (gate) audio = recorded.ToArray();
        if (DateTime.Now - pressedAt < TimeSpan.FromMilliseconds(400) || audio.Length < Recognizer.SampleRate / 3)
        {
            Set(State.Idle);
            return;
        }
        Set(State.Working);
        _ = Task.Run(() => Process(audio));
    }

    private async Task Process(float[] audio)
    {
        try
        {
            var r = recognizer.Recognize(audio);
            var text = HeardText.Clean(r.Text);
            if (!text.Any(char.IsLetter))
            {
                Problem?.Invoke("没听清，再说一次");
                return;
            }
            var target = Target();
            string spoken;
            if (Languages.SameLanguage(r.Lang, target))
            {
                // Already in their language: say it as it was said.
                spoken = text;
            }
            else
            {
                var t = await translator.Translate(text, r.Lang, target, TranslationService.Kind.Speak);
                if (t == null || !TranslationService.LooksTranslated(text, t, target))
                {
                    Problem?.Invoke("翻译没成功，这句没有说出去");
                    Log.Debug("speak: no usable translation, nothing spoken");
                    return;
                }
                spoken = t;
            }
            Spoke?.Invoke(text, r.Lang, spoken, target);
            Set(State.Speaking);
            await SayIntoRoom(spoken, target);
        }
        catch (Exception e)
        {
            Log.Debug($"speak: failed {e.Message}");
            Problem?.Invoke("说话功能出错：" + e.Message);
        }
        finally
        {
            Set(State.Idle);
        }
    }

    /// Ming's built-in voice for a language (nil: Ming has none for it), and its `language_id`.
    public static (string Voice, string? Language)? MingVoice(string code)
    {
        var k = code.Split('-')[0];
        return k switch
        {
            "en" => ("US_Emma", null),
            "zh" or "yue" => ("灵小雅", null),
            "ja" or "ko" or "de" or "fr" or "es" => ("US_Emma", k),
            _ => null,
        };
    }

    /// Where the voice goes: the virtual mic, plus my speakers when monitoring (or only them for a try).
    private List<MMDevice> Outputs(bool intoRoom)
    {
        var devices = new List<MMDevice>();
        if (intoRoom && VirtualCable.Input() is { } cable) devices.Add(cable);
        if (!intoRoom || settings.SpeakMonitor)
            devices.Add(new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        return devices;
    }

    /// Speaks `text` into the virtual mic: Ming with a Ling key (natural voices, every language it
    /// has), else a Windows voice.
    public async Task SayIntoRoom(string text, string language, bool intoRoom = true)
    {
        var useWindowsVoice = settings.Voices.ContainsKey(language) && Voices.For(language, settings.Voices[language]) != null;
        if (!useWindowsVoice && ling?.Usable == true && MingVoice(language) is { } ming)
        {
            try
            {
                await PlayStream(ling.Speech(text, ming.Voice, ming.Language), Outputs(intoRoom));
                Log.Debug("speak: voiced by ming");
                return;
            }
            catch (Exception e)
            {
                Log.Debug($"speak: ming failed {e.Message}, using a Windows voice");
            }
        }
        var voice = Voices.For(language, settings.Voices.GetValueOrDefault(language));
        if (voice == null)
        {
            Problem?.Invoke($"系统里没有{Languages.ChineseName(language)}语音：在 Windows 设置 › 时间和语言 › 语音 里添加，或填入 Ling key 使用 Ming 的声音");
            return;
        }
        using var synth = new SpeechSynthesizer { Voice = voice };
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        var bytes = new byte[stream.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        await Task.WhenAll(Outputs(intoRoom).Select(d => Play(bytes, d)));
    }

    /// Plays streamed 24 kHz mono PCM as it arrives (the first words start before the rest exist).
    private static async Task PlayStream(IAsyncEnumerable<byte[]> chunks, List<MMDevice> devices)
    {
        var format = new WaveFormat(24000, 16, 1);
        var outs = devices.Select(d =>
        {
            var buffer = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromSeconds(120), DiscardOnBufferOverflow = true };
            var player = new WasapiOut(d, AudioClientShareMode.Shared, true, 100);
            player.Init(buffer);
            return (buffer, player);
        }).ToList();
        var started = false;
        var any = false;
        try
        {
            await foreach (var chunk in chunks)
            {
                any = true;
                foreach (var (buffer, _) in outs) buffer.AddSamples(chunk, 0, chunk.Length);
                if (!started)
                {
                    started = true;
                    foreach (var (_, player) in outs) player.Play();
                }
            }
            if (!any) throw new InvalidDataException("ming: no audio");
            // Let what is buffered play out.
            while (outs.Any(o => o.buffer.BufferedDuration > TimeSpan.Zero)) await Task.Delay(50);
            await Task.Delay(150);
        }
        finally
        {
            foreach (var (_, player) in outs) player.Dispose();
        }
    }

    private static Task Play(byte[] wav, MMDevice device)
    {
        var done = new TaskCompletionSource();
        var reader = new WaveFileReader(new MemoryStream(wav));
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.Init(reader);
        output.PlaybackStopped += (_, _) =>
        {
            output.Dispose();
            reader.Dispose();
            done.TrySetResult();
        };
        output.Play();
        return done.Task;
    }
}
