using System.Runtime.InteropServices;
using System.Text;

namespace YuukaOCR.Core.Interop;

/// <summary>Writes text to the Windows clipboard with retries (the clipboard is a shared lock).</summary>
public static class ClipboardWriter
{
    public static bool SetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(30);
                continue;
            }

            try
            {
                NativeMethods.EmptyClipboard();
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (handle == IntPtr.Zero) return false;

                var pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(handle);
                    return false;
                }

                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(handle);
                    return false;
                }

                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return false;
    }
}
