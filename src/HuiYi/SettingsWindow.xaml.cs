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
        Title = $"{Brand.Name} · 设置";
        Icon = Icons.Mark(64, false);
        var s = model.Settings;

        Fill(TargetBox, [("auto", "自动"), .. Languages.Target.Select(l => (l.Code, l.Name))], s.Target);
        ModeNotes.IsChecked = s.Display == DisplayMode.Notes;
        ModeSubs.IsChecked = s.Display == DisplayMode.Subtitles;
        FontSlider.Value = s.SubtitleFontSize;
        FontText.Text = $"悬浮字幕字号  {s.SubtitleFontSize:0}";
        TranslateOn.IsChecked = s.TranslationOn;
        VoicesOn.IsChecked = s.SpeakerVoices;
        Capturable.IsChecked = s.NotesCapturable;

        Fill(ModelBox, LingClient.Models.Select(m => (m, m)).ToArray(), s.LingModel);
        Fill(NotesLangBox, [("auto", "跟字幕一致"), .. Languages.Target.Select(l => (l.Code, l.Name))], s.NotesLanguage);

        SpeakOn.IsChecked = s.SpeakEnabled;
        KeyCtrl.IsChecked = s.SpeakKey == SpeakKey.RightCtrl;
        KeyAlt.IsChecked = s.SpeakKey == SpeakKey.RightAlt;
        Fill(SpeakTargetBox, [("auto", "自动（对方用的语言）"), .. Languages.Target.Select(l => (l.Code, l.Name))], s.SpeakTarget);
        MonitorOn.IsChecked = s.SpeakMonitor;

        FollowApps.IsChecked = s.FollowMeetingApps;
        Fill(SilenceBox, [("Prompt", "先提醒再结束"), ("End", "直接结束"), ("Off", "不处理")], s.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), $"{m} 分钟")).ToArray(), s.SilenceMinutes.ToString());
        LoginOn.IsChecked = s.LaunchAtLogin;
        AboutText.Text = $"{Brand.Name} {typeof(App).Assembly.GetName().Version?.ToString(3)} · 语音识别与发言人区分都在本机完成。开源组件与许可见安装目录里的 NOTICE。";

        model.Ling.Changed += () => Dispatcher.BeginInvoke(RenderKey);
        Activated += (_, _) => { RenderCable(); RenderVoices(); RenderModels(); };
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

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var s = model.Settings;
        var oldDisplay = s.Display;
        var oldCapturable = s.NotesCapturable;
        s.Target = TagOf(TargetBox);
        s.Display = ModeSubs.IsChecked == true ? DisplayMode.Subtitles : DisplayMode.Notes;
        s.TranslationOn = TranslateOn.IsChecked == true;
        s.SpeakerVoices = VoicesOn.IsChecked == true;
        s.NotesCapturable = Capturable.IsChecked == true;
        s.LingModel = TagOf(ModelBox);
        s.NotesLanguage = TagOf(NotesLangBox);
        s.SpeakEnabled = SpeakOn.IsChecked == true;
        s.SpeakKey = KeyAlt.IsChecked == true ? SpeakKey.RightAlt : SpeakKey.RightCtrl;
        s.SpeakTarget = TagOf(SpeakTargetBox);
        s.SpeakMonitor = MonitorOn.IsChecked == true;
        s.FollowMeetingApps = FollowApps.IsChecked == true;
        s.SilenceEnd = Enum.TryParse<MeetingEndWatch.Silence>(TagOf(SilenceBox), out var sil) ? sil : MeetingEndWatch.Silence.Prompt;
        s.SilenceMinutes = int.TryParse(TagOf(SilenceMinBox), out var m) ? m : 10;
        if (s.LaunchAtLogin != (LoginOn.IsChecked == true))
        {
            s.LaunchAtLogin = LoginOn.IsChecked == true;
            Native.SetLaunchAtLogin(s.LaunchAtLogin);
        }
        model.SaveSettings();
        if (oldDisplay != s.Display || oldCapturable != s.NotesCapturable) ((App)Application.Current).SwitchDisplay();
        if (sender == SpeakTargetBox) RenderVoices();
    }

    private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontText == null) return;
        FontText.Text = $"悬浮字幕字号  {FontSlider.Value:0}";
        if (loading) return;
        model.Settings.SubtitleFontSize = FontSlider.Value;
        model.SaveSettings();
    }

    // MARK: Ling key

    private void RenderKey()
    {
        var ling = model.Ling;
        KeyState.Text = ling.State switch
        {
            LingClient.KeyState.None => "未填写",
            LingClient.KeyState.Checking => "验证中…",
            LingClient.KeyState.Ok => $"✓ 已验证  {AIKey.Masked(ling.Key ?? "")} · 响应 {ling.CheckSeconds:0.0} 秒",
            LingClient.KeyState.Invalid => "这个 key 不可用，请检查后重新填写",
            LingClient.KeyState.Unreachable => "暂时连不上 Ling，请检查网络或代理",
            _ => $"已保存 {AIKey.Masked(ling.Key ?? "")}",
        };
        KeyState.Foreground = ling.State is LingClient.KeyState.Invalid or LingClient.KeyState.Unreachable ? Theme.Warn : Theme.Ink;
        RemoveKeyBtn.Visibility = ling.HasKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) return;
        SaveKeyBtn.IsEnabled = false;
        await model.Ling.SetKey(key);
        KeyBox.Clear();
        SaveKeyBtn.IsEnabled = true;
        RenderKey();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        model.Ling.RemoveKey();
        RenderKey();
    }

    private void GetKey_Click(object sender, RoutedEventArgs e) => Native.Open("https://chat.ant-ling.com/platform/api");

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
        var ming = model.Ling.Usable && SpeakEngine.MingVoice(lang) != null;
        VoiceHelp.Text = ming && !model.Settings.Voices.ContainsKey(lang)
            ? $"已填 Ling key：{Languages.ChineseName(lang)}默认用 Ming 合成；选一个 Windows 声音则改用它。"
            : voices.Count == 0
                ? $"这台电脑没有{Languages.ChineseName(lang)}语音包：可在 Windows 设置 › 时间和语言 › 语音 中添加，或填入 Ling key 改用 Ming。"
                : $"播报{Languages.ChineseName(lang)}时使用的 Windows 语音。";
    }

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

    private void RenderCable()
    {
        var ok = VirtualCable.Installed;
        CableText.Text = ok ? "✓ 已检测到 VB-CABLE" : "未检测到虚拟麦克风";
        CableBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cable_Click(object sender, RoutedEventArgs e) => Native.Open(VirtualCable.Download);

    // MARK: General

    private void RenderModels()
    {
        ModelState.Text = model.Models.Ready ? "识别模型已就绪：中文、英语、日语、韩语、粤语，以及发言人区分" : "识别模型未下载，首次收听前会自动下载";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Native.Open(Paths.Roaming);

    private void OpenLog_Click(object sender, RoutedEventArgs e) => Native.Open(Paths.DebugLog);
}
