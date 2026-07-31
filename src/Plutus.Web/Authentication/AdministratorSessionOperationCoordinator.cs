namespace Plutus.Web.Authentication;

/// <summary>
/// Single-process operation lease coordinator. A lease stays active through a
/// complete state-changing operation. Revocation cancels and drains all active
/// leases before the HTTP logout response can complete, preventing a retained
/// circuit from committing a write after logout.
/// </summary>
public sealed class AdministratorSessionOperationCoordinator(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionEntry> _sessions = [];

    public async Task<AdministratorSessionOperationLease?> TryAcquireAsync(
        Guid sessionId,
        string passwordHashFingerprint,
        AdministratorSessionStore sessionStore,
        CancellationToken cancellationToken = default)
    {
        SessionEntry entry;
        lock (_gate)
        {
            entry = GetOrCreateEntry(sessionId);
            if (entry.Revoking)
            {
                return null;
            }

            entry.ActiveOperations++;
        }

        var lease = new AdministratorSessionOperationLease(this, sessionId, entry);
        try
        {
            var session = await sessionStore.GetActiveAsync(sessionId, passwordHashFingerprint, cancellationToken);
            if (session is null || !lease.SetExpiration(session.ExpiresAt, timeProvider.GetUtcNow().UtcDateTime))
            {
                lease.Dispose();
                return null;
            }

            lease.CancellationToken.ThrowIfCancellationRequested();
            return lease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            throw;
        }
        catch (Exception)
        {
            lease.Dispose();
            return null;
        }
    }

    public async Task RevokeAndDrainAsync(
        Guid sessionId,
        Func<CancellationToken, Task> persistRevocation)
    {
        Task drained;
        lock (_gate)
        {
            var entry = GetOrCreateEntry(sessionId);
            entry.Revoking = true;
            entry.Cancellation.Cancel();
            drained = entry.ActiveOperations == 0 ? Task.CompletedTask : entry.Drained.Task;
        }

        // Do not use RequestAborted here: a server that has begun a logout must
        // finish draining and persist revocation before it can treat the session
        // as ended. This single-instance coordinator is the shared boundary.
        await drained;
        await persistRevocation(CancellationToken.None);
    }

    private SessionEntry GetOrCreateEntry(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            entry = new SessionEntry();
            _sessions.Add(sessionId, entry);
        }

        return entry;
    }

    private void Release(Guid sessionId, SessionEntry entry)
    {
        lock (_gate)
        {
            if (--entry.ActiveOperations == 0 && entry.Revoking)
            {
                entry.Drained.TrySetResult();
            }
        }
    }

    internal sealed class SessionEntry
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveOperations { get; set; }
        public bool Revoking { get; set; }
    }

    public sealed class AdministratorSessionOperationLease : IDisposable
    {
        private readonly AdministratorSessionOperationCoordinator _owner;
        private readonly Guid _sessionId;
        private readonly SessionEntry _entry;
        private int _disposed;

        internal AdministratorSessionOperationLease(
            AdministratorSessionOperationCoordinator owner,
            Guid sessionId,
            SessionEntry entry)
        {
            _owner = owner;
            _sessionId = sessionId;
            _entry = entry;
        }

        public CancellationToken CancellationToken => _entry.Cancellation.Token;

        internal bool SetExpiration(DateTime expiresAt, DateTime now)
        {
            var remaining = expiresAt - now;
            if (remaining <= TimeSpan.Zero || _entry.Cancellation.IsCancellationRequested)
            {
                return false;
            }

            _entry.Cancellation.CancelAfter(remaining);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_sessionId, _entry);
            }
        }
    }
}
