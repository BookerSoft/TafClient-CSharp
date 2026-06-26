namespace TafClient.Service;

/// <summary>
/// Minimal append-only file logger for host/join actions, separate from the
/// structured Serilog pipeline. Writes plain timestamped lines to hostlog.txt
/// and joinlog.txt under $HOME/TAF/Logs (see LogPaths), so the full lifecycle
/// of a single host/join attempt (request sent → server response → launch
/// steps → process exit) can be read back as one flat narrative without
/// filtering through the rest of the app's logs.
/// </summary>
public static class ActionLogger
{
    private static readonly object HostLock = new();
    private static readonly object JoinLock = new();

    public static void Host(string message) => Append(LogPaths.HostLogPath, HostLock, message);
    public static void Join(string message) => Append(LogPaths.JoinLogPath, JoinLock, message);

    private static void Append(string path, object gate, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try
        {
            lock (gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw or interrupt a host/join flow — if the
            // app directory isn't writable for some reason, silently drop.
        }
        // Always echo to console too, so existing diagnostic visibility is unchanged.
        Console.WriteLine(line);
    }
}
