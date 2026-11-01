using System.Buffers;
using System.IO.Pipelines;
using StreamJsonRpc;

namespace Workbench.Tools;

/// <summary>Bounds unconsumed input buffers; Microsoft StreamJsonRpc still owns every framing/parsing decision.</summary>
public static class BoundedRpcPipes
{
    public const int MaximumBufferedBytes = 8 * 1024 * 1024;

    public static HeaderDelimitedMessageHandler CreateHandler(Stream sending, Stream receiving, IJsonRpcMessageFormatter formatter,
        int maximumBufferedBytes = MaximumBufferedBytes) => new(
            PipeWriter.Create(sending), CreateReader(receiving, maximumBufferedBytes), formatter);

    public static PipeReader CreateReader(Stream receiving, int maximumBufferedBytes = MaximumBufferedBytes) =>
        LimitReader(PipeReader.Create(receiving), maximumBufferedBytes);

    public static PipeReader LimitReader(PipeReader reader, int maximumBufferedBytes = MaximumBufferedBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumBufferedBytes is < 1 or > MaximumBufferedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBufferedBytes));
        return new BoundedReader(reader, maximumBufferedBytes);
    }

    private sealed class BoundedReader(PipeReader inner, int maximumBufferedBytes) : PipeReader
    {
        private InvalidDataException? _failure;
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override bool TryRead(out ReadResult result)
        {
            ThrowIfFailed();
            if (!inner.TryRead(out result)) return false;
            Check(result);
            return true;
        }
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfFailed();
            var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            Check(result);
            return result;
        }
        private void ThrowIfFailed() { if (_failure is not null) throw _failure; }
        private void Check(ReadResult result)
        {
            if (result.Buffer.Length <= maximumBufferedBytes) return;
            _failure = new InvalidDataException("JSON-RPC input exceeds the unconsumed-buffer budget. The connection cannot be reused.");
            // Releasing the BCL reader also closes its owned input stream. Never drain,
            // recover, replay, or keep allocating in search of a framing boundary.
            inner.Complete(_failure);
            throw _failure;
        }
    }
}
