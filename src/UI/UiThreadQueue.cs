using System.Collections.Concurrent;

namespace TafClient.UI;

/// <summary>
/// A thread-safe queue of UI actions that are drained on the SFML render thread
/// once per frame, just before gui.Draw().
///
/// TGUI (and SFML) are not thread-safe.  Background tasks (login, network)
/// must never touch TGUI widgets directly.  Instead they Post() an Action here,
/// and TafApp.Run() drains the queue on every frame.
///
/// This is the C# equivalent of JavaFX's Platform.runLater() /
/// JavaFxUtil.runLater() used throughout the Java client.
/// </summary>
public sealed class UiThreadQueue
{
    private readonly ConcurrentQueue<Action> _queue = new();

    /// <summary>Enqueue an action to run on the render thread.</summary>
    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>
    /// Drain and execute all pending actions.
    /// Called by TafApp on the render thread before each gui.Draw().
    /// </summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex)
            {
                // Don't let a bad UI action crash the render loop
                Console.Error.WriteLine($"[UiThreadQueue] Action threw: {ex.Message}");
            }
        }
    }
}
