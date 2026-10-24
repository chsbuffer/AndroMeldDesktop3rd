using System.Collections.Concurrent;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    private readonly SemaphoreSlim _openWindow = new(1, 1);
    private readonly ConcurrentDictionary<string, MirrorState> _sessions = new(StringComparer.Ordinal);
    private Uri? _endpoint;

    public event Action<MirrorState>? WindowStateChanged;

    public MirrorState[] GetSessions()
    {
        return _sessions.Values.ToArray();
    }

    /// <summary>open-window has no request id; serialize requests until their opening event arrives.</summary>
    public Task<MirrorSession> OpenMirrorAsync(PhoneApp? app, CancellationToken ct, int width = 480, int height = 800, double dpr = 1)
    {
        return OpenMirrorCoreAsync(app, null, width, height, dpr, ct);
    }

    public Task<MirrorSession> OpenIntentMirrorAsync(AndroidIntentUri intent, CancellationToken ct, int width = 480, int height = 800, double dpr = 1)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return OpenMirrorCoreAsync(null, intent, width, height, dpr, ct);
    }

    private async Task<MirrorSession> OpenMirrorCoreAsync(PhoneApp? app, AndroidIntentUri? intent, int width, int height, double dpr,
        CancellationToken ct)
    {
        bool phone = app is null && intent is null;
        if (!Capabilities.Contains("video") || !Capabilities.Contains("multi-session") ||
            (phone && !Capabilities.Contains("phone-screen")))
        {
            throw new NotSupportedException("手机未提供此镜像功能，请更新或重新开启 AndroMeld 服务。");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!double.IsFinite(dpr) || dpr <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpr));
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _openWindow.WaitAsync(linked.Token).ConfigureAwait(false);
        HashSet<string> previousSessions = _sessions.Keys.ToHashSet(StringComparer.Ordinal);
        TaskCompletionSource<MirrorState> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(MirrorState state)
        {
            if (previousSessions.Contains(state.SessionId) || state.IsPhone != phone ||
                (app is not null && !string.Equals(state.PackageName, app.PackageName, StringComparison.Ordinal)) ||
                (intent is not null && (!string.IsNullOrEmpty(state.PackageName) || !string.IsNullOrEmpty(state.Kind))))
            {
                return;
            }

            if (string.Equals(state.State, "failed", StringComparison.Ordinal))
            {
                opened.TrySetException(new IOException(MirrorState.FailureMessage(state.Reason)));
            }
            else if (state.State is "opening" or "live" && !string.IsNullOrEmpty(state.SessionId))
            {
                opened.TrySetResult(state);
            }
        }

        WindowStateChanged += OnState;
        try
        {
            object request;
            if (intent is not null)
            {
                request = new
                {
                    c = "open-window",
                    intentUrl = intent.Value,
                    w = width,
                    h = height,
                    dpr
                };
            }
            else if (app is null)
            {
                request = new { c = "open-window", kind = "phone" };
            }
            else
            {
                request = new
                {
                    c = "open-window",
                    pkg = app.PackageName,
                    userId = app.UserId,
                    w = width,
                    h = height,
                    dpr
                };
            }
            await SendJsonAsync(request, false, 4, 1, linked.Token).ConfigureAwait(false);
            // After sending, keep waiting even if this window is closed, so its eventual session can be released.
            MirrorState state = await opened.Task.WaitAsync(TimeSpan.FromSeconds(25), _lifetime.Token).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                await CloseMirrorAsync(state.SessionId, _lifetime.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            return new MirrorSession(this, new UriBuilder(_endpoint!) { Path = "/media" }.Uri, Token!, state);
        }
        finally
        {
            WindowStateChanged -= OnState;
            _openWindow.Release();
        }
    }

    public Task CloseMirrorAsync(string sessionId, CancellationToken ct)
    {
        return SendJsonAsync(new { c = "close-window", sessionId }, false, 4, 1, ct);
    }

    internal async Task CloseMirrorAndWaitAsync(string sessionId, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(MirrorState state)
        {
            if (string.Equals(state.SessionId, sessionId, StringComparison.Ordinal) && state.State is "closed" or "failed")
            {
                closed.TrySetResult();
            }
        }

        WindowStateChanged += OnState;
        try
        {
            // A remote close already removed the session; do not send another close request.
            if (!_sessions.ContainsKey(sessionId))
            {
                return;
            }

            await CloseMirrorAsync(sessionId, linked.Token).ConfigureAwait(false);
            await closed.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { WindowStateChanged -= OnState; }
    }

    internal Task AcknowledgeVideoAsync(string sessionId, ulong bytes, CancellationToken ct)
    {
        return SendJsonAsync(new { c = "video-ack", sessionId, bytes }, false, 4, 1, ct);
    }
}