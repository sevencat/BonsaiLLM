using System.Text.RegularExpressions;

namespace BonsaiLLM.Core.Detection;

/// <summary>
/// 解析 WeVisOne-VL 的检测输出。坐标是 0~1000 的整数 token，
/// 形如 <c>&lt;ref&gt;person&lt;/ref&gt;&lt;box&gt;&lt;98&gt;&lt;49&gt;&lt;121&gt;&lt;169&gt;,...&lt;/box&gt;</c>。
/// 纯逻辑，不依赖 UI 与 llama，便于单测。
/// </summary>
public static class WeVisOneParser
{
    private static readonly Regex CoordTokenRegex = new(@"<(\d{1,4})>", RegexOptions.Compiled);

    private static readonly Regex PlainQuadRegex = new(
        @"\(?\s*(\d{1,4})\s*,\s*(\d{1,4})\s*,\s*(\d{1,4})\s*,\s*(\d{1,4})\s*\)?",
        RegexOptions.Compiled);

    private static readonly Regex RefTagRegex = new(@"<ref>(.*?)</ref>", RegexOptions.Compiled | RegexOptions.Singleline);

    private const string EndOfTurn = "<|im_end|>";

    private const string EndOfText = "<|endoftext|>";

    private const string ThinkingEnd = "<｜end▁of▁thinking｜>";

    /// <summary>清洗模型输出：去掉停止符与思考段。</summary>
    public static string Clean(string raw)
    {
        var text = raw ?? string.Empty;

        foreach (var stop in new[] { EndOfTurn, EndOfText })
        {
            var index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0)
            {
                text = text[..index];
            }
        }

        // Qwen3 系列可能输出思考过程，直接丢弃
        var thinkEnd = text.LastIndexOf(ThinkingEnd, StringComparison.Ordinal);
        if (thinkEnd >= 0)
        {
            text = text[(thinkEnd + ThinkingEnd.Length)..];
        }

        return text.Trim();
    }

    /// <summary>无目标时模型输出 None（或空）。</summary>
    public static bool IsNone(string cleaned) =>
        cleaned.Length == 0
        || cleaned.Equals("None", StringComparison.OrdinalIgnoreCase)
        || cleaned.Equals("None.", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析原始输出，得到归一化框、标签与清洗后的原文。</summary>
    public static DetectionResult Parse(string raw)
    {
        var cleaned = Clean(raw);
        var none = IsNone(cleaned);
        var label = ParseLabel(cleaned);

        if (none)
        {
            return new DetectionResult([], label, cleaned, IsNone: true);
        }

        // 每 4 个数字组成一个框；坐标按 0~1000 归一化反算
        var values = CoordTokenRegex.Matches(cleaned)
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToList();

        if (values.Count < 4)
        {
            // 兜底：部分量化/服务端会把坐标 token 渲染成普通数字，例如 100,200,300,400
            values.Clear();
            foreach (Match match in PlainQuadRegex.Matches(cleaned))
            {
                for (var group = 1; group <= 4; group++)
                {
                    values.Add(int.Parse(match.Groups[group].Value));
                }
            }
        }

        var boxes = new List<DetectionBox>();
        for (var i = 0; i + 3 < values.Count; i += 4)
        {
            var x1 = values[i];
            var y1 = values[i + 1];
            var x2 = values[i + 2];
            var y2 = values[i + 3];

            if (x1 is < 0 or > 1000 || y1 is < 0 or > 1000 || x2 is < 0 or > 1000 || y2 is < 0 or > 1000)
            {
                continue;
            }

            var box = new DetectionBox(x1 / 1000f, y1 / 1000f, x2 / 1000f, y2 / 1000f).Clamp();
            if (box.Width <= 0.002f || box.Height <= 0.002f)
            {
                continue;
            }

            boxes.Add(box);
        }

        return new DetectionResult(Deduplicate(boxes), label, cleaned, IsNone: false);
    }

    /// <summary>
    /// 取 &lt;ref&gt;…&lt;/ref&gt; 里的标签。
    /// llama.cpp 的 mtmd 执行器会吞掉生成序列的第一个 token，常见后果是只剩 &lt;/ref&gt;；
    /// 此时把 &lt;/ref&gt; 之前的内容整段当作标签反推回来（内容本身没有丢）。
    /// </summary>
    public static string? ParseLabel(string raw)
    {
        var text = Clean(raw);
        if (IsNone(text))
        {
            return null;
        }

        var tagged = RefTagRegex.Match(text);
        if (tagged.Success)
        {
            var value = NormalizeLabel(tagged.Groups[1].Value);
            if (value.Length > 0)
            {
                return value;
            }
        }

        var close = text.IndexOf("</ref>", StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        var open = text.LastIndexOf("<ref>", close, StringComparison.Ordinal);
        var body = open >= 0 ? text[(open + 5)..close] : text[..close];
        var label = NormalizeLabel(body);

        // 只剩 </ref> 时，标签应从 </ref> 前的一小段文字反推；
        // 若那段里混进了坐标 token 或整句描述，宁可不要标签也不能显示成垃圾
        if (label.Length == 0 || label.Length > 40 || label.Contains('<') || label.Contains('>'))
        {
            return null;
        }

        return label;
    }

    private static string NormalizeLabel(string body)
    {
        var text = body.Trim().Trim('"', '\'', '`', '*', ' ', '：', ':');

        foreach (var junk in new[] { "<|im_start|>assistant", "<|im_start|>user", "assistant" })
        {
            if (text.StartsWith(junk, StringComparison.OrdinalIgnoreCase))
            {
                text = text[junk.Length..].Trim().Trim(':').Trim();
            }
        }

        return text;
    }

    private static List<DetectionBox> Deduplicate(List<DetectionBox> boxes)
    {
        var kept = new List<DetectionBox>(boxes.Count);
        foreach (var box in boxes)
        {
            if (!kept.Any(existing => Iou(existing, box) > 0.92f))
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    private static float Iou(DetectionBox a, DetectionBox b)
    {
        var ix1 = Math.Max(a.X1, b.X1);
        var iy1 = Math.Max(a.Y1, b.Y1);
        var ix2 = Math.Min(a.X2, b.X2);
        var iy2 = Math.Min(a.Y2, b.Y2);

        var intersection = Math.Max(0, ix2 - ix1) * Math.Max(0, iy2 - iy1);
        var union = a.Area + b.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
