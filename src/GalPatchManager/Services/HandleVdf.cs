using System.Text;

namespace GalPatchManager.Services;

/// <summary>
/// Valve KeyValues（VDF / ACF）最小解析器。
///
/// 支持 Steam 实际使用的两种写法：
///   "libraryfolders"
///   {
///       "0" { "path" "D:\\SteamLibrary" "apps" { "730" "123456" } }
///       "1" { "path" "E:\\Games" }
///   }
/// 以及老的扁平写法：
///   "LibraryFolders" { "1" "D:\\SteamLibrary" "2" "E:\\Games" }
/// </summary>
public sealed class VdfNode
{
    /// <summary>父节点，根节点为 null。</summary>
    public VdfNode? Parent { get; set; }

    /// <summary>子节点。键名不区分大小写。</summary>
    public Dictionary<string, VdfNode> Children { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本节点的直接字符串值。Steam 的 VDF 键不区分大小写。</summary>
    public Dictionary<string, string> Values { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string? Name { get; set; }

    public bool HasValue(string key) => Values.ContainsKey(key);

    public string? GetValue(string key) =>
        Values.TryGetValue(key, out var v) ? v : null;

    public VdfNode? GetChild(string key) =>
        Children.TryGetValue(key, out var v) ? v : null;

    /// <summary>按顺序返回所有子节点。</summary>
    public IEnumerable<VdfNode> GetChildren() => Children.Values;

    /// <summary>大小写不敏感地取第一个匹配的子节点。</summary>
    public VdfNode? FindChild(string key)
    {
        foreach (var kv in Children)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }
}

public static class HandleVdf
{
    /// <summary>
    /// 解析 VDF 文本。解析失败会抛出 <see cref="FormatException"/>，调用方需捕获。
    /// </summary>
    public static VdfNode Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        // 去掉 UTF-8 BOM
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        int i = 0;
        var root = new VdfNode { Name = "" };
        var current = root;

        while (true)
        {
            SkipWhitespaceAndComments(text, ref i);
            if (i >= text.Length) break;

            if (text[i] == '{')
            {
                // 容错：出现了没有父键名的块，忽略
                i++;
                continue;
            }

            if (text[i] == '}')
            {
                i++;
                current = current.Parent ?? root;
                continue;
            }

            // 读取键
            var key = ReadToken(text, ref i);
            if (key is null) break;

            SkipWhitespaceAndComments(text, ref i);
            if (i >= text.Length)
            {
                current.Values[key] = "";
                break;
            }

            if (text[i] == '{')
            {
                // 这是一个子节点
                i++;
                var child = new VdfNode { Name = key, Parent = current };
                current.Children[key] = child;
                current = child;
            }
            else
            {
                var value = ReadToken(text, ref i);
                if (value is null) break;
                current.Values[key] = value;
            }
        }

        return root;
    }

    public static VdfNode ParseFile(string path)
    {
        // Steam 的 VDF 一般是 UTF-8（无 BOM）。个别旧文件可能是 ANSI/UTF-16。
        var bytes = File.ReadAllBytes(path);

        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes);
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // 非 UTF-8，退回系统默认代码页
                text = Encoding.Default.GetString(bytes);
            }
        }

        return Parse(text);
    }

    /// <summary>安全解析，失败返回 null 并记录日志。</summary>
    public static VdfNode? TryParseFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ParseFile(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 VDF 失败: {path} -> {ex.Message}");
            return null;
        }
    }

    private static void SkipWhitespaceAndComments(string text, ref int i)
    {
        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                // // 行注释
                if (text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
            }

            break;
        }
    }

    private static string? ReadToken(string text, ref int i)
    {
        if (i >= text.Length) return null;

        if (text[i] == '"')
        {
            i++; // 跳过起始引号
            var sb = new StringBuilder();

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '\\' && i + 1 < text.Length)
                {
                    var n = text[i + 1];
                    i += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        default:
                            // 未识别的转义原样保留（例如 Windows 路径里的 "\S"）
                            sb.Append('\\').Append(n);
                            break;
                    }
                    continue;
                }

                if (c == '"')
                {
                    i++; // 跳过结束引号
                    return sb.ToString();
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        // 无引号 token（部分 VDF 允许裸值）
        var sb2 = new StringBuilder();
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}')
        {
            sb2.Append(text[i]);
            i++;
        }

        return sb2.Length == 0 ? null : sb2.ToString();
    }
}
