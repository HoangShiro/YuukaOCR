using System.Runtime.InteropServices;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Input;

/// <summary>
/// System-wide hotkey via a low-level hook, running on its own thread with a message pump (hooks
/// need one, and it must never be the UI thread). Only one hook is installed at a time — keyboard
/// or mouse — depending on the configured hotkey.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private readonly object _lock = new();
    private readonly Action<string>? _log;

    private HotkeySpec _spec = HotkeySpec.None;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private NativeMethods.HookProc? _keyboardProc;
    private NativeMethods.HookProc? _mouseProc;
    private long _lastFiredTicks;
    private volatile bool _running;

    public GlobalHotkey(Action<string>? log = null) => _log = log;

    public event Action? Pressed;

    public HotkeySpec Current
    {
        get { lock (_lock) return _spec; }
    }

    public bool IsActive => _keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

    /// <summary>Installs (or replaces) the hotkey. Passing <see cref="HotkeySpec.None"/> removes it.</summary>
    public void Set(HotkeySpec spec)
    {
        lock (_lock)
        {
            StopCore();
            _spec = spec;
            if (spec.IsValid) StartCore(spec);
        }
    }

    public void Stop()
    {
        lock (_lock) StopCore();
    }

    private void StartCore(HotkeySpec spec)
    {
        _running = true;
        _thread = new Thread(() => Pump(spec))
        {
            IsBackground = true,
            Name = "YuukaHotkey"
        };
        _thread.Start();
    }

    private void StopCore()
    {
        if (!_running && _thread is null) return;
        _running = false;

        var threadId = _threadId;
        if (threadId != 0)
            NativeMethods.PostThreadMessageW(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        _thread?.Join(600);
        _thread = null;
        _threadId = 0;
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
        _keyboardProc = null;
        _mouseProc = null;
    }

    private void Pump(HotkeySpec spec)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        var module = NativeMethods.GetModuleHandleW(null);

        if (spec.Kind == HotkeyKind.Keyboard)
        {
            _keyboardProc = KeyboardProc;
            _keyboardHook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        }
        else
        {
            _mouseProc = MouseProc;
            _mouseHook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        }

        if (_keyboardHook == IntPtr.Zero && _mouseHook == IntPtr.Zero)
        {
            _log?.Invoke(L.F("hook.globalFailed", spec.Describe()));
            _running = false;
            return;
        }

        while (_running && NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var spec = Current;
                if (spec.Kind == HotkeyKind.Keyboard &&
                    (int)data.vkCode == spec.VirtualKey &&
                    ModifiersMatch(spec.Modifiers))
                {
                    Fire();
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            var spec = Current;

            if (spec.Kind == HotkeyKind.Mouse)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var button = message switch
                {
                    NativeMethods.WM_MBUTTONDOWN => MouseButton.Middle,
                    NativeMethods.WM_XBUTTONDOWN => ((data.mouseData >> 16) & 0xFFFF) == 1
                        ? MouseButton.X1
                        : MouseButton.X2,
                    NativeMethods.WM_LBUTTONDOWN => MouseButton.Left,
                    NativeMethods.WM_RBUTTONDOWN => MouseButton.Right,
                    _ => MouseButton.None
                };

                if (button != MouseButton.None && button == spec.Button && ModifiersMatch(spec.Modifiers))
                    Fire();
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool ModifiersMatch(HotkeyModifiers expected)
    {
        var actual = HotkeyModifiers.None;
        if (IsDown(NativeMethods.VK_CONTROL)) actual |= HotkeyModifiers.Control;
        if (IsDown(NativeMethods.VK_SHIFT)) actual |= HotkeyModifiers.Shift;
        if (IsDown(NativeMethods.VK_MENU)) actual |= HotkeyModifiers.Alt;
        if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN)) actual |= HotkeyModifiers.Windows;
        return actual == expected;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void Fire()
    {
        // Holding the key repeats WM_KEYDOWN; keep one action per press.
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastFiredTicks);
        if (now - previous < 250) return;
        Interlocked.Exchange(ref _lastFiredTicks, now);

        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Invoke(L.F("hook.handlerError", ex.Message));
        }
    }

    public void Dispose() => Stop();
}
