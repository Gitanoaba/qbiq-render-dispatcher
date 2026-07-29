using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Generates ribbon button icons at runtime using System.Drawing,
    /// so no external PNG files need to be shipped with the plugin.
    ///
    /// Three icons, each available at 32x32 (LargeImage) and 16x16 (Image):
    ///   - RenderServerIcon: green rounded-square with a white "play" triangle
    ///   - ViewLogIcon:      dark gray rounded-square with a white "document" + lines
    ///   - CreateTicketIcon: blue rounded-square with a white paper airplane
    /// </summary>
    public static class RibbonIcons
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        // ===== Public factories =====================================

        public static BitmapSource RenderServer(int size = 32)
        {
            return Draw(size, (g, s) =>
            {
                FillRoundedBg(g, s, Color.FromArgb(30, 130, 60));   // green
                DrawPlayTriangle(g, s, Color.White);
            });
        }

        public static BitmapSource ViewLog(int size = 32)
        {
            return Draw(size, (g, s) =>
            {
                FillRoundedBg(g, s, Color.FromArgb(70, 70, 75));    // dark gray
                DrawDocument(g, s, Color.White, Color.FromArgb(70, 70, 75));
            });
        }

        public static BitmapSource CreateTicket(int size = 32)
        {
            return Draw(size, (g, s) =>
            {
                FillRoundedBg(g, s, Color.FromArgb(0, 120, 215));   // qbiq blue
                DrawPaperAirplane(g, s, Color.White);
            });
        }

        // ===== Drawing primitives ===================================

        private static BitmapSource Draw(int size, Action<Graphics, int> paint)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode     = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    paint(g, size);
                }
                return BitmapToSource(bmp);
            }
        }

        private static void FillRoundedBg(Graphics g, int size, Color color)
        {
            using (var brush = new SolidBrush(color))
            using (var path = RoundedRectPath(0, 0, size, size, Math.Max(2f, size / 6f)))
            {
                g.FillPath(brush, path);
            }
        }

        private static void DrawPlayTriangle(Graphics g, int size, Color color)
        {
            float pad = size * 0.27f;
            var pts = new PointF[]
            {
                new PointF(pad,            pad),
                new PointF(pad,            size - pad),
                new PointF(size - pad,     size * 0.5f)
            };
            using (var brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, pts);
            }
        }

        private static void DrawDocument(Graphics g, int size, Color paper, Color lineColor)
        {
            float marginX = size * 0.22f;
            float marginY = size * 0.18f;
            float paperW  = size - 2 * marginX;
            float paperH  = size - 2 * marginY;
            float foldSz  = size * 0.18f;

            // Paper (rounded rect with one folded corner) - draw as path
            using (var path = new GraphicsPath())
            {
                path.AddLine(marginX,                     marginY,
                             marginX + paperW - foldSz,   marginY);
                path.AddLine(marginX + paperW - foldSz,   marginY,
                             marginX + paperW,            marginY + foldSz);
                path.AddLine(marginX + paperW,            marginY + foldSz,
                             marginX + paperW,            marginY + paperH);
                path.AddLine(marginX + paperW,            marginY + paperH,
                             marginX,                     marginY + paperH);
                path.AddLine(marginX,                     marginY + paperH,
                             marginX,                     marginY);
                path.CloseFigure();

                using (var fill = new SolidBrush(paper))
                {
                    g.FillPath(fill, path);
                }
            }

            // Horizontal lines (mock text)
            float lineY0     = marginY + paperH * 0.30f;
            float lineSpace  = paperH * 0.18f;
            float lineX0     = marginX + paperW * 0.12f;
            float lineXEnd   = marginX + paperW * 0.85f;
            using (var pen = new Pen(lineColor, Math.Max(1f, size / 18f)))
            {
                for (int i = 0; i < 3; i++)
                {
                    float y    = lineY0 + i * lineSpace;
                    float xEnd = (i == 2) ? marginX + paperW * 0.55f : lineXEnd;
                    g.DrawLine(pen, lineX0, y, xEnd, y);
                }
            }
        }

        private static void DrawPaperAirplane(Graphics g, int size, Color color)
        {
            // Two-triangle paper-airplane silhouette pointing right.
            // Body
            var body = new PointF[]
            {
                new PointF(size * 0.15f, size * 0.50f),  // tail tip
                new PointF(size * 0.85f, size * 0.22f),  // nose top
                new PointF(size * 0.50f, size * 0.58f)   // fold (back)
            };
            // Wing fold (under body)
            var wing = new PointF[]
            {
                new PointF(size * 0.50f, size * 0.58f),
                new PointF(size * 0.85f, size * 0.22f),
                new PointF(size * 0.62f, size * 0.80f)
            };
            using (var brush     = new SolidBrush(color))
            using (var shadowBrh = new SolidBrush(Color.FromArgb(180, 0, 90, 170)))
            {
                g.FillPolygon(brush,     body);
                g.FillPolygon(shadowBrh, wing);
            }
        }

        private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float r)
        {
            float d = r * 2f;
            var path = new GraphicsPath();
            path.AddArc(x,             y,             d, d, 180, 90);
            path.AddArc(x + w - d,     y,             d, d, 270, 90);
            path.AddArc(x + w - d,     y + h - d,     d, d,   0, 90);
            path.AddArc(x,             y + h - d,     d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        private static BitmapSource BitmapToSource(Bitmap bmp)
        {
            IntPtr hBmp = bmp.GetHbitmap();
            try
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }
    }
}
