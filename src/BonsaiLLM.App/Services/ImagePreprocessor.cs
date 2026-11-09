using SkiaSharp;

namespace BonsaiLLM.App.Services;

/// <summary>预处理后的图片：统一成 JPEG 字节，便于存盘、发送与显示。</summary>
public sealed record PreparedImage(byte[] JpegBytes, int Width, int Height);

/// <summary>
/// 发图前的固定四步：按 EXIF 纠偏 → 最长边缩到 1600 → JPEG 92。
/// 归一化坐标与缩放无关，但方向必须先纠，否则模型看到的是横躺的图。
/// </summary>
public static class ImagePreprocessor
{
    public const int MaxEdge = 1600;

    public const int JpegQuality = 92;

    public static PreparedImage Prepare(byte[] input)
    {
        using var bitmap = DecodeOriented(input);
        using var resized = Resize(bitmap, MaxEdge);
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new InvalidOperationException("图片编码失败，可能是不支持的格式。");

        return new PreparedImage(data.ToArray(), resized.Width, resized.Height);
    }

    /// <summary>解码并按 EXIF EncodedOrigin 纠正方向。</summary>
    public static SKBitmap DecodeOriented(byte[] input)
    {
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidOperationException("无法识别图片格式。");

        using var decoded = SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException("图片解码失败。");

        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return decoded.Copy();
        }

        return ApplyOrientation(decoded, origin);
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var swapped = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

        var width = swapped ? source.Height : source.Width;
        var height = swapped ? source.Width : source.Height;

        // 目标像素 = 源像素的线性变换：x' = ScaleX*x + SkewX*y + TransX
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix
            {
                ScaleX = -1, TransX = source.Width, ScaleY = 1, Persp2 = 1,
            },
            SKEncodedOrigin.BottomRight => new SKMatrix
            {
                ScaleX = -1, TransX = source.Width, ScaleY = -1, TransY = source.Height, Persp2 = 1,
            },
            SKEncodedOrigin.BottomLeft => new SKMatrix
            {
                ScaleX = 1, ScaleY = -1, TransY = source.Height, Persp2 = 1,
            },
            SKEncodedOrigin.LeftTop => new SKMatrix
            {
                ScaleX = 0, SkewX = 1, ScaleY = 1, SkewY = 0, Persp2 = 1,
            },
            SKEncodedOrigin.RightTop => new SKMatrix
            {
                ScaleX = 0, SkewX = -1, TransX = source.Height, ScaleY = 1, SkewY = 0, Persp2 = 1,
            },
            SKEncodedOrigin.RightBottom => new SKMatrix
            {
                ScaleX = 0, SkewX = -1, TransX = source.Height,
                SkewY = -1, ScaleY = 0, TransY = source.Width, Persp2 = 1,
            },
            SKEncodedOrigin.LeftBottom => new SKMatrix
            {
                ScaleX = 0, SkewX = 1, ScaleY = 0, SkewY = -1, TransY = source.Width, Persp2 = 1,
            },
            _ => SKMatrix.CreateIdentity(),
        };

        var target = new SKBitmap(width, height);
        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        return target;
    }

    private static SKBitmap Resize(SKBitmap source, int maxEdge)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxEdge)
        {
            return source.Copy();
        }

        var scale = (float)maxEdge / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType);

        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("图片缩放失败。");
    }
}
