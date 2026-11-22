namespace YuukaOCR.Core.Interop;

/// <summary>
/// Polls the clipboard sequence number on a background timer. Much cheaper than the old version's
/// QTimer + processEvents on the UI thread, and it never touches clipboard contents itself.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly Timer _timer;
    private readonly int _intervalMs;
    private uint _lastSequence;
    private int _suppressNext;
    private bool _started;

    public ClipboardWatcher(int intervalMs = 350)
    {
        _intervalMs = Math.Max(100, intervalMs);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a thread-pool thread when the clipboard changed.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _lastSequence = NativeMethods.GetClipboardSequenceNumber();
        _timer.Change(_intervalMs, _intervalMs);
    }

    public void Stop()
    {
        _started = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Ignores the next change — used right after the app copies its own OCR result.</summary>
    public void SuppressNext() => Interlocked.Exchange(ref _suppressNext, 1);

    private void Tick()
    {
        try
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence == _lastSequence) return;
            _lastSequence = sequence;

            if (Interlocked.Exchange(ref _suppressNext, 0) == 1) return;

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Clipboard watcher: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
