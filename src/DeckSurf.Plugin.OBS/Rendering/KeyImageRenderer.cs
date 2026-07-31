using DeckSurf.SDK.Models;
using DeckSurf.SDK.Util;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.Versioning;

namespace DeckSurf.Plugin.OBS.Rendering
{
    public enum KeyVisualState
    {
        /// <summary>Scene is the current program scene.</summary>
        Active,

        /// <summary>Connected to OBS, but the scene is not live.</summary>
        Inactive,

        /// <summary>No connection to OBS.</summary>
        Disconnected
    }

    /// <summary>
    /// Renders scene-name key images. The active scene is drawn with a red fill
    /// and a LIVE badge so the program scene stands out. On Windows this uses
    /// System.Drawing, which the SDK already depends on; elsewhere it falls back
    /// to solid color keys from <see cref="ImageHelper"/> so no extra
    /// dependencies are required.
    /// </summary>
    public static class KeyImageRenderer
    {
        // Matches the red OBS uses for the program scene.
        private static readonly Color LiveRed = Color.FromArgb(198, 32, 46);

        public static byte[] Render(int buttonResolution, string text, KeyVisualState state)
        {
            var size = Math.Max(buttonResolution, 72);

            if (OperatingSystem.IsWindows())
            {
                return RenderWindows(size, text, state);
            }

            return RenderFallback(size, state);
        }

        /// <summary>
        /// Renders the recording key: a REC circle that is red with white text
        /// while recording and greyed out otherwise. <paramref name="pulse"/> is
        /// the animation position in [0..1] (0 = dimmest, 1 = brightest) and only
        /// applies while recording; idle and disconnected keys are static.
        /// </summary>
        public static byte[] RenderRecordKey(int buttonResolution, KeyVisualState state, float pulse)
        {
            var size = Math.Max(buttonResolution, 72);

            if (OperatingSystem.IsWindows())
            {
                return RenderRecordWindows(size, state, Math.Clamp(pulse, 0f, 1f));
            }

            return RenderFallback(size, state);
        }

        /// <summary>
        /// Composites a scene screenshot onto a key: center-cropped to square, with
        /// the scene name in a band at the bottom. Scenes on program get a red
        /// border and a LIVE pill. On non-Windows platforms the screenshot is
        /// passed through untouched (the device resizes it) since the overlay
        /// needs System.Drawing.
        /// </summary>
        public static byte[] RenderPreview(int buttonResolution, byte[] screenshot, string sceneName, bool isLive)
        {
            if (!OperatingSystem.IsWindows())
            {
                return screenshot;
            }

            return RenderPreviewWindows(Math.Max(buttonResolution, 72), screenshot, sceneName, isLive);
        }

        [SupportedOSPlatform("windows")]
        private static byte[] RenderPreviewWindows(int size, byte[] screenshot, string sceneName, bool isLive)
        {
            using var source = new Bitmap(new MemoryStream(screenshot));
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Cover-crop: scale so the shorter side fills the key, center the rest.
            var scale = Math.Max((float)size / source.Width, (float)size / source.Height);
            var scaledWidth = source.Width * scale;
            var scaledHeight = source.Height * scale;
            graphics.DrawImage(source, (size - scaledWidth) / 2, (size - scaledHeight) / 2, scaledWidth, scaledHeight);

            // Name band along the bottom so the label stays readable over any content.
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                var bandHeight = size * 0.26f;
                var bandRect = new RectangleF(0, size - bandHeight, size, bandHeight);
                using var bandBrush = new SolidBrush(Color.FromArgb(180, 12, 12, 16));
                graphics.FillRectangle(bandBrush, bandRect);

                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };

                using var nameFont = new Font("Segoe UI", bandHeight * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var nameBrush = new SolidBrush(Color.White);
                graphics.DrawString(sceneName, nameFont, nameBrush, bandRect, format);
            }

            if (isLive)
            {
                var borderWidth = Math.Max(3f, size * 0.06f);
                using var border = new Pen(LiveRed, borderWidth);
                graphics.DrawRectangle(border, borderWidth / 2, borderWidth / 2, size - borderWidth, size - borderWidth);

                var pillWidth = size * 0.36f;
                var pillHeight = size * 0.15f;
                var pillRect = new RectangleF(borderWidth + (size * 0.03f), borderWidth + (size * 0.03f), pillWidth, pillHeight);

                using var pillPath = RoundedRect(pillRect, pillHeight / 2);
                using var pillBrush = new SolidBrush(LiveRed);
                graphics.FillPath(pillBrush, pillPath);

                using var pillFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var pillFont = new Font("Segoe UI", pillHeight * 0.58f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var pillTextBrush = new SolidBrush(Color.White);
                graphics.DrawString("LIVE", pillFont, pillTextBrush, pillRect, pillFormat);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        private static byte[] RenderFallback(int size, KeyVisualState state)
        {
            var color = state switch
            {
                KeyVisualState.Active => new DeviceColor(LiveRed.R, LiveRed.G, LiveRed.B),
                KeyVisualState.Inactive => new DeviceColor(30, 30, 36),
                _ => new DeviceColor(64, 24, 24)
            };

            return ImageHelper.CreateBlankImage(size, color);
        }

        [SupportedOSPlatform("windows")]
        private static byte[] RenderWindows(int size, string text, KeyVisualState state)
        {
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (state == KeyVisualState.Active)
            {
                RenderLiveKey(graphics, size, text);
            }
            else
            {
                RenderIdleKey(graphics, size, text, state);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static byte[] RenderRecordWindows(int size, KeyVisualState state, float pulse)
        {
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            graphics.Clear(Color.FromArgb(30, 30, 36));

            var recording = state == KeyVisualState.Active;
            var diameter = size * 0.62f;
            var circleRect = new RectangleF((size - diameter) / 2, (size - diameter) / 2, diameter, diameter);

            if (recording)
            {
                // Soft halo that breathes with the pulse so the animation reads
                // even from the corner of the eye.
                var haloGrowth = size * 0.14f * pulse;
                var haloRect = RectangleF.Inflate(circleRect, haloGrowth / 2, haloGrowth / 2);
                using var halo = new SolidBrush(Color.FromArgb((int)(30 + (70 * pulse)), LiveRed));
                graphics.FillEllipse(halo, haloRect);
            }

            using var circleBrush = new SolidBrush(recording ? LiveRed : Color.FromArgb(56, 56, 64));
            graphics.FillEllipse(circleBrush, circleRect);

            var textColor = recording
                ? Color.FromArgb((int)(120 + (135 * pulse)), Color.White)
                : Color.FromArgb(146, 146, 154);

            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            using var font = new Font("Segoe UI", diameter * 0.28f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(textColor);
            graphics.DrawString("REC", font, textBrush, circleRect, format);

            if (state == KeyVisualState.Disconnected)
            {
                var dotSize = size * 0.14f;
                var margin = size * 0.08f;
                using var dot = new SolidBrush(Color.FromArgb(220, 68, 68));
                graphics.FillEllipse(dot, size - margin - dotSize, margin, dotSize, dotSize);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static void RenderLiveKey(Graphics graphics, int size, string text)
        {
            graphics.Clear(LiveRed);

            var pillWidth = size * 0.5f;
            var pillHeight = size * 0.17f;
            var pillRect = new RectangleF((size - pillWidth) / 2, size * 0.09f, pillWidth, pillHeight);

            using var pillPath = RoundedRect(pillRect, pillHeight / 2);
            using var pillBrush = new SolidBrush(Color.White);
            graphics.FillPath(pillBrush, pillPath);

            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };

            using var badgeFont = new Font("Segoe UI", pillHeight * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var badgeBrush = new SolidBrush(LiveRed);
            graphics.DrawString("LIVE", badgeFont, badgeBrush, pillRect, centered);

            var padding = size * 0.08f;
            var textTop = pillRect.Bottom + (size * 0.04f);
            var textArea = new RectangleF(padding, textTop, size - (2 * padding), size - textTop - padding);

            using var textBrush = new SolidBrush(Color.White);
            using var font = FitFont(graphics, text, size, textArea);
            graphics.DrawString(text, font, textBrush, textArea, centered);
        }

        [SupportedOSPlatform("windows")]
        private static void RenderIdleKey(Graphics graphics, int size, string text, KeyVisualState state)
        {
            var foreground = state == KeyVisualState.Inactive
                ? Color.FromArgb(210, 210, 215)
                : Color.FromArgb(110, 110, 118);

            graphics.Clear(Color.FromArgb(30, 30, 36));

            if (state == KeyVisualState.Disconnected)
            {
                var dotSize = size * 0.14f;
                var margin = size * 0.08f;
                using var dot = new SolidBrush(Color.FromArgb(220, 68, 68));
                graphics.FillEllipse(dot, size - margin - dotSize, margin, dotSize, dotSize);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                var padding = size * 0.08f;
                var textArea = new RectangleF(padding, padding, size - (2 * padding), size - (2 * padding));

                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter
                };

                using var brush = new SolidBrush(foreground);
                using var font = FitFont(graphics, text, size, textArea);
                graphics.DrawString(text, font, brush, textArea, format);
            }
        }

        [SupportedOSPlatform("windows")]
        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        [SupportedOSPlatform("windows")]
        private static Font FitFont(Graphics graphics, string text, int size, RectangleF textArea)
        {
            var fontSize = size * 0.22f;
            var minimumFontSize = size * 0.09f;

            while (fontSize > minimumFontSize)
            {
                using var candidate = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(text, candidate, (int)textArea.Width);
                if (measured.Height <= textArea.Height)
                {
                    break;
                }

                fontSize -= 2;
            }

            return new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        }
    }
}
