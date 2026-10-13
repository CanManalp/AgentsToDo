using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentsToDo;

/// <summary>Draws the taskbar icon: a tiny version of the notepad.</summary>
internal static class PadIcon
{
    public static BitmapSource Create()
    {
        var board = new SolidColorBrush(Color.FromRgb(0x9A, 0x6B, 0x3F));
        var paper = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF5));
        var ink = new SolidColorBrush(Color.FromRgb(0x23, 0x3C, 0x8C));
        var metal = new SolidColorBrush(Color.FromRgb(0xB9, 0xBE, 0xC4));

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(board, null, new Rect(8, 8, 48, 54), 8, 8);
            dc.DrawRectangle(paper, null, new Rect(14, 16, 36, 40));
            for (int y = 28; y <= 48; y += 9)
                dc.DrawRectangle(ink, null, new Rect(19, y, 26, 3));
            for (int x = 17; x <= 44; x += 9)
                dc.DrawRoundedRectangle(metal, null, new Rect(x, 2, 4, 18), 2, 2);
        }

        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
