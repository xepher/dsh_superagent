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
            return Results.Text(result.Body, result.ContentType, Encoding.UTF8, result.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "处理 Source {SourceId} 回调失败", sourceId);
            return Results.StatusCode(500);
        }
    }
}
