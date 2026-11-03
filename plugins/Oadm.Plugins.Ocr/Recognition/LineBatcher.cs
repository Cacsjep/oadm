using System.Threading.Channels;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// On a graphics card the lines of all pictures waiting at that moment go through the reader together (docs/spec/ocr.md
/// "GPU"): each call puts its lines in the queue; one loop takes everything waiting, reads it in large batches and hands
/// every call its own results. While the card reads, new lines gather for the next round.
/// </summary>
internal sealed class LineBatcher : IDisposable
{
    private readonly Func<IReadOnlyList<SKBitmap>, CancellationToken, Task<LineScores[]>> _recognize;
    private readonly Channel<Request> _queue = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <param name="recognize">Reads lines (the graphics card's set).</param>
    public LineBatcher(Func<IReadOnlyList<SKBitmap>, CancellationToken, Task<LineScores[]>> recognize)
    {
        _recognize = recognize;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The number of reader rounds so far (tests: several calls in one round).</summary>
    public int Rounds { get; private set; }

    /// <summary>The reader's output of <paramref name="lines"/>, read together with the lines of the other waiting calls.</summary>
    public async Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        var request = new Request(lines, new TaskCompletionSource<LineScores[]>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        ObjectDisposedException.ThrowIf(!_queue.Writer.TryWrite(request), this);

        // Never return before the loop is done with the bitmaps: the caller disposes them afterwards.
        return await request.Done.Task.ConfigureAwait(false);
    }

    private async Task LoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                var round = new List<Request>();
                while (reader.TryRead(out var request))
                {
                    if (request.Ct.IsCancellationRequested)
                    {
                        request.Done.TrySetCanceled(request.Ct);
                        continue;
                    }

                    round.Add(request);
                }

                if (round.Count == 0)
                {
                    continue;
                }

                try
                {
                    var all = round.SelectMany(r => r.Lines).ToList();
                    var scores = await _recognize(all, _stop.Token).ConfigureAwait(false);
                    Rounds++;
                    var offset = 0;
                    foreach (var request in round)
                    {
                        request.Done.TrySetResult(scores.AsSpan(offset, request.Lines.Count).ToArray());
                        offset += request.Lines.Count;
                    }
                }
#pragma warning disable CA1031 // The failure belongs to the waiting calls.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    foreach (var request in round)
                    {
                        request.Done.TrySetException(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }

        while (reader.TryRead(out var left))
        {
            left.Done.TrySetCanceled();
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Stops the loop; calls still waiting end as cancelled. Waits for a running round (at most <paramref name="timeout"/>,
    /// default 30 s) so the models it reads with can be disposed afterwards.
    /// </summary>
    /// <returns>True when the loop has ended.</returns>
    public bool Stop(TimeSpan? timeout = null)
    {
        _queue.Writer.TryComplete();
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop has ended already.
        }

        return _loop.Wait(timeout ?? TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc cref="Stop"/>
    public void Dispose() => Stop();

    private sealed record Request(IReadOnlyList<SKBitmap> Lines, TaskCompletionSource<LineScores[]> Done, CancellationToken Ct);
}
