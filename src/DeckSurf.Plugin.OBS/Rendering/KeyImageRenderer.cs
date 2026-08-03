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
        /// <summary>The key's function is engaged: scene on program, recording
        /// running, input muted, virtual camera on.</summary>
        Active,

        /// <summary>Connected to OBS, but the key's function is not engaged.</summary>
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

        private static readonly Color PausedAmber = Color.FromArgb(222, 152, 32);
        private static readonly Color VirtualCamBlue = Color.FromArgb(38, 128, 235);
        private static readonly Color PanelBackground = Color.FromArgb(30, 30, 36);
        private static readonly Color IdleCircle = Color.FromArgb(56, 56, 64);
        private static readonly Color IdleForeground = Color.FromArgb(146, 146, 154);

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
        /// while recording, amber while the recording is paused, and greyed out
        /// otherwise. <paramref name="pulse"/> is the animation position in
        /// [0..1] (0 = dimmest, 1 = brightest) and only applies while actively
        /// recording; paused, idle, and disconnected keys are static.
        /// </summary>
        public static byte[] RenderRecordKey(int buttonResolution, KeyVisualState state, float pulse, bool paused)
        {
            var size = Math.Max(buttonResolution, 72);

            if (OperatingSystem.IsWindows())
            {
                return RenderRecordWindows(size, state, Math.Clamp(pulse, 0f, 1f), paused);
            }

            return RenderFallback(size, state);
        }

        /// <summary>
        /// Renders the mute key: a microphone glyph with the input name in a band
        /// at the bottom. <see cref="KeyVisualState.Active"/> means muted and
        /// draws the glyph in red with a slash through it.
        /// </summary>
        public static byte[] RenderMuteKey(int buttonResolution, KeyVisualState state, string inputName)
        {
            var size = Math.Max(buttonResolution, 72);

            if (OperatingSystem.IsWindows())
            {
                return RenderMuteWindows(size, state, inputName);
            }

            return RenderFallback(size, state);
        }

        /// <summary>
        /// Renders the recording pause key: pause bars in a circle that turns
        /// amber while the recording is paused. <see cref="KeyVisualState.Active"/>
        /// means a recording is running; idle keys stay dimmed since pausing does
        /// not apply.
        /// </summary>
        public static byte[] RenderPauseKey(int buttonResolution, KeyVisualState state, bool paused)
        {
            var size = Math.Max(buttonResolution, 72);

            if (OperatingSystem.IsWindows())
            {
                return RenderPauseWindows(size, state, paused);
            }

            return paused
                ? ImageHelper.CreateBlankImage(size, new DeviceColor(PausedAmber.R, PausedAmber.G, PausedAmber.B))
                : RenderFallback(size, state);
        }

        /// <summary>
        /// Renders the virtual camera key: a CAM circle that lights up blue while
        /// the virtual camera is running.
        /// </summary>
        public static byte[] RenderVirtualCamKey(int buttonResolution, KeyVisualState state)
        {
            var size = Math.Max(buttonResolution, 72);

            if (OperatingSystem.IsWindows())
            {
                return RenderVirtualCamWindows(size, state);
            }

            return state == KeyVisualState.Active
                ? ImageHelper.CreateBlankImage(size, new DeviceColor(VirtualCamBlue.R, VirtualCamBlue.G, VirtualCamBlue.B))
                : RenderFallback(size, state);
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
        private static byte[] RenderRecordWindows(int size, KeyVisualState state, float pulse, bool paused)
        {
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            graphics.Clear(PanelBackground);

            var recording = state == KeyVisualState.Active;
            var diameter = size * 0.62f;
            var circleRect = new RectangleF((size - diameter) / 2, (size - diameter) / 2, diameter, diameter);

            if (recording && !paused)
            {
                // Soft halo that breathes with the pulse so the animation reads
                // even from the corner of the eye.
                var haloGrowth = size * 0.14f * pulse;
                var haloRect = RectangleF.Inflate(circleRect, haloGrowth / 2, haloGrowth / 2);
                using var halo = new SolidBrush(Color.FromArgb((int)(30 + (70 * pulse)), LiveRed));
                graphics.FillEllipse(halo, haloRect);
            }

            var circleColor = !recording ? IdleCircle : paused ? PausedAmber : LiveRed;
            using var circleBrush = new SolidBrush(circleColor);
            graphics.FillEllipse(circleBrush, circleRect);

            var textColor = !recording
                ? IdleForeground
                : paused
                    ? Color.White
                    : Color.FromArgb((int)(120 + (135 * pulse)), Color.White);

            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            using var font = new Font("Segoe UI", diameter * 0.28f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(textColor);
            graphics.DrawString("REC", font, textBrush, circleRect, format);

            DrawDisconnectedDot(graphics, size, state);

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static byte[] RenderMuteWindows(int size, KeyVisualState state, string inputName)
        {
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            graphics.Clear(PanelBackground);

            var muted = state == KeyVisualState.Active;
            var glyphColor = state switch
            {
                KeyVisualState.Active => LiveRed,
                KeyVisualState.Inactive => Color.FromArgb(210, 210, 215),
                _ => IdleForeground
            };

            var glyphArea = new RectangleF(size * 0.31f, size * 0.10f, size * 0.38f, size * 0.54f);
            DrawMicGlyph(graphics, glyphArea, glyphColor);

            if (muted)
            {
                // The slash gets a background-colored underlay so it separates
                // from the glyph strokes it crosses.
                var slashStart = new PointF(glyphArea.Left - (size * 0.07f), glyphArea.Top - (size * 0.04f));
                var slashEnd = new PointF(glyphArea.Right + (size * 0.07f), glyphArea.Bottom + (size * 0.04f));

                using var underlay = new Pen(PanelBackground, size * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(underlay, slashStart, slashEnd);

                using var slash = new Pen(LiveRed, size * 0.06f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(slash, slashStart, slashEnd);
            }

            if (!string.IsNullOrWhiteSpace(inputName))
            {
                var bandHeight = size * 0.24f;
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
                using var nameBrush = new SolidBrush(state == KeyVisualState.Disconnected ? IdleForeground : Color.White);
                graphics.DrawString(inputName, nameFont, nameBrush, bandRect, format);
            }

            DrawDisconnectedDot(graphics, size, state);

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static byte[] RenderPauseWindows(int size, KeyVisualState state, bool paused)
        {
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            graphics.Clear(PanelBackground);

            var recording = state == KeyVisualState.Active;
            var diameter = size * 0.62f;
            var circleRect = new RectangleF((size - diameter) / 2, (size - diameter) / 2, diameter, diameter);

            using var circleBrush = new SolidBrush(paused ? PausedAmber : IdleCircle);
            graphics.FillEllipse(circleBrush, circleRect);

            // White bars while pausing applies (recording or already paused);
            // dimmed otherwise so the key reads as not currently actionable.
            var barColor = paused || recording ? Color.White : IdleForeground;
            var barWidth = diameter * 0.13f;
            var barHeight = diameter * 0.38f;
            var barGap = diameter * 0.14f;
            var barTop = circleRect.Top + ((diameter - barHeight) / 2);
            var centerX = circleRect.Left + (diameter / 2);

            using var barBrush = new SolidBrush(barColor);
            using var leftBar = RoundedRect(new RectangleF(centerX - (barGap / 2) - barWidth, barTop, barWidth, barHeight), barWidth * 0.3f);
            using var rightBar = RoundedRect(new RectangleF(centerX + (barGap / 2), barTop, barWidth, barHeight), barWidth * 0.3f);
            graphics.FillPath(barBrush, leftBar);
            graphics.FillPath(barBrush, rightBar);

            DrawDisconnectedDot(graphics, size, state);

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static byte[] RenderVirtualCamWindows(int size, KeyVisualState state)
        {
            using var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            graphics.Clear(PanelBackground);

            var active = state == KeyVisualState.Active;
            var diameter = size * 0.62f;
            var circleRect = new RectangleF((size - diameter) / 2, (size - diameter) / 2, diameter, diameter);

            using var circleBrush = new SolidBrush(active ? VirtualCamBlue : IdleCircle);
            graphics.FillEllipse(circleBrush, circleRect);

            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            using var font = new Font("Segoe UI", diameter * 0.28f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(active ? Color.White : IdleForeground);
            graphics.DrawString("CAM", font, textBrush, circleRect, format);

            DrawDisconnectedDot(graphics, size, state);

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static void DrawMicGlyph(Graphics graphics, RectangleF area, Color color)
        {
            var capsuleWidth = area.Width * 0.52f;
            var capsuleHeight = area.Height * 0.58f;
            var capsuleRect = new RectangleF(area.Left + ((area.Width - capsuleWidth) / 2), area.Top, capsuleWidth, capsuleHeight);

            using var capsule = RoundedRect(capsuleRect, capsuleWidth / 2);
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, capsule);

            var penWidth = Math.Max(2f, area.Width * 0.14f);
            using var pen = new Pen(color, penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // Cradle: the lower half of an ellipse wrapped around the capsule,
            // then the stem and base plate below it.
            var cradleRect = new RectangleF(area.Left, capsuleRect.Top + (capsuleHeight * 0.4f), area.Width, capsuleHeight * 0.85f);
            graphics.DrawArc(pen, cradleRect, 0, 180);

            var centerX = area.Left + (area.Width / 2);
            var baseY = area.Bottom - (penWidth / 2);
            graphics.DrawLine(pen, centerX, cradleRect.Bottom, centerX, baseY);
            graphics.DrawLine(pen, centerX - (area.Width * 0.28f), baseY, centerX + (area.Width * 0.28f), baseY);
        }

        [SupportedOSPlatform("windows")]
        private static void DrawDisconnectedDot(Graphics graphics, int size, KeyVisualState state)
        {
            if (state != KeyVisualState.Disconnected)
            {
                return;
            }

            var dotSize = size * 0.14f;
            var margin = size * 0.08f;
            using var dot = new SolidBrush(Color.FromArgb(220, 68, 68));
            graphics.FillEllipse(dot, size - margin - dotSize, margin, dotSize, dotSize);
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
