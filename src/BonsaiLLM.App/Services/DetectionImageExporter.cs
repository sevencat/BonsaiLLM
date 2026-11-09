using BonsaiLLM.App.ViewModels;
using SkiaSharp;

namespace BonsaiLLM.App.Services;

/// <summary>把检测框烧进图片，导出 PNG 供保存/分享。</summary>
public static class DetectionImageExporter
{
    private static readonly SKColor StrokeColor = new(0xE5, 0x3E, 0x3E);

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    public static byte[] Render(byte[] sourceBytes, IReadOnlyList<DetectionBoxViewModel> boxes)
    {
        using var data = SKData.CreateCopy(sourceBytes);
        using var original = SKBitmap.Decode(data)
            ?? throw new InvalidOperationException("图片解码失败。");

        using var surface = new SKBitmap(original.Width, original.Height);
        using (var canvas = new SKCanvas(surface))
        {
            canvas.DrawBitmap(original, 0, 0, Sampling);

            var stroke = Math.Max(3f, Math.Min(original.Width, original.Height) / 260f);
            using var fill = new SKPaint { Color = StrokeColor.WithAlpha(36), IsAntialias = true };
            using var border = new SKPaint
            {
                Color = StrokeColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
            };
            foreach (var box in boxes)
            {
                var rect = new SKRect(
                    (float)(box.X1 * original.Width),
                    (float)(box.Y1 * original.Height),
                    (float)(box.X2 * original.Width),
                    (float)(box.Y2 * original.Height));

                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, border);
            }

            // 几十个同类框逐框挂标签只会糊住画面，改成左下角一行图例
            var label = boxes.Select(box => box.Label).FirstOrDefault(value => value.Length > 0) ?? "object";
            var legend = $"{label} x {boxes.Count}";
            using var legendBackground = new SKPaint { Color = StrokeColor.WithAlpha(220), IsAntialias = true };
            using var legendPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var legendSize = Math.Max(14f, Math.Min(original.Width, original.Height) / 26f);
            using var legendFont = new SKFont(
                SKTypeface.FromFamilyName("Microsoft YaHei"), legendSize);
            var legendWidth = legendFont.MeasureText(legend, legendPaint) + legendSize;
            var legendHeight = legendSize * 1.7f;
            var legendRect = new SKRect(
                stroke * 2,
                original.Height - legendHeight - stroke * 2,
                stroke * 2 + legendWidth,
                original.Height - stroke * 2);
            canvas.DrawRect(legendRect, legendBackground);
            canvas.DrawText(
                legend,
                legendRect.Left + legendSize * 0.5f,
                legendRect.Top + legendSize * 1.2f,
                SKTextAlign.Left,
                legendFont,
                legendPaint);
        }

        using var image = SKImage.FromBitmap(surface);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("导出标注图失败。");

        return encoded.ToArray();
    }
}
