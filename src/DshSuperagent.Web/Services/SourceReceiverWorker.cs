using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DshSuperagent.Web.Services;

/// <summary>Starts one receiver per enabled Source and stops the old receiver before applying new configuration.</summary>
public sealed class SourceReceiverWorker(
    IServiceScopeFactory scopes,
    SourceProviderRegistry providers,
    ILogger<SourceReceiverWorker> logger) : BackgroundService
{
    private sealed record Receiver(string Fingerprint, CancellationTokenSource Cancellation, Task Task);
    private readonly Dictionary<Guid, Receiver> _receivers = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "同步后台 Source 接收任务失败");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var receiver in _receivers.Values) await StopAsync(receiver);
            _receivers.Clear();
        }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sources = await db.Sources.AsNoTracking().Where(source => source.Enabled).ToListAsync(token);
        var desired = sources.Where(source => providers.Find(source.ProviderId) is IBackgroundSourceProvider)
            .ToDictionary(source => source.Id);
        foreach (var id in _receivers.Keys.ToList())
        {
            var receiver = _receivers[id];
            if (desired.TryGetValue(id, out var source) && receiver.Fingerprint == Fingerprint(source)
                && !receiver.Task.IsCompleted) continue;
            await StopAsync(receiver);
            _receivers.Remove(id);
        }
        foreach (var source in desired.Values.Where(source => !_receivers.ContainsKey(source.Id)))
        {
            var provider = (IBackgroundSourceProvider)providers.GetRequired(source.ProviderId);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var runtime = new SourceRuntime(scopes, source, provider);
            var task = Task.Run(async () =>
            {
                try
                {
                    await runtime.ReportAsync("receiver", "starting", "后台接收任务正在启动。", cancellation.Token);
                    await provider.RunAsync(source, runtime, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning("Source {SourceId} 接收任务失败：{Error}", source.Id, ex.Message);
                    try { await runtime.ReportAsync("receiver", "error", ex.Message, cancellation.Token); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception) { logger.LogWarning("Source {SourceId} 无法保存接收状态，将重新启动接收任务。", source.Id); }
                }
            }, CancellationToken.None);
            _receivers.Add(source.Id, new Receiver(Fingerprint(source), cancellation, task));
        }
    }

    private static string Fingerprint(DshSuperagent.Domain.Sources.Source source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            source.ProviderId, source.Name, source.DefaultRepositoryId,
            configuration = source.Configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        }))));

    private static async Task StopAsync(Receiver receiver)
    {
        await receiver.Cancellation.CancelAsync();
        try { await receiver.Task; }
        catch (OperationCanceledException) { }
        finally { receiver.Cancellation.Dispose(); }
    }
}
