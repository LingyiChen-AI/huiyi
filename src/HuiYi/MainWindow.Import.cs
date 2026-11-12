using System.Windows;

namespace HuiYi.App;

public partial class MainWindow
{
    private void ImportMedia_Click(object sender, RoutedEventArgs e)
    {
        var window = new MediaImportWindow(model) { Owner = this };
        if (window.ShowDialog() != true || window.ImportedId is not { } id) return;
        selected = id;
        SearchBox.Clear();
        archiveScope = 0;
        ActiveScope.IsChecked = true;
        foreach (var group in treeGroups) if (group.Id == "") group.IsExpanded = true;
        RefreshList();
        ShowDetail(id, keepTab: false);
    }
}
