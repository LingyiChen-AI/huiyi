using HuiYi.Localization;
using System.Windows;
using System.Windows.Threading;
using HuiYi.Audio;
using HuiYi.Speak;
using HuiYi.Speech;

namespace HuiYi.App;

/// The product name shown to users. Kept in one place: it is not "Cicada"/"知了", which belong to
/// the macOS project this one follows.
public static class Brand
{
    public static string Name => L.Language.StartsWith("zh") ? L.T("会译") : "HuiYi";
    public static string Tagline => L.T("会议实时字幕与纪要");
}

/// Wires the engines together and owns the windows. Engines raise events on worker threads; every
/// handler here hops to the UI thread first.
public sealed class AppModel
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public SessionStore Store { get; } = new();
    public AiClient Ai { get; } = new();
    public ModelStore Models { get; } = new();
    public TranslationService Translator { get; }
    public NotesEngine Notes { get; }
    public MeetingController? Meeting { get; private set; }
    public SpeakEngine? Speak { get; private set; }
    /// 同声传译's microphone switch: CABLE Output as the system mic while it is on.
    public MicRouting Routing { get; }
    public Recognizer? Recognizer { get; private set; }
    /// The user's own notes templates (built-in ones are in `NoteTemplates.BuiltIn`).
    public List<NoteTemplate> MyTemplates { get; } = NoteTemplates.LoadMine();

    public IReadOnlyList<NoteTemplate> Templates => NoteTemplates.All(MyTemplates);

    /// The template a meeting's notes follow: its own pick, else the default.
    public NoteTemplate TemplateFor(Session? s) => NoteTemplates.Find(s?.TemplateId ?? Settings.DefaultTemplateId, MyTemplates);

    public void SaveTemplates()
    {
        NoteTemplates.SaveMine(MyTemplates);
        // A deleted default falls back to the general template.
        if (Templates.All(t => t.Id != Settings.DefaultTemplateId))
        {
            Settings.DefaultTemplateId = NoteTemplates.DefaultId;
            Settings.Save();
        }
        StateChanged?.Invoke();
    }

    public event Action? StateChanged;
    public event Action<MeetingController.Subtitle>? Subtitle;
    public event Action<string>? LineChanged;
    public event Action? NotesChanged;
    public event Action? SpeakChanged;
    public event Action<string>? Toast;
    public event Action<MeetingEndWatch.Pending?>? EndPending;

    private readonly Dispatcher ui = Application.Current.Dispatcher;
    private Task? loading;
    private Native.KeyWatch? keyWatch;

    public bool IsListening => Meeting?.IsListening == true;
    public string? LiveSessionId => Meeting?.SessionId;

    public static IReadOnlyList<string> SystemLanguages()
    {
        try { return Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray(); }
        catch { return [System.Globalization.CultureInfo.CurrentUICulture.Name]; }
    }

    public AppModel(bool preview = false)
    {
        L.SetLanguage(Settings.UiLanguage);
        Routing = new MicRouting(Settings);
        // A recording kept for the after-meeting check outlives the app only if it crashed: drop it.
        if (!preview)
        {
            try { if (System.IO.Directory.Exists(Paths.Recordings)) System.IO.Directory.Delete(Paths.Recordings, true); } catch { }
        }
        Ai.Provider = Settings.ActiveProvider;
        Ai.Refresh();
        Translator = new TranslationService(Ai);
        Notes = new NotesEngine(Ai, Store) { Template = TemplateFor };
        Ai.Changed += () => Ui(() => StateChanged?.Invoke());
        Notes.Changed += () => Ui(() => NotesChanged?.Invoke());
        Translator.GoogleLimited += () => Ui(() => Toast?.Invoke(Ai.Usable
            ? L.F("Google 翻译暂时被限流，整句译文改由 {0} 提供", Ai.Current?.Name)
            : L.T("Google 翻译暂时被限流，字幕先只显示原文。在「设置 › AI 服务」接入一个大模型即可继续翻译")));
        if (!preview)
        {
            if (Ai.State == AiClient.KeyState.Unchecked) _ = Ai.Check();
            if (Models.Ready) loading = Task.Run(Load);
            ApplySpeakKey();
        }
    }

    private void Ui(Action a)
    {
        if (ui.CheckAccess()) a();
        else ui.BeginInvoke(a);
    }

    /// Loads the recognizer (~1 s for the 230 MB model) and builds the engines that need it.
    private void Load()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rec = new Recognizer(Models);
        var listen = new ListenEngine(rec, Models);
        // Voiceprints load on first use; without the model, lines just stay 对方.
        Voiceprints? prints = null;
        var printsFailed = false;
        Voiceprints? Prints()
        {
            if (prints != null || printsFailed) return prints;
            try { prints = new Voiceprints(Models); }
            catch (Exception e)
            {
                printsFailed = true;
                Log.Debug($"voiceprint model failed to load: {e.Message}");
            }
            return prints;
        }
        var tagger = new SpeakerTagger(Prints);
        // A long stretch holding two people (no pause between them) is cut where the voice changes.
        listen.Turns = (samples, heard) =>
        {
            if (!Settings.SpeakerVoices || Prints() is not { } p) return [];
            var finder = new TurnFinder(p.Embed, tagger.Nearest);
            return finder.Points(samples, heard.SentenceEnds());
        };
        var meeting = new MeetingController(Settings, Store, Translator, Notes, listen, SystemLanguages,
            tagger, new SpeakerNamer(Ai, Store));
        var speak = new SpeakEngine(Settings, rec, Translator, () => meeting.TheirLanguage, SystemLanguages, Ai);
        meeting.SubtitleChanged += s => Ui(() => Subtitle?.Invoke(s));
        meeting.LineChanged += id => Ui(() => LineChanged?.Invoke(id));
        meeting.StateChanged += () => Ui(() => StateChanged?.Invoke());
        meeting.Error += m => Ui(() => Toast?.Invoke(m));
        meeting.EndPending += p => Ui(() => EndPending?.Invoke(p));
        meeting.Redo = (audio, lines) => Prints() is { } vp ? MeetingRedo.Run(audio, lines, vp.Embed, rec.Recognize) : null;
        meeting.Redone += (id, relabelled, split) => Ui(() => Toast?.Invoke(
            L.F("已用整场录音重新校对了发言人：{0} 句有调整{1}，纪要会随之更新。", relabelled + split, (split > 0 ? $"（其中 {split} 句拆成了两个人）" : ""))));
        speak.MyMic = Routing.MyMicId;
        speak.StateChanged += () => Ui(() => SpeakChanged?.Invoke());
        speak.Problem += m => Ui(() => Toast?.Invoke(m));
        speak.Spoke += (src, sl, dst, dl) => meeting.AddMine(src, sl, dst, dl);
        Recognizer = rec;
        Meeting = meeting;
        Speak = speak;
        Log.Debug($"app: recognizer loaded in {watch.ElapsedMilliseconds} ms");
    }

    /// Makes sure the engines exist: false when the models still need downloading.
    public async Task<bool> EnsureEngines()
    {
        if (Meeting != null) return true;
        if (!Models.Ready) return false;
        loading ??= Task.Run(Load);
        try { await loading; }
        catch (Exception e)
        {
            loading = null;
            Log.Debug($"app: recognizer failed {e.Message}");
            Toast?.Invoke(L.T("识别模型加载失败：") + e.Message);
            return false;
        }
        return Meeting != null;
    }

    /// Developer switch (--feed): listen to this audio file instead of the PC, at real speed.
    public string? FeedFile { get; set; }

    public async Task<bool> StartListening()
    {
        if (IsListening) return true;
        if (!await EnsureEngines()) return false;
        IAudioSource source;
        if (FeedFile != null) source = new FileSource(FeedFile);
        else
        {
            // Listening to the cable would hear only our own interpreted voice, never the meeting.
            DefaultDevices.KeepRealSpeaker();
            // Both ends of the meeting. While I hold the interpreting key my words become a 我 line
            // of their own, so the microphone is muted for them here.
            var mic = Settings.ListenMic && DefaultDevices.Mics().Count > 0 ? new MicSource(ListenMicId) : null;
            source = new MixSource(new LoopbackSource(), mic, () => Speak?.Status == SpeakEngine.State.Recording);
        }
        Meeting!.Start(source);
        StateChanged?.Invoke();
        return true;
    }

    /// The room's microphone: the one chosen, else my real one (never the cable, which is the
    /// system default while interpreting is on).
    public string? ListenMicId() =>
        Settings.ListenMicId is { } chosen && DefaultDevices.Active(chosen) ? chosen : Routing.MyMicId();

    public void StopListening()
    {
        Meeting?.Stop();
        StateChanged?.Invoke();
    }

    public void KeepGoing() => Meeting?.KeepGoing();

    /// The translation switch (panel, tray menu, settings): one place, so they all agree.
    public void SetTranslation(bool on)
    {
        if (Settings.TranslationOn == on) return;
        Settings.TranslationOn = on;
        Settings.Save();
        if (on) Meeting?.TranslationResumed();
        StateChanged?.Invoke();
    }

    /// Who said a line of the live meeting ("发言人 2", a name, or 发言人) and their number, if shown.
    public (string Who, int? Number) SpeakerOf(string? lineId)
    {
        if (lineId == null || LiveSessionId is not { } id || Store.Snapshot(id) is not { } s) return (L.T("发言人"), null);
        var line = s.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return (L.T("发言人"), null);
        var numbers = SpeakerLabels.Numbers(s.Lines);
        var names = SpeakerLabels.Names(s);
        int? number = numbers.TryGetValue(lineId, out var n) ? n : null;
        var label = line.Speaker == "me" ? L.T("我") : number is { } speaker
            ? names.GetValueOrDefault(speaker) ?? L.F("发言人 {0}", speaker) : L.T("发言人");
        return (label, number);
    }

    /// Names a voice in a meeting; every view of its notes redraws with the name.
    public void RenameVoice(string sessionId, int voice, string name)
    {
        Store.SetVoiceName(sessionId, voice, name);
        NotesChanged?.Invoke();
    }

    public void SaveSettings()
    {
        Settings.Save();
        ApplySpeakKey();
        StateChanged?.Invoke();
    }

    /// 同声传译 on or off: the push-to-talk key is watched, and the system mic switched, only while it is on.
    public void ApplySpeakKey()
    {
        try { Routing.Apply(); }
        catch (Exception e) { Log.Debug($"routing: {e.Message}"); }
        var key = Settings.SpeakKey == SpeakKey.RightAlt ? Native.VK_RMENU : Native.VK_RCONTROL;
        if (!Settings.SpeakEnabled)
        {
            keyWatch?.Dispose();
            keyWatch = null;
            return;
        }
        if (keyWatch == null)
        {
            keyWatch = new Native.KeyWatch(key);
            keyWatch.Down += () => _ = PressSpeak();
            keyWatch.Up += () => Speak?.Release();
        }
        keyWatch.Key = key;
    }

    private async Task PressSpeak()
    {
        if (!await EnsureEngines()) { Toast?.Invoke(L.T("请先完成识别模型下载")); return; }
        Speak?.Press();
    }

    /// One-click VB-CABLE (settings and setup share it). Returns what to tell the user, null when it just worked.
    public async Task<string?> InstallCable(IProgress<string>? progress = null)
    {
        var (result, error) = await VirtualCable.Install(progress);
        if (result == VirtualCable.InstallResult.Installed) ApplySpeakKey();
        StateChanged?.Invoke();
        return result switch
        {
            VirtualCable.InstallResult.Installed => null,
            VirtualCable.InstallResult.NeedsRestart => L.T("安装程序已经运行完。如果这里还显示没装上，重启电脑后就能用了。"),
            VirtualCable.InstallResult.Cancelled => L.T("安装已取消。"),
            _ => L.T("安装没成功：") + error + L.T("。也可以去 vb-audio.com/Cable 手动下载安装。"),
        };
    }

    public void Shutdown()
    {
        keyWatch?.Dispose();
        try { Routing.Restore(); }
        catch (Exception e) { Log.Debug($"routing: restore failed {e.Message}"); }
        if (IsListening) Meeting!.Stop();
        Store.Flush();
    }
}
