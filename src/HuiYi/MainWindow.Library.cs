using HuiYi.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HuiYi.App;

public partial class MainWindow
{
    private int archiveScope;
    private readonly Dictionary<string, bool> expandedGroups = [];
    private List<SessionGroupNode> treeGroups = [];
    private string treeQuery = "";
    private int treeScope;

    private void BuildMeetingTree(IReadOnlyList<SessionRow> rows, string query)
    {
        foreach (var group in treeGroups) expandedGroups[group.Id] = group.IsExpanded;
        var byGroup = rows.GroupBy(r => model.Store.GroupOf(r.Id) ?? "").ToDictionary(g => g.Key, g => g.ToList());
        var groups = new List<SessionGroup> { new("", L.T("未分组")) };
        groups.AddRange(model.Store.Groups);
        treeGroups = groups.Select(group =>
        {
            var children = byGroup.GetValueOrDefault(group.Id) ?? [];
            return new SessionGroupNode(group.Id, group.Name, children)
            {
                IsExpanded = query.Length > 0 && (query != treeQuery || archiveScope != treeScope)
                    ? children.Count > 0 : expandedGroups.GetValueOrDefault(group.Id, true)
            };
        }).ToList();
        treeQuery = query;
        treeScope = archiveScope;
        SessionList.ItemsSource = treeGroups;
    }

    private bool MatchesArchiveScope(string id)
    {
        var archived = model.Store.IsArchived(id);
        return archiveScope == 1 || (archiveScope == 2 ? archived : !archived);
    }

    private bool InLibraryScope(Session session) => MatchesArchiveScope(session.Id);

    private string LibraryMeta(string id, bool includeArchive = true)
    {
        var groupId = model.Store.GroupOf(id);
        var group = model.Store.Groups.FirstOrDefault(g => g.Id == groupId);
        return (group == null ? "" : " · " + group.Name) + (includeArchive && model.Store.IsArchived(id) ? " · " + L.T("已归档") : "");
    }

    private void RenderLibraryEmpty(string query)
    {
        UiText.Set(EmptyTitle, "Text", () => query.Length > 0 ? L.T("没有匹配的会议")
            : archiveScope == 2 ? L.T("暂无已归档会议")
            : L.T("还没有会议记录"));
        UiText.Set(EmptyHint, "Text", () => query.Length > 0 || archiveScope != 0
            ? L.T("试试其他关键词，或调整分组与归档范围。")
            : L.T("点左上角「开始收听」，或单击任务栏通知区域的图标。会议里听到的内容会整理在这里。"));
    }

    private void LibraryScope_Checked(object sender, RoutedEventArgs e)
    {
        if (SessionList == null || sender is not RadioButton { Tag: string value }) return;
        archiveScope = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        searchTimer.Stop();
        RefreshList();
    }

    private void RefreshSelectedDetails()
    {
        if (selected == null) return;
        var session = model.Store.Get(selected);
        var query = SearchBox.Text.Trim();
        if (session != null && InLibraryScope(session) && (SessionSearch.Matches(session, query)
            || DisplayTitle(session).Contains(query, StringComparison.OrdinalIgnoreCase)))
            ShowDetail(session.Id, keepTab: true);
        else RefreshList();
    }

    private void LibraryAction(Action action)
    {
        try
        {
            action();
            LibraryError.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            UiText.Set(LibraryError, "Text", () => e is IOException or UnauthorizedAccessException
                ? L.T("保存失败，请检查数据目录的写入权限后重试。") : L.T("会议或分组已变更，请重新选择。"));
            LibraryError.Visibility = Visibility.Visible;
        }
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e) => OpenNewGroup(null);

    private void OpenNewGroup(string? meetingId)
    {
        var dialog = new GroupNameWindow(model.Store, meetingId) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Created == null) return;
        if (meetingId == null) expandedGroups[dialog.Created.Id] = true;
        RefreshList();
    }

    private void TreeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { TemplatedParent: TreeViewItem item }) return;
        OpenTreeMenu(item, (FrameworkElement)sender);
        e.Handled = true;
    }

    private void TreeMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        e.Handled = true;
        OpenTreeMenu(item, item);
    }

    private void OpenTreeMenu(TreeViewItem item, FrameworkElement target)
    {
        if (item.ContextMenu is not { } menu) return;
        menu.Items.Clear();
        if (item.DataContext is SessionRow row)
        {
            if (model.Store.Get(row.Id) is not { } session) return;
            row.IsSelected = true;
            ShowDetail(row.Id, keepTab: false);
            PopulateMeetingMenu(menu, session);
        }
        else if (item.DataContext is SessionGroupNode group) PopulateGroupMenu(menu, group);
        else return;
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void PopulateGroupMenu(ContextMenu menu, SessionGroupNode node)
    {
        var group = model.Store.Groups.FirstOrDefault(g => g.Id == node.Id);
        var rename = UiText.Bind(new MenuItem { IsEnabled = group != null }, ("Header", () => L.T("重命名分组")));
        rename.Click += (_, _) =>
        {
            menu.IsOpen = false;
            if (group != null) new GroupNameWindow(model.Store, editGroup: group) { Owner = this }.ShowDialog();
        };
        menu.Items.Add(rename);
        var occupied = group != null && model.Store.GroupHasMeetings(group.Id);
        var delete = UiText.Bind(new MenuItem { IsEnabled = group != null && !occupied },
            ("Header", () => L.T("删除分组")), ("ToolTip", () => occupied ? L.T("请先移出或删除分组内的会议（含归档）。") : L.T("删除分组")));
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(delete, true);
        delete.Click += (_, _) =>
        {
            menu.IsOpen = false;
            if (group == null) return;
            if (Dialog.Confirm(this, L.T("删除分组"), L.F("删除分组「{0}」？", group.Name), L.T("删除"), danger: true))
                LibraryAction(() => model.Store.DeleteGroup(group.Id));
        };
        menu.Items.Add(delete);
    }

    private void PopulateMeetingMenu(ContextMenu menu, Session session)
    {
        var id = session.Id;
        var currentGroup = model.Store.GroupOf(id);
        var move = UiText.Bind(new MenuItem(), ("Header", () => L.T("移动到分组")));
        void AddGroup(string? groupId, string label)
        {
            var item = new MenuItem { Header = new TextBlock { Text = label, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = label, IsCheckable = true,
                IsChecked = currentGroup == groupId, MaxWidth = 300 };
            item.Click += (_, _) => LibraryAction(() => model.Store.MoveToGroup(id, groupId));
            move.Items.Add(item);
        }
        var create = UiText.Bind(new MenuItem(), ("Header", () => "＋ " + L.T("新建分组")));
        create.Click += (_, _) => { menu.IsOpen = false; OpenNewGroup(id); };
        move.Items.Add(create);
        move.Items.Add(new Separator());
        AddGroup(null, L.T("未分组"));
        foreach (var group in model.Store.Groups) AddGroup(group.Id, group.Name);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        var archived = model.Store.IsArchived(id);
        var archive = UiText.Bind(new MenuItem { IsEnabled = id != model.Store.LiveId },
            ("Header", () => archived ? L.T("恢复") : L.T("归档")));
        archive.Click += (_, _) => LibraryAction(() => model.Store.SetArchived(id, !archived));
        menu.Items.Add(archive);
        menu.Items.Add(new Separator());
        var delete = UiText.Bind(new MenuItem { IsEnabled = id != model.Store.LiveId,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0x48, 0x4D)) },
            ("Header", () => L.T("删除")));
        delete.Click += (_, _) => { menu.IsOpen = false; DeleteSession(id); };
        menu.Items.Add(delete);
    }

}
