using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>安装历史页：记录每次检测/安装的结果、错误信息与备份，并支持还原。</summary>
public sealed class HistoryTab : UserControl
{
    private readonly AppContext _ctx;
    private readonly FormMain _main;

    private readonly DataGridView _grid = new();
    private readonly TextBox _detailBox = new();
    private readonly Label _summary = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);

    public HistoryTab(AppContext ctx, FormMain main)
    {
        _ctx = ctx;
        _main = main;

        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBack;

        BuildLayout();
    }

    private void BuildLayout()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        var btnReload = UiTheme.CreateButton("刷新", width: 70);
        btnReload.Click += (_, _) => Reload();

        var btnOpenBackups = UiTheme.CreateButton("打开备份目录", width: 120);
        btnOpenBackups.Click += (_, _) => OpenFolder(Paths.BackupFolder);

        var btnRestore = UiTheme.CreateButton("还原该条备份", width: 130);
        btnRestore.Click += (_, _) => RestoreSelected();

        var btnExport = UiTheme.CreateButton("导出 CSV", width: 90);
        btnExport.Click += (_, _) => ExportCsv();

        var btnClear = UiTheme.CreateButton("清空历史", width: 90);
        btnClear.Click += (_, _) => ClearHistory();

        toolbar.Controls.Add(btnReload);
        toolbar.Controls.Add(btnOpenBackups);
        toolbar.Controls.Add(btnRestore);
        toolbar.Controls.Add(btnExport);
        toolbar.Controls.Add(btnClear);
        toolbar.Controls.Add(_summary);

        BuildGrid();

        _detailBox.Dock = DockStyle.Bottom;
        _detailBox.Height = 150;
        _detailBox.Multiline = true;
        _detailBox.ScrollBars = ScrollBars.Vertical;
        _detailBox.ReadOnly = true;
        _detailBox.Font = UiTheme.FontMono;
        _detailBox.BackColor = Color.FromArgb(250, 251, 253);

        Controls.Add(_grid);
        Controls.Add(_detailBox);
        Controls.Add(toolbar);
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = UiTheme.CardBack;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.Font = UiTheme.FontNormal;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 243, 247);
        _grid.ColumnHeadersDefaultCellStyle.Font = UiTheme.FontBold;
        _grid.ColumnHeadersHeight = 30;
        _grid.RowTemplate.Height = 26;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "时间",
            DataPropertyName = nameof(InstallRecord.FinishedAt),
            Width = 150,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss" },
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "游戏",
            DataPropertyName = nameof(InstallRecord.GameName),
            Width = 200,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "补丁文件",
            DataPropertyName = nameof(InstallRecord.PatchFileName),
            Width = 240,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "方式",
            DataPropertyName = nameof(InstallRecord.Source),
            Width = 80,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "结果",
            Name = "StatusText",
            Width = 90,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "文件数",
            DataPropertyName = nameof(InstallRecord.FilesCopied),
            Width = 70,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "字节数",
            DataPropertyName = nameof(InstallRecord.BytesCopied),
            Width = 100,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" },
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "说明 / 错误",
            DataPropertyName = nameof(InstallRecord.Message),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.RowPrePaint += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;

            if (_grid.Rows[e.RowIndex].DataBoundItem is not InstallRecord record) return;

            _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = record.Status switch
            {
                InstallStatus.Success => UiTheme.TextPrimary,
                InstallStatus.RolledBack => UiTheme.Warning,
                _ => UiTheme.Danger,
            };
        };
    }

    // ------------------------------------------------------------------ 数据

    public void Reload()
    {
        var records = _ctx.History
            .OrderByDescending(h => h.FinishedAt)
            .ToList();

        _grid.DataSource = new BindingSource { DataSource = records };
        _grid.Refresh();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is not InstallRecord record) continue;

            row.Cells["StatusText"].Value = record.Status switch
            {
                InstallStatus.Success => "成功",
                InstallStatus.Failed => "失败",
                InstallStatus.RolledBack => "已回滚",
                InstallStatus.Rejected => "已拒绝",
                _ => record.Status.ToString(),
            };
        }

        var success = records.Count(r => r.Status == InstallStatus.Success);
        var failed = records.Count(r => r.Status is InstallStatus.Failed or InstallStatus.RolledBack);

        _summary.Text = $"  共 {records.Count} 条（成功 {success} / 失败或回滚 {failed}）";

        ShowDetail();
    }

    private InstallRecord? SelectedRecord()
    {
        if (_grid.CurrentRow?.DataBoundItem is InstallRecord record) return record;

        return null;
    }

    private void ShowDetail()
    {
        var record = SelectedRecord();

        if (record is null)
        {
            _detailBox.Text = "";
            return;
        }

        var sb = new StringBuilder();

        sb.AppendLine($"补丁: {record.PatchFileName}");
        sb.AppendLine($"路径: {record.PatchPath}");
        sb.AppendLine($"SHA-256: {record.Sha256}");
        sb.AppendLine($"游戏: {record.GameName} ({record.GameId})");
        sb.AppendLine($"方式: {record.Source}");
        sb.AppendLine($"开始: {record.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"结束: {record.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"结果: {record.Status}");
        sb.AppendLine($"复制文件: {record.FilesCopied}，备份文件: {record.FilesBackedUp}，字节: {record.BytesCopied:N0}");
        sb.AppendLine($"备份批次: {record.BackupBatchId}");
        sb.AppendLine();

        if (record.Message.Length > 0)
        {
            sb.AppendLine("说明 / 错误:");
            sb.AppendLine(record.Message);
        }
        else
        {
            sb.AppendLine("（没有额外的说明或错误信息）");
        }

        _detailBox.Text = sb.ToString();
    }

    // ------------------------------------------------------------------ 操作

    private void RestoreSelected()
    {
        var record = SelectedRecord();

        if (record is null)
        {
            MessageBox.Show(this, "请先选择一条记录。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 找到该批次的备份
        var batchDir = Path.Combine(Paths.BackupFolder, record.BackupBatchId);

        if (!Directory.Exists(batchDir))
        {
            MessageBox.Show(this,
                $"找不到该批次的备份目录：\r\n{batchDir}\r\n\r\n可能已被清理。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var backups = new List<BackupEntry>();

        try
        {
            // 备份文件名格式为 8位随机码_原文件名，需要结合记录里的游戏目录还原
            var game = _ctx.Library.FindById(record.GameId);

            if (game is null)
            {
                MessageBox.Show(this,
                    "该记录对应的游戏已不在列表中，无法自动推断还原位置。\r\n"
                    + $"请手动把 {batchDir} 中的文件复制回游戏目录。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            foreach (var file in Directory.EnumerateFiles(batchDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var underscore = name.IndexOf('_');

                var originalName = underscore >= 0 ? name[(underscore + 1)..] : name;

                backups.Add(new BackupEntry
                {
                    BackupPath = file,
                    OriginalPath = Path.Combine(game.InstallDir, originalName),
                    GameId = record.GameId,
                    PatchKey = record.PatchKey,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举备份失败", ex);
            MessageBox.Show(this, $"枚举备份失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (backups.Count == 0)
        {
            MessageBox.Show(this, "该批次没有可还原的备份文件。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var preview = string.Join(Environment.NewLine,
            backups.Take(15).Select(b => $"{Path.GetFileName(b.BackupPath)}  ->  {b.OriginalPath}"));

        if (backups.Count > 15) preview += $"{Environment.NewLine}...（共 {backups.Count} 个）";

        var confirm = MessageBox.Show(this,
            $"将用备份覆盖回游戏目录，共 {backups.Count} 个文件：\r\n\r\n{preview}\r\n\r\n继续吗？",
            "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        var restored = _ctx.Installer.RestoreBackups(backups);

        MessageBox.Show(this, $"已还原 {restored} / {backups.Count} 个文件。", "完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

        _main.SetStatus($"已还原 {restored} 个文件。");
    }

    private void ExportCsv()
    {
        if (_ctx.History.Count == 0)
        {
            MessageBox.Show(this, "没有可导出的历史记录。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"补丁安装历史-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();

            sb.AppendLine("时间,游戏,补丁文件,方式,结果,复制文件数,备份文件数,字节数,备份批次,SHA256,说明");

            foreach (var r in _ctx.History.OrderByDescending(h => h.FinishedAt))
            {
                sb.AppendLine(string.Join(",",
                    Csv(r.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(r.GameName),
                    Csv(r.PatchFileName),
                    Csv(r.Source.ToString()),
                    Csv(r.Status.ToString()),
                    r.FilesCopied,
                    r.FilesBackedUp,
                    r.BytesCopied,
                    Csv(r.BackupBatchId),
                    Csv(r.Sha256),
                    Csv(r.Message)));
            }

            // 带 BOM，Excel 打开中文不乱码
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));

            _main.SetStatus($"已导出 {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error("导出 CSV 失败", ex);
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";
    }

    private void ClearHistory()
    {
        if (_ctx.History.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"将清空 {_ctx.History.Count} 条历史记录。\r\n"
            + "注意：这不会删除备份文件，也不会还原游戏文件。\r\n\r\n继续吗？",
            "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        _ctx.History.Clear();
        _ctx.NotifyHistoryChanged();
        Reload();

        _main.SetStatus("历史记录已清空。");
    }

    private void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, $"目录不存在：{folder}", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开目录失败", ex);
        }
    }
}
