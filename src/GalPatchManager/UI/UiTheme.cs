using System.Drawing;
using System.Windows.Forms;

namespace GalPatchManager.UI;

/// <summary>
/// 统一的界面主题（参考 PCL 的浅色卡片风格：浅色底 + 蓝色强调 + 圆角卡片）。
/// </summary>
public static class UiTheme
{
    public static readonly Color Accent = Color.FromArgb(19, 116, 219);
    public static readonly Color AccentHover = Color.FromArgb(38, 140, 240);
    public static readonly Color AccentLight = Color.FromArgb(232, 242, 254);

    public static readonly Color WindowBack = Color.FromArgb(244, 246, 249);
    public static readonly Color CardBack = Color.White;
    public static readonly Color NavBack = Color.FromArgb(236, 240, 246);

    public static readonly Color TextPrimary = Color.FromArgb(32, 36, 42);
    public static readonly Color TextSecondary = Color.FromArgb(110, 118, 130);
    public static readonly Color Border = Color.FromArgb(220, 225, 232);

    public static readonly Color Success = Color.FromArgb(46, 160, 87);
    public static readonly Color Warning = Color.FromArgb(214, 148, 24);
    public static readonly Color Danger = Color.FromArgb(206, 66, 62);

    public static readonly Font FontNormal = new("Microsoft YaHei UI", 9F);
    public static readonly Font FontSmall = new("Microsoft YaHei UI", 8.25F);
    public static readonly Font FontBold = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font FontTitle = new("Microsoft YaHei UI", 12F, FontStyle.Bold);
    public static readonly Font FontMono = new("Consolas", 9F);

    /// <summary>创建统一风格的按钮。</summary>
    public static Button CreateButton(string text, bool primary = false, int width = 96)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            Font = FontNormal,
            Cursor = Cursors.Hand,
            BackColor = primary ? Accent : CardBack,
            ForeColor = primary ? Color.White : TextPrimary,
        };

        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : AccentLight;

        return button;
    }

    /// <summary>创建只读或可编辑的文本框。</summary>
    public static TextBox CreateTextBox(bool readOnly = false, int width = 200)
    {
        return new TextBox
        {
            Width = width,
            Font = FontNormal,
            ReadOnly = readOnly,
            BackColor = readOnly ? Color.FromArgb(248, 249, 251) : Color.White,
        };
    }

    public static Label CreateLabel(string text, bool bold = false, Color? color = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = bold ? FontBold : FontNormal,
            ForeColor = color ?? TextPrimary,
            BackColor = Color.Transparent,
        };
    }
}
