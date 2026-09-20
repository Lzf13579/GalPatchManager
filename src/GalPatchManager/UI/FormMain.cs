using System.Drawing;
using System.Windows.Forms;
using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>
/// 主窗口：顶部标题栏 + 左侧导航 + 内容区。
/// 五个页面：游戏列表 / 查找补丁 / 待处理补丁 / 安装历史 / 设置。
/// </summary>
public sealed class FormMain : Form
{
    private readonly AppContext _ctx;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    private readonly Panel _nav = new() { Dock = DockStyle.Left, Width = 168, BackColor = UiTheme.NavBack };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "就绪" };
    private readonly ToolStripStatusLabel _monitorLabel = new() { Text = "" };
    private readonly ToolStripStatusLabel _pendingLabel = new() { Text = "" };

    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Dictionary<string, Control> _pages = new();

    /// <summary>二级菜单里「全部」筛选项的键。</summary>
    private const string GameTypeFilterAll = "type:*all*";

    /// <summary>当前显示的页面键。</summary>
    private string _currentPage = "games";

    private readonly GamesTab _gamesTab;
    private readonly SearchTab _searchTab;
    private readonly DownloadsTab _downloadsTab;
    private readonly HistoryTab _historyTab;
    private readonly SettingsTab _settingsTab;

    /// <summary>启动时显示的页面（可由 --tab= 指定）。</summary>
    private readonly string _initialPage;

    public FormMain(AppContext ctx, string? initialPage = null)
    {
        _ctx = ctx;
        _initialPage = initialPage ?? "games";

        Text = "游戏补丁安装器";
        Font = UiTheme.FontNormal;
        BackColor = UiTheme.WindowBack;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 640);
        Width = Math.Max(980, _ctx.Settings.WindowWidth);
        Height = Math.Max(640, _ctx.Settings.WindowHeight);

        // ---------------- 顶部标题 ----------------
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = UiTheme.Accent,
        };

        var title = new Label
        {
            Text = "  游戏补丁安装器",
            ForeColor = Color.White,
            Font = UiTheme.FontTitle,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        header.Controls.Add(title);

        // ---------------- 状态栏 ----------------
        _status.Items.Add(_statusLabel);
        _status.Items.Add(new ToolStripStatusLabel { Spring = true });
        _status.Items.Add(_pendingLabel);
        _status.Items.Add(new ToolStripStatusLabel { Text = " | " });
        _status.Items.Add(_monitorLabel);
        _status.SizingGrip = false;

        // ---------------- 页面 ----------------
        _gamesTab = new GamesTab(_ctx, this);
        _searchTab = new SearchTab(_ctx, this);
        _downloadsTab = new DownloadsTab(_ctx, this);
        _historyTab = new HistoryTab(_ctx, this);
        _settingsTab = new SettingsTab(_ctx, this, OnSettingsChanged);

        AddPage("games", "游戏列表", _gamesTab);
        AddPage("search", "查找补丁", _searchTab);
        AddPage("downloads", "待处理补丁", _downloadsTab);
        AddPage("history", "安装历史", _historyTab);
        AddPage("settings", "设置", _settingsTab);

        BuildNav();

        _content.Controls.AddRange(_pages.Values.ToArray());

        // Dock 顺序：先加内容，再加导航，最后加标题，避免被遮挡
        Controls.Add(_content);
        Controls.Add(_nav);
        Controls.Add(header);
        Controls.Add(_status);

        _ctx.PendingChanged += (_, _) => UpdateCounters();
        _ctx.HistoryChanged += (_, _) => UpdateCounters();

        Load += async (_, _) =>
        {
            ShowPage(_initialPage);
            UpdateCounters();

            _ctx.StartMonitor();
            UpdateMonitorLabel();

            // 诊断：DumpNavOrder=1 时把当前导航按钮的实际顺序与坐标写入文件。
            // 用于确认 Dock=Top 的渲染顺序，避免只靠读代码猜测。
            DumpNavOrderIfRequested();

            // 监控进度实时反映到“待处理补丁”页
            _ctx.Monitor.Progress += (_, e) =>
            {
                var item = _ctx.Pending.FirstOrDefault(p =>
                    string.Equals(p.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase));

                if (item is not null)
                {
                    item.Size = e.Size;
                    item.StabilityChecks = e.StabilityChecks;

                    if (!item.IsComplete)
                    {
                        item.State = PatchState.Downloading;
                    }
                }

                SetStatus(e.Message);
            };

            await _gamesTab.InitializeAsync().ConfigureAwait(true);

            _historyTab.Reload();
            _downloadsTab.Reload();

            RefreshGameTypeCounts();
            RefreshNavSelection();
            UpdateCounters();
        };

        FormClosing += (_, _) =>
        {
            try
            {
                _ctx.Settings.WindowWidth = Width;
                _ctx.Settings.WindowHeight = Height;
                _ctx.SaveAll();
            }
            catch (Exception ex)
            {
                Log.Warn($"保存窗口状态失败: {ex.Message}");
            }

            _ctx.Dispose();
        };
    }

    // ------------------------------------------------------------------ 导航

    private void AddPage(string key, string title, Control control)
    {
        control.Dock = DockStyle.Fill;
        control.Visible = false;
        _pages[key] = control;
        _ = title;
    }

    private void BuildNav()
    {
        var spacer = new Panel { Dock = DockStyle.Top, Height = 10 };
        _nav.Controls.Add(spacer);

        // 目标（自上而下）：游戏列表 / （游戏类型二级菜单） / 查找补丁 / 待处理补丁 / 安装历史 / 设置。
        //
        // 注意 WinForms 的 Dock=Top 语义：依次 Add 多个 Dock=Top 控件时，
        // **先添加的排在最上面**，后添加的依次往下叠。
        // 所以这里按目标顺序**从下往上**添加，最终渲染出来才是上面的顺序。
        var bottomToTop = new (string Key, string Text)[]
        {
            ("settings", "设置"),
            ("history", "安装历史"),
            ("downloads", "待处理补丁"),
            ("search", "查找补丁"),
        };

        foreach (var (key, text) in bottomToTop)
        {
            AddNavButton(key, text, 0, 42, bold: false, isSubItem: false);
        }

        // 「游戏列表」标题 + 其下的类型二级菜单
        BuildGameTypeSubNav();

        PinSettingsToBottom();
    }

    /// <summary>添加一个导航按钮。</summary>
    private Button AddNavButton(string key, string text, int indent, int height, bool bold, bool isSubItem)
    {
        var button = new Button
        {
            Text = "  " + new string(' ', indent) + text,
            Dock = DockStyle.Top,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = isSubItem ? UiTheme.FontSmall : (bold ? UiTheme.FontBold : UiTheme.FontNormal),
            BackColor = UiTheme.NavBack,
            ForeColor = isSubItem ? UiTheme.TextSecondary : UiTheme.TextPrimary,
            Cursor = Cursors.Hand,
            Tag = key,
            Padding = new Padding(0),
        };

        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = UiTheme.AccentLight;

        // 二级菜单项：走类型筛选，而不是切换页面
        if (isSubItem)
        {
            button.Click += (_, _) => SelectGameType(key);
        }
        else
        {
            button.Click += (_, _) => ShowPage(key);
        }

        _nav.Controls.Add(button);

        if (!isSubItem) _navButtons[key] = button;

        return button;
    }

    /// <summary>
    /// 构建「游戏列表」+ 其下的游戏类型二级菜单。
    /// 顺序：全部 / 各类型 / 未分类。
    /// </summary>
    private void BuildGameTypeSubNav()
    {
        var typeList = GameTypeCatalog.BuildTypeList(_ctx.Settings);

        // 从下往上添加，最终「游戏列表」在最上面
        // 1) 未分类
        // 2) 各类型
        // 3) 「全部」
        // 4) 游戏列表标题

        foreach (var type in typeList.AsEnumerable().Reverse())
        {
            var key = type.Key;
            AddNavButton(key, type.Name, 2, 30, bold: false, isSubItem: true);
        }

        AddNavButton(GameTypeFilterAll, "全部", 2, 30, bold: false, isSubItem: true);

        // 标题（点击回到「全部」）
        var header = AddNavButton("games", "游戏列表", 0, 42, bold: false, isSubItem: false);
        header.Font = UiTheme.FontBold;
    }

    /// <summary>切换游戏类型筛选。</summary>
    private void SelectGameType(string key)
    {
        // 保证在游戏列表页
        ShowPage("games");

        _gamesTab?.SetTypeFilter(key);
        RefreshNavSelection();
    }

    /// <summary>供设置页调用：触发一次自动归类。</summary>
    public Task RunAutoClassifyAsync() => _gamesTab.RunAutoClassifyAsync();

    /// <summary>刷新导航高亮（主项 + 二级项）。</summary>
    private void RefreshNavSelection()
    {
        var activeType = _gamesTab?.CurrentTypeKey ?? GameTypeFilterAll;

        foreach (var kv in _nav.Controls.OfType<Button>())
        {
            var key = kv.Tag as string ?? "";
            var isSub = key.StartsWith("type:", StringComparison.Ordinal) || key == GameTypeFilterAll;

            bool active;

            if (isSub)
            {
                active = key == activeType && _currentPage == "games";
            }
            else
            {
                active = key == _currentPage;
            }

            kv.BackColor = active ? UiTheme.Accent : UiTheme.NavBack;
            kv.ForeColor = active
                ? Color.White
                : (isSub ? UiTheme.TextSecondary : UiTheme.TextPrimary);
            kv.Font = active
                ? UiTheme.FontBold
                : (isSub ? UiTheme.FontSmall : UiTheme.FontNormal);
        }
    }

    /// <summary>更新二级菜单里的类型数量（游戏列表变化后调用）。</summary>
    public void RefreshGameTypeCounts()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshGameTypeCounts));
            return;
        }

        var games = _ctx.Library.AllGames;
        var typeList = GameTypeCatalog.BuildTypeList(_ctx.Settings);
        var counts = GameTypeCatalog.CountByType(games, _ctx.Settings, typeList);

        foreach (var button in _nav.Controls.OfType<Button>())
        {
            var key = button.Tag as string;
            if (key is null) continue;

            if (key == GameTypeFilterAll)
            {
                SetSubItemText(button, "全部", games.Count);
                continue;
            }

            if (!key.StartsWith("type:", StringComparison.Ordinal)) continue;

            var name = key["type:".Length..];
            var count = counts.TryGetValue(name, out var c) ? c : 0;

            SetSubItemText(button, name, count);
        }
    }

    /// <summary>重写二级项文字并保留缩进。</summary>
    private static void SetSubItemText(Button button, string name, int count)
    {
        var text = count > 0 ? $"{name} ({count})" : name;

        button.Text = "    " + text;
    }

    /// <summary>
    /// 在“设置”按钮上方插入一条分隔线，让底部的设置与其他页面分开。
    /// </summary>
    private void PinSettingsToBottom()
    {
        if (!_navButtons.TryGetValue("settings", out var settingsButton)) return;

        var separator = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = UiTheme.Border,
        };

        _nav.Controls.Add(separator);

        // 分隔线要排在设置按钮的**上面**
        var settingsIndex = _nav.Controls.GetChildIndex(settingsButton);
        _nav.Controls.SetChildIndex(separator, settingsIndex);
    }

    public void ShowPage(string key)
    {
        // 允许 --tab= 使用中文页签名或英文键名
        key = NormalizePageKey(key);

        if (!_pages.TryGetValue(key, out var page)) return;

        foreach (var kv in _pages) kv.Value.Visible = kv.Key == key;

        _currentPage = key;

        // 切到待处理页时刷新一次
        if (key == "downloads") _downloadsTab.Reload();
        if (key == "history") _historyTab.Reload();
        if (key == "search") _searchTab.LoadGames();
        if (key == "games") _gamesTab.Reload();
        if (key == "settings") _settingsTab.ReloadFromSettings();

        RefreshNavSelection();
    }

    /// <summary>把页签名（中文或英文）统一成内部键名。</summary>
    private static string NormalizePageKey(string key)
    {
        return (key ?? "").Trim().ToLowerInvariant() switch
        {
            "games" or "游戏列表" => "games",
            "search" or "查找补丁" => "search",
            "downloads" or "待处理补丁" or "待处理" => "downloads",
            "history" or "安装历史" => "history",
            "settings" or "设置" => "settings",
            _ => "games",
        };
    }

    /// <summary>
    /// 诊断用：设置环境变量 DumpNavOrder=1 启动时，把导航按钮的显示顺序（按 Y 坐标排序）
    /// 写入 %AppData%\GalPatchManager\nav-order.txt。
    /// </summary>
    private void DumpNavOrderIfRequested()
    {
        if (Environment.GetEnvironmentVariable("DumpNavOrder") != "1") return;

        try
        {
            var lines = _nav.Controls.OfType<Button>()
                .OrderBy(b => b.Top)
                .Select(b => $"y={b.Top,4}  h={b.Height,2}  key={b.Tag,-22} text={b.Text.Trim()}");

            var report = "导航按钮实际渲染顺序（按 Y 坐标从上到下）:" + Environment.NewLine
                         + string.Join(Environment.NewLine, lines);

            File.WriteAllText(
                Path.Combine(Paths.AppDataRoot, "nav-order.txt"),
                report,
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断失败不影响正常使用
        }
    }

    // ------------------------------------------------------------------ 状态

    public void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text)));
            return;
        }

        _statusLabel.Text = text;
    }

    public void UpdateCounters()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(UpdateCounters));
            return;
        }

        var pending = _ctx.Pending.Count(p =>
            p.State is PatchState.Detected or PatchState.Downloading or PatchState.Downloaded
                or PatchState.Matched);

        _pendingLabel.Text = $"待处理: {pending}";
        _navButtons.TryGetValue("downloads", out var btn);

        if (btn is not null)
        {
            var label = pending > 0 ? $"  待处理补丁 ({pending})" : "  待处理补丁";

            if (btn.Text != label) btn.Text = label;
        }
    }

    public void UpdateMonitorLabel()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(UpdateMonitorLabel));
            return;
        }

        _monitorLabel.Text = _ctx.Monitor.IsRunning
            ? $"监控中: {_ctx.Settings.DownloadFolder}"
            : "监控已停止";
    }

    private void OnSettingsChanged()
    {
        // 设置页保存后：重启监控、刷新状态
        try
        {
            _ctx.Monitor.Restart();
        }
        catch (Exception ex)
        {
            Log.Warn($"重启监控失败: {ex.Message}");
        }

        UpdateMonitorLabel();
        SetStatus("设置已保存。");
        _gamesTab.Reload();
    }
}
