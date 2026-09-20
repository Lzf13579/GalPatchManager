using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>
/// 设置页：下载监控、搜索服务、白名单、可信 EXE 规则、匹配规则、备份策略。
/// </summary>
public sealed class SettingsTab : UserControl
{
    private readonly AppContext _ctx;
    private readonly FormMain _main;
    private readonly Action _onSaved;

    // 下载
    private readonly TextBox _downloadFolder = UiTheme.CreateTextBox(false, 400);
    private readonly CheckBox _monitorEnabled = new();
    private readonly NumericUpDown _pollInterval = new();
    private readonly NumericUpDown _stableChecks = new();
    private readonly NumericUpDown _minQuiet = new();
    private readonly CheckBox _onlyNewFiles = new();
    private readonly NumericUpDown _minFileSize = new();
    private readonly Label _baselineLabel = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);
    private readonly TextBox _ignoredExt = UiTheme.CreateTextBox(false, 400);

    // 安装
    private readonly CheckBox _backupBeforeOverwrite = new();
    private readonly CheckBox _autoRollback = new();
    private readonly CheckBox _archiveInstalled = new();
    private readonly TextBox _archiveFolder = UiTheme.CreateTextBox(false, 400);
    private readonly NumericUpDown _retentionDays = new();

    // 搜索
    private readonly ComboBox _provider = new();
    private readonly TextBox _searxngUrl = UiTheme.CreateTextBox(false, 400);
    private readonly TextBox _customUrl = UiTheme.CreateTextBox(false, 400);
    private readonly TextBox _customKey = UiTheme.CreateTextBox(false, 400);
    private readonly ComboBox _customKeyMode = new();
    private readonly TextBox _customKeyName = UiTheme.CreateTextBox(false, 160);
    private readonly TextBox _customResultsPath = UiTheme.CreateTextBox(false, 200);
    private readonly TextBox _keywordTemplates = new();
    private readonly NumericUpDown _resultLimit = new();
    private readonly NumericUpDown _httpTimeout = new();

    // 白名单
    private readonly ListBox _hostList = new();
    private readonly ListBox _urlList = new();
    private readonly TextBox _newHost = UiTheme.CreateTextBox(false, 220);
    private readonly TextBox _newUrl = UiTheme.CreateTextBox(false, 300);

    // 可信 EXE
    private readonly DataGridView _exeRuleGrid = new();
    private readonly CheckBox _alwaysConfirmExe = new();
    private readonly CheckBox _autoRunTrustedExe = new();

    // 匹配规则
    private readonly DataGridView _matchRuleGrid = new();

    // 游戏归类
    private readonly CheckBox _enableSteamMetadata = new();
    private readonly CheckBox _enableHeuristicType = new();
    private readonly NumericUpDown _metadataCacheDays = new();
    private readonly ComboBox _networkMode = new();
    private readonly TextBox _customProxyUrl = UiTheme.CreateTextBox(false, 220);
    private readonly NumericUpDown _metadataConcurrency = new();
    private readonly CheckBox _enableNameTypeRule = new();
    private readonly CheckBox _enableCommunityTags = new();
    private readonly DataGridView _nameRuleGrid = new();
    private readonly DataGridView _genreMapGrid = new();
    private readonly DataGridView _tagMapGrid = new();
    private readonly Label _tagCacheLabel = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);

    public SettingsTab(AppContext ctx, FormMain main, Action onSaved)
    {
        _ctx = ctx;
        _main = main;
        _onSaved = onSaved;

        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBack;

        BuildLayout();
        LoadFromSettings();
    }

    // ------------------------------------------------------------------ 布局

    private Control BuildLayout()
    {
        var container = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiTheme.WindowBack,
            Padding = new Padding(0, 0, 12, 0),
        };

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
            BackColor = UiTheme.WindowBack,
        };

        stack.Controls.Add(BuildDownloadGroup());
        stack.Controls.Add(BuildInstallGroup());
        stack.Controls.Add(BuildGameClassifyGroup());
        stack.Controls.Add(BuildSearchGroup());
        stack.Controls.Add(BuildWhitelistGroup());
        stack.Controls.Add(BuildExeGroup());
        stack.Controls.Add(BuildMatchRuleGroup());
        stack.Controls.Add(BuildAboutGroup());

        container.Controls.Add(stack);
        Controls.Add(container);

        // 底部固定的保存栏
        var saveBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            BackColor = UiTheme.WindowBack,
            Padding = new Padding(0, 8, 0, 0),
        };

        var btnSave = UiTheme.CreateButton("保存设置", primary: true, width: 110);
        btnSave.Click += (_, _) => Commit();

        var btnReload = UiTheme.CreateButton("重新载入", width: 100);
        btnReload.Click += (_, _) => LoadFromSettings();

        saveBar.Controls.Add(btnSave);
        saveBar.Controls.Add(btnReload);
        btnSave.Location = new Point(0, 8);
        btnReload.Location = new Point(120, 8);

        Controls.Add(saveBar);

        return container;
    }

    private static GroupBox CreateGroup(string title, int height)
    {
        return new GroupBox
        {
            Text = " " + title,
            Width = 1080,
            Height = height,
            Font = UiTheme.FontNormal,
            ForeColor = UiTheme.TextPrimary,
            BackColor = UiTheme.CardBack,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12),
        };
    }

    private static FlowLayoutPanel Row()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            AutoSize = false,
        };
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, int width = 70)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = width,
            Font = UiTheme.FontNormal,
        };
    }

    // ------------------------------------------------------------ 下载设置

    private GroupBox BuildDownloadGroup()
    {
        var group = CreateGroup("下载监控", 290);

        var r1 = Row();
        var btnBrowse = UiTheme.CreateButton("浏览...", width: 80);
        btnBrowse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择要监控的下载目录",
                ShowNewFolderButton = true,
            };

            if (Directory.Exists(_downloadFolder.Text)) dialog.SelectedPath = _downloadFolder.Text;

            if (dialog.ShowDialog(this) == DialogResult.OK) _downloadFolder.Text = dialog.SelectedPath;
        };

        r1.Controls.Add(UiTheme.CreateLabel("下载目录:"));
        r1.Controls.Add(_downloadFolder);
        r1.Controls.Add(btnBrowse);

        var btnOpenDrop = UiTheme.CreateButton("打开监控目录", width: 110);
        btnOpenDrop.Click += (_, _) => OpenMonitorFolder();

        r1.Controls.Add(btnOpenDrop);

        // 目录改了要刷新基线状态提示
        _downloadFolder.TextChanged += (_, _) => UpdateBaselineLabel();

        var r2 = Row();
        _monitorEnabled.Text = "启用下载目录监控";
        _monitorEnabled.AutoSize = true;
        _monitorEnabled.Font = UiTheme.FontNormal;

        r2.Controls.Add(_monitorEnabled);
        r2.Controls.Add(UiTheme.CreateLabel("   轮询间隔(秒):"));
        r2.Controls.Add(_pollInterval);
        r2.Controls.Add(UiTheme.CreateLabel("   稳定判定次数:"));
        r2.Controls.Add(_stableChecks);
        r2.Controls.Add(UiTheme.CreateLabel("   最短静默(秒):"));
        r2.Controls.Add(_minQuiet);

        var r3 = Row();
        r3.Controls.Add(UiTheme.CreateLabel("忽略的临时扩展名:"));
        r3.Controls.Add(_ignoredExt);

        // 只处理新增文件 + 最小体积
        var r4 = Row();
        _onlyNewFiles.Text = "只处理新下载的文件（忽略监控目录里已存在的文件）";
        _onlyNewFiles.AutoSize = true;
        _onlyNewFiles.Font = UiTheme.FontNormal;

        r4.Controls.Add(_onlyNewFiles);
        r4.Controls.Add(UiTheme.CreateLabel("   小于(MB)的文件忽略:"));
        r4.Controls.Add(_minFileSize);

        // 基线状态 + 重建
        var r5 = Row();
        var btnRebaseline = UiTheme.CreateButton("重新建立基线", width: 110);
        btnRebaseline.Click += (_, _) => RebuildBaseline();

        var btnClearBaseline = UiTheme.CreateButton("清除基线", width: 90);
        btnClearBaseline.Click += (_, _) => ClearBaseline();

        r5.Controls.Add(btnRebaseline);
        r5.Controls.Add(btnClearBaseline);
        r5.Controls.Add(_baselineLabel);

        var r6 = Row();
        var hint = UiTheme.CreateLabel(
            "完成判定：连续 N 次大小不变 + 已静默足够秒数 + 文件可完整读取 + 压缩包可打开；"
            + ".crdownload / .part / .tmp 等临时文件会被忽略。",
            color: UiTheme.TextSecondary);
        r6.Controls.Add(hint);

        var r7 = Row();
        var hint2 = UiTheme.CreateLabel(
            "提示：小于体积下限的文件会被直接忽略（既不进列表、也不进基线）。"
            + "如果你调小了下限，建议先点「重新建立基线」，把目录里积压的小文件一次性清掉，避免它们全部冒出来。",
            color: UiTheme.Warning);
        r7.Controls.Add(hint2);

        group.Controls.Add(r7);
        group.Controls.Add(r6);
        group.Controls.Add(r5);
        group.Controls.Add(r4);
        group.Controls.Add(r3);
        group.Controls.Add(r2);
        group.Controls.Add(r1);

        return group;
    }

    // ------------------------------------------------------------ 安装设置

    private GroupBox BuildInstallGroup()
    {
        var group = CreateGroup("安装与备份", 170);

        var r1 = Row();
        _backupBeforeOverwrite.Text = "覆盖前自动备份";
        _backupBeforeOverwrite.AutoSize = true;

        _autoRollback.Text = "失败时自动回滚";
        _autoRollback.AutoSize = true;

        r1.Controls.Add(_backupBeforeOverwrite);
        r1.Controls.Add(UiTheme.CreateLabel("   "));
        r1.Controls.Add(_autoRollback);
        r1.Controls.Add(UiTheme.CreateLabel("   备份保留天数:"));
        r1.Controls.Add(_retentionDays);

        var r2 = Row();
        _archiveInstalled.Text = "安装成功后归档补丁文件到:";
        _archiveInstalled.AutoSize = true;

        r2.Controls.Add(_archiveInstalled);
        r2.Controls.Add(_archiveFolder);

        var r3 = Row();
        r3.Controls.Add(UiTheme.CreateLabel(
            "备份保存在 " + Paths.BackupFolder + "，可在“安装历史”页一键还原。",
            color: UiTheme.TextSecondary));

        group.Controls.Add(r3);
        group.Controls.Add(r2);
        group.Controls.Add(r1);

        return group;
    }

    // ------------------------------------------------------------ 游戏归类

    private GroupBox BuildGameClassifyGroup()
    {
        var group = CreateGroup("游戏归类（按类型/标签）", 800);

        var r1 = Row();
        _enableSteamMetadata.Text = "联网抓取 Steam 官方类型（genres）辅助归类";
        _enableSteamMetadata.AutoSize = true;
        _enableSteamMetadata.Font = UiTheme.FontNormal;

        _enableHeuristicType.Text = "启用本地引擎特征识别";
        _enableHeuristicType.AutoSize = true;
        _enableHeuristicType.Font = UiTheme.FontNormal;

        r1.Controls.Add(_enableSteamMetadata);
        r1.Controls.Add(UiTheme.CreateLabel("   "));
        r1.Controls.Add(_enableHeuristicType);
        r1.Controls.Add(UiTheme.CreateLabel("   缓存天数:"));
        r1.Controls.Add(_metadataCacheDays);

        var r1b = Row();
        _enableNameTypeRule.Text = "启用「游戏名关键词」归类（纯离线，不依赖网络）";
        _enableNameTypeRule.AutoSize = true;
        _enableNameTypeRule.Font = UiTheme.FontNormal;

        _enableCommunityTags.Text = "抓取 Steam 社区标签（视觉小说等，需联网）";
        _enableCommunityTags.AutoSize = true;
        _enableCommunityTags.Font = UiTheme.FontNormal;

        r1b.Controls.Add(_enableNameTypeRule);
        r1b.Controls.Add(UiTheme.CreateLabel("   "));
        r1b.Controls.Add(_enableCommunityTags);

        // 社区标签映射表
        var rTagHint = Row();
        rTagHint.Controls.Add(UiTheme.CreateLabel(
            "社区标签 → 游戏类型（顺序即优先级）。「视觉小说」就靠 Visual Novel 标签命中 —— 官方 genres 里没有这一项。",
            color: UiTheme.TextSecondary));

        BuildRuleGrid(_tagMapGrid, "Steam 社区标签（英文）");

        // 联网方式（代理问题最常见的坑：代理破坏了 TLS，直连反而可用）
        var rNet = Row();
        _networkMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _networkMode.Width = 130;
        _networkMode.Font = UiTheme.FontNormal;
        _networkMode.Items.AddRange(new object[] { "Auto", "Direct", "SystemProxy", "CustomProxy" });
        _networkMode.SelectedIndexChanged += (_, _) =>
        {
            _customProxyUrl.Enabled = (_networkMode.SelectedItem as string) == "CustomProxy";
        };

        rNet.Controls.Add(UiTheme.CreateLabel("联网方式:"));
        rNet.Controls.Add(_networkMode);
        rNet.Controls.Add(UiTheme.CreateLabel("  自定义代理:"));
        rNet.Controls.Add(_customProxyUrl);
        rNet.Controls.Add(UiTheme.CreateLabel("   并发数:"));
        rNet.Controls.Add(_metadataConcurrency);

        var rNetHint = Row();
        rNetHint.Controls.Add(UiTheme.CreateLabel(
            "Auto = 先直连、失败再试系统代理（推荐）。若你开了代理软件却取不到类型，"
            + "多半是代理破坏了 TLS，可改成 Direct。",
            color: UiTheme.TextSecondary));

        var r2 = Row();
        r2.Controls.Add(UiTheme.CreateLabel(
            "Steam 官方接口只提供 genres（Action/Adventure/RPG…），不提供社区用户标签，"
            + "因此归类依据是「官方类型 + 本地引擎特征」，不是用户标签。",
            color: UiTheme.TextSecondary));

        var r3 = Row();
        r3.Controls.Add(UiTheme.CreateLabel("商店类型 → 本程序游戏类型（顺序即优先级）:",
            color: UiTheme.TextSecondary));

        // 名称关键词表格
        var r3b = Row();
        r3b.Controls.Add(UiTheme.CreateLabel(
            "游戏名/目录名关键词 → 游戏类型（顺序即优先级，纯离线可用）:",
            color: UiTheme.TextSecondary));

        BuildRuleGrid(_nameRuleGrid, "关键词");
        BuildRuleGrid(_genreMapGrid, "Steam genre（英文）");

        var r4 = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        var btnClearCache = UiTheme.CreateButton("清除元数据缓存", width: 130);
        btnClearCache.Click += (_, _) =>
        {
            TagCache.Clear();
            UpdateTagCacheLabel();

            MessageBox.Show(this,
                "已清除 Steam 元数据缓存。下次归类会重新抓取。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var btnTestNet = UiTheme.CreateButton("测试连接", width: 90);
        btnTestNet.Click += async (_, _) => await TestMetadataNetworkAsync().ConfigureAwait(true);

        var btnReclassify = UiTheme.CreateButton("立即重新归类", primary: true, width: 120);
        btnReclassify.Click += async (_, _) =>
        {
            // 先保存当前设置（映射表/联网方式可能有改动），再归类
            Commit();

            // 联网方式变了要重新探测
            SteamMetadataService.ResetProbe();

            _main.ShowPage("games");
            await _main.RunAutoClassifyAsync().ConfigureAwait(true);
        };

        r4.Controls.Add(btnReclassify);
        r4.Controls.Add(btnTestNet);
        r4.Controls.Add(btnClearCache);
        r4.Controls.Add(_tagCacheLabel);

        group.Controls.Add(r4);
        group.Controls.Add(_genreMapGrid);
        group.Controls.Add(r3);
        group.Controls.Add(_tagMapGrid);
        group.Controls.Add(rTagHint);
        group.Controls.Add(_nameRuleGrid);
        group.Controls.Add(r3b);
        group.Controls.Add(r2);
        group.Controls.Add(rNetHint);
        group.Controls.Add(rNet);
        group.Controls.Add(r1b);
        group.Controls.Add(r1);

        return group;
    }

    /// <summary>构建一张"关键词 -> 类型"的编辑表格。</summary>
    private static void BuildRuleGrid(DataGridView grid, string keyHeader)
    {
        grid.Dock = DockStyle.Top;
        grid.Height = 150;
        grid.BackgroundColor = UiTheme.CardBack;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoGenerateColumns = false;
        grid.Font = UiTheme.FontNormal;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 243, 247);
        grid.ColumnHeadersDefaultCellStyle.Font = UiTheme.FontBold;
        grid.ColumnHeadersHeight = 28;
        grid.RowTemplate.Height = 26;

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = keyHeader,
            Name = "GenreColumn",
            Width = 220,
        });

        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "映射到",
            Name = "TypeColumn",
            Width = 200,
            FlatStyle = FlatStyle.Flat,
        });
    }

    /// <summary>测试联网络径：逐个策略试一遍，把结果如实反馈。</summary>
    private async Task TestMetadataNetworkAsync()
    {
        try
        {
            _main.SetStatus("正在测试 Steam 接口连通性…");

            SteamMetadataService.ResetProbe();

            var strategy = await SteamMetadataService
                .ProbeAsync(_ctx.Settings, CancellationToken.None).ConfigureAwait(true);

            if (strategy is null)
            {
                MessageBox.Show(this,
                    "无法访问 Steam 商店接口。\r\n\r\n"
                    + $"当前联网方式: {_networkMode.SelectedItem}\r\n"
                    + $"自定义代理: {(string.IsNullOrWhiteSpace(_customProxyUrl.Text) ? "(未填写)" : _customProxyUrl.Text)}\r\n\r\n"
                    + "可以依次尝试：\r\n"
                    + "  · 改用 Direct（直连）—— 若你开了代理软件，代理常会破坏 TLS\r\n"
                    + "  · 改用 SystemProxy\r\n"
                    + "  · 填写一个可用代理地址后用 CustomProxy\r\n\r\n"
                    + "网络不可用时，归类会退回本地引擎识别，不影响其他功能。",
                    "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                _main.SetStatus("Steam 接口不可达。");
                return;
            }

            MessageBox.Show(this,
                $"连接成功，可用方式：{strategy}\r\n\r\n现在可以点「立即重新归类」，"
                + "会抓取官方类型并缓存下来。之后断网也能用缓存。",
                "连接正常", MessageBoxButtons.OK, MessageBoxIcon.Information);

            _main.SetStatus($"Steam 接口可用（{strategy}）。");
        }
        catch (Exception ex)
        {
            Log.Error("测试联网失败", ex);

            MessageBox.Show(this, $"测试失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>把映射表刷到表格里。</summary>
    private void RefreshGenreMap()
    {
        var knownTypes = GameTypeCatalog.BuildTypeList(_ctx.Settings).Select(t => t.Name).ToList();

        FillRuleGrid(_genreMapGrid, _ctx.Settings.GenreTypeMapping, knownTypes);
        FillRuleGrid(_nameRuleGrid, _ctx.Settings.NameTypeRules, knownTypes);
        FillRuleGrid(_tagMapGrid, _ctx.Settings.TagTypeMapping, knownTypes);

        UpdateTagCacheLabel();
    }

    /// <summary>把字典填进表格并绑定类型下拉框。</summary>
    private static void FillRuleGrid(
        DataGridView grid,
        Dictionary<string, string> map,
        List<string> knownTypes)
    {
        var rows = map.Select(kv => new { Genre = kv.Key, Type = kv.Value }).ToList();

        grid.DataSource = new BindingSource { DataSource = rows };

        if (grid.Columns["TypeColumn"] is DataGridViewComboBoxColumn combo)
        {
            combo.DataSource = knownTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        grid.Refresh();
    }

    /// <summary>从映射表格读回映射（保持表格行顺序 = 优先级）。</summary>
    private static Dictionary<string, string> ReadRuleGrid(DataGridView grid)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;

            var genre = row.Cells["GenreColumn"].Value?.ToString()?.Trim() ?? "";
            var type = row.Cells["TypeColumn"].Value?.ToString()?.Trim() ?? "";

            if (genre.Length == 0) continue;

            // 空类型表示"该关键词不参与归类"
            if (type.Length == 0) continue;

            if (!map.ContainsKey(genre)) map[genre] = type;
        }

        return map;
    }

    private void UpdateTagCacheLabel()
    {
        var count = TagCache.Count;
        var last = TagCache.LastUpdated;

        _tagCacheLabel.Text = count == 0
            ? "  元数据缓存：空"
            : $"  元数据缓存：{count} 个游戏（最后更新 {(last.HasValue ? last.Value.ToString("yyyy-MM-dd HH:mm") : "—")}）";
    }

    // ------------------------------------------------------------ 搜索设置

    private GroupBox BuildSearchGroup()
    {
        var group = CreateGroup("查找补丁 / 搜索服务", 300);

        var r1 = Row();
        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.Width = 200;
        _provider.Font = UiTheme.FontNormal;
        _provider.Items.AddRange(new object[]
        {
            "Browser", "Searxng", "CustomJson",
        });
        _provider.SelectedIndexChanged += (_, _) => UpdateProviderFields();

        r1.Controls.Add(UiTheme.CreateLabel("搜索方式:"));
        r1.Controls.Add(_provider);
        r1.Controls.Add(UiTheme.CreateLabel(
            "   Browser=浏览器搜索（无需 API Key）；Searxng=自建实例；CustomJson=自定义 JSON 接口",
            color: UiTheme.TextSecondary));

        var r2 = Row();
        r2.Controls.Add(UiTheme.CreateLabel("SearXNG 地址:"));
        r2.Controls.Add(_searxngUrl);
        var testSearx = UiTheme.CreateButton("测试连接", width: 90);
        testSearx.Click += async (_, _) => await TestSearxngAsync().ConfigureAwait(true);
        r2.Controls.Add(testSearx);

        var r3 = Row();
        r3.Controls.Add(UiTheme.CreateLabel("自定义接口模板:"));
        r3.Controls.Add(_customUrl);
        r3.Controls.Add(UiTheme.CreateLabel("  必须含 {query}", color: UiTheme.TextSecondary));

        var r4 = Row();
        _customKeyMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _customKeyMode.Width = 100;
        _customKeyMode.Items.AddRange(new object[] { "None", "Header", "Query" });
        _customKeyMode.Font = UiTheme.FontNormal;

        r4.Controls.Add(UiTheme.CreateLabel("API Key:"));
        r4.Controls.Add(_customKey);
        r4.Controls.Add(UiTheme.CreateLabel(" 附加方式:"));
        r4.Controls.Add(_customKeyMode);
        r4.Controls.Add(UiTheme.CreateLabel(" 名称:"));
        r4.Controls.Add(_customKeyName);
        r4.Controls.Add(UiTheme.CreateLabel(" 结果数组路径:"));
        r4.Controls.Add(_customResultsPath);

        var r5 = Row();
        r5.Controls.Add(UiTheme.CreateLabel("关键词模板:"));
        var templatesHint = UiTheme.CreateLabel(
            "{0} 代表游戏名，每行一个。例如：{0} 汉化补丁",
            color: UiTheme.TextSecondary);
        r5.Controls.Add(templatesHint);

        _keywordTemplates.Multiline = true;
        _keywordTemplates.Height = 60;
        _keywordTemplates.Width = 500;
        _keywordTemplates.Font = UiTheme.FontNormal;
        _keywordTemplates.ScrollBars = ScrollBars.Vertical;

        var r6 = Row();
        r6.Controls.Add(_keywordTemplates);

        var r7 = Row();
        r7.Controls.Add(UiTheme.CreateLabel("结果上限:"));
        r7.Controls.Add(_resultLimit);
        r7.Controls.Add(UiTheme.CreateLabel("   请求超时(秒):"));
        r7.Controls.Add(_httpTimeout);

        group.Controls.Add(r7);
        group.Controls.Add(r6);
        group.Controls.Add(r5);
        group.Controls.Add(r4);
        group.Controls.Add(r3);
        group.Controls.Add(r2);
        group.Controls.Add(r1);

        return group;
    }

    private GroupBox BuildWhitelistGroup()
    {
        var group = CreateGroup("可信网址白名单", 210);

        var r1 = Row();
        r1.Controls.Add(UiTheme.CreateLabel(
            "白名单只表示“这个来源由你信任”，程序不会因此把网页当作已验证的补丁文件。",
            color: UiTheme.TextSecondary));

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 140,
            ColumnCount = 2,
        };

        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        var leftPanel = new Panel { Dock = DockStyle.Fill };
        _hostList.Dock = DockStyle.Fill;
        _hostList.Font = UiTheme.FontNormal;

        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32 };
        var addHost = UiTheme.CreateButton("添加域名", width: 80);
        addHost.Click += (_, _) => AddHost();
        var removeHost = UiTheme.CreateButton("移除", width: 60);
        removeHost.Click += (_, _) =>
        {
            if (_hostList.SelectedItem is string s) _hostList.Items.Remove(s);
        };

        leftButtons.Controls.Add(_newHost);
        leftButtons.Controls.Add(addHost);
        leftButtons.Controls.Add(removeHost);

        leftPanel.Controls.Add(_hostList);
        leftPanel.Controls.Add(leftButtons);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        _urlList.Dock = DockStyle.Fill;
        _urlList.Font = UiTheme.FontNormal;

        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32 };
        var addUrl = UiTheme.CreateButton("添加网址", width: 80);
        addUrl.Click += (_, _) => AddUrl();
        var removeUrl = UiTheme.CreateButton("移除", width: 60);
        removeUrl.Click += (_, _) =>
        {
            if (_urlList.SelectedItem is string s) _urlList.Items.Remove(s);
        };

        rightButtons.Controls.Add(_newUrl);
        rightButtons.Controls.Add(addUrl);
        rightButtons.Controls.Add(removeUrl);

        rightPanel.Controls.Add(_urlList);
        rightPanel.Controls.Add(rightButtons);

        split.Controls.Add(leftPanel, 0, 0);
        split.Controls.Add(rightPanel, 1, 0);

        group.Controls.Add(split);
        group.Controls.Add(r1);

        return group;
    }

    private GroupBox BuildExeGroup()
    {
        var group = CreateGroup("可信 EXE 规则（用于判断能否自动运行 .exe 补丁）", 300);

        var r1 = Row();
        _alwaysConfirmExe.Text = "即使命中可信规则也要求我确认（推荐）";
        _alwaysConfirmExe.AutoSize = true;

        _autoRunTrustedExe.Text = "允许自动运行可信 EXE";
        _autoRunTrustedExe.AutoSize = true;

        r1.Controls.Add(_alwaysConfirmExe);
        r1.Controls.Add(UiTheme.CreateLabel("   "));
        r1.Controls.Add(_autoRunTrustedExe);

        var r2 = Row();
        r2.Controls.Add(UiTheme.CreateLabel(
            "只有“命中规则 + SHA-256 或来源域名校验通过 + 规则里配置了静默参数”才可能自动运行；"
            + "程序不会猜测静默参数，也不会绕过 UAC。",
            color: UiTheme.TextSecondary));

        _exeRuleGrid.Dock = DockStyle.Top;
        _exeRuleGrid.Height = 170;
        _exeRuleGrid.BackgroundColor = UiTheme.CardBack;
        _exeRuleGrid.BorderStyle = BorderStyle.FixedSingle;
        _exeRuleGrid.AllowUserToAddRows = false;
        _exeRuleGrid.RowHeadersVisible = false;
        _exeRuleGrid.AutoGenerateColumns = false;
        _exeRuleGrid.Font = UiTheme.FontNormal;
        _exeRuleGrid.EnableHeadersVisualStyles = false;
        _exeRuleGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 243, 247);
        _exeRuleGrid.ColumnHeadersDefaultCellStyle.Font = UiTheme.FontBold;
        _exeRuleGrid.ColumnHeadersHeight = 28;
        _exeRuleGrid.RowTemplate.Height = 26;

        _exeRuleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "规则名称",
            DataPropertyName = nameof(ExeTrustRule.Name),
            Width = 140,
        });

        _exeRuleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "文件名通配",
            DataPropertyName = nameof(ExeTrustRule.FileNamePattern),
            Width = 140,
        });

        _exeRuleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "允许的 SHA-256（分号分隔）",
            Name = "Sha",
            Width = 260,
        });

        _exeRuleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "允许的来源域名（分号分隔）",
            Name = "Hosts",
            Width = 200,
        });

        _exeRuleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "静默参数",
            DataPropertyName = nameof(ExeTrustRule.SilentArguments),
            Width = 120,
        });

        var r3 = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        var add = UiTheme.CreateButton("新增规则", width: 90);
        add.Click += (_, _) =>
        {
            var rule = new ExeTrustRule
            {
                Name = "新规则",
                FileNamePattern = "*.exe",
                Mode = ExeRunMode.Interactive,
            };

            _ctx.Settings.ExeTrustRules.Add(rule);
            RefreshExeRules();
        };

        var remove = UiTheme.CreateButton("删除选中规则", width: 110);
        remove.Click += (_, _) =>
        {
            if (_exeRuleGrid.CurrentRow?.Index is not int index || index < 0) return;
            if (index >= _ctx.Settings.ExeTrustRules.Count) return;

            _ctx.Settings.ExeTrustRules.RemoveAt(index);
            RefreshExeRules();
        };

        var compute = UiTheme.CreateButton("计算选中 EXE 的 SHA-256...", width: 190);
        compute.Click += async (_, _) => await ComputeHashAsync().ConfigureAwait(true);

        r3.Controls.Add(add);
        r3.Controls.Add(remove);
        r3.Controls.Add(compute);

        group.Controls.Add(r3);
        group.Controls.Add(r2);
        group.Controls.Add(r1);
        group.Controls.Add(_exeRuleGrid);

        return group;
    }

    private GroupBox BuildMatchRuleGroup()
    {
        var group = CreateGroup("补丁关键词匹配规则", 240);

        var r1 = Row();
        r1.Controls.Add(UiTheme.CreateLabel(
            "补丁文件名包含关键词时，自动匹配到指定游戏。AppID 匹配优先级更高。",
            color: UiTheme.TextSecondary));

        _matchRuleGrid.Dock = DockStyle.Top;
        _matchRuleGrid.Height = 140;
        _matchRuleGrid.BackgroundColor = UiTheme.CardBack;
        _matchRuleGrid.BorderStyle = BorderStyle.FixedSingle;
        _matchRuleGrid.AllowUserToAddRows = false;
        _matchRuleGrid.RowHeadersVisible = false;
        _matchRuleGrid.AutoGenerateColumns = false;
        _matchRuleGrid.Font = UiTheme.FontNormal;
        _matchRuleGrid.EnableHeadersVisualStyles = false;
        _matchRuleGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 243, 247);
        _matchRuleGrid.ColumnHeadersDefaultCellStyle.Font = UiTheme.FontBold;
        _matchRuleGrid.ColumnHeadersHeight = 28;
        _matchRuleGrid.RowTemplate.Height = 26;

        _matchRuleGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "启用",
            DataPropertyName = nameof(MatchRuleEntry.Enabled),
            Width = 55,
        });

        _matchRuleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "关键词",
            DataPropertyName = nameof(MatchRuleEntry.Keyword),
            Width = 260,
        });

        _matchRuleGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "对应游戏",
            Name = "GameColumn",
            Width = 320,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        });

        var r2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        var add = UiTheme.CreateButton("新增规则", width: 90);
        add.Click += (_, _) =>
        {
            var games = _ctx.Library.AllGames;

            if (games.Count == 0)
            {
                MessageBox.Show(this, "请先在“游戏列表”页添加游戏。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _ctx.Settings.MatchRules.Add(new MatchRuleEntry
            {
                Keyword = "关键词",
                GameId = games[0].Id,
                Enabled = true,
            });

            RefreshMatchRules();
        };

        var remove = UiTheme.CreateButton("删除选中规则", width: 110);
        remove.Click += (_, _) =>
        {
            if (_matchRuleGrid.CurrentRow?.Index is not int index || index < 0) return;
            if (index >= _ctx.Settings.MatchRules.Count) return;

            _ctx.Settings.MatchRules.RemoveAt(index);
            RefreshMatchRules();
        };

        r2.Controls.Add(add);
        r2.Controls.Add(remove);

        group.Controls.Add(r2);
        group.Controls.Add(r1);
        group.Controls.Add(_matchRuleGrid);

        return group;
    }

    private GroupBox BuildAboutGroup()
    {
        var group = CreateGroup("日志与数据位置", 130);

        var r1 = Row();
        var openConfig = UiTheme.CreateButton("打开配置目录", width: 110);
        openConfig.Click += (_, _) => OpenFolder(Paths.AppDataRoot);

        var openLogs = UiTheme.CreateButton("打开日志目录", width: 110);
        openLogs.Click += (_, _) => OpenFolder(Paths.LogFolder);

        var openBackup = UiTheme.CreateButton("打开备份目录", width: 110);
        openBackup.Click += (_, _) => OpenFolder(Paths.BackupFolder);

        var cleanTemp = UiTheme.CreateButton("清理临时解压目录", width: 140);
        cleanTemp.Click += (_, _) =>
        {
            Paths.CleanTempQuietly(TimeSpan.Zero);
            MessageBox.Show(this, "已清理临时解压目录。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        r1.Controls.Add(openConfig);
        r1.Controls.Add(openLogs);
        r1.Controls.Add(openBackup);
        r1.Controls.Add(cleanTemp);

        var r2 = Row();
        r2.Controls.Add(UiTheme.CreateLabel($"配置: {Paths.ConfigFile}", color: UiTheme.TextSecondary));

        var r3 = Row();
        r3.Controls.Add(UiTheme.CreateLabel($"日志: {Paths.TodayLogFile}", color: UiTheme.TextSecondary));

        group.Controls.Add(r3);
        group.Controls.Add(r2);
        group.Controls.Add(r1);

        return group;
    }

    // ------------------------------------------------------------------ 保存

    private void LoadFromSettings()
    {
        var s = _ctx.Settings;

        // 保证迁移逻辑已跑过（老配置的监控目录会从系统下载目录切到 DropFolder）
        s.ApplyDefaults(Paths.AppDataRoot);

        _downloadFolder.Text = s.DownloadFolder;
        _monitorEnabled.Checked = s.MonitorEnabled;
        _pollInterval.Minimum = 1;
        _pollInterval.Maximum = 60;
        _pollInterval.Value = Clamp(s.PollIntervalSeconds, 1, 60);
        _stableChecks.Minimum = 1;
        _stableChecks.Maximum = 30;
        _stableChecks.Value = Clamp(s.StableChecksRequired, 1, 30);
        _minQuiet.Minimum = 0;
        _minQuiet.Maximum = 60;
        _minQuiet.Value = Clamp(s.MinQuietSeconds, 0, 60);
        _onlyNewFiles.Checked = s.OnlyNewFiles;
        _minFileSize.Minimum = 0;
        _minFileSize.Maximum = 10240;
        _minFileSize.Value = Clamp(s.MinFileSizeMB, 0, 10240);
        _ignoredExt.Text = string.Join(", ", s.IgnoredExtensions);
        UpdateBaselineLabel();

        _backupBeforeOverwrite.Checked = s.BackupBeforeOverwrite;
        _autoRollback.Checked = s.AutoRollbackOnFailure;
        _archiveInstalled.Checked = s.ArchiveInstalledPatches;
        _archiveFolder.Text = s.InstalledArchiveFolder;
        _retentionDays.Minimum = 1;
        _retentionDays.Maximum = 3650;
        _retentionDays.Value = Clamp(s.BackupRetentionDays, 1, 3650);

        _provider.SelectedItem = s.SearchProvider;
        if (_provider.SelectedIndex < 0) _provider.SelectedIndex = 0;

        _searxngUrl.Text = s.SearxngBaseUrl;
        _customUrl.Text = s.CustomSearchUrlTemplate;
        _customKey.Text = s.CustomSearchApiKey;
        _customKeyMode.SelectedItem = s.CustomSearchApiKeyMode;
        if (_customKeyMode.SelectedIndex < 0) _customKeyMode.SelectedIndex = 0;
        _customKeyName.Text = s.CustomSearchApiKeyName;
        _customResultsPath.Text = s.CustomSearchResultsPath;
        _keywordTemplates.Text = string.Join(Environment.NewLine, s.SearchKeywordTemplates);
        _resultLimit.Minimum = 1;
        _resultLimit.Maximum = 500;
        _resultLimit.Value = Clamp(s.SearchResultLimit, 1, 500);
        _httpTimeout.Minimum = 5;
        _httpTimeout.Maximum = 120;
        _httpTimeout.Value = Clamp(s.HttpTimeoutSeconds, 5, 120);

        _hostList.Items.Clear();
        foreach (var h in s.WhitelistedHosts) _hostList.Items.Add(h);

        _urlList.Items.Clear();
        foreach (var u in s.WhitelistedUrls) _urlList.Items.Add(u);

        _alwaysConfirmExe.Checked = s.AlwaysConfirmExe;
        _autoRunTrustedExe.Checked = s.AutoRunTrustedExe;

        // ---- 游戏归类 ----
        _enableSteamMetadata.Checked = s.EnableSteamMetadata;
        _enableHeuristicType.Checked = s.EnableHeuristicType;
        _metadataCacheDays.Minimum = 1;
        _metadataCacheDays.Maximum = 365;
        _metadataCacheDays.Value = Clamp(s.MetadataCacheDays, 1, 365);

        _networkMode.SelectedItem = s.NetworkMode;
        if (_networkMode.SelectedIndex < 0) _networkMode.SelectedIndex = 0;
        _customProxyUrl.Text = s.CustomProxyUrl;
        _customProxyUrl.Enabled = s.NetworkMode == "CustomProxy";

        _metadataConcurrency.Minimum = 1;
        _metadataConcurrency.Maximum = 16;
        _metadataConcurrency.Value = Clamp(s.MetadataConcurrency, 1, 16);

        _enableNameTypeRule.Checked = s.EnableNameTypeRule;
        _enableCommunityTags.Checked = s.EnableCommunityTags;

        RefreshExeRules();
        RefreshMatchRules();
        RefreshGenreMap();
        UpdateProviderFields();
    }

    private static decimal Clamp(int value, int min, int max) =>
        Math.Min(Math.Max(value, min), max);

    private void UpdateProviderFields()
    {
        var provider = _provider.SelectedItem as string ?? "Browser";

        _searxngUrl.Enabled = provider == "Searxng";
        _customUrl.Enabled = provider == "CustomJson";
        _customKey.Enabled = provider == "CustomJson";
        _customKeyMode.Enabled = provider == "CustomJson";
        _customKeyName.Enabled = provider == "CustomJson";
        _customResultsPath.Enabled = provider == "CustomJson";
    }

    private void RefreshExeRules()
    {
        var view = _ctx.Settings.ExeTrustRules
            .Select(r => new
            {
                r.Name,
                r.FileNamePattern,
                Sha = string.Join("; ", r.AllowedSha256),
                Hosts = string.Join("; ", r.AllowedHosts),
                r.SilentArguments,
            })
            .ToList();

        _exeRuleGrid.DataSource = new BindingSource { DataSource = view };
        _exeRuleGrid.Refresh();
    }

    private void RefreshMatchRules()
    {
        var games = _ctx.Library.AllGames;

        var view = _ctx.Settings.MatchRules
            .Select(r => new
            {
                r.Enabled,
                r.Keyword,
                GameId = r.GameId,
            })
            .ToList();

        _matchRuleGrid.DataSource = new BindingSource { DataSource = view };

        if (_matchRuleGrid.Columns["GameColumn"] is DataGridViewComboBoxColumn combo)
        {
            combo.DataSource = games;
            combo.DisplayMember = nameof(GameEntry.Name);
            combo.ValueMember = nameof(GameEntry.Id);
        }

        _matchRuleGrid.Refresh();
    }

    /// <summary>供主窗体在切到设置页时重新载入当前配置。</summary>
    public void ReloadFromSettings() => LoadFromSettings();

    /// <summary>把界面上的值写回设置并保存。</summary>
    public void SaveToSettings()
    {
        var s = _ctx.Settings;

        s.DownloadFolder = _downloadFolder.Text.Trim();
        s.MonitorEnabled = _monitorEnabled.Checked;
        s.PollIntervalSeconds = (int)_pollInterval.Value;
        s.StableChecksRequired = (int)_stableChecks.Value;
        s.MinQuietSeconds = (int)_minQuiet.Value;
        s.OnlyNewFiles = _onlyNewFiles.Checked;
        s.MinFileSizeMB = (int)_minFileSize.Value;
        s.IgnoredExtensions = _ignoredExt.Text
            .Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();

        s.BackupBeforeOverwrite = _backupBeforeOverwrite.Checked;
        s.AutoRollbackOnFailure = _autoRollback.Checked;
        s.ArchiveInstalledPatches = _archiveInstalled.Checked;
        s.InstalledArchiveFolder = _archiveFolder.Text.Trim();
        s.BackupRetentionDays = (int)_retentionDays.Value;

        s.SearchProvider = _provider.SelectedItem as string ?? "Browser";
        s.SearxngBaseUrl = _searxngUrl.Text.Trim();
        s.CustomSearchUrlTemplate = _customUrl.Text.Trim();
        s.CustomSearchApiKey = _customKey.Text.Trim();
        s.CustomSearchApiKeyMode = _customKeyMode.SelectedItem as string ?? "None";
        s.CustomSearchApiKeyName = _customKeyName.Text.Trim();
        s.CustomSearchResultsPath = _customResultsPath.Text.Trim();
        s.SearchKeywordTemplates = _keywordTemplates.Text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        if (s.SearchKeywordTemplates.Count == 0)
        {
            s.SearchKeywordTemplates.Add("{0} 汉化补丁");
        }

        s.SearchResultLimit = (int)_resultLimit.Value;
        s.HttpTimeoutSeconds = (int)_httpTimeout.Value;

        s.WhitelistedHosts = _hostList.Items.Cast<string>().ToList();
        s.WhitelistedUrls = _urlList.Items.Cast<string>().ToList();

        s.AlwaysConfirmExe = _alwaysConfirmExe.Checked;

        // 自动运行开关必须显式确认
        if (_autoRunTrustedExe.Checked && !s.AutoRunTrustedExe)
        {
            var confirm = MessageBox.Show(this,
                "开启后，命中可信规则、哈希校验通过、并且配置了静默参数的 .exe 补丁会在你点击“运行 EXE 补丁”后直接静默安装。\r\n\r\n"
                + "程序仍然不会绕过 Windows 的 UAC / SmartScreen。确定开启吗？",
                "确认开启自动运行", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            s.AutoRunTrustedExe = confirm == DialogResult.Yes;
            _autoRunTrustedExe.Checked = s.AutoRunTrustedExe;
        }
        else
        {
            s.AutoRunTrustedExe = _autoRunTrustedExe.Checked;
        }

        // ---- 游戏归类 ----
        s.EnableSteamMetadata = _enableSteamMetadata.Checked;
        s.EnableHeuristicType = _enableHeuristicType.Checked;
        s.MetadataCacheDays = (int)_metadataCacheDays.Value;
        s.NetworkMode = _networkMode.SelectedItem as string ?? "Auto";
        s.CustomProxyUrl = _customProxyUrl.Text.Trim();
        s.MetadataConcurrency = (int)_metadataConcurrency.Value;
        s.EnableNameTypeRule = _enableNameTypeRule.Checked;
        s.EnableCommunityTags = _enableCommunityTags.Checked;

        s.GenreTypeMapping = ReadGenreMapFromGrid();
        s.NameTypeRules = ReadRuleGrid(_nameRuleGrid);
        s.TagTypeMapping = ReadRuleGrid(_tagMapGrid);

        s.ApplyDefaults(Paths.AppDataRoot);
        ConfigStore.SaveSettings(s);

        RefreshGenreMap();

        _main.SetStatus("设置已保存。");
    }

    /// <summary>从映射表格读回映射（保持表格行顺序 = 优先级）。</summary>
    private Dictionary<string, string> ReadGenreMapFromGrid() => ReadRuleGrid(_genreMapGrid);

    // ------------------------------------------------------------------ 操作

    // ------------------------------------------------------------ 基线

    /// <summary>刷新基线状态文字。</summary>
    private void UpdateBaselineLabel()
    {
        var folder = _downloadFolder.Text.Trim();

        if (string.IsNullOrWhiteSpace(folder))
        {
            _baselineLabel.Text = "  尚未设置监控目录。";
            _baselineLabel.ForeColor = UiTheme.TextSecondary;
            return;
        }

        if (!IgnoreBaseline.HasBaseline(folder))
        {
            _baselineLabel.Text = $"  尚未建立基线：下次开始监控时会把「{folder}」中已有文件全部忽略。";
            _baselineLabel.ForeColor = UiTheme.Warning;
            return;
        }

        var count = IgnoreBaseline.CountOf(folder);
        var created = IgnoreBaseline.CreatedAt(folder);

        _baselineLabel.Text = $"  基线已建立（{created:yyyy-MM-dd HH:mm:ss}），已忽略 {count} 个已存在文件。";
        _baselineLabel.ForeColor = UiTheme.Success;
    }

    /// <summary>把当前目录里的文件全部登记为“已知”，只有之后新增的才会被处理。</summary>
    private void RebuildBaseline()
    {
        var folder = _downloadFolder.Text.Trim();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(this, "请先设置一个存在的监控目录。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将把「{folder}」中现有的所有 zip / 7z / rar / exe 文件全部登记为“已存在”，\r\n"
            + "它们不会被当作待处理补丁；只有之后新下载的文件才会被处理。\r\n\r\n"
            + "继续吗？",
            "重新建立基线", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        var count = IgnoreBaseline.CreateBaseline(folder);

        UpdateBaselineLabel();

        _main.SetStatus($"已重新建立基线，忽略 {count} 个已存在文件。");

        MessageBox.Show(this,
            $"已建立基线，忽略 {count} 个已存在文件。\r\n\r\n"
            + "提示：如果是你自己想手动处理的补丁，可以直接把文件用「待处理补丁」页手动添加，"
            + "或把它移出监控目录再移回来触发一次新文件判定。",
            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>清除基线，下次监控时会重新建立。</summary>
    private void ClearBaseline()
    {
        var folder = _downloadFolder.Text.Trim();

        if (string.IsNullOrWhiteSpace(folder)) return;

        IgnoreBaseline.Clear(folder);
        UpdateBaselineLabel();

        _main.SetStatus("已清除基线。");
    }

    /// <summary>打开（必要时创建）当前监控目录。</summary>
    private void OpenMonitorFolder()
    {
        var folder = _downloadFolder.Text.Trim();

        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "请先设置监控目录。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // 默认的 DropFolder 可能还没创建过，这里顺手建出来，避免“目录不存在”
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"无法创建/访问该目录：\r\n{folder}\r\n\r\n{ex.Message}",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        OpenFolder(folder);
    }

    private void AddHost()    {
        var host = _newHost.Text.Trim().TrimStart('.');

        if (host.Length == 0) return;

        if (_hostList.Items.Cast<string>().Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
            return;

        _hostList.Items.Add(host);
        _newHost.Text = "";
    }

    private void AddUrl()
    {
        var url = _newUrl.Text.Trim();

        if (url.Length == 0) return;

        if (_urlList.Items.Cast<string>().Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
            return;

        _urlList.Items.Add(url);
        _newUrl.Text = "";
    }

    private async Task TestSearxngAsync()
    {
        var url = _searxngUrl.Text.Trim();

        if (url.Length == 0)
        {
            MessageBox.Show(this, "请先填写 SearXNG 实例地址。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 临时应用设置以复用搜索服务
        var original = _ctx.Settings.SearxngBaseUrl;
        _ctx.Settings.SearxngBaseUrl = url;

        try
        {
            var service = new SearchService(_ctx.Settings);

            var outcome = await service.SearchAsync("测试", CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.IsError)
            {
                MessageBox.Show(this, outcome.Message, "测试结果",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(this,
                    $"连接成功，返回 {outcome.Candidates.Count} 条结果。",
                    "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"测试失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _ctx.Settings.SearxngBaseUrl = original;
        }
    }

    private async Task ComputeHashAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要计算 SHA-256 的 EXE 补丁",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _main.SetStatus("正在计算 SHA-256...");

            var hash = await FileHash.Sha256Async(dialog.FileName).ConfigureAwait(true);

            _main.SetStatus("SHA-256 计算完成。");

            var copy = MessageBox.Show(this,
                $"文件: {dialog.FileName}\r\n\r\nSHA-256:\r\n{hash}\r\n\r\n要复制到剪贴板吗？",
                "SHA-256", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (copy == DialogResult.Yes)
            {
                Clipboard.SetText(hash);
            }
            else
            {
                // 直接填到当前选中规则里，方便用户配置
                if (_exeRuleGrid.CurrentRow?.Index is int index &&
                    index >= 0 && index < _ctx.Settings.ExeTrustRules.Count)
                {
                    var rule = _ctx.Settings.ExeTrustRules[index];

                    if (!rule.AllowedSha256.Contains(hash, StringComparer.OrdinalIgnoreCase))
                    {
                        rule.AllowedSha256.Add(hash);
                    }

                    rule.Mode = ExeRunMode.Silent;
                    RefreshExeRules();

                    MessageBox.Show(this,
                        "已把哈希填入当前选中的规则，并把该规则设为“静默”模式。\r\n"
                        + "请自行确认并填写静默安装参数。",
                        "已更新规则", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"计算失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

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

    /// <summary>供主窗体调用，触发一次完整保存流程。</summary>
    public void Commit()
    {
        SaveToSettings();
        _onSaved();
    }
}
