using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderUsageTracker usageTracker,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null
) : NntpClient
{
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();

    /// <summary>
    /// Tag the current async flow with a read-session id so SegmentFetch rows
    /// emitted while fulfilling this read can be correlated back to the session.
    /// Disposing the returned scope restores the previous value.
    /// </summary>
    public static IDisposable BeginReadSessionScope(Guid readSessionId)
    {
        var previous = ReadSessionScope.Value;
        ReadSessionScope.Value = readSessionId;
        return new ScopeReleaser(() => ReadSessionScope.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    // Per-call attribution. Caller (e.g. PlaybackFastVerifier) sets a mutable
    // holder on AttributionContext BEFORE invoking; we read it inside the call and
    // mutate Host on a non-"missing" response. AsyncLocal reliably flows the holder
    // reference DOWN to us; mutating its property is then visible to the caller via
    // their reference (which sidesteps AsyncLocal's child→parent non-propagation).
    public sealed class ResponderAttribution { public string? Host; }
    public static readonly AsyncLocal<ResponderAttribution?> AttributionContext = new();

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup((x, ct) => x.StatAsync(segmentId, ct), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup((x, ct) => x.HeadAsync(segmentId, ct), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup((x, ct) => x.DecodedBodyAsync(segmentId, ct), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup((x, ct) => x.DecodedArticleAsync(segmentId, ct), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup((x, ct) => x.DateAsync(ct), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                (x, ct) => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, ct),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                (x, ct) => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, ct),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    // How long we wait for the in-flight provider to start producing a response
    // before firing the next provider in parallel. Healthy providers reply in
    // well under a second so the hedge rarely fires; when the first provider
    // is stalled, the next attempt is already underway by the time
    // FirstResponseDeadline (inside MultiConnectionNntpClient) trips the first.
    // Only applied to BODY/ARTICLE — STAT/HEAD/DATE are too cheap to hedge.
    private static readonly TimeSpan HedgeDelay = TimeSpan.FromSeconds(1);

    private Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, CancellationToken, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var isHedgeable = typeof(T) == typeof(UsenetDecodedBodyResponse)
                          || typeof(T) == typeof(UsenetDecodedArticleResponse);
        return isHedgeable
            ? RunHedged(task, cancellationToken)
            : RunSerial(task, cancellationToken);
    }

    private async Task<T> RunSerial<T>
    (
        Func<INntpClient, CancellationToken, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = GetOrderedProviders();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Information("NNTP fallback: previous provider failed with `{Msg}`. Trying {Host}.",
                    msg, provider.Host);
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await task.Invoke(provider, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                    continue;
                }

                if (attribution != null && result.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                    attribution.Host = provider.Host;

                RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                RecordFetch(provider.Host, ClassifyException(e), stopwatch.ElapsedMilliseconds, i);
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    // Staggered hedge for BODY/ARTICLE. Fires provider 0 immediately; if it
    // hasn't completed within HedgeDelay, fires provider 1 in parallel; etc.
    // First task to return a usable response wins — losers are cancelled to
    // free their connections. A NoArticleWithThatMessageId is treated as
    // "missing" and we keep waiting on the rest. If every provider fails or
    // reports missing, the last exception (or last NoArticle response) is
    // returned, matching the serial path's behaviour.
    private async Task<T> RunHedged<T>
    (
        Func<INntpClient, CancellationToken, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        var orderedProviders = GetOrderedProviders();
        if (orderedProviders.Count == 0)
            throw new Exception("There are no usenet providers configured.");

        var inFlight = new List<HedgeAttempt<T>>();
        ExceptionDispatchInfo? lastException = null;
        T? lastMissingResult = default;
        var lastMissingIdx = -1;
        var spawned = 0;

        void Spawn()
        {
            if (spawned >= orderedProviders.Count) return;
            var idx = spawned++;
            var provider = orderedProviders[idx];
            if (idx > 0)
            {
                Log.Information("NNTP hedge: first provider still pending after {Delay}ms, racing {Host}.",
                    (int)HedgeDelay.TotalMilliseconds, provider.Host);
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sw = Stopwatch.StartNew();
            var t = task.Invoke(provider, cts.Token);
            inFlight.Add(new HedgeAttempt<T>(t, provider, idx, cts, sw));
        }

        try
        {
            Spawn();

            while (inFlight.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task settled;
                CancellationTokenSource? hedgeTimerCts = null;
                Task? hedgeTimer = null;
                try
                {
                    var anyCompleted = Task.WhenAny(inFlight.Select(a => a.Task));
                    if (spawned < orderedProviders.Count)
                    {
                        hedgeTimerCts = new CancellationTokenSource();
                        hedgeTimer = Task.Delay(HedgeDelay, hedgeTimerCts.Token);
                        settled = await Task.WhenAny(anyCompleted, hedgeTimer).ConfigureAwait(false);
                    }
                    else
                    {
                        settled = await anyCompleted.ConfigureAwait(false);
                    }
                }
                finally
                {
                    hedgeTimerCts?.Cancel();
                    hedgeTimerCts?.Dispose();
                }

                if (settled == hedgeTimer)
                {
                    // Hedge delay elapsed before any in-flight settled — fire next provider.
                    Spawn();
                    continue;
                }

                // An in-flight task completed.
                var completedTask = (Task<T>)settled;
                var attempt = inFlight.First(a => a.Task == completedTask);
                inFlight.Remove(attempt);
                attempt.Sw.Stop();

                if (completedTask.IsCompletedSuccessfully)
                {
                    var result = completedTask.Result;
                    var hasMoreCandidates = inFlight.Count > 0 || spawned < orderedProviders.Count;

                    if (hasMoreCandidates && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                    {
                        // Missing from this provider — keep waiting on the others. Spawn
                        // an additional provider immediately (no need to wait the hedge
                        // delay; we already know one provider didn't have the article).
                        RecordFetch(attempt.Provider.Host, SegmentFetch.FetchStatus.Missing,
                            attempt.Sw.ElapsedMilliseconds, attempt.Idx);
                        attempt.Cts.Dispose();
                        lastMissingResult = result;
                        lastMissingIdx = attempt.Idx;
                        if (spawned < orderedProviders.Count) Spawn();
                        continue;
                    }

                    if (attribution != null && result.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                        attribution.Host = attempt.Provider.Host;

                    if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse
                        && result.ResponseType is UsenetResponseType.ArticleRetrievedBodyFollows
                                              or UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                    {
                        usageTracker.RecordSuccess(attempt.Provider.Host);
                        RecordFetch(attempt.Provider.Host, SegmentFetch.FetchStatus.Ok,
                            attempt.Sw.ElapsedMilliseconds, attempt.Idx);
                        result = WrapStreamForByteCounting(result, attempt.Provider.Host);
                    }
                    else
                    {
                        RecordFetch(attempt.Provider.Host, SegmentFetch.FetchStatus.Missing,
                            attempt.Sw.ElapsedMilliseconds, attempt.Idx);
                    }

                    attempt.Cts.Dispose();
                    CancelAndObserveLosers(inFlight);
                    return result;
                }

                // Faulted — capture exception and try next provider.
                try
                {
                    await completedTask.ConfigureAwait(false);
                }
                catch (Exception e) when (!e.IsCancellationException())
                {
                    RecordFetch(attempt.Provider.Host, ClassifyException(e),
                        attempt.Sw.ElapsedMilliseconds, attempt.Idx);
                    lastException = ExceptionDispatchInfo.Capture(e);
                    Log.Information("NNTP hedge: {Host} failed with `{Msg}`.",
                        attempt.Provider.Host, e.Message);
                }
                catch (Exception)
                {
                    // Outer-CT cancellation observed via this attempt's linked CT — just
                    // record the elapsed time, no fetch entry needed.
                }

                attempt.Cts.Dispose();

                // Spawn next provider immediately (one provider failed — no point waiting
                // for the hedge delay before trying another).
                if (spawned < orderedProviders.Count) Spawn();
            }

            // Every in-flight completed without a winner. If at least one returned
            // NoArticle, surface that response (matches serial path's "return missing
            // from last provider" behaviour). Each NoArticle was already recorded by
            // RecordFetch in the missing branch, so no extra record here. Otherwise,
            // rethrow the last exception observed.
            if (lastMissingResult is not null)
            {
                _ = lastMissingIdx; // silence unused warning; idx already recorded
                return lastMissingResult;
            }

            lastException?.Throw();
            throw new Exception("There are no usenet providers configured.");
        }
        catch
        {
            // Ensure in-flight attempts are cancelled before propagating.
            CancelAndObserveLosers(inFlight);
            throw;
        }
    }

    private static void CancelAndObserveLosers<T>(List<HedgeAttempt<T>> losers) where T : UsenetResponse
    {
        foreach (var loser in losers)
        {
            try { loser.Cts.Cancel(); } catch { /* already disposed */ }
            // Observe the loser's task to suppress unobserved-exception warnings,
            // and dispose any streaming response we won't be using.
            _ = loser.Task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } r)
                {
                    switch (r)
                    {
                        case UsenetDecodedBodyResponse b:
                            try { b.Stream?.Dispose(); } catch { }
                            break;
                        case UsenetDecodedArticleResponse a:
                            try { a.Stream?.Dispose(); } catch { }
                            break;
                    }
                }
                if (t.IsFaulted) _ = t.Exception; // observe
                try { loser.Cts.Dispose(); } catch { }
            }, TaskScheduler.Default);
        }
    }

    private sealed record HedgeAttempt<T>(
        Task<T> Task,
        MultiConnectionNntpClient Provider,
        int Idx,
        CancellationTokenSource Cts,
        Stopwatch Sw
    ) where T : UsenetResponse;

    private void RecordFetch(string host, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        if (metricsWriter == null) return;
        metricsWriter.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = host,
            ReadSessionId = ReadSessionScope.Value,
            Bytes = 0, // bytes flow lazily through CountingYencStream → ProviderBytesTracker
            DurationMs = (int)Math.Min(int.MaxValue, durationMs),
            Status = status,
            Retries = retries,
        });
    }

    private T WrapStreamForByteCounting<T>(T result, string host) where T : UsenetResponse
    {
        if (bytesTracker == null) return result;
        return result switch
        {
            UsenetDecodedBodyResponse b
                => (T)(object)(b with { Stream = new CountingYencStream(b.Stream, bytesTracker, host) }),
            UsenetDecodedArticleResponse a
                => (T)(object)(a with { Stream = new CountingYencStream(a.Stream, bytesTracker, host) }),
            _ => result,
        };
    }

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex is TimeoutException) return SegmentFetch.FetchStatus.Timeout;
        if (ex is UnauthorizedAccessException) return SegmentFetch.FetchStatus.Auth;
        if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException) return SegmentFetch.FetchStatus.Network;
        return SegmentFetch.FetchStatus.Other;
    }

    private List<MultiConnectionNntpClient> GetOrderedProviders()
    {
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .Where(x => !IsOverLimit(x))
            .OrderBy(x => x.ProviderType)
            // Within the same tier, prefer the provider with the most remaining
            // bytes. Uncapped providers report long.MaxValue and so are always
            // preferred over a capped one that's been chewed down. The net
            // effect: when a primary misses and we fall through to two blocks,
            // the fresher block is picked first — no user effort required.
            .ThenByDescending(x => GetRemainingBytes(x))
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        var healthy = enabled.Where(x => !x.IsTripped).ToList();

        // Always return at least one provider so cooldown probes can fire.
        // Note we intentionally do NOT relax the over-limit filter here:
        // exhausting a paid block must be a hard stop, not a soft preference.
        return healthy.Count > 0 ? healthy : enabled;
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
        // Stop at the effective cutoff (95% of cap) so in-flight fetches that
        // already passed this check can't push the actual count past the cap.
        // See ProviderUsageHelper.EffectiveLimitFraction for the rationale.
        var effective = (long)(limit.Value * ProviderUsageHelper.EffectiveLimitFraction);
        return used >= effective;
    }

    private long GetRemainingBytes(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return long.MaxValue;
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
        return Math.Max(0, limit.Value - used);
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}