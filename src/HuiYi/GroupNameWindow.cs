using HuiYi.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HuiYi.App;

/// A focused create flow also moves a meeting in the same persisted operation when opened from its menu.
public sealed class GroupNameWindow : Window
{
    public SessionGroup? Created { get; private set; }

    public GroupNameWindow(SessionStore store, string? meetingId = null, SessionGroup? editGroup = null)
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.Transparent;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI");
        UiText.Set(this, "Title", () => editGroup == null ? L.T("新建分组") : L.T("重命名分组"));
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
        body.Children.Add(UiText.Bind(new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink,
            Margin = new Thickness(0, 0, 0, 16) }, ("Text", () => editGroup == null ? L.T("新建分组") : L.T("重命名分组"))));
        body.Children.Add(UiText.Bind(new TextBlock { FontSize = 12, Foreground = Theme.Grey, Margin = new Thickness(0, 0, 0, 6) },
            ("Text", () => L.T("分组名称"))));
        var name = UiText.Bind(new TextBox { Style = (Style)FindResource("Field"), MaxLength = 80, Text = editGroup?.Name ?? "" },
            ("Tag", () => L.T("例如：项目周会、客户沟通")));
        body.Children.Add(name);
        var error = new TextBlock { Foreground = Theme.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(error);
        if (meetingId != null)
            body.Children.Add(UiText.Bind(new TextBlock { Foreground = Theme.Grey, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0) }, ("Text", () => L.T("新建后将会议移入此分组。"))));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0) };
        var cancel = UiText.Bind(new Button { IsCancel = true, Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) },
            ("Content", () => L.T("取消")));
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        var create = UiText.Bind(new Button { IsDefault = true, Style = (Style)FindResource("PrimaryBtn") },
            ("Content", () => editGroup == null ? L.T("创建分组") : L.T("保存")));
        create.Click += (_, _) =>
        {
            var value = name.Text.Trim();
            string? failure = value.Length == 0 ? "请输入分组名称"
                : store.Groups.Any(g => g.Id != editGroup?.Id && string.Equals(g.Name, value, StringComparison.OrdinalIgnoreCase)) ? "分组名称已存在" : null;
            if (failure == null)
            {
                try
                {
                    if (editGroup == null) Created = store.CreateGroup(value, meetingId);
                    else store.RenameGroup(editGroup.Id, value);
                    DialogResult = true;
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { failure = e is IOException or UnauthorizedAccessException ? "保存失败，请检查数据目录的写入权限后重试。" : "会议或分组已变更，请重新选择。"; }
            }
            UiText.Set(error, "Text", () => L.T(failure));
            error.Visibility = Visibility.Visible;
        };
        buttons.Children.Add(create);
        body.Children.Add(buttons);
        Content = new Border
        {
            Background = Theme.Surface, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Margin = new Thickness(12), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = .16 }
        };
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
