using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.Http;

namespace BonsaiLLM.Server;

/// <summary>index.html 由 vite-plugin-singlefile 打成单文件后嵌入程序集，Server 不带静态目录。带 ETag，重复打开回 304。</summary>
internal static class WebAssets
{
    private const string ResourceName = "web.index.html";
    private const string ContentType = "text/html; charset=utf-8";

    /// <summary>页面字节，首次访问时读一次嵌入资源。</summary>
    private static readonly Lazy<byte[]?> Page = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>没构建前端时的兜底页。</summary>
    private static readonly byte[] Fallback = Encoding.UTF8.GetBytes(
        """
        <!doctype html>
        <meta charset="utf-8">
        <title>BonsaiLLM Server</title>
        <p>前端还没构建。先到 <code>src/BonsaiLLM.Server/web</code> 里执行
        <code>npm install</code> 和 <code>npm run build</code>，再重新编译本程序。</p>
        """);

    public static async Task ServeAsync(HttpContext context)
    {
        var body = Page.Value ?? Fallback;
        var etag = $"\"{body.Length:x}-{typeof(WebAssets).Assembly.ManifestModule.ModuleVersionId:N}\"";

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;

        if (IsNotModified(context, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;

        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private static byte[]? Load()
    {
        using var stream = typeof(WebAssets).Assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool IsNotModified(HttpContext context, string etag)
    {
        var header = context.Request.Headers.IfNoneMatch.ToString();

        if (header.Length == 0)
        {
            return false;
        }

        foreach (var item in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (item == "*" || string.Equals(item, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
