using System.Collections.Concurrent;

namespace D2RHost;

public sealed class DiscordNotificationQueue
{
    private readonly ConcurrentQueue<string> _messages = new();

    public event Action? MessageQueued;

    public void Enqueue(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _messages.Enqueue(message.Trim());
        MessageQueued?.Invoke();
    }

    public string[] Drain()
    {
        var messages = new List<string>();
        while (_messages.TryDequeue(out var message))
        {
            messages.Add(message);
        }

        return messages.ToArray();
    }

    public void Requeue(IEnumerable<string> messages)
    {
        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _messages.Enqueue(message);
            }
        }
    }
}
