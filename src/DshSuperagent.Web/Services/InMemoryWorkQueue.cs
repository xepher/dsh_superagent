using System.Threading.Channels;
using DshSuperagent.Core.Storage;

namespace DshSuperagent.Web.Services;

public sealed class InMemoryWorkQueue : IWorkQueue
{
    private readonly Channel<WorkItem> _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
    });
    private readonly HashSet<WorkItem> _pending = [];
    private readonly object _gate = new();

    public ValueTask EnqueueAsync(WorkItem item, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_pending.Add(item))
            {
                return ValueTask.CompletedTask;
            }
        }

        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    public async ValueTask<WorkItem> DequeueAsync(CancellationToken cancellationToken)
    {
        var item = await _channel.Reader.ReadAsync(cancellationToken);
        lock (_gate)
        {
            _pending.Remove(item);
        }

        return item;
    }
}
