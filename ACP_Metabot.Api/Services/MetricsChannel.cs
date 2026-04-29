using System.Threading.Channels;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// Bounded in-process queue between RequestMetricsMiddleware (producer)
// and MetricsWriterService (single consumer). DropOldest means under
// burst we lose metrics, never block real requests.
//
// Capacity 4096: at 250ms drain interval and 1000 req/sec sustained
// burst we'd queue ~250 events between flushes — well under cap.
public sealed class MetricsChannel
{
    private readonly Channel<RequestMetricEvent> _channel;
    private long _droppedCount;

    public MetricsChannel()
    {
        _channel = Channel.CreateBounded<RequestMetricEvent>(
            new BoundedChannelOptions(4096)
            {
                FullMode       = BoundedChannelFullMode.DropOldest,
                SingleReader   = true,
                SingleWriter   = false,
                AllowSynchronousContinuations = false,
            },
            // DropOldest silently discards the oldest queued event when full;
            // TryWrite still returns true. This callback fires for the dropped
            // item, so we get an accurate count.
            itemDropped: _ => Interlocked.Increment(ref _droppedCount));
    }

    public ChannelReader<RequestMetricEvent> Reader => _channel.Reader;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    // Non-blocking. Returns true if the event made it into the channel
    // (DropOldest mode means this should always succeed for normal load).
    // Returns false only on writer-completed channel; callers ignore it.
    public bool TryWrite(RequestMetricEvent evt) => _channel.Writer.TryWrite(evt);
}
