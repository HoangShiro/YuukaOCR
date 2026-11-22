using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuukaOCR.Core.Interop;

namespace YuukaOCR.App.Ui;

public static class PngEncoder
{
    /// <summary>Wraps the raw BGRA screen buffer into a WPF bitmap and encodes it as PNG bytes.</summary>
    public static byte[] FromCapture(CapturedImage image)
    {
        var source = BitmapSource.Create(
            image.Width, image.Height, 96, 96,
            PixelFormats.Bgr32, null,
            image.Bgra, image.Stride);

        return Encode(source);
    }

    public static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
