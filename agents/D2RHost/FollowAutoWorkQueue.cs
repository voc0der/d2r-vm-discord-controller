namespace D2RHost;

/// <summary>
/// Run-owned work, polled by the fleet loop without waiting for its slowest account. Only the
/// fleet loop accesses this queue; operations return results instead of mutating fleet state.
/// Reservations survive game changes and cancellation until the operation actually finishes.
/// </summary>
internal sealed class FollowAutoWorkQueue<T>
{
    private readonly List<Work> _work = [];
    private readonly CancellationToken _cancellationToken;

    public FollowAutoWorkQueue(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

    public bool HasWork => _work.Count > 0;
    public bool IsBusy(string accountKey) => _work.Any(work => work.Accounts.Contains(accountKey));

    public Task<T> Enqueue(
        IEnumerable<string> accountKeys,
        bool gameScoped,
        Func<CancellationToken, Task<T>> operation)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var accounts = accountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (accounts.Count == 0)
        {
            throw new ArgumentException("Work must reserve at least one account.", nameof(accountKeys));
        }

        // A node restart reserves every affected account immediately, but must let commands
        // already issued to those accounts unwind before it takes the node down.
        var predecessors = _work.Where(work => work.Accounts.Overlaps(accounts))
            .Select(work => work.Task).ToArray();
        var task = Task.Run(async () =>
        {
            await Task.WhenAll(predecessors);
            _cancellationToken.ThrowIfCancellationRequested();
            return await operation(_cancellationToken);
        }, _cancellationToken);
        _work.Add(new Work(accounts, gameScoped, task));
        return task;
    }

    public void InvalidateChecks(IEnumerable<string>? accountKeys = null)
    {
        var accounts = accountKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var work in _work)
        {
            if (work.GameScoped && (accounts is null || work.Accounts.Overlaps(accounts)))
            {
                work.IsCurrent = false;
            }
        }
    }

    public IReadOnlyList<T> TakeCompleted()
    {
        var completed = _work.Where(work => work.Task.IsCompleted).ToArray();
        var results = new List<T>();
        foreach (var work in completed)
        {
            _work.Remove(work);
            // Observe faults even when the result belongs to an abandoned game.
            var result = work.Task.GetAwaiter().GetResult();
            if (work.IsCurrent)
            {
                results.Add(result);
            }
        }

        return results;
    }

    public Task DrainAsync() => Task.WhenAll(_work.Select(work => work.Task));

    private sealed class Work(HashSet<string> accounts, bool gameScoped, Task<T> task)
    {
        public HashSet<string> Accounts { get; } = accounts;
        public bool GameScoped { get; } = gameScoped;
        public Task<T> Task { get; } = task;
        public bool IsCurrent { get; set; } = true;
    }
}
