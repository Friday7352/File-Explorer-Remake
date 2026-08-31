using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGenerator;

internal static class Program
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [STAThread]
    private static void Main()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var output = Path.Combine(root, "Clearspace", "Assets", "Clearspace.ico");
        var images = IconSizes.Select(RenderPng).ToArray();

        using var stream = File.Create(output);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);

        var offset = 6 + (16 * images.Length);
        foreach (var image in images)
        {
            var size = image.Size;
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(image.Bytes.Length);
            writer.Write(offset);
            offset += image.Bytes.Length;
        }

        foreach (var image in images)
            writer.Write(image.Bytes);
    }

    private static IconImage RenderPng(int size)
    {
        const double canvas = 256;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var scale = size / canvas;
            context.PushTransform(new ScaleTransform(scale, scale));
            DrawIcon(context);
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new IconImage(size, stream.ToArray());
    }

    private static void DrawIcon(DrawingContext context)
    {
        var tile = new Rect(10, 10, 236, 236);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(34, 34, 33)),
            null,
            tile,
            54,
            54);

        // Folder back and tab: deliberately simple, with broad geometry that survives taskbar sizes.
        var back = new StreamGeometry();
        using (var geometry = back.Open())
        {
            geometry.BeginFigure(new Point(42, 93), true, true);
            geometry.LineTo(new Point(103, 93), true, false);
            geometry.LineTo(new Point(124, 72), true, false);
            geometry.LineTo(new Point(170, 72), true, false);
            geometry.LineTo(new Point(190, 93), true, false);
            geometry.LineTo(new Point(214, 93), true, false);
            geometry.LineTo(new Point(214, 175), true, false);
            geometry.LineTo(new Point(42, 175), true, false);
        }
        back.Freeze();
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(215, 164, 52)), null, back);

        // The front is a clean page-like plane with a small inset. The open C is the Clearspace mark.
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(249, 198, 70)),
            null,
            new Rect(36, 103, 184, 92),
            16,
            16);

        var cPen = new Pen(new SolidColorBrush(Color.FromRgb(34, 34, 33)), 17)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var clearspaceMark = new StreamGeometry();
        using (var geometry = clearspaceMark.Open())
        {
            geometry.BeginFigure(new Point(169, 124), false, false);
            geometry.ArcTo(new Point(169, 174), new Size(32, 32), 0, true, SweepDirection.Counterclockwise, true, false);
        }
        clearspaceMark.Freeze();
        context.DrawGeometry(null, cPen, clearspaceMark);

        // A single mint point brings the icon to life without introducing a theme color into the app.
        context.DrawEllipse(new SolidColorBrush(Color.FromRgb(98, 209, 169)), null, new Point(183, 149), 5, 5);
    }

    private sealed record IconImage(int Size, byte[] Bytes);
}
