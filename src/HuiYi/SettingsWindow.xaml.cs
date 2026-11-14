using HuiYi.Localization;
using System.Windows;
using System.Windows.Controls;
using HuiYi.Speak;

namespace HuiYi.App;

public partial class SettingsWindow : Window
{
    private readonly AppModel model;
    private bool loading = true;

    public SettingsWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        UiText.Set(this, "Title", () => L.F("{0} · 设置", L.T("会译")));
        Icon = Icons.Mark(64, false);
        var s = model.Settings;
        FillUiLanguages();
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");

        Fill(TargetBox, [("auto", L.T("自动")), .. Languages.Target.Select(l => (l.Code, l.Name))], s.Target);
        ModeNotes.IsChecked = s.Display == DisplayMode.Notes;
        ModeSubs.IsChecked = s.Display == DisplayMode.Subtitles;
        FontSlider.Value = s.SubtitleFontSize;
        UiText.Set(FontText, "Text", () => L.F("悬浮字幕字号  {0:0}", s.SubtitleFontSize));
        TranslateOn.IsChecked = s.TranslationOn;
        VoicesOn.IsChecked = s.SpeakerVoices;
        RedoOn.IsChecked = s.RedoSpeakers;
        Capturable.IsChecked = s.HideFromCaptures;
        RenderFrom();

        Fill(PresetBox, ProviderPreset.All.Select(p => (p.Id, L.T(p.Name))).ToArray(), "");
        FillTemplates();
        Fill(NotesLangBox, [("auto", L.T("跟字幕一致")), .. Languages.Target.Select(l => (l.Code, l.Name))], s.NotesLanguage);

        SpeakOn.IsChecked = s.SpeakEnabled;
        KeyCtrl.IsChecked = s.SpeakKey == SpeakKey.RightCtrl;
        KeyAlt.IsChecked = s.SpeakKey == SpeakKey.RightAlt;
        Fill(SpeakTargetBox, [("auto", L.T("自动（对方用的语言）")), .. Languages.Target.Select(l => (l.Code, l.Name))], s.SpeakTarget);
        MonitorOn.IsChecked = s.SpeakMonitor;
        RouteOn.IsChecked = s.SpeakRouteMic;
        FillMics();

        FollowApps.IsChecked = s.FollowMeetingApps;
        Fill(SilenceBox, [("Prompt", L.T("先提醒再结束")), ("End", L.T("直接结束")), ("Off", L.T("不处理"))], s.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), L.F("{0} 分钟", m))).ToArray(), s.SilenceMinutes.ToString());
        LoginOn.IsChecked = s.LaunchAtLogin;
        TrayStartOn.IsChecked = s.TrayClickStarts;
        UiText.Set(AboutText, "Text", () => L.T("语音识别与发言人区分都在本机完成。开源组件与许可见安装目录里的 NOTICE。"));
        App.Updates.Changed += () => Dispatcher.BeginInvoke(RenderUpdates);
        RenderUpdates();

        model.Ai.Changed += () => Dispatcher.BeginInvoke(RenderKey);
        // The translation switch also lives in the panel and the tray menu.
        model.StateChanged += () =>
        {
            // Templates may have changed in the template center.
            if (!TemplateDefaultBox.IsDropDownOpen) FillTemplates();
            var was = loading;
            loading = true;
            TranslateOn.IsChecked = model.Settings.TranslationOn;
            loading = was;
        };
        Activated += (_, _) => { if (!installing) RenderCable(); RenderVoices(); RenderModels(); };
        RenderProviders();
        RenderKey();
        RenderCable();
        RenderVoices();
        RenderModels();
        loading = false;
    }

    private static void Fill(ComboBox box, (string Tag, string Text)[] items, string selected)
    {
        box.Items.Clear();
        foreach (var (tag, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            box.Items.Add(item);
            if (tag == selected) box.SelectedItem = item;
        }
        if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void FillUiLanguages() => Fill(UiLanguageBox,
        [("auto", L.T("跟随系统")), ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"),
         ("en", "English"), ("ja", "日本語"), ("ko", "한국어")], model.Settings.UiLanguage);

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        model.Settings.UiLanguage = TagOf(UiLanguageBox);
        model.Settings.Save();
        L.SetLanguage(model.Settings.UiLanguage);
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var wasLoading = loading;
        loading = true;
        FillUiLanguages();
        Fill(TargetBox, [("auto", L.T("自动")), .. Languages.Target.Select(l => (l.Code, l.Name))], model.Settings.Target);
        Fill(NotesLangBox, [("auto", L.T("跟字幕一致")), .. Languages.Target.Select(l => (l.Code, l.Name))], model.Settings.NotesLanguage);
        Fill(SpeakTargetBox, [("auto", L.T("自动（对方用的语言）")), .. Languages.Target.Select(l => (l.Code, l.Name))], model.Settings.SpeakTarget);
        Fill(SilenceBox, [("Prompt", L.T("先提醒再结束")), ("End", L.T("直接结束")), ("Off", L.T("不处理"))], model.Settings.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), L.F("{0} 分钟", m))).ToArray(), model.Settings.SilenceMinutes.ToString());
        FillMics();
        FillTemplates();
        var preset = TagOf(PresetBox);
        Fill(PresetBox, ProviderPreset.All.Select(p => (p.Id, L.T(p.Name))).ToArray(), preset);
        RenderUpdates();
        RenderVoices();
        RenderKey();
        loading = wasLoading;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var s = model.Settings;
        var oldDisplay = s.Display;
        var oldCapturable = s.HideFromCaptures;
        s.Target = TagOf(TargetBox);
        s.Display = ModeSubs.IsChecked == true ? DisplayMode.Subtitles : DisplayMode.Notes;
        s.SpeakerVoices = VoicesOn.IsChecked == true;
        s.RedoSpeakers = RedoOn.IsChecked == true;
        s.HideFromCaptures = Capturable.IsChecked == true;
        s.NotesLanguage = TagOf(NotesLangBox);
        if (TagOf(TemplateDefaultBox) is { Length: > 0 } tid) s.DefaultTemplateId = tid;
        s.SpeakEnabled = SpeakOn.IsChecked == true;
        s.SpeakKey = KeyAlt.IsChecked == true ? SpeakKey.RightAlt : SpeakKey.RightCtrl;
        s.SpeakTarget = TagOf(SpeakTargetBox);
        s.SpeakMonitor = MonitorOn.IsChecked == true;
        s.SpeakRouteMic = RouteOn.IsChecked == true;
        s.SpeakMicId = TagOf(MicBox) is { Length: > 0 } mic ? mic : null;
        s.ListenMicId = TagOf(ListenMicBox) is { Length: > 0 } room ? room : null;
        s.FollowMeetingApps = FollowApps.IsChecked == true;
        s.SilenceEnd = Enum.TryParse<MeetingEndWatch.Silence>(TagOf(SilenceBox), out var sil) ? sil : MeetingEndWatch.Silence.Prompt;
        s.SilenceMinutes = int.TryParse(TagOf(SilenceMinBox), out var m) ? m : 10;
        s.TrayClickStarts = TrayStartOn.IsChecked == true;
        if (s.LaunchAtLogin != (LoginOn.IsChecked == true))
        {
            s.LaunchAtLogin = LoginOn.IsChecked == true;
            Native.SetLaunchAtLogin(s.LaunchAtLogin);
        }
        model.SaveSettings();
        model.SetTranslation(TranslateOn.IsChecked == true);
        if (oldDisplay != s.Display || oldCapturable != s.HideFromCaptures) ((App)Application.Current).SwitchDisplay();
        if (sender == SpeakTargetBox) RenderVoices();
    }

    private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontText == null) return;
        UiText.Set(FontText, "Text", () => L.F("悬浮字幕字号  {0:0}", FontSlider.Value));
        if (loading) return;
        model.Settings.SubtitleFontSize = FontSlider.Value;
        model.SaveSettings();
    }

    // MARK: Notes templates

    private void FillTemplates()
    {
        var was = loading;
        loading = true;
        Fill(TemplateDefaultBox, model.Templates.Select(t => (t.Id, $"{(t.BuiltIn ? L.T(t.Name) : t.Name)}　· {L.T(t.Category)}")).ToArray(), model.Settings.DefaultTemplateId);
        loading = was;
    }

    private void Templates_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenTemplates();

    // MARK: AI services

    /// The service being edited: always the one in use (switching the active one switches the editor).
    private AiProvider? editing;

    private void RenderProviders()
    {
        var s = model.Settings;
        var was = loading;
        loading = true;
        Fill(ActiveBox, s.Providers.Select(p => (p.Id, $"{p.Name} · {p.Model}")).ToArray(), s.ActiveProvider()?.Id ?? "");
        if (s.Providers.Count == 0) ActiveBox.Items.Add(UiText.Bind(new ComboBoxItem { Content = L.T("还没有添加 AI 服务"), Tag = "", IsEnabled = false }, ("Content", () => L.T("还没有添加 AI 服务"))));
        if (s.Providers.Count == 0) ActiveBox.SelectedIndex = 0;
        loading = was;
        LoadEditor(s.ActiveProvider());
    }

    private void LoadEditor(AiProvider? p)
    {
        editing = p;
        EditorCard.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) return;
        var was = loading;
        loading = true;
        PresetBox.SelectedItem = PresetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Preset);
        NameBox.Text = p.Name;
        UrlBox.Text = p.BaseUrl;
        KeyBox.Clear();
        SetModels(p.Info.Models, p.Model);
        loading = was;
        RenderPresetHints(p.Info, p.Id);
    }

    private void SetModels(IEnumerable<string> models, string current)
    {
        ModelBox.Items.Clear();
        foreach (var m in models) ModelBox.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        ModelBox.Text = current;
    }

    private void RenderPresetHints(ProviderPreset info, string providerId)
    {
        UiText.Set(PresetHint, "Text", () => L.T(info.Hint ?? ""));
        PresetHint.Visibility = info.Hint == null ? Visibility.Collapsed : Visibility.Visible;
        UrlBox.IsReadOnly = false;
        var saved = AIKey.Read(providerId);
        UiText.Set(KeyHint, "Text", () => info.KeyOptional ? L.T("这个服务不需要 key") : saved != null ? L.F("已保存 {0}，留空则不修改", AIKey.Masked(saved)) : L.T("还没有填写"));
        UiText.Set(KeyLink, "Content", () => info.KeyOptional ? L.T("打开服务官网 ↗") : L.T("去申请 key ↗"));
        KeyLink.Visibility = info.KeyUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UiText.Set(ModelHint, "Text", () => info.Models.Length > 0 ? L.T("可以从列表里选，也可以直接输入模型名") : L.T("直接输入模型名，或先填 key 再获取列表"));
    }

    private void RenderKey()
    {
        var ai = model.Ai;
        UiText.Set(KeyState, "Text", () => ai.Current == null ? L.T("未设置：添加一个 AI 服务后即可生成纪要") : ai.State switch
        {
            AiClient.KeyState.None => L.F("{0}：还没有填写 API key", ai.Current.Name),
            AiClient.KeyState.Checking => L.F("正在验证 {0}…", ai.Describe),
            AiClient.KeyState.Ok => L.F("✓ {0} 可用 · 响应 {1:0.0} 秒", ai.Describe, ai.CheckSeconds),
            AiClient.KeyState.Invalid => L.F("{0}：key 不可用，请检查后重新填写", ai.Current.Name),
            AiClient.KeyState.Unreachable => $"{ai.Current.Name}：{ai.CheckError ?? "暂时连不上，请检查网络或代理"}",
            _ => L.F("{0}（尚未验证）", ai.Describe),
        });
        KeyState.Foreground = ai.State is AiClient.KeyState.Invalid or AiClient.KeyState.Unreachable ? Theme.Warn
            : ai.State == AiClient.KeyState.Ok ? Theme.OnAir : Theme.Ink;
    }

    private void Active_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || TagOf(ActiveBox) is not { Length: > 0 } id) return;
        model.Settings.ActiveProviderId = id;
        model.SaveSettings();
        model.Ai.Refresh();
        _ = model.Ai.Check();
        LoadEditor(model.Settings.ActiveProvider());
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        // A new service starts as the aggregator (one key, many models); the platform is one click away.
        var p = ProviderPreset.Find("ucloud").Create();
        if (model.Settings.Providers.Any(x => x.Name == p.Name)) p.Name += $" {model.Settings.Providers.Count + 1}";
        model.Settings.Providers.Add(p);
        model.Settings.ActiveProviderId = p.Id;
        model.SaveSettings();
        model.Ai.Refresh();
        RenderProviders();
        RenderKey();
        PresetBox.Focus();
    }

    /// Picking a platform fills in its address and a model; the name follows unless the user set one.
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || editing == null) return;
        var info = ProviderPreset.Find(TagOf(PresetBox));
        var oldInfo = editing.Info;
        if (NameBox.Text.Trim().Length == 0 || NameBox.Text.StartsWith(oldInfo.Name)) NameBox.Text = info.Name;
        if (info.BaseUrl.Length > 0 || oldInfo.Id != "custom") UrlBox.Text = info.BaseUrl;
        SetModels(info.Models, info.Models.FirstOrDefault() ?? "");
        RenderPresetHints(info, editing.Id);
    }

    /// The editor's values into the service (the key only when one was typed).
    private bool ApplyEditor()
    {
        if (editing == null) return false;
        var info = ProviderPreset.Find(TagOf(PresetBox));
        var url = info.NormalizeBaseUrl(UrlBox.Text);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            UiText.Set(ModelHint, "Text", () => L.T("Base URL 不对，应该像 https://api.example.com/v1"));
            ModelHint.Foreground = Theme.Warn;
            return false;
        }
        ModelHint.ClearValue(ForegroundProperty);
        editing.Preset = TagOf(PresetBox) is { Length: > 0 } preset ? preset : "custom";
        editing.Name = NameBox.Text.Trim() is { Length: > 0 } n ? n : editing.Info.Name;
        editing.BaseUrl = url.TrimEnd('/');
        UrlBox.Text = editing.BaseUrl;
        editing.Model = ModelBox.Text.Trim();
        if (KeyBox.Password.Trim() is { Length: > 0 } key) AIKey.Save(editing.Id, key);
        KeyBox.Clear();
        model.SaveSettings();
        return true;
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyEditor() || editing == null) return;
        if (editing.Model.Length == 0)
        {
            UiText.Set(ModelHint, "Text", () => L.T("请先选择或输入一个模型"));
            ModelHint.Foreground = Theme.Warn;
            return;
        }
        model.Settings.ActiveProviderId = editing.Id;
        model.SaveSettings();
        SaveBtn.IsEnabled = false;
        model.Ai.Refresh();
        RenderProviders();
        await model.Ai.Check();
        SaveBtn.IsEnabled = true;
        RenderKey();
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyEditor() || editing == null) return;
        FetchBtn.IsEnabled = false;
        UiText.Set(ModelHint, "Text", () => L.T("正在获取…"));
        try
        {
            var list = await AiClient.ListModels(editing);
            SetModels(list, editing.Model);
            UiText.Set(ModelHint, "Text", () => list.Count == 0 ? L.T("这个服务没有返回模型，请直接输入模型名") : L.F("共 {0} 个模型，点右侧箭头选择", list.Count));
            if (list.Count > 0) ModelBox.IsDropDownOpen = true;
        }
        catch (AiException ex) when (ex.Failure == AiFailure.InvalidKey)
        {
            UiText.Set(ModelHint, "Text", () => L.T("key 不对或没有权限，请检查"));
        }
        catch (Exception)
        {
            UiText.Set(ModelHint, "Text", () => L.T("这个服务不支持获取模型列表，请直接输入模型名"));
        }
        FetchBtn.IsEnabled = true;
    }

    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (editing == null) return;
        if (!Dialog.Confirm(this, L.F("删除「{0}」？", editing.Name), L.T("这个 AI 服务和它的 API key 会一起删除。"), L.T("删除"), danger: true)) return;
        AIKey.Remove(editing.Id);
        model.Settings.Providers.Remove(editing);
        if (model.Settings.ActiveProviderId == editing.Id) model.Settings.ActiveProviderId = model.Settings.Providers.FirstOrDefault()?.Id;
        model.SaveSettings();
        model.Ai.Refresh();
        if (model.Ai.HasKey) _ = model.Ai.Check();
        RenderProviders();
        RenderKey();
    }

    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        var url = ProviderPreset.Find(TagOf(PresetBox)).KeyUrl;
        if (url.Length > 0) Native.Open(url);
    }

    // MARK: Speaking

    private string SpeakLanguage()
    {
        var t = TagOf(SpeakTargetBox);
        return t == "auto" || t.Length == 0 ? model.Speak?.Target() ?? "en" : t;
    }

    private void RenderVoices()
    {
        var lang = SpeakLanguage();
        var voices = Voices.All.Where(v => Voices.Fits(v, lang)).ToList();
        var was = loading;
        loading = true;
        VoiceBox.Items.Clear();
        var preferred = Voices.For(lang, model.Settings.Voices.GetValueOrDefault(lang));
        foreach (var v in voices)
        {
            var item = new ComboBoxItem { Content = v.DisplayName, Tag = v.Id };
            VoiceBox.Items.Add(item);
            if (v.Id == preferred?.Id) VoiceBox.SelectedItem = item;
        }
        loading = was;
        var name = L.T(Languages.ChineseName(lang));
        var ming = model.Ai.HasMingVoices && SpeakEngine.MingVoice(lang) != null;
        var missing = voices.Count == 0;
        VoiceRow.Visibility = missing && !ming ? Visibility.Collapsed : Visibility.Visible;
        VoiceMissing.Visibility = missing && !ming ? Visibility.Visible : Visibility.Collapsed;
        VoiceSettingsBtn.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (!installingVoice)
        {
            UiText.Set(VoiceMissingText, "Text", () => L.F("这台电脑还没有{0}语音", name));
            UiText.Set(VoiceInstallBtn, "Content", () => L.F("一键安装{0}语音", name));
            VoiceInstallBtn.IsEnabled = true;
        }
        UiText.Set(VoiceHelp, "Text", () => ming && !model.Settings.Voices.ContainsKey(lang)
            ? L.F("当前使用蚂蚁百灵：{0}默认用 Ming 合成；选一个 Windows 声音则改用它。", name)
            : missing
                ? L.F("安装 Windows 自带的{0}语音包（从 Windows 更新下载，约几十 MB，需要管理员权限）。装好后在这里选声音、试听。", name)
                : L.F("播报{0}时使用的 Windows 语音。", name));
    }

    private bool installingVoice;

    private async void VoiceInstall_Click(object sender, RoutedEventArgs e)
    {
        var lang = SpeakLanguage();
        var name = L.T(Languages.ChineseName(lang));
        installingVoice = true;
        VoiceInstallBtn.IsEnabled = false;
        UiText.Set(VoiceInstallBtn, "Content", () => L.T("安装中…"));
        // Windows reports no progress to us: show the time so far, so a slow download doesn't look frozen.
        var started = DateTime.Now;
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) =>
        {
            var t = DateTime.Now - started;
            UiText.Set(VoiceMissingText, "Text", () => L.F("正在从 Windows 更新下载{0}语音 · 已用 {1}:{2:00}", name, (int)t.TotalMinutes, t.Seconds));
        };
        tick.Start();
        UiText.Set(VoiceMissingText, "Text", () => L.F("正在从 Windows 更新下载{0}语音 · 已用 0:00", name));
        UiText.Set(VoiceHelp, "Text", () => L.T("Windows 更新有时较慢，可能要几分钟；可以先关掉这个窗口，装好后重新打开设置就能选声音。"));
        var result = await Voices.Install(lang);
        tick.Stop();
        installingVoice = false;
        RenderVoices();
        UiText.Set(VoiceHelp, "Text", () => result switch
        {
            Voices.InstallResult.Installed => L.F("✓ {0}语音装好了，可以试听。", name),
            Voices.InstallResult.NeedsRestart => L.F("{0}语音已经安装，重启会译后就能在这里选到。", name),
            Voices.InstallResult.Cancelled => L.T("安装已取消。"),
            _ => L.F("没能自动安装（可能是公司电脑限制了 Windows 更新）。可以点下面的按钮，在系统设置 › 语音 › 添加语音 里手动添加{0}。", name),
        });
    }

    private void VoiceSettings_Click(object sender, RoutedEventArgs e) => Native.Open("ms-settings:speech");

    private void Voice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || VoiceBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        model.Settings.Voices[SpeakLanguage()] = id;
        model.SaveSettings();
    }

    private async void TryVoice_Click(object sender, RoutedEventArgs e)
    {
        var lang = SpeakLanguage();
        var sample = lang switch
        {
            "zh-CN" => "你好，我这边能听清楚。",
            "zh-TW" => "你好，我這邊聽得很清楚。",
            "ja" => "こんにちは、よく聞こえています。",
            "ko" => "안녕하세요, 잘 들립니다.",
            "es" => "Hola, te escucho bien.",
            "fr" => "Bonjour, je vous entends bien.",
            "de" => "Hallo, ich höre Sie gut.",
            "pt" => "Olá, estou ouvindo bem.",
            _ => "Hi, I can hear you clearly.",
        };
        if (!await model.EnsureEngines()) return;
        await model.Speak!.SayIntoRoom(sample, lang, intoRoom: false);
    }

    private bool installing;

    private void ListenMic_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.Settings.ListenMic = ListenMicOn.IsChecked == true;
        model.SaveSettings();
        RenderFrom();
    }

    private void RenderFrom()
    {
        var mic = model.Settings.ListenMic;
        ListenMicOn.IsChecked = mic;
        ListenMicRow.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        UiText.Set(FromHelp, "Text", () => mic
            ? L.T("电脑放出来的声音（对方）和麦克风（你自己、会议室里的人）分开识别，两边同时说话也都能记下。用音箱外放时，麦克风收到的回声会自动去掉，同一句不会记两遍。")
            : L.T("只听电脑放出来的声音，你自己和身边的人说的话不会记下来。"));
        if (model.IsListening) UiText.Set(FromHelp, "Text", () => L.T("改动从下一场会议开始生效。"));
    }


    private void FillMics()
    {
        var was = loading;
        loading = true;
        Fill(ListenMicBox, [("", L.T("自动（系统麦克风）")), .. DefaultDevices.Mics().Select(m => (m.Id, m.Name))], model.Settings.ListenMicId ?? "");
        Fill(MicBox, [("", L.T("自动（系统原来的麦克风）")), .. DefaultDevices.Mics().Select(m => (m.Id, m.Name))], model.Settings.SpeakMicId ?? "");
        loading = was;
    }

    private void RenderCable()
    {
        var ok = VirtualCable.Installed;
        UiText.Set(CableText, "Text", () => ok ? L.T("✓ 已安装 VB-CABLE 虚拟麦克风") : L.T("还没有虚拟麦克风"));
        UiText.Set(CableHelp, "Text", () => ok
            ? model.Routing.Routed ? L.T("系统麦克风现在是 CABLE Output，播报会送进会议。") : L.T("同声传译打开后，播报会从 CABLE Output 送进会议。")
            : L.T("同声传译要靠它把译好的声音送进会议。点「一键安装」会从 VB-Audio 官网下载免费的 VB-CABLE，校验后打开它的安装程序，需要管理员权限。"));
        CableBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        CableBtn.IsEnabled = true;
        UiText.Set(CableBtn, "Content", () => L.T("一键安装"));
        RouteOn.IsEnabled = ok;
        FillMics();
    }

    private async void Cable_Click(object sender, RoutedEventArgs e)
    {
        installing = true;
        CableBtn.IsEnabled = false;
        UiText.Set(CableBtn, "Content", () => L.T("安装中…"));
        var message = await model.InstallCable(new Progress<string>(m => CableHelp.Text = m));
        installing = false;
        RenderCable();
        if (message != null) CableHelp.Text = message;
    }

    // MARK: Updates

    private void RenderUpdates()
    {
        var u = App.Updates;
        VersionText.Text = $"{Brand.Name} {u.Version}";
        var found = u.AvailableVersion;
        UiText.Set(UpdateBtn, "Content", () => found != null ? L.F("查看新版本 {0}", found) : L.T("检查更新"));
        UpdateBtn.Style = (Style)FindResource(found != null ? "PrimaryBtn" : "Btn");
        UiText.Set(UpdateText, "Text", () => u.Status.Length > 0 ? u.Status
            : u.Installed ? L.T("每 6 小时自动检查一次新版本，发现后会问你要不要更新。") : L.T("当前不是安装版（开发版），不能自动更新。"));
    }

    /// Checks GitHub; a newer version opens the 发现新版本 box, where the user decides.
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        var found = App.Updates.AvailableVersion != null || await App.Updates.Check();
        UpdateBtn.IsEnabled = true;
        if (found) UpdateWindow.ShowFor(model, App.Updates);
    }

    // MARK: General

    private void RenderModels()
    {
        UiText.Set(ModelState, "Text", () => model.Models.Ready ? L.T("识别模型已就绪：中文、英语、日语、韩语、粤语，以及发言人区分") : L.T("识别模型未下载，首次收听前会自动下载"));
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Native.Open(Paths.Roaming);

    private void OpenLog_Click(object sender, RoutedEventArgs e) => Native.Open(Paths.DebugLog);
}
