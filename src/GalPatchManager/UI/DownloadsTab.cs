using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>
/// 待处理补丁页：展示监控到的补丁、匹配游戏、预览安装计划、执行导入或安装 EXE。
/// </summary>
public sealed class DownloadsTab : UserControl
{
    private readonly AppContext _ctx;
    private readonly FormMain _main;

    private readonly DataGridView _grid = new();
    private readonly DataGridView _planGrid = new();
    private readonly TextBox _logBox = new();

    private readonly TextBox _fileBox = UiTheme.CreateTextBox(true, 420);
    private readonly TextBox _hashBox = UiTheme.CreateTextBox(true, 420);
    private readonly TextBox _reasonBox = UiTheme.CreateTextBox(true, 420);
    private readonly ComboBox _gameCombo = new();
    private readonly Label _stateLabel = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);

    private readonly Button _btnExtract;
    private readonly Button _btnInstall;
    private readonly Button _btnRunExe;
    private readonly Button _btnMatch;

    /// <summary>列表为空时显示的提示（覆盖在表格上方）。</summary>
    private readonly Label _emptyHint = new();

    private List<CopyStep> _currentPlan = new();
    private string? _currentExtractRoot;
    private bool _busy;

    public DownloadsTab(AppContext ctx, FormMain main)
    {
        _ctx = ctx;
        _main = main;

        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBack;

        _btnExtract = UiTheme.CreateButton("解压并预览", width: 110);
        _btnExtract.Click += async (_, _) => await ExtractAndPreviewAsync().ConfigureAwait(true);

        _btnInstall = UiTheme.CreateButton("安装到游戏", primary: true, width: 110);
        _btnInstall.Click += async (_, _) => await InstallAsync().ConfigureAwait(true);

        _btnRunExe = UiTheme.CreateButton("运行 EXE 补丁", width: 120);
        _btnRunExe.Click += async (_, _) => await RunExeAsync().ConfigureAwait(true);

        _btnMatch = UiTheme.CreateButton("手动匹配", width: 100);
        _btnMatch.Click += (_, _) => ApplyManualMatch();

        BuildLayout();
    }

    // ------------------------------------------------------------------ 布局

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

        var btnOpenFolder = UiTheme.CreateButton("打开下载目录", width: 110);
        btnOpenFolder.Click += (_, _) => OpenDownloadFolder();

        var btnRemove = UiTheme.CreateButton("忽略/移除", width: 100);
        btnRemove.Click += (_, _) => RemoveSelected();

        var btnClear = UiTheme.CreateButton("清理已处理", width: 100);
        btnClear.Click += (_, _) => ClearHandled();

        toolbar.Controls.Add(btnReload);
        toolbar.Controls.Add(btnOpenFolder);
        toolbar.Controls.Add(_btnExtract);
        toolbar.Controls.Add(_btnInstall);
        toolbar.Controls.Add(_btnRunExe);
        toolbar.Controls.Add(_btnMatch);
        toolbar.Controls.Add(btnRemove);
        toolbar.Controls.Add(btnClear);

        BuildGrid();
        BuildDetail();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 250,
            SplitterWidth = 6,
            BackColor = UiTheme.WindowBack,
        };

        split.Panel1.Controls.Add(_emptyHint);
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(BuildDetailContainer());

        Controls.Add(split);
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
            HeaderText = "文件名",
            DataPropertyName = nameof(PatchDownloadItem.FileName),
            Width = 300,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "大小",
            DataPropertyName = nameof(PatchDownloadItem.Size),
            Width = 100,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" },
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "状态",
            DataPropertyName = nameof(PatchDownloadItem.StateText),
            Width = 90,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "匹配游戏",
            Name = "MatchedGame",
            Width = 220,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "匹配依据",
            DataPropertyName = nameof(PatchDownloadItem.MatchReason),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "重复",
            DataPropertyName = nameof(PatchDownloadItem.IsDuplicate),
            Width = 50,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "错误",
            DataPropertyName = nameof(PatchDownloadItem.Error),
            Width = 220,
        });

        _grid.SelectionChanged += (_, _) => OnSelectionChanged();

        // 空列表提示：默认隐藏，由 RefreshGrid 控制
        _emptyHint.Dock = DockStyle.Fill;
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.ForeColor = UiTheme.TextSecondary;
        _emptyHint.BackColor = UiTheme.CardBack;
        _emptyHint.Font = UiTheme.FontNormal;
        _emptyHint.Visible = false;
        _emptyHint.Text = "";
    }

    private void BuildDetail()
    {
        _planGrid.Dock = DockStyle.Fill;
        _planGrid.BackgroundColor = UiTheme.CardBack;
        _planGrid.BorderStyle = BorderStyle.FixedSingle;
        _planGrid.AllowUserToAddRows = false;
        _planGrid.AllowUserToDeleteRows = false;
        _planGrid.ReadOnly = true;
        _planGrid.RowHeadersVisible = false;
        _planGrid.AutoGenerateColumns = false;
        _planGrid.Font = UiTheme.FontNormal;
        _planGrid.EnableHeadersVisualStyles = false;
        _planGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 243, 247);
        _planGrid.ColumnHeadersDefaultCellStyle.Font = UiTheme.FontBold;
        _planGrid.ColumnHeadersHeight = 28;
        _planGrid.RowTemplate.Height = 24;

        _planGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "动作",
            DataPropertyName = nameof(CopyStep.ActionText),
            Width = 70,
        });

        _planGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "将复制的文件（相对游戏目录）",
            DataPropertyName = nameof(CopyStep.RelativeTarget),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        _planGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "大小",
            DataPropertyName = nameof(CopyStep.SourceSize),
            Width = 110,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" },
        });

        _planGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "说明",
            DataPropertyName = nameof(CopyStep.BlockReason),
            Width = 240,
        });

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.ReadOnly = true;
        _logBox.Font = UiTheme.FontMono;
        _logBox.BackColor = Color.FromArgb(250, 251, 253);
    }

    private Control BuildDetailContainer()
    {
        var container = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.WindowBack };

        // 左侧：文件信息 + 手动匹配
        var left = new Panel
        {
            Dock = DockStyle.Left,
            Width = 460,
            BackColor = UiTheme.CardBack,
            Padding = new Padding(10),
        };

        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Height = 150,
        };

        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddInfo(string label, Control control)
        {
            info.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            info.Controls.Add(UiTheme.CreateLabel(label), 0, info.RowCount);
            info.Controls.Add(control, 1, info.RowCount);
            info.RowCount++;
        }

        info.RowCount = 0;
        AddInfo("文件:", _fileBox);
        AddInfo("SHA-256:", _hashBox);
        AddInfo("匹配:", _reasonBox);

        _gameCombo.Width = 300;
        _gameCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _gameCombo.Font = UiTheme.FontNormal;
        AddInfo("指定游戏:", _gameCombo);

        _stateLabel.Margin = new Padding(0, 6, 0, 0);

        left.Controls.Add(_stateLabel);
        left.Controls.Add(info);

        // 右侧：计划 + 日志
        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 130,
            SplitterWidth = 6,
        };

        right.Panel1.Controls.Add(_planGrid);
        right.Panel2.Controls.Add(_logBox);

        container.Controls.Add(right);
        container.Controls.Add(left);

        return container;
    }

    // ------------------------------------------------------------------ 数据

    public void Reload()
    {
        _gameCombo.DataSource = null;
        _gameCombo.DataSource = _ctx.Library.AllGames;
        _gameCombo.DisplayMember = nameof(GameEntry.Name);

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        var items = _ctx.Pending
            .OrderByDescending(p => p.FirstSeen)
            .ToList();

        _grid.DataSource = new BindingSource { DataSource = items };
        _grid.Refresh();

        // 手动填充“匹配游戏”列
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is not PatchDownloadItem item) continue;

            var game = _ctx.Library.FindById(item.MatchedGameId);
            row.Cells["MatchedGame"].Value = game?.Name ?? "";
        }

        UpdateEmptyHint(items.Count);

        _main.UpdateCounters();
    }

    /// <summary>没有待处理补丁时给出明确的操作指引，而不是一片空白。</summary>
    private void UpdateEmptyHint(int itemCount)
    {
        if (itemCount > 0)
        {
            _emptyHint.Visible = false;
            return;
        }

        var folder = _ctx.Settings.DownloadFolder;

        _emptyHint.Text =
            "暂无待处理补丁\r\n\r\n"
            + "把下载好的补丁（.zip / .7z / .rar / .exe）复制或拖到监控目录即可：\r\n"
            + folder + "\r\n\r\n"
            + (_ctx.Settings.OnlyNewFiles
                ? "当前为「只处理新下载的文件」模式：目录里早就存在的文件会被忽略，\r\n"
                  + "只有之后新增或内容有变化的文件才会出现在这里。"
                : "当前会列出监控目录里所有符合扩展名的文件。")
            + (Environment.NewLine + Environment.NewLine
               + $"Tip：小于 {_ctx.Settings.MinFileSizeMB} MB 的文件会被忽略（可在设置中调整）。");

        _emptyHint.Visible = true;
        _emptyHint.BringToFront();
    }

    private PatchDownloadItem? SelectedItem()
    {
        if (_grid.CurrentRow?.DataBoundItem is PatchDownloadItem item) return item;

        return null;
    }

    private void OnSelectionChanged()
    {
        var item = SelectedItem();

        _currentPlan = new List<CopyStep>();
        _currentExtractRoot = null;

        if (item is null)
        {
            _fileBox.Text = "";
            _hashBox.Text = "";
            _reasonBox.Text = "";
            _stateLabel.Text = "";
            _planGrid.DataSource = null;
            return;
        }

        _fileBox.Text = item.FilePath;
        _hashBox.Text = item.Sha256;
        _reasonBox.Text = item.MatchReason;
        _stateLabel.Text = $"状态: {item.State}   稳定性判定: {item.StabilityChecks}   大小: {item.Size:N0} 字节"
                           + (item.IsComplete ? "   下载已完成" : "   下载进行中")
                           + (item.IsDuplicate ? "   ⚠ 该补丁已安装过" : "");

        if (item.MatchedGameId is not null)
        {
            var game = _ctx.Library.FindById(item.MatchedGameId);

            if (game is not null)
            {
                var index = (_gameCombo.DataSource as List<GameEntry>)?.FindIndex(g => g.Id == game.Id) ?? -1;

                if (index >= 0) _gameCombo.SelectedIndex = index;
            }
        }

        BuildPreviewPlan(item);
    }

    /// <summary>根据文件类型生成预览计划（压缩包先看条目，EXE 走运行流程）。</summary>
    private void BuildPreviewPlan(PatchDownloadItem item)
    {
        _logBox.Clear();
        _planGrid.DataSource = null;

        if (!File.Exists(item.FilePath))
        {
            AppendLog($"文件已不存在: {item.FilePath}");
            return;
        }

        if (item.IsExe || item.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var check = ExeTrustPolicy.Evaluate(item.FilePath, item.Sha256, _ctx.Settings);

            item.IsTrustedExe = check.IsTrusted;
            item.TrustedRuleName = check.RuleName;
            item.AutoRunAllowed = check.AutoRunAllowed;

            AppendLog("这是一个 EXE 补丁，不会解压。");
            AppendLog($"可信判定: {(check.IsTrusted ? "可信" : "不可信")}");
            AppendLog($"判定说明: {check.Reason}");
            AppendLog($"是否允许自动运行: {(check.AutoRunAllowed ? "是" : "否（需要你确认后手动运行）")}");
            AppendLog("");
            AppendLog("点击“运行 EXE 补丁”后会提示确认，程序不会绕过 Windows 的权限提示。");
            return;
        }

        if (!ArchiveService.IsArchiveExtension(item.FilePath))
        {
            AppendLog("既不是受支持的压缩包，也不是 .exe 补丁，无法处理。");
            return;
        }

        var info = ArchiveService.Probe(item.FilePath);

        if (info.Entries.Count == 0)
        {
            AppendLog("压缩包为空或无法读取。");
            return;
        }

        var game = _ctx.Library.FindById(item.MatchedGameId);

        AppendLog($"压缩包类型: {info.KindText}");
        AppendLog($"条目数: {info.FileCount}，解压后约 {info.TotalBytes:N0} 字节");
        AppendLog(info.RootPrefix is null
            ? "根目录结构: 压缩包内是散装文件（解压后会直接铺到游戏目录）"
            : $"根目录结构: 检测到统一根目录「{info.RootPrefix}」（解压后会自动剥离）");

        if (game is null)
        {
            AppendLog("");
            AppendLog("尚未确定对应游戏，请先在上方选择游戏并点击“手动匹配”，");
            AppendLog("然后点击“解压并预览”查看将要复制的文件。");
            return;
        }

        AppendLog($"目标游戏: {game.Name}");
        AppendLog($"目标目录: {game.InstallDir}");
        AppendLog("");
        AppendLog("点击“解压并预览”可查看完整复制清单（解压到临时目录，不会直接写入游戏目录）。");

        var preview = _ctx.Installer.PreviewArchiveEntries(info, game.InstallDir);
        _planGrid.DataSource = new BindingSource { DataSource = preview };
        _planGrid.Refresh();
    }

    // ------------------------------------------------------------------ 操作

    private async Task ExtractAndPreviewAsync()
    {
        if (_busy) return;

        var item = SelectedItem();

        if (item is null)
        {
            MessageBox.Show(this, "请先选择一个待处理补丁。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var game = _ctx.Library.FindById(item.MatchedGameId);

        if (game is null)
        {
            MessageBox.Show(this, "请先为该补丁指定游戏（在上面选择游戏后点击“手动匹配”）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (item.IsExe || item.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "EXE 补丁不需要解压，请使用“运行 EXE 补丁”。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var extractRoot = Path.Combine(Paths.TempFolder, item.Id);

            if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);

            Directory.CreateDirectory(extractRoot);

            _main.SetStatus("正在解压补丁到临时目录...");
            AppendLog($"开始解压: {item.FilePath}");
            AppendLog($"临时目录: {extractRoot}");

            var result = await Task.Run(() => ArchiveService.Extract(
                item.FilePath,
                extractRoot,
                preferOriginalStructure: false,
                CancellationToken.None,
                (done, total) =>
                {
                    if (done % 50 == 0 || done == total)
                    {
                        BeginInvoke(new Action(() =>
                            _main.SetStatus($"正在解压... {done}/{total}")));
                    }
                })).ConfigureAwait(true);

            if (!result.Success)
            {
                AppendLog($"解压失败: {result.Error}");
                _main.SetStatus($"解压失败: {result.Error}");

                MessageBox.Show(this, $"解压失败：{result.Error}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _currentExtractRoot = extractRoot;

            AppendLog($"解压完成: {result.FilesExtracted} 个文件，{result.BytesExtracted:N0} 字节");

            _currentPlan = _ctx.Installer.PlanFromExtractDirectory(extractRoot, game.InstallDir);

            _planGrid.DataSource = new BindingSource { DataSource = _currentPlan };
            _planGrid.Refresh();

            var blocked = _currentPlan.Count(s => s.IsBlocked);

            AppendLog($"将复制 {_currentPlan.Count - blocked} 个文件到 {game.InstallDir}");

            if (blocked > 0)
            {
                AppendLog($"其中 {blocked} 个条目被安全策略拒绝（路径穿越或非法路径）。");
            }

            _main.SetStatus($"解压完成：{result.FilesExtracted} 个文件，计划复制 {_currentPlan.Count - blocked} 个。");
        }
        catch (Exception ex)
        {
            Log.Error("解压预览失败", ex);
            AppendLog($"异常: {ex.Message}");

            MessageBox.Show(this, $"解压失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private async Task InstallAsync()
    {
        if (_busy) return;

        var item = SelectedItem();

        if (item is null)
        {
            MessageBox.Show(this, "请先选择一个待处理补丁。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var game = _ctx.Library.FindById(item.MatchedGameId);

        if (game is null)
        {
            MessageBox.Show(this, "请先为该补丁指定游戏。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!Directory.Exists(game.InstallDir))
        {
            MessageBox.Show(this, $"游戏目录不存在：{game.InstallDir}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var patchKey = InstallService.BuildPatchKey(item.FilePath);

        // 去重检查
        var existing = _ctx.FindInstalledRecord(patchKey);

        if (existing is not null)
        {
            var choice = MessageBox.Show(this,
                $"这个补丁似乎已经安装过（{existing.FinishedAt:yyyy-MM-dd HH:mm:ss}，游戏「{existing.GameName}」）。\r\n\r\n"
                + "仍要重新安装吗？",
                "重复安装确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (choice != DialogResult.Yes)
            {
                item.State = PatchState.Skipped;
                item.Error = $"重复补丁，已跳过（此前安装于 {existing.FinishedAt:yyyy-MM-dd HH:mm:ss}）";
                _ctx.NotifyPendingChanged();
                RefreshGrid();
                return;
            }
        }

        // 确保已解压
        if (_currentExtractRoot is null || _currentPlan.Count == 0)
        {
            var answer = MessageBox.Show(this,
                "还没有生成安装计划，需要先解压到临时目录。现在解压吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            await ExtractAndPreviewAsync().ConfigureAwait(true);

            if (_currentExtractRoot is null) return;
        }

        // 重新按当前游戏目录生成计划，避免用户中途改了目录
        _currentPlan = _ctx.Installer.PlanFromExtractDirectory(_currentExtractRoot, game.InstallDir);

        var blocked = _currentPlan.FirstOrDefault(s => s.IsBlocked);

        if (blocked is not null)
        {
            MessageBox.Show(this,
                $"安装计划被拒绝：{blocked.BlockReason}", "安全限制",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var overwrites = _currentPlan.Count(s => s.TargetExists);

        var confirm = MessageBox.Show(this,
            $"即将把 {_currentPlan.Count} 个文件导入：\r\n"
            + $"游戏: {game.Name}\r\n"
            + $"目录: {game.InstallDir}\r\n"
            + $"覆盖已有文件: {overwrites} 个（覆盖前会自动备份）\r\n\r\n"
            + "继续吗？",
            "确认安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        _busy = true;
        SetButtonsEnabled(false);

        item.State = PatchState.Installing;
        RefreshGrid();

        var record = new InstallRecord
        {
            PatchKey = patchKey,
            GameId = game.Id,
            GameName = game.Name,
            PatchFileName = item.FileName,
            PatchPath = item.FilePath,
            Sha256 = item.Sha256,
            Source = InstallSource.Archive,
            StartedAt = DateTime.Now,
        };

        try
        {
            _main.SetStatus("正在安装补丁...");
            AppendLog("开始安装...");

            var report = await _ctx.Installer.InstallAsync(_currentPlan, game, patchKey)
                .ConfigureAwait(true);

            record.FinishedAt = DateTime.Now;
            record.FilesCopied = report.FilesCopied;
            record.FilesBackedUp = report.FilesBackedUp;
            record.BytesCopied = report.BytesCopied;
            record.BackupBatchId = report.BackupBatchId;

            if (report.Success)
            {
                record.Status = InstallStatus.Success;
                record.Message = $"成功导入 {report.FilesCopied} 个文件，"
                                 + $"备份 {report.FilesBackedUp} 个。";

                item.State = PatchState.Completed;
                item.Error = "";

                game.LastPatchedAt = DateTime.Now;
                game.InstalledPatchCount++;

                foreach (var line in report.Steps) AppendLog(line);

                AppendLog(record.Message);
                _main.SetStatus($"安装成功：{report.FilesCopied} 个文件。");

                // 归档补丁文件
                if (_ctx.Settings.ArchiveInstalledPatches) ArchivePatchFile(item);

                MessageBox.Show(this,
                    $"安装成功。\r\n\r\n{record.Message}", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                record.Status = report.RolledBack ? InstallStatus.RolledBack : InstallStatus.Failed;
                record.Message = report.Error + (report.RolledBack ? "（已回滚）" : "");

                item.State = PatchState.Failed;
                item.Error = record.Message;

                AppendLog($"安装失败: {report.Error}");

                if (report.RolledBack) AppendLog("已自动回滚：新增文件已删除，被覆盖文件已还原。");

                _main.SetStatus("安装失败。");

                MessageBox.Show(this,
                    $"安装失败，未做任何成功标记。\r\n\r\n{record.Message}",
                    "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            _ctx.History.Add(record);
            _ctx.NotifyHistoryChanged();

            _ctx.SaveAll();
        }
        catch (Exception ex)
        {
            record.FinishedAt = DateTime.Now;
            record.Status = InstallStatus.Failed;
            record.Message = ex.Message;

            _ctx.History.Add(record);
            _ctx.NotifyHistoryChanged();
            _ctx.SaveAll();

            item.State = PatchState.Failed;
            item.Error = ex.Message;

            AppendLog($"异常: {ex.Message}");
            Log.Error("安装失败", ex);

            MessageBox.Show(this, $"安装失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);

            _ctx.NotifyPendingChanged();
            RefreshGrid();
        }
    }

    private async Task RunExeAsync()
    {
        if (_busy) return;

        var item = SelectedItem();

        if (item is null)
        {
            MessageBox.Show(this, "请先选择一个待处理补丁。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!item.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "只有 .exe 补丁才能用这种方式运行。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var game = _ctx.Library.FindById(item.MatchedGameId);

        if (game is null)
        {
            MessageBox.Show(this, "请先为该补丁指定游戏。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppendLog("正在校验 EXE 可信性...");

        var check = ExeTrustPolicy.Evaluate(item.FilePath, item.Sha256, _ctx.Settings);

        item.IsTrustedExe = check.IsTrusted;
        item.TrustedRuleName = check.RuleName;
        item.AutoRunAllowed = check.AutoRunAllowed;

        AppendLog($"可信: {(check.IsTrusted ? "是" : "否")}（规则: {(check.RuleName.Length > 0 ? check.RuleName : "无")}）");
        AppendLog($"说明: {check.Reason}");

        // 可信 + 允许自动运行 + 有静默参数 -> 才自动跑；否则一律要用户确认
        if (check.AutoRunAllowed)
        {
            var auto = MessageBox.Show(this,
                $"该 EXE 命中可信规则「{check.RuleName}」且已开启自动运行。\r\n"
                + $"静默参数: {check.SilentArguments}\r\n\r\n"
                + "要现在自动运行吗？",
                "自动运行确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (auto != DialogResult.Yes) return;
        }
        else
        {
            var confirm = MessageBox.Show(this,
                $"即将运行 EXE 补丁：\r\n\r\n{Path.GetFileName(item.FilePath)}\r\n\r\n"
                + $"SHA-256: {item.Sha256}\r\n"
                + $"可信判定: {(check.IsTrusted ? "可信" : "不可信")}\r\n"
                + $"说明: {check.Reason}\r\n\r\n"
                + "程序不会绕过 Windows 的 UAC / SmartScreen 提示。\r\n"
                + "确定要运行这个安装程序吗？",
                "运行 EXE 补丁", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                AppendLog("用户取消了运行。");
                return;
            }
        }

        _busy = true;
        SetButtonsEnabled(false);

        var record = new InstallRecord
        {
            PatchKey = InstallService.BuildPatchKey(item.FilePath),
            GameId = game.Id,
            GameName = game.Name,
            PatchFileName = item.FileName,
            PatchPath = item.FilePath,
            Sha256 = item.Sha256,
            Source = InstallSource.Exe,
            StartedAt = DateTime.Now,
        };

        try
        {
            var silentArgs = check.IsTrusted ? check.SilentArguments : "";

            AppendLog(silentArgs.Length > 0
                ? $"以静默参数运行: {silentArgs}"
                : "以交互方式运行（无静默参数）");

            _main.SetStatus("正在运行 EXE 补丁...");

            var result = await ExePatchRunner.RunAsync(
                item.FilePath, silentArgs, waitForExit: true).ConfigureAwait(true);

            AppendLog(result.Message);

            record.FinishedAt = DateTime.Now;

            if (result.Started && result.ExitedSuccessfully != false)
            {
                record.Status = InstallStatus.Success;
                record.Message = result.Message;

                item.State = PatchState.Completed;
                item.Error = "";

                game.LastPatchedAt = DateTime.Now;
                game.InstalledPatchCount++;

                _main.SetStatus("EXE 补丁已运行完成。");
            }
            else
            {
                record.Status = InstallStatus.Failed;
                record.Message = result.Message;

                item.State = PatchState.Failed;
                item.Error = result.Message;

                _main.SetStatus("EXE 补丁未能成功完成。");
            }

            _ctx.History.Add(record);
            _ctx.NotifyHistoryChanged();
            _ctx.SaveAll();

            MessageBox.Show(this, result.Message,
                record.Status == InstallStatus.Success ? "完成" : "失败",
                MessageBoxButtons.OK,
                record.Status == InstallStatus.Success
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            record.FinishedAt = DateTime.Now;
            record.Status = InstallStatus.Failed;
            record.Message = ex.Message;

            _ctx.History.Add(record);
            _ctx.NotifyHistoryChanged();
            _ctx.SaveAll();

            item.State = PatchState.Failed;
            item.Error = ex.Message;

            AppendLog($"异常: {ex.Message}");
            Log.Error("运行 EXE 失败", ex);

            MessageBox.Show(this, $"运行失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);

            _ctx.NotifyPendingChanged();
            RefreshGrid();
        }
    }

    private void ApplyManualMatch()
    {
        var item = SelectedItem();

        if (item is null)
        {
            MessageBox.Show(this, "请先选择一个待处理补丁。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_gameCombo.SelectedItem is not GameEntry game)
        {
            MessageBox.Show(this, "请在下拉框中选择一个游戏。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        item.MatchedGameId = game.Id;
        item.MatchReason = $"用户手动指定：{game.Name}";
        item.State = PatchState.Matched;

        _ctx.NotifyPendingChanged();
        RefreshGrid();

        BuildPreviewPlan(item);

        _main.SetStatus($"已把补丁匹配到「{game.Name}」。");
    }

    private void ArchivePatchFile(PatchDownloadItem item)
    {
        try
        {
            var folder = _ctx.Settings.InstalledArchiveFolder;

            if (string.IsNullOrWhiteSpace(folder)) return;

            Directory.CreateDirectory(folder);

            var target = Path.Combine(folder, item.FileName);

            if (File.Exists(target))
            {
                target = Path.Combine(folder,
                    $"{Path.GetFileNameWithoutExtension(item.FileName)}-{DateTime.Now:HHmmss}{Path.GetExtension(item.FileName)}");
            }

            File.Move(item.FilePath, target);

            AppendLog($"补丁文件已归档到: {target}");
            Log.Info($"补丁已归档: {target}");
        }
        catch (Exception ex)
        {
            Log.Warn($"归档补丁失败: {ex.Message}");
            AppendLog($"归档失败（不影响安装结果）: {ex.Message}");
        }
    }

    private void RemoveSelected()
    {
        var item = SelectedItem();

        if (item is null) return;

        item.State = PatchState.Skipped;
        item.Error = "用户忽略";

        _ctx.NotifyPendingChanged();
        RefreshGrid();
    }

    private void ClearHandled()
    {
        var removable = _ctx.Pending
            .Where(p => p.State is PatchState.Completed or PatchState.Skipped)
            .ToList();

        if (removable.Count == 0)
        {
            MessageBox.Show(this, "没有可清理的条目。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"将从列表中移除 {removable.Count} 条已完成/已忽略的记录（不影响安装历史和文件）。",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        foreach (var item in removable) _ctx.Pending.Remove(item);

        _ctx.NotifyPendingChanged();
        RefreshGrid();
    }

    private void OpenDownloadFolder()
    {
        var folder = _ctx.Settings.DownloadFolder;

        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, $"目录不存在：{folder}", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开下载目录失败", ex);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnExtract.Enabled = enabled;
        _btnInstall.Enabled = enabled;
        _btnRunExe.Enabled = enabled;
        _btnMatch.Enabled = enabled;
    }

    private void AppendLog(string line)
    {
        if (_logBox.IsDisposed) return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => AppendLog(line)));
            }
            catch
            {
                // 窗口正在关闭
            }

            return;
        }

        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }

    /// <summary>供外部（监控事件）调用的状态刷新。</summary>
    public void RefreshFromMonitor(PatchFileProgressEventArgs e)
    {
        var item = _ctx.Pending.FirstOrDefault(p =>
            string.Equals(p.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase));

        if (item is null) return;

        item.Size = e.Size;
        item.StabilityChecks = e.StabilityChecks;
        item.State = item.IsComplete ? PatchState.Downloaded : PatchState.Downloading;
    }

    /// <summary>导出当前计划为文本，方便用户留档。</summary>
    public string DescribePlan()
    {
        var sb = new StringBuilder();

        foreach (var step in _currentPlan)
        {
            sb.AppendLine(step.IsBlocked
                ? $"[拒绝] {step.RelativeTarget} - {step.BlockReason}"
                : $"[{step.ActionText}] {step.RelativeTarget}");
        }

        return sb.ToString();
    }
}
