using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private readonly long _expectedSegmentSize;
    private readonly Channel<Task<Stream>> _streamTasks;
    private readonly ContextualCancellationTokenSource _cts;
    private Stream? _stream;
    private bool _disposed;

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        CancellationToken cancellationToken
    )
    {
        return articleBufferSize == 0
            ? new UnbufferedMultiSegmentStream(segmentIds, usenetClient, expectedSegmentSize)
            : new MultiSegmentStream(segmentIds, usenetClient, articleBufferSize, expectedSegmentSize, cancellationToken);
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        CancellationToken cancellationToken
    )
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
        _expectedSegmentSize = expectedSegmentSize;
        _streamTasks = Channel.CreateBounded<Task<Stream>>(articleBufferSize);
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = DownloadSegments(_cts.Token);
    }

    private async Task DownloadSegments(CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < _segmentIds.Length; i++)
            {
                var segmentId = _segmentIds.Span[i];

                await _streamTasks.Writer.WaitToWriteAsync(cancellationToken);
                var connection = await _usenetClient.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);
                var streamTask = DownloadSegment(segmentId, connection, cancellationToken);
                if (_streamTasks.Writer.TryWrite(streamTask)) continue;

                // if we never get a chance to write the stream to the writer
                // then make sure the stream gets disposed.
                _ = Task.Run(async () => await (await streamTask).DisposeAsync(), CancellationToken.None);
                break;
            }
        }
        finally
        {
            _streamTasks.Writer.TryComplete();
        }

        return;
    }

    private async Task<Stream> DownloadSegment
    (
        string segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var bodyResponse = await _usenetClient
                .DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken)
                .ConfigureAwait(false);
            return bodyResponse.Stream;
        }
        catch (UsenetArticleNotFoundException e)
        {
            // All providers report this segment missing. Substitute zeros so the
            // HTTP response stays byte-aligned with Content-Length and the player
            // can resync to the next keyframe instead of seeing a truncated body
            // (which closes VLC/MPV/Plex). The outer LimitLengthStream clips any
            // overshoot, so erring on "estimated full segment" is safe.
            var fill = _expectedSegmentSize > 0 ? _expectedSegmentSize : 1;
            Log.Warning(
                "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
                e.SegmentId, fill);
            return new MemoryStream(new byte[fill], writable: false);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (!cancellationToken.IsCancellationRequested)
        {
            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_streamTasks.Reader.TryRead(out var streamTask)) return 0;
                _stream = await streamTask;
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync();
            _stream = null;
        }

        return 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _stream?.Dispose();
        _streamTasks.Writer.TryComplete();

        // ensure that streams that were never read from the channel get disposed
        while (_streamTasks.Reader.TryRead(out var streamTask))
            _ = Task.Run(async () => await (await streamTask).DisposeAsync(), CancellationToken.None);

        base.Dispose();
    }
}