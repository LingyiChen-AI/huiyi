using HuiYi.Localization;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HuiYi.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        L.SetLanguage("auto");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}

/// One page: where to install, and an install button. The work is Velopack's Setup.exe (embedded),
/// run silently with installto; it makes the shortcuts and the Apps & features entry, and the app
/// updates itself in place afterwards.
internal sealed class SetupForm : Form
{
    private const string PackId = "HuiYi.App";
    private static readonly Color Accent = Color.FromArgb(0x11, 0x9C, 0x7A);
    private static readonly Color Ink = Color.FromArgb(0x1E, 0x22, 0x35);
    private static readonly Color Grey = Color.FromArgb(0x6B, 0x70, 0x80);

    private readonly TextBox pathBox;
    private readonly Button browseBtn;
    private readonly Button installBtn;
    private readonly Button cancelBtn;
    private readonly Label statusLabel;
    private readonly ProgressBar progress;
    private readonly string existing;

    public SetupForm()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Text = L.T("安装 会译");
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 374);

        var logo = new PictureBox
        {
            Image = new Icon(Icon, 64, 64).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(32, 28, 56, 56),
        };
        var title = new Label
        {
            Text = L.T("安装 会译"), Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), ForeColor = Ink,
            AutoSize = true, Location = new Point(102, 28),
        };
        var versionLabel = new Label
        {
            Text = L.F("版本 {0}.{1}.{2}", version.Major, version.Minor, version.Build), ForeColor = Grey, AutoSize = true,
            Location = new Point(104 + TextRenderer.MeasureText(title.Text, title.Font).Width + 12, 38),
        };
        var subtitle = new Label
        {
            Text = L.T("Windows 上的 AI 会议助手：实时字幕与翻译、AI 纪要、同声传译"),
            ForeColor = Grey, AutoSize = false, Bounds = new Rectangle(104, 66, 424, 44),
        };

        var where = new Label { Text = L.T("安装位置"), ForeColor = Ink, Font = new Font(Font, FontStyle.Bold), AutoSize = true, Location = new Point(32, 118) };
        existing = InstalledAt();
        pathBox = new TextBox
        {
            Text = existing ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PackId),
            Bounds = new Rectangle(32, 144, 400, 28),
        };
        browseBtn = FlatButton(L.T("浏览…"), false);
        browseBtn.Bounds = new Rectangle(442, 142, 86, 30);
        browseBtn.Click += (_, _) => Browse();
        var help = new Label
        {
            Text = existing != null
                ? L.T("已经安装在这里，将覆盖安装为这个版本。要换位置，请先在 Windows「设置 › 应用」里卸载会译。")
                : L.T("程序约 300 MB；首次启动还会下载约 260 MB 的识别模型。装在当前用户下，不需要管理员权限，之后会自动更新。"),
            ForeColor = Grey, AutoSize = false, Bounds = new Rectangle(32, 180, 496, 64),
        };
        if (existing != null)
        {
            pathBox.ReadOnly = true;
            browseBtn.Enabled = false;
        }

        progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Bounds = new Rectangle(32, 252, 496, 6), Visible = false };
        statusLabel = new Label { ForeColor = Grey, AutoSize = false, Bounds = new Rectangle(32, 264, 496, 42) };

        installBtn = FlatButton(existing != null ? L.T("更新") : L.T("安装"), true);
        installBtn.Bounds = new Rectangle(428, 322, 100, 34);
        installBtn.Click += async (_, _) => await Install();
        cancelBtn = FlatButton(L.T("取消"), false);
        cancelBtn.Bounds = new Rectangle(318, 322, 100, 34);
        cancelBtn.Click += (_, _) => Close();
        AcceptButton = installBtn;
        // Open with the install button focused, not the path selected.
        Shown += (_, _) =>
        {
            ActiveControl = installBtn;
            pathBox.SelectionStart = pathBox.Text.Length;
            pathBox.SelectionLength = 0;
        };
        CancelButton = cancelBtn;

        Controls.AddRange([logo, title, versionLabel, subtitle, where, pathBox, browseBtn, help, progress, statusLabel, cancelBtn, installBtn]);
    }

    private Button FlatButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(0xD5, 0xD9, 0xE3);
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0x0E, 0x87, 0x6A) : Color.FromArgb(0xF3, 0xF5, 0xF9);
        return b;
    }

    /// Where Velopack installed it before (its Apps & features entry), if anywhere.
    private static string InstalledAt()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{PackId}");
        var dir = key?.GetValue("InstallLocation") as string;
        return dir != null && File.Exists(Path.Combine(dir, "Update.exe")) ? dir : null;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = L.T("选择安装会译的文件夹"), ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var chosen = dialog.SelectedPath;
        // A folder with other things in it gets its own 会译 subfolder, so uninstalling never touches them.
        var name = Path.GetFileName(chosen.TrimEnd('\\'));
        var empty = !Directory.EnumerateFileSystemEntries(chosen).Any();
        pathBox.Text = empty || name.Equals("HuiYi", StringComparison.OrdinalIgnoreCase) ? chosen : Path.Combine(chosen, "HuiYi");
    }

    /// Can we write there without administrator rights? (Program Files can't: nor could updates later.)
    private static bool Writable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".huiyi-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task Install()
    {
        var dir = pathBox.Text.Trim();
        if (dir.Length == 0 || !Path.IsPathRooted(dir))
        {
            MessageBox.Show(this, L.T("请选择一个完整的文件夹路径，例如 D:\\Apps\\HuiYi。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        dir = Path.GetFullPath(dir);
        if (!Writable(dir))
        {
            MessageBox.Show(this, L.T("没有权限写入这个文件夹（例如 Program Files 需要管理员权限，以后自动更新也会写不进去）。\n\n请换一个位置，例如 D:\\Apps\\HuiYi。"),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var running = Process.GetProcessesByName("HuiYi");
        if (running.Length > 0)
        {
            if (MessageBox.Show(this, "会译正在运行，安装前需要先关闭它（正在记录的会议会被中断）。\n\n现在关闭并继续吗？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            foreach (var p in running)
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }

        installBtn.Enabled = cancelBtn.Enabled = browseBtn.Enabled = pathBox.Enabled = false;
        progress.Visible = true;
        statusLabel.Text = L.T("正在安装，大约需要半分钟…");
        var (ok, error) = await Task.Run(() => RunVelopack(dir));
        progress.Visible = false;
        if (!ok)
        {
            statusLabel.Text = "";
            MessageBox.Show(this, L.T("安装没有成功：") + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            installBtn.Enabled = cancelBtn.Enabled = true;
            browseBtn.Enabled = pathBox.Enabled = existing == null;
            return;
        }
        statusLabel.Text = L.T("安装完成，正在打开会译…");
        var app = Path.Combine(dir, "current", "HuiYi.exe");
        try { if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app) }); }
        catch { }
        await Task.Delay(1200);
        Close();
    }

    private static (bool Ok, string Error) RunVelopack(string dir)
    {
        var temp = Path.Combine(Path.GetTempPath(), "HuiYi-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("VelopackSetup.exe");
            if (payload == null) return (false, L.T("安装包不完整（缺少程序文件），请重新下载。"));
            Directory.CreateDirectory(temp);
            var setup = Path.Combine(temp, "Setup.exe");
            using (var file = File.Create(setup)) payload.CopyTo(file);
            var log = Path.Combine(temp, "setup.log");
            using var p = Process.Start(new ProcessStartInfo(setup, $"--silent --installto \"{dir}\" --log \"{log}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            p.WaitForExit();
            if (p.ExitCode == 0 && File.Exists(Path.Combine(dir, "Update.exe"))) return (true, null);
            var tail = File.Exists(log) ? string.Join("\n", File.ReadAllLines(log).Reverse().Take(4).Reverse()) : "";
            return (false, L.F("安装程序返回 {0}。{1}", p.ExitCode, tail));
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
