using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using BonsaiLLM.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NLog.Extensions.Logging;

var builder = WebApplication.CreateSlimBuilder(args);

var options = builder.Configuration.GetSection("BonsaiLLM").Get<ServerOptions>() ?? new ServerOptions();

var urlsOverride = Environment.GetEnvironmentVariable("BONSAI_URLS");
if (!string.IsNullOrWhiteSpace(urlsOverride))
{
    options.Urls = urlsOverride;
}

builder.WebHost.UseUrls(options.Urls);

// NLog：控制台 + 按天滚动的文件 + 内存副本（/api/logs）
var memoryLogs = LoggingSetup.Configure(options, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(memoryLogs);
builder.Services.AddSingleton<ChatHost>();

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new NLogLoggerProvider());

// 级别只由 NLog 规则决定，别让框架默认的 Information 先筛一遍
builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);

// Native AOT 不开反射：JSON 全部交给源生成的 AppJson
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default);
});

var app = builder.Build();

// 内嵌页面：直接发明文，ETag 命中回 304
app.MapGet("/", (RequestDelegate)WebAssets.ServeAsync);

// ---- OpenAI 兼容接口 ----
app.MapGet("/v1/models", (ChatHost host) => Json(ListModels(host), AppJson.Default.OpenAiModelList));
app.MapGet("/v1/models/{id}", (string id, ChatHost host) => GetModel(host, id));
app.MapPost(
    "/v1/chat/completions",
    (HttpContext context, OpenAiChatRequest request, ChatHost host) => ChatCompletionsAsync(context, request, host));

// ---- 界面辅助端点（不属于 OpenAI 协议）----
app.MapGet("/api/state", (ChatHost host) => Json(host.Snapshot(), AppJson.Default.StateDto));
app.MapPost("/api/load", (LoadRequest request, ChatHost host) => LoadModel(host, request));
app.MapPost("/api/stop", (ChatHost host) => Json(new StopDto(host.Stop()), AppJson.Default.StopDto));
app.MapGet(
    "/api/logs",
    (MemoryLogTarget target) => Json(target.Snapshot().ToList(), AppJson.Default.ListLogLineDto));

var chatHost = app.Services.GetRequiredService<ChatHost>();

app.Logger.LogInformation("监听 {Urls}，OpenAI 接口挂在 /v1/chat/completions", options.Urls);

chatHost.StartBackground();
app.Run();

static IResult Json<T>(T payload, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK) =>
    new JsonResponse<T>(payload, typeInfo, statusCode);

static OpenAiModelList ListModels(ChatHost host)
{
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var data = host.Snapshot().Models
        .Select(model => new OpenAiModel(model.Id, created, "bonsai"))
        .ToList();

    return new OpenAiModelList(data);
}

static IResult GetModel(ChatHost host, string id)
{
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var model = host.Snapshot().Models.FirstOrDefault(item =>
        string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    return model is null
        ? Json(
            new OpenAiErrorResponse(new OpenAiError($"模型不存在：{id}", "invalid_request_error", "model_not_found")),
            AppJson.Default.OpenAiErrorResponse,
            StatusCodes.Status404NotFound)
        : Json(new OpenAiModel(model.Id, created, "bonsai"), AppJson.Default.OpenAiModel);
}

static IResult LoadModel(ChatHost host, LoadRequest request) =>
    host.StartLoad(request.Model)
        ? Json(host.Snapshot(), AppJson.Default.StateDto)
        : Json(
            new ErrorDto("模型不存在、文件缺失或正在加载"),
            AppJson.Default.ErrorDto,
            StatusCodes.Status409Conflict);

static async Task<IResult> ChatCompletionsAsync(HttpContext context, OpenAiChatRequest request, ChatHost host)
{
    var modelId = host.ResolveModelId(request.Model);
    var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (request.Stream == true)
    {
        return await StreamCompletionAsync(context, request, host, completionId, created, modelId);
    }

    var text = new StringBuilder();
    OpenAiUsage? usage = null;
    string? error = null;

    await foreach (var evt in host.CompleteAsync(request, context.RequestAborted))
    {
        switch (evt.Type)
        {
            case "delta":
                text.Append(evt.Text);
                break;

            case "done":
                usage = evt.Usage is null
                    ? null
                    : new OpenAiUsage(evt.Usage.PromptTokens, evt.Usage.CompletionTokens);
                break;

            case "error":
                error = evt.Message;
                break;
        }
    }

    if (error is not null)
    {
        return Json(
            new OpenAiErrorResponse(new OpenAiError(error, "server_error")),
            AppJson.Default.OpenAiErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }

    return Json(
        new OpenAiChatResponse(
            completionId,
            created,
            modelId,
            [new OpenAiChoice(0, new OpenAiResponseMessage("assistant", text.ToString()), "stop")],
            usage ?? new OpenAiUsage(0, 0)),
        AppJson.Default.OpenAiChatResponse);
}

static async Task<IResult> StreamCompletionAsync(
    HttpContext context,
    OpenAiChatRequest request,
    ChatHost host,
    string completionId,
    long created,
    string modelId)
{
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    var cancellationToken = context.RequestAborted;
    var sentRole = false;

    try
    {
        await foreach (var evt in host.CompleteAsync(request, cancellationToken))
        {
            switch (evt.Type)
            {
                case "delta":
                    await WriteChunkAsync(
                        context,
                        completionId,
                        created,
                        modelId,
                        new OpenAiDelta(evt.Text, sentRole ? null : "assistant"),
                        finishReason: null,
                        cancellationToken);
                    sentRole = true;
                    break;

                case "done":
                case "cancelled":
                    await WriteChunkAsync(
                        context,
                        completionId,
                        created,
                        modelId,
                        new OpenAiDelta(null),
                        finishReason: "stop",
                        cancellationToken);
                    await WriteRawAsync(context, "[DONE]", cancellationToken);
                    break;

                case "error":
                    await WriteJsonAsync(
                        context,
                        new OpenAiErrorResponse(new OpenAiError(evt.Message ?? "生成失败", "server_error")),
                        AppJson.Default.OpenAiErrorResponse,
                        cancellationToken);
                    await WriteRawAsync(context, "[DONE]", cancellationToken);
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // 客户端断开就结束，不用再回包
    }

    return Results.Empty;
}

static async Task WriteChunkAsync(
    HttpContext context,
    string completionId,
    long created,
    string modelId,
    OpenAiDelta delta,
    string? finishReason,
    CancellationToken cancellationToken)
{
    var chunk = new OpenAiChunk(
        completionId,
        created,
        modelId,
        [new OpenAiChunkChoice(0, delta, finishReason)]);

    await WriteJsonAsync(context, chunk, AppJson.Default.OpenAiChunk, cancellationToken);
}

static async Task WriteJsonAsync<T>(
    HttpContext context,
    T payload,
    JsonTypeInfo<T> typeInfo,
    CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(payload, typeInfo);
    await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

static async Task WriteRawAsync(HttpContext context, string raw, CancellationToken cancellationToken)
{
    await context.Response.WriteAsync($"data: {raw}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

/// <summary>显式走源生成元数据的 JSON 响应，避免 Minimal API 的反射回退。</summary>
internal sealed class JsonResponse<T>(T value, JsonTypeInfo<T> typeInfo, int statusCode) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(httpContext.Response.Body, value, typeInfo, httpContext.RequestAborted);
    }
}
