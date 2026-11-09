namespace BonsaiLLM.Core.Chat;

/// <summary>tokenizer 不可用时的粗估：中英混合按 1.6 字符 ≈ 1 token。</summary>
public static class TokenEstimator
{
    public static int Estimate(string text) =>
        text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / 1.6);
}
