using System.Runtime.InteropServices;

namespace YuukaOCR.Core.Interop;

public sealed record CapturedImage(int Width, int Height, byte[] Bgra)
{
    /// <summary>Raw bytes per pixel (32bpp).</summary>
    public int Stride => Width * 4;
}

/// <summary>BitBlt screen capture straight into a top-down BGRA buffer (no System.Drawing).</summary>
public static class ScreenCapture
{
    public static CapturedImage? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            var copied = NativeMethods.BitBlt(
                memoryDc, 0, 0, width, height,
                screenDc, x, y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            if (!copied) return null;

            var buffer = new byte[width * height * 4];
            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative = top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                },
                bmiColors = new uint[256]
            };

            var lines = NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)height, buffer, ref info, 0);
            return lines == 0 ? null : new CapturedImage(width, height, buffer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screen capture failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
