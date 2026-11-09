namespace BonsaiLLM.Core.Detection;

/// <summary>归一化检测框（0~1，相对图片宽高）。</summary>
public readonly record struct DetectionBox(float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;

    public float Height => Y2 - Y1;

    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>把四个角夹到 0~1，并保证左上角小于右下角。</summary>
    public DetectionBox Clamp()
    {
        static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

        var x1 = Clamp01(X1);
        var y1 = Clamp01(Y1);
        var x2 = Clamp01(X2);
        var y2 = Clamp01(Y2);

        return new DetectionBox(
            Math.Min(x1, x2),
            Math.Min(y1, y2),
            Math.Max(x1, x2),
            Math.Max(y1, y2));
    }

    public override string ToString() => $"({X1:0.000},{Y1:0.000})-({X2:0.000},{Y2:0.000})";
}
