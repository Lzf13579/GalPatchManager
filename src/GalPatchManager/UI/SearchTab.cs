using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>
/// 查找补丁页。
///
/// 重要说明：这里返回的都是**候选网址**，程序不会把它们当作已经验证过的补丁文件。
/// 没有配置搜索服务时只提供浏览器搜索入口，绝不编造结果。
/// </summary>
public sealed class SearchTab : UserControl
{
    private readonly AppContext _ctx;
    private readonly FormMain _main;

    private readonly ComboBox _gameCombo = new();
    private readonly TextBox _queryBox = UiTheme.CreateTextBox(false, 320);
    private readonly Button _searchButton;
    private readonly Label _providerLabel = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);
    private readonly Label _statusLabel = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);

    private readonly DataGridView _grid = new();
    private readonly ListBox _browserList = new();

    private bool _busy;

    public SearchTab(AppContext ctx, FormMain main)
    {
        _ctx = ctx;
        _main = main;

        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBack;

        _searchButton = UiTheme.CreateButton("查找补丁", primary: true, width: 110);
        _searchButton.Click += async (_, _) => await DoSearchAsync().ConfigureAwait(true);

        BuildLayout();
        UpdateProviderLabel();
    }

    // ------------------------------------------------------------------ 布局

    private void BuildLayout()
    {
        // ---------------- 顶部 ----------------
        var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = UiTheme.CardBack, Padding = new Padding(10) };

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _gameCombo.Width = 260;
        _gameCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _gameCombo.Font = UiTheme.FontNormal;

        _gameCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_gameCombo.SelectedItem is GameEntry game)
            {
                _queryBox.Text = game.Name;
            }
        };

        var btnReloadGames = UiTheme.CreateButton("刷新游戏", width: 90);
        btnReloadGames.Click += (_, _) => LoadGames();

        row1.Controls.Add(UiTheme.CreateLabel("从游戏列表选择:"));
        row1.Controls.Add(_gameCombo);
        row1.Controls.Add(btnReloadGames);
        row1.Controls.Add(UiTheme.CreateLabel("  或直接输入:"));
        row1.Controls.Add(_queryBox);
        row1.Controls.Add(_searchButton);

        _queryBox.PlaceholderText = "游戏名称";

        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        var btnOpen = UiTheme.CreateButton("浏览器打开", width: 100);
        btnOpen.Click += (_, _) => OpenSelectedInBrowser();

        var btnCopy = UiTheme.CreateButton("复制网址", width: 90);
        btnCopy.Click += (_, _) => CopySelectedUrl();

        var btnWhitelist = UiTheme.CreateButton("域名加入白名单", width: 130);
        btnWhitelist.Click += (_, _) => AddSelectedToWhitelist();

        var btnExport = UiTheme.CreateButton("导出候选列表", width: 120);
        btnExport.Click += (_, _) => ExportCandidates();

        var btnSettings = UiTheme.CreateButton("搜索设置", width: 90);
        btnSettings.Click += (_, _) => _main.ShowPage("settings");

        row2.Controls.Add(btnOpen);
        row2.Controls.Add(btnCopy);
        row2.Controls.Add(btnWhitelist);
        row2.Controls.Add(btnExport);
        row2.Controls.Add(btnSettings);
        row2.Controls.Add(_providerLabel);

        top.Controls.Add(row2);
        top.Controls.Add(row1);

        // ---------------- 结果表 ----------------
        BuildGrid();

        // ---------------- 浏览器备用链接 ----------------
        var bottom = new GroupBox
        {
            Dock = DockStyle.Bottom,
            Height = 130,
            Text = " 浏览器搜索链接（备用方案：没有配置搜索服务时使用）",
            Font = UiTheme.FontNormal,
            BackColor = UiTheme.WindowBack,
        };

        _browserList.Dock = DockStyle.Fill;
        _browserList.Font = UiTheme.FontNormal;
        _browserList.DoubleClick += (_, _) => OpenSelectedBrowserLink();

        var browserButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        var btnOpenBrowser = UiTheme.CreateButton("打开选中链接", width: 110);
        btnOpenBrowser.Click += (_, _) => OpenSelectedBrowserLink();

        var btnOpenAll = UiTheme.CreateButton("全部用浏览器打开", width: 140);
        btnOpenAll.Click += (_, _) => OpenAllBrowserLinks();

        browserButtons.Controls.Add(btnOpenBrowser);
        browserButtons.Controls.Add(btnOpenAll);

        bottom.Controls.Add(_browserList);
        bottom.Controls.Add(browserButtons);

        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 26 };
        _statusLabel.Dock = DockStyle.Fill;
        statusPanel.Controls.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(bottom);
        Controls.Add(statusPanel);
        Controls.Add(top);
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
            HeaderText = "标题",
            DataPropertyName = nameof(PatchCandidate.Title),
            Width = 320,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "网址",
            DataPropertyName = nameof(PatchCandidate.Url),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "来源网站",
            DataPropertyName = nameof(PatchCandidate.Site),
            Width = 150,
        });

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "白名单",
            DataPropertyName = nameof(PatchCandidate.IsWhitelisted),
            Width = 60,
            ReadOnly = true,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "引擎",
            DataPropertyName = nameof(PatchCandidate.Engine),
            Width = 80,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "简要说明",
            DataPropertyName = nameof(PatchCandidate.Snippet),
            Width = 300,
        });

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) OpenSelectedInBrowser();
        };
    }

    // ------------------------------------------------------------------ 数据

    public void LoadGames()
    {
        var games = _ctx.Library.AllGames;

        _gameCombo.DataSource = null;
        _gameCombo.DataSource = games;
        _gameCombo.DisplayMember = nameof(GameEntry.Name);

        if (games.Count > 0 && _gameCombo.SelectedIndex < 0)
        {
            _gameCombo.SelectedIndex = 0;
        }
    }

    public void UpdateProviderLabel()
    {
        var provider = _ctx.Settings.SearchProvider;

        var text = provider switch
        {
            "Searxng" => _ctx.Settings.SearxngBaseUrl.Length > 0
                ? $"  搜索方式: SearXNG ({_ctx.Settings.SearxngBaseUrl})"
                : "  搜索方式: SearXNG（尚未配置实例地址）",
            "CustomJson" => _ctx.Settings.CustomSearchUrlTemplate.Length > 0
                ? "  搜索方式: 自定义 JSON 接口"
                : "  搜索方式: 自定义 JSON 接口（尚未配置 URL 模板）",
            _ => "  搜索方式: 浏览器搜索（不会伪造结果）",
        };

        _providerLabel.Text = text;
    }

    // ------------------------------------------------------------------ 搜索

    private async Task DoSearchAsync()
    {
        if (_busy) return;

        var gameName = _queryBox.Text.Trim();

        if (gameName.Length == 0)
        {
            MessageBox.Show(this, "请先选择或输入游戏名称。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _busy = true;
        _searchButton.Enabled = false;
        _statusLabel.Text = "正在搜索...";
        _main.SetStatus($"正在查找「{gameName}」的补丁网址...");

        try
        {
            var outcome = await _ctx.Search.SearchAsync(gameName, CancellationToken.None)
                .ConfigureAwait(true);

            _ctx.Search.FinalizeCandidates(outcome);

            _ctx.LastCandidates.Clear();
            _ctx.LastCandidates.AddRange(outcome.Candidates);

            _ctx.LastBrowserUrls.Clear();
            _ctx.LastBrowserUrls.AddRange(outcome.BrowserUrls);

            _grid.DataSource = new BindingSource
            {
                DataSource = _ctx.LastCandidates.Select(c => c).ToList(),
            };

            _browserList.Items.Clear();

            foreach (var url in _ctx.LastBrowserUrls)
            {
                _browserList.Items.Add(url);
            }

            _grid.Refresh();

            var whitelisted = _ctx.LastCandidates.Count(c => c.IsWhitelisted);

            _statusLabel.Text = $"{outcome.Message}  候选 {_ctx.LastCandidates.Count} 条"
                                + (whitelisted > 0 ? $"（其中 {whitelisted} 条来自白名单域名）" : "")
                                + (outcome.UsedBrowserFallback ? $"  浏览器链接 {_ctx.LastBrowserUrls.Count} 条" : "");

            _main.SetStatus($"搜索完成：候选网址 {_ctx.LastCandidates.Count} 条。");

            // 如果当前是浏览器方案，直接把搜索页面打开，省去用户再点一次
            if (outcome.UsedBrowserFallback && _ctx.LastBrowserUrls.Count > 0 &&
                _ctx.Settings.SearchProvider == "Browser")
            {
                OpenAllBrowserLinks();
            }
        }
        catch (Exception ex)
        {
            Log.Error("搜索失败", ex);

            MessageBox.Show(this, $"搜索失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _searchButton.Enabled = true;
        }
    }

    // ------------------------------------------------------------------ 操作

    private PatchCandidate? SelectedCandidate()
    {
        if (_grid.CurrentRow?.DataBoundItem is PatchCandidate candidate) return candidate;

        return null;
    }

    private void OpenSelectedInBrowser()
    {
        var candidate = SelectedCandidate();

        if (candidate is null)
        {
            MessageBox.Show(this, "请先选择一条结果。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenUrl(candidate.Url);
    }

    private void CopySelectedUrl()
    {
        var candidate = SelectedCandidate();

        if (candidate is null) return;

        try
        {
            Clipboard.SetText(candidate.Url);
            _main.SetStatus("网址已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Log.Warn($"复制失败: {ex.Message}");
        }
    }

    private void AddSelectedToWhitelist()
    {
        var candidate = SelectedCandidate();

        if (candidate is null)
        {
            MessageBox.Show(this, "请先选择一条结果。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri))
        {
            MessageBox.Show(this, "无法解析该网址。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var choice = MessageBox.Show(this,
            $"要把域名「{uri.Host}」加入白名单吗？\r\n\r\n"
            + "是 = 只加域名（该站所有链接都标记为可信候选）\r\n"
            + "否 = 只加这一条完整网址\r\n\r\n"
            + "注意：白名单只表示“这个来源是你信任的”，并不等于该网址就是可用的补丁文件。",
            "加入白名单", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (choice == DialogResult.Cancel) return;

        if (choice == DialogResult.Yes)
        {
            if (!_ctx.Settings.WhitelistedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                _ctx.Settings.WhitelistedHosts.Add(uri.Host);
            }
        }
        else
        {
            if (!_ctx.Settings.WhitelistedUrls.Contains(candidate.Url, StringComparer.OrdinalIgnoreCase))
            {
                _ctx.Settings.WhitelistedUrls.Add(candidate.Url);
            }
        }

        ConfigStore.SaveSettings(_ctx.Settings);

        // 重新标注
        var outcome = new SearchOutcome();
        outcome.Candidates.AddRange(_ctx.LastCandidates);
        _ctx.Search.FinalizeCandidates(outcome);

        _ctx.LastCandidates.Clear();
        _ctx.LastCandidates.AddRange(outcome.Candidates);

        _grid.DataSource = new BindingSource
        {
            DataSource = _ctx.LastCandidates.Select(c => c).ToList(),
        };

        _grid.Refresh();
        _main.SetStatus($"已加入白名单：{uri.Host}");
    }

    private void ExportCandidates()
    {
        if (_ctx.LastCandidates.Count == 0)
        {
            MessageBox.Show(this, "当前没有可导出的候选结果。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"补丁候选网址-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# 补丁候选网址（由 游戏补丁安装器 导出）");
            sb.AppendLine($"# 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("# 注意：这些只是候选网址，程序未验证其内容是否为可用补丁。");
            sb.AppendLine();

            foreach (var c in _ctx.LastCandidates)
            {
                sb.AppendLine($"[{(c.IsWhitelisted ? "白名单" : "未验证")}] {c.Title}");
                sb.AppendLine($"  {c.Url}");
                sb.AppendLine($"  来源: {c.Site} / {c.Engine}");

                if (!string.IsNullOrWhiteSpace(c.Snippet)) sb.AppendLine($"  说明: {c.Snippet}");

                sb.AppendLine();
            }

            if (_ctx.LastBrowserUrls.Count > 0)
            {
                sb.AppendLine("# 浏览器搜索链接");
                foreach (var url in _ctx.LastBrowserUrls) sb.AppendLine(url);
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);

            _main.SetStatus($"已导出到 {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error("导出失败", ex);
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedBrowserLink()
    {
        if (_browserList.SelectedItem is string url) OpenUrl(url);
    }

    private void OpenAllBrowserLinks()
    {
        if (_browserList.Items.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"将用默认浏览器打开 {_browserList.Items.Count} 个搜索页面，继续吗？",
            "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        foreach (var item in _browserList.Items)
        {
            if (item is string url) OpenUrl(url);

            // 避免瞬间弹出过多窗口
            Thread.Sleep(150);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });

            Log.Info($"在浏览器中打开: {url}");
        }
        catch (Exception ex)
        {
            Log.Error($"打开网址失败: {url}", ex);
            MessageBox.Show(this, $"打开失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
