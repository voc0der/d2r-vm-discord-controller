namespace AgentCommon;

public static class LogFileRotator
{
    // Each process start gets its own log.0, with up to `keep` prior runs preserved as
    // log.1, log.2, ... - hosts here are respawned unattended (self-update, /d2r restart), so
    // whatever crashed or misbehaved on the last run is otherwise gone with the window
    // that nobody was watching.
    public static string RotateAndPrepare(string logsDir, int keep = 3)
    {
        Directory.CreateDirectory(logsDir);

        var oldest = Path.Combine(logsDir, $"log.{keep - 1}");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = keep - 2; i >= 0; i--)
        {
            var src = Path.Combine(logsDir, $"log.{i}");
            var dst = Path.Combine(logsDir, $"log.{i + 1}");
            if (File.Exists(src))
            {
                File.Move(src, dst);
            }
        }

        return Path.Combine(logsDir, "log.0");
    }
}
