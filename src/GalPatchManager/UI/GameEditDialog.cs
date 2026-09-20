using System.Drawing;
using System.Windows.Forms;

namespace GalPatchManager.UI;

/// <summary>手动添加游戏 / 修正识别结果的对话框。</summary>
public sealed class GameEditDialog : Form
{
    private readonly TextBox _nameBox = UiTheme.CreateTextBox(false, 360);
    private readonly TextBox _dirBox = UiTheme.CreateTextBox(false, 300);
    private readonly TextBox _keywordsBox = UiTheme.CreateTextBox(false, 360);

    public string GameName => _nameBox.Text.Trim();

    public string InstallDir => _dirBox.Text.Trim();

    public List<string> Keywords => _keywordsBox.Text
        .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(k => k.Trim())
        .Where(k => k.Length > 0)
        .ToList();

    public GameEditDialog()
    {
        Text = "添加游戏";
        Font = UiTheme.FontNormal;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 230);
        BackColor = UiTheme.WindowBack;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(16),
            RowCount = 4,
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        table.Controls.Add(UiTheme.CreateLabel("游戏名称:"), 0, 0);
        table.Controls.Add(_nameBox, 1, 0);

        table.Controls.Add(UiTheme.CreateLabel("安装目录:"), 0, 1);
        table.Controls.Add(_dirBox, 1, 1);

        var browse = UiTheme.CreateButton("浏览...", width: 80);
        browse.Click += (_, _) => BrowseFolder();
        table.Controls.Add(browse, 2, 1);

        table.Controls.Add(UiTheme.CreateLabel("关键词:"), 0, 2);
        table.Controls.Add(_keywordsBox, 1, 2);

        var hint = UiTheme.CreateLabel(
            "关键词用于把补丁文件名匹配到该游戏，多个用逗号分隔（可留空）。",
            color: UiTheme.TextSecondary);
        table.Controls.Add(hint, 1, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 16, 0),
        };

        var ok = UiTheme.CreateButton("确定", primary: true, width: 90);
        ok.Click += (_, _) =>
        {
            if (GameName.Length == 0 || InstallDir.Length == 0)
            {
                MessageBox.Show(this, "游戏名称和安装目录都必须填写。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(InstallDir))
            {
                var r = MessageBox.Show(this,
                    $"目录不存在：{InstallDir}\r\n仍然要添加吗？",
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (r != DialogResult.Yes) return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = UiTheme.CreateButton("取消", width: 90);
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(table);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择游戏安装目录",
            ShowNewFolderButton = false,
        };

        if (Directory.Exists(_dirBox.Text)) dialog.SelectedPath = _dirBox.Text;

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _dirBox.Text = dialog.SelectedPath;

            if (_nameBox.Text.Trim().Length == 0)
            {
                _nameBox.Text = Path.GetFileName(dialog.SelectedPath.TrimEnd('\\', '/'));
            }
        }
    }
}
