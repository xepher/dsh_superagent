using System.Diagnostics;
using System.Text;
using DshSuperagent.Core.Application;
using DshSuperagent.Core.Providers.Sources;

namespace DshSuperagent.Web.Endpoints;

public static class SourceCallbackEndpoints
{
    public static IEndpointRouteBuilder MapSourceCallbacks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(
            "/api/sources/{sourceId:guid}/callback",
            ["GET", "POST"],
            HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        Guid sourceId,
        HttpContext http,
        InboundIngestionService ingestion,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SourceCallback");
        var elapsed = Stopwatch.StartNew();
        var requestId = http.TraceIdentifier;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["SourceId"] = sourceId,
            ["CallbackRequestId"] = requestId,
        });
        logger.LogInformation(
            "Source {SourceId} 回调开始：Request {RequestId} Method {Method} 到达时间 {ReceivedAt:O} 路径 {Path}",
            sourceId, requestId, http.Request.Method, DateTimeOffset.UtcNow, http.Request.Path.Value);
        http.Response.OnCompleted(() =>
        {
            logger.LogInformation(
                "Source {SourceId} 回调 HTTP 写出完成：Request {RequestId} Status {StatusCode}，总耗时 {ElapsedMs} ms；不代表企微已接受消息",
                sourceId, requestId, http.Response.StatusCode, elapsed.ElapsedMilliseconds);
            return Task.CompletedTask;
        });
        if (http.Request.ContentLength > 1024 * 1024)
        {
            return Results.StatusCode(413);
        }

        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        var request = new ProviderCallbackRequest(
            http.Request.Method,
            http.Request.Query.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
            http.Request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
            body);

        try
        {
            var result = await ingestion.HandleCallbackAsync(sourceId, request, cancellationToken);
            logger.LogInformation(
                "Source {SourceId} 回调响应已准备：Request {RequestId} Status {StatusCode}，响应 {ResponseBytes} 字节，耗时 {ElapsedMs} ms",
                sourceId, requestId, result.StatusCode, Encoding.UTF8.GetByteCount(result.Body), elapsed.ElapsedMilliseconds);
            return Results.Text(result.Body, result.ContentType, Encoding.UTF8, result.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Source {SourceId} 回调已取消：Request {RequestId}，耗时 {ElapsedMs} ms",
                sourceId, requestId, elapsed.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "处理 Source {SourceId} 回调失败", sourceId);
            return Results.StatusCode(500);
        }
    }
}
