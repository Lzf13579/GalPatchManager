using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>
/// 游戏列表页：Steam 自动识别 + 手动添加，支持刷新、搜索、修正识别结果。
/// </summary>
public sealed class GamesTab : UserControl
{
    private readonly AppContext _ctx;
    private readonly FormMain _main;

    private readonly TextBox _searchBox = UiTheme.CreateTextBox(false, 240);
    private readonly DataGridView _grid = new();
    private readonly Label _summary = UiTheme.CreateLabel("", color: UiTheme.TextSecondary);

    private readonly TextBox _nameBox = UiTheme.CreateTextBox(false, 320);
    private readonly TextBox _dirBox = UiTheme.CreateTextBox(false, 320);
    private readonly TextBox _appIdBox = UiTheme.CreateTextBox(true, 120);
    private readonly TextBox _sourceBox = UiTheme.CreateTextBox(true, 160);
    private readonly ComboBox _typeBox = new();
    private readonly TextBox _keywordsBox = UiTheme.CreateTextBox(false, 320);
    private readonly TextBox _noteBox = UiTheme.CreateTextBox(true, 320);

    /// <summary>二级菜单里「全部」筛选项的键，与 FormMain.GameTypeFilterAll 保持一致。</summary>
    private const string TypeFilterAll = "type:*all*";

    private List<GameEntry> _all = new();
    private List<GameEntry> _view = new();

    private string _typeFilter = TypeFilterAll;

    private bool _loading;
    private bool _suppressTypeSave;

    public GamesTab(AppContext ctx, FormMain main)
    {
        _ctx = ctx;
        _main = main;

        Dock = DockStyle.Fill;
        BackColor = UiTheme.WindowBack;

        BuildLayout();
    }

    // ------------------------------------------------------------------ 布局

    private void BuildLayout()
    {
        // ---------------- 顶部工具条 ----------------
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
            BackColor = UiTheme.WindowBack,
        };

        var btnRefresh = UiTheme.CreateButton("刷新列表", primary: true, width: 100);
        btnRefresh.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        var btnScanSteam = UiTheme.CreateButton("重新扫描 Steam", width: 130);
        btnScanSteam.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        var btnAdd = UiTheme.CreateButton("手动添加游戏", width: 120);
        btnAdd.Click += (_, _) => AddManualGame();

        toolbar.Controls.Add(btnRefresh);
        toolbar.Controls.Add(btnScanSteam);
        toolbar.Controls.Add(btnAdd);
        toolbar.Controls.Add(UiTheme.CreateLabel("  搜索:"));
        toolbar.Controls.Add(_searchBox);
        toolbar.Controls.Add(_summary);
        toolbar.Controls.Add(new Label { Width = 8, Text = "" });

        _searchBox.TextChanged += (_, _) => ApplyFilter();
        _searchBox.PlaceholderText = "按名称 / AppID / 路径过滤";

        // ---------------- 表格 ----------------
        BuildGrid();

        // ---------------- 详情 ----------------
        var detail = BuildDetailPanel();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
            SplitterWidth = 6,
            BackColor = UiTheme.WindowBack,
        };

        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(detail);

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
        _grid.MultiSelect = true;
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
            HeaderText = "游戏名称",
            DataPropertyName = nameof(GameEntry.Name),
            Width = 300,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Steam AppID",
            DataPropertyName = nameof(GameEntry.DisplayAppId),
            Width = 110,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "安装目录",
            DataPropertyName = nameof(GameEntry.InstallDir),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "来源",
            DataPropertyName = nameof(GameEntry.DisplaySource),
            Width = 130,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "类型",
            Name = "TypeColumn",
            Width = 130,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "库检测",
            Name = "StatusColumn",
            Width = 150,
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "说明",
            DataPropertyName = nameof(GameEntry.Note),
            Width = 240,
        });

        _grid.SelectionChanged += (_, _) => OnSelectionChanged();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) OpenGameFolder();
        };
    }

    private Control BuildDetailPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBack,
            Padding = new Padding(12),
            AutoScroll = true,
        };

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            AutoSize = true,
            Height = 200,
        };

        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddRow(string label, Control control)
        {
            form.Controls.Add(UiTheme.CreateLabel(label), 0, form.RowCount);
            form.Controls.Add(control, 1, form.RowCount);

            var row = form.RowCount;
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            form.RowCount++;

            _ = row;
        }

        form.RowCount = 0;
        AddRow("名称:", _nameBox);
        AddRow("AppID:", _appIdBox);
        AddRow("目录:", _dirBox);
        AddRow("来源:", _sourceBox);

        // 类型：可编辑下拉框，既可选内置类型也能自己敲一个
        _typeBox.DropDownStyle = ComboBoxStyle.DropDown;
        _typeBox.Font = UiTheme.FontNormal;
        _typeBox.Width = 200;

        var typePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0),
        };

        typePanel.Controls.Add(_typeBox);

        var typeHint = UiTheme.CreateLabel("（可自行输入新类型）", color: UiTheme.TextSecondary);
        typeHint.Margin = new Padding(6, 6, 0, 0);
        typePanel.Controls.Add(typeHint);

        AddRow("类型:", typePanel);

        AddRow("关键词:", _keywordsBox);
        AddRow("备注:", _noteBox);

        // 类型选择变化时立即保存
        _typeBox.SelectedIndexChanged += (_, _) => SaveTypeFromCombo();
        _typeBox.TextUpdate += (_, _) => { /* 只在失去焦点/回车时保存，见下方事件 */ };
        _typeBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                SaveTypeFromCombo();
                e.SuppressKeyPress = true;
            }
        };
        _typeBox.Leave += (_, _) => SaveTypeFromCombo();

        var tip = UiTheme.CreateLabel(
            "关键词用于把补丁文件名匹配到本游戏；多个关键词用逗号分隔。",
            color: UiTheme.TextSecondary);
        tip.Margin = new Padding(6, 4, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        var btnOpen = UiTheme.CreateButton("打开目录", width: 90);
        btnOpen.Click += (_, _) => OpenGameFolder();

        var btnSave = UiTheme.CreateButton("保存修正", primary: true, width: 100);
        btnSave.Click += (_, _) => SaveCorrection();

        var btnSearch = UiTheme.CreateButton("查找该游戏补丁", width: 130);
        btnSearch.Click += (_, _) =>
        {
            var game = SelectedGame();
            if (game is null) return;

            _main.ShowPage("search");
            _main.SetStatus($"已选择游戏「{game.Name}」，可在“查找补丁”页点击开始搜索。");
        };

        var btnKeywords = UiTheme.CreateButton("添加为匹配规则", width: 130);
        btnKeywords.Click += (_, _) => AddKeywordRule();

        var btnAutoType = UiTheme.CreateButton("自动归类（按类型）", width: 150);
        btnAutoType.Click += async (_, _) => await AutoDetectTypesAsync().ConfigureAwait(true);

        var btnApplyType = UiTheme.CreateButton("应用类型到选中", width: 130);
        btnApplyType.Click += (_, _) => ApplyTypeToSelected();

        var btnRemove = UiTheme.CreateButton("移除/隐藏", width: 100);
        btnRemove.Click += (_, _) => RemoveSelected();

        buttons.Controls.Add(btnOpen);
        buttons.Controls.Add(btnSave);
        buttons.Controls.Add(btnSearch);
        buttons.Controls.Add(btnKeywords);
        buttons.Controls.Add(btnAutoType);
        buttons.Controls.Add(btnApplyType);
        buttons.Controls.Add(btnRemove);

        panel.Controls.Add(tip);
        panel.Controls.Add(form);
        panel.Controls.Add(buttons);

        return panel;
    }

    // ------------------------------------------------------------------ 数据

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        if (_loading) return;

        _loading = true;
        _main.SetStatus("正在扫描 Steam 库...");

        try
        {
            _all = await _ctx.Library.RefreshSteamAsync().ConfigureAwait(true);

            _ctx.Settings.ExtraSteamLibraryPaths = _ctx.Library.SteamLibraryPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            ConfigStore.SaveSettings(_ctx.Settings);
        }
        catch (Exception ex)
        {
            Log.Error("刷新游戏列表失败", ex);
            MessageBox.Show(this,
                $"刷新失败：{ex.Message}",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loading = false;
        }

        ApplyFilter();
        Reload();

        // 刷新后重算左侧二级菜单的类型数量
        _main.RefreshGameTypeCounts();

        _main.SetStatus($"共 {_all.Count} 个游戏。");
    }

    /// <summary>只根据当前的内存数据重建表格（不重新扫描）。</summary>
    public void Reload()
    {
        _all = _ctx.Library.AllGames;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _all = _ctx.Library.AllGames;

        _view = _ctx.Library.Search(_all, _searchBox.Text);

        // 类型筛选（来自左侧二级菜单）
        if (_typeFilter != TypeFilterAll)
        {
            var wanted = _typeFilter.StartsWith("type:", StringComparison.Ordinal)
                ? _typeFilter["type:".Length..]
                : _typeFilter;

            _view = _view
                .Where(g => string.Equals(
                    GameTypeCatalog.ResolveType(g, _ctx.Settings), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _grid.DataSource = new BindingSource { DataSource = _view.Select(g => g).ToList() };
        _grid.Refresh();

        FillGridTypeColumn();

        var steamCount = _view.Count(g => g.Source == GameSource.Steam);
        var manualCount = _view.Count - steamCount;

        var typeText = _typeFilter == TypeFilterAll
            ? "全部类型"
            : (_typeFilter.StartsWith("type:", StringComparison.Ordinal)
                ? _typeFilter["type:".Length..]
                : _typeFilter);

        _summary.Text = $"  {typeText}：{_view.Count} 个（Steam {steamCount} / 手动 {manualCount}）";

        var libs = string.Join("; ", _ctx.Library.SteamLibraryPaths);

        if (!string.IsNullOrWhiteSpace(libs))
        {
            _summary.Text += $"   库: {libs}";
        }

        // 诊断信息只记录到日志，避免界面过长
        foreach (var diag in _ctx.Library.SteamDiagnostics)
        {
            Log.Debug($"Steam 诊断: {diag}");
        }

        RefreshTypeComboItems();
    }

    /// <summary>填充表格里的「类型」列（类型是计算出来的，不能直接绑定）。</summary>
    private void FillGridTypeColumn()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is not GameEntry game) continue;

            var type = GameTypeCatalog.ResolveType(game, _ctx.Settings);
            var isAuto = GameTypeCatalog.IsAutoDetected(game, _ctx.Settings);

            // 顺带把 genres 写进单元格提示，方便核对归类依据
            var cached = TagCache.Get(GameTypeCatalog.BuildStableKey(game));

            var text = isAuto ? $"{type}（自动）" : type;

            if (cached is not null && cached.Genres.Count > 0)
            {
                text += $" · {string.Join("/", cached.Genres.Take(3))}";
            }

            row.Cells["TypeColumn"].Value = text;

            row.Cells["StatusColumn"].Value = BuildLibraryStatus(game);
        }
    }

    /// <summary>
    /// 「库检测」列：原生中文支持 + 待处理/已安装补丁状态。
    /// 具体文本生成逻辑在 <see cref="LibraryStatus.Describe"/>，便于自检覆盖。
    /// </summary>
    private string BuildLibraryStatus(GameEntry game)
    {
        var cached = TagCache.Get(GameTypeCatalog.BuildStableKey(game));

        var pending = _ctx.Pending.Count(p =>
            string.Equals(p.MatchedGameId, game.Id, StringComparison.Ordinal) &&
            p.State is PatchState.Detected or PatchState.Downloading or PatchState.Downloaded
                or PatchState.Matched);

        var installed = _ctx.History.Count(h =>
            string.Equals(h.GameId, game.Id, StringComparison.Ordinal) &&
            h.Status == InstallStatus.Success);

        return LibraryStatus.Describe(cached, pending, installed);
    }

    /// <summary>刷新类型下拉框选项。</summary>
    private void RefreshTypeComboItems()
    {
        var current = _typeBox.Text;

        var items = GameTypeCatalog.BuildTypeList(_ctx.Settings)
            .Select(t => t.Name)
            .ToArray();

        _suppressTypeSave = true;

        try
        {
            _typeBox.Items.Clear();
            _typeBox.Items.AddRange(items);

            if (current.Length > 0 && !_typeBox.Items.Contains(current))
            {
                _typeBox.Items.Add(current);
            }
        }
        finally
        {
            _suppressTypeSave = false;
        }
    }

    // ------------------------------------------------------------------ 类型

    /// <summary>当前二级菜单选中的类型键。</summary>
    public string CurrentTypeKey => _typeFilter;

    /// <summary>供左侧二级菜单调用：设置类型筛选。</summary>
    public void SetTypeFilter(string key)
    {
        _typeFilter = string.IsNullOrWhiteSpace(key) ? TypeFilterAll : key;

        ApplyFilter();
    }

    /// <summary>把下拉框里选的类型写到当前游戏。</summary>
    private void SaveTypeFromCombo()
    {
        if (_suppressTypeSave) return;

        var game = SelectedGame();
        if (game is null) return;

        var value = _typeBox.Text.Trim();

        var resolved = GameTypeCatalog.ResolveType(game, _ctx.Settings);

        // 没变化就不写盘
        if (string.Equals(value, resolved, StringComparison.OrdinalIgnoreCase)) return;

        GameTypeCatalog.SetType(game, _ctx.Settings, value);
        ConfigStore.SaveSettings(_ctx.Settings);

        Log.Info($"已设置游戏类型: {game.Name} -> {(value.Length == 0 ? "未分类" : value)}");

        Reload();
        _main.RefreshGameTypeCounts();
        _main.SetStatus($"已把「{game.Name}」的类型设为 {(value.Length == 0 ? "未分类" : value)}");
    }

    /// <summary>把下拉框里的类型应用到所有选中行。</summary>
    private void ApplyTypeToSelected()
    {
        var value = _typeBox.Text.Trim();

        var targets = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as GameEntry)
            .Where(g => g is not null)
            .Cast<GameEntry>()
            .ToList();

        if (targets.Count == 0)
        {
            MessageBox.Show(this, "请先在列表中选择一个或多个游戏。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var g in targets)
        {
            GameTypeCatalog.SetType(g, _ctx.Settings, value);
        }

        ConfigStore.SaveSettings(_ctx.Settings);

        Reload();
        _main.RefreshGameTypeCounts();

        _main.SetStatus($"已把 {targets.Count} 个游戏设为「{(value.Length == 0 ? "未分类" : value)}」。");
    }

    /// <summary>供设置页调用：直接触发一次自动归类。</summary>
    public Task RunAutoClassifyAsync() => AutoDetectTypesAsync();

    /// <summary>
    /// 自动归类：优先用 Steam 官方类型（genres）映射，其次本地引擎特征识别。
    /// 绝不覆盖用户手动指定的类型。
    /// </summary>
    private async Task AutoDetectTypesAsync()
    {
        var games = _all.ToList();

        if (games.Count == 0)
        {
            MessageBox.Show(this, "游戏列表为空。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var appIdCount = games.Count(g => !string.IsNullOrWhiteSpace(g.AppId));

        var confirm = MessageBox.Show(this,
            $"将对 {games.Count} 个游戏自动归类。\r\n\r\n"
            + "归类顺序：\r\n"
            + "  1. 你手动指定的类型（最高优先级，绝不覆盖）\r\n"
            + $"  2. Steam 官方类型（genres）按映射规则转换（{appIdCount} 个游戏有 AppID）\r\n"
            + "  3. 游戏名 / 目录名关键词（纯离线）\r\n"
            + "  4. 本地引擎特征识别（Ren'Py / KiriKiri / RPG Maker / Unity）—— 最后手段\r\n\r\n"
            + "说明：Steam 官方接口只提供 genres（Action/Adventure/RPG…），\r\n"
            + "不提供社区用户标签，所以这里不是按用户标签分类。\r\n"
            + "引擎识别排在最后，因为它只能判断技术栈（是不是 Unity），判断不了玩法类型。\r\n\r\n"
            + (_ctx.Settings.EnableSteamMetadata
                ? "会先测试网络；不可用则自动跳过联网，只用本地规则。\r\n"
                : "当前已关闭联网抓取，仅使用本地缓存与本地识别。\r\n")
            + "\r\n继续吗？",
            "自动归类", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        var report = new ClassifyReport();

        try
        {
            _main.SetStatus("正在检查网络…");

            // 先探测一次：网络不通就立刻放弃联网部分，只做本地识别，
            // 否则每个游戏都要等超时，几十个游戏会卡好几分钟。
            var allowNetwork = false;

            if (_ctx.Settings.EnableSteamMetadata && appIdCount > 0)
            {
                var strategy = await SteamMetadataService
                    .ProbeAsync(_ctx.Settings, CancellationToken.None).ConfigureAwait(true);

                if (strategy is not null)
                {
                    allowNetwork = true;
                    _main.SetStatus($"网络可用（{strategy}），开始归类…");
                }
                else
                {
                    Log.Warn("网络不可用，本次归类只使用本地规则");

                    MessageBox.Show(this,
                        "无法访问 Steam 商店接口，本次归类将只使用**本地规则**：\r\n"
                        + "引擎特征识别 + 游戏名关键词。\r\n\r\n"
                        + "原因可能是：网络不通、代理破坏了 TLS、或站点被屏蔽。\r\n"
                        + "可在「设置 → 游戏归类」里切换联网方式（直连 / 系统代理 / 自定义代理），\r\n"
                        + "或点「测试连接」查看详细结果。\r\n\r\n"
                        + "本地规则不依赖网络，仍然生效。",
                        "网络不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            var classifier = new AutoClassifier(_ctx.Settings);
            var done = 0;

            foreach (var game in games)
            {
                done++;

                _main.SetStatus($"正在归类… {done}/{games.Count}：{game.Name}");

                try
                {
                    await classifier.ClassifyAsync(game, report, allowNetwork)
                        .ConfigureAwait(true);

                    classifier.Apply(game, report);
                }
                catch (Exception ex)
                {
                    Log.Warn($"归类失败 {game.Name}: {ex.Message}");
                }
            }

            TagCache.Save();

            _all = _ctx.Library.AllGames;

            // 把识别依据写进条目备注
            foreach (var game in _all)
            {
                var key = GameTypeCatalog.BuildStableKey(game);

                if (report.Reasons.TryGetValue(key, out var reason))
                {
                    game.DetectedTypeReason = reason;
                }
            }

            ConfigStore.SaveSettings(_ctx.Settings);

            Reload();
            _main.RefreshGameTypeCounts();

            var msg = $"归类完成。\r\n\r\n"
                      + $"· 按 Steam 官方类型映射：{report.FromGenre} 个\r\n"
                      + $"· 按本地引擎特征识别：{report.FromHeuristic} 个\r\n"
                      + $"· 按游戏名关键词：{report.FromName} 个\r\n"
                      + $"· 跳过（你手动指定过）：{report.SkippedManual} 个\r\n"
                      + $"· 仍为未分类：{report.Unclassified} 个\r\n"
                      + $"· 元数据：命中缓存 {report.FromCache} 个，新抓取 {report.FromNetwork} 个\r\n";

            if (report.NetworkFailures > 0)
            {
                msg += $"\r\n⚠ 有 {report.NetworkFailures} 个游戏请求 Steam 接口失败"
                       + "（网络不可达或被限制）。这些游戏只用了本地引擎识别。\r\n"
                       + "已抓到的结果会缓存，断网时仍可使用。";
            }

            msg += "\r\n自动结果在列表里标注「（自动）」，可在「类型」下拉框里随时改。";

            _main.SetStatus($"归类完成：官方类型 {report.FromGenre}，引擎识别 {report.FromHeuristic}，"
                            + $"名称关键词 {report.FromName}，未分类 {report.Unclassified}。");

            MessageBox.Show(this, msg, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("自动归类失败", ex);

            MessageBox.Show(this, $"归类失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private GameEntry? SelectedGame()
    {
        if (_grid.CurrentRow?.DataBoundItem is GameEntry game) return game;

        return null;
    }

    private void OnSelectionChanged()
    {
        var game = SelectedGame();

        _suppressTypeSave = true;

        try
        {
            if (game is null)
            {
                _nameBox.Text = "";
                _dirBox.Text = "";
                _appIdBox.Text = "";
                _sourceBox.Text = "";
                _typeBox.Text = "";
                _keywordsBox.Text = "";
                _noteBox.Text = "";
                return;
            }

            _nameBox.Text = game.Name;
            _dirBox.Text = game.InstallDir;
            _appIdBox.Text = game.DisplayAppId;
            _sourceBox.Text = game.DisplaySource;
            _typeBox.Text = GameTypeCatalog.ResolveType(game, _ctx.Settings);
            _keywordsBox.Text = string.Join(", ", game.MatchKeywords);

            // 备注里补上类型识别依据，方便排查为什么是"自动"
            var note = game.Note;

            if (GameTypeCatalog.IsAutoDetected(game, _ctx.Settings) &&
                !string.IsNullOrWhiteSpace(game.DetectedTypeReason))
            {
                note = $"{note} | 类型自动识别依据: {game.DetectedTypeReason}";
            }

            _noteBox.Text = note;
        }
        finally
        {
            _suppressTypeSave = false;
        }
    }

    // ------------------------------------------------------------------ 操作

    private async void AddManualGame()
    {
        using var dialog = new GameEditDialog();

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var entry = _ctx.Library.AddManual(
                dialog.GameName, dialog.InstallDir, dialog.Keywords);

            _ctx.Settings.ExtraSteamLibraryPaths = _ctx.Settings.ExtraSteamLibraryPaths
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            ConfigStore.SaveSettings(_ctx.Settings);

            Reload();
            _main.SetStatus($"已添加游戏「{entry.Name}」。");

            await Task.CompletedTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("添加游戏失败", ex);
            MessageBox.Show(this, $"添加失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveCorrection()
    {
        var game = SelectedGame();

        if (game is null)
        {
            MessageBox.Show(this, "请先在列表中选择一个游戏。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameBox.Text) || string.IsNullOrWhiteSpace(_dirBox.Text))
        {
            MessageBox.Show(this, "游戏名称和安装目录都不能为空。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var keywords = _keywordsBox.Text
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToList();

            _ctx.Library.UpdateGame(game, _nameBox.Text, _dirBox.Text, keywords);
            Reload();

            MessageBox.Show(this, "已保存修正结果。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("保存修正失败", ex);
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddKeywordRule()
    {
        var game = SelectedGame();

        if (game is null)
        {
            MessageBox.Show(this, "请先在列表中选择一个游戏。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var keyword = _keywordsBox.Text
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(keyword))
        {
            MessageBox.Show(this, "请先在“关键词”里填写至少一个关键词。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_ctx.Settings.MatchRules.Any(r =>
                string.Equals(r.Keyword, keyword, StringComparison.OrdinalIgnoreCase) &&
                r.GameId == game.Id))
        {
            MessageBox.Show(this, "该关键词规则已存在。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _ctx.Settings.MatchRules.Add(new MatchRuleEntry
        {
            Keyword = keyword,
            GameId = game.Id,
            Enabled = true,
        });

        ConfigStore.SaveSettings(_ctx.Settings);

        MessageBox.Show(this,
            $"已添加匹配规则：补丁文件名包含「{keyword}」时自动对应「{game.Name}」。",
            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RemoveSelected()
    {
        var game = SelectedGame();

        if (game is null) return;

        var what = game.Source == GameSource.Steam ? "从列表隐藏" : "删除";

        if (MessageBox.Show(this,
                $"确定要{what}「{game.Name}」吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _ctx.Library.Remove(game);
        Reload();
    }

    private void OpenGameFolder()
    {
        var game = SelectedGame();

        if (game is null) return;

        if (!Directory.Exists(game.InstallDir))
        {
            MessageBox.Show(this, $"目录不存在：{game.InstallDir}", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{game.InstallDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开目录失败", ex);
        }
    }
}
