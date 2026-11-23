using System;
using System.Linq;
using Avalonia;

namespace LGLauncher
{
    internal sealed class ArtworkFraming
    {
        public const int SampleSize = 128;
        private readonly double aspectRatio;
        private readonly double[] columns;
        private readonly double[] rows;

        private ArtworkFraming(double aspectRatio, double[] columns, double[] rows)
        {
            this.aspectRatio = aspectRatio;
            this.columns = columns;
            this.rows = rows;
        }

        public static ArtworkFraming Analyze(byte[] pixels, int width, int height, double aspectRatio)
        {
            var stride = width * 4;
            double blue = 0, green = 0, red = 0;
            for (int p = 0; p < pixels.Length; p += 4)
            {
                blue += pixels[p];
                green += pixels[p + 1];
                red += pixels[p + 2];
            }
            var pixelCount = width * height;
            blue /= pixelCount;
            green /= pixelCount;
            red /= pixelCount;

            var columns = new double[width];
            var rows = new double[height];
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    var p = y * stride + x * 4;
                    var texture = (Difference(pixels, p - 4, p + 4) + Difference(pixels, p - stride, p + stride)) / 1530.0;
                    var contrast = (Math.Abs(blue - pixels[p]) + Math.Abs(green - pixels[p + 1]) + Math.Abs(red - pixels[p + 2])) / 765.0;
                    var brightness = Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2])) / 255.0;
                    var detail = (texture + 1.1 * contrast * contrast) * (.35 + .65 * brightness);
                    var weight = detail * detail;
                    columns[x] += weight;
                    rows[y] += weight;
                }
            }
            return new ArtworkFraming(aspectRatio, columns, rows);
        }

        public Rect Viewbox(Size viewport)
        {
            var span = CropSpan(aspectRatio, viewport);
            return new Rect(FindOffset(columns, span.Width), FindOffset(rows, span.Height), span.Width, span.Height);
        }

        public static Rect CenteredViewbox(double aspectRatio, Size viewport, Point position, double zoom = ArtworkPosition.MinimumZoom)
        {
            var span = CropSpan(aspectRatio, viewport);
            var width = span.Width / zoom;
            var height = span.Height / zoom;
            return new Rect(ClampOffset(position.X - width / 2, width),
                ClampOffset(position.Y - height / 2, height), width, height);
        }

        public static Point ZoomPosition(Rect crop, Point pointer, double currentZoom, double nextZoom)
        {
            var ratio = currentZoom / nextZoom;
            return new Point(ZoomAxis(crop.X, crop.Width, pointer.X, ratio),
                ZoomAxis(crop.Y, crop.Height, pointer.Y, ratio));
        }

        private static double ZoomAxis(double offset, double span, double pointer, double ratio)
        {
            var nextSpan = Math.Min(1, span * ratio);
            var anchor = Math.Max(0, Math.Min(1, pointer));
            return ClampOffset(offset + (span - nextSpan) * anchor, nextSpan) + nextSpan / 2;
        }

        public static Point DragPosition(Rect crop, Size viewport, Point position, Vector movement)
        {
            return new Point(
                DragAxis(crop.X, crop.Width, viewport.Width, position.X, movement.X),
                DragAxis(crop.Y, crop.Height, viewport.Height, position.Y, movement.Y));
        }

        private static Size CropSpan(double aspectRatio, Size viewport)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0) return new Size(1, 1);
            var viewportAspect = viewport.Width / viewport.Height;
            return new Size(Math.Min(1, viewportAspect / aspectRatio), Math.Min(1, aspectRatio / viewportAspect));
        }

        private static double DragAxis(double offset, double span, double viewport, double position, double movement)
        {
            if (span >= 1 || viewport <= 0 || movement == 0) return position;
            return ClampOffset(offset - movement * span / viewport, span) + span / 2;
        }

        private static double ClampOffset(double offset, double span)
        {
            return Math.Max(0, Math.Min(1 - span, offset));
        }

        private static int Difference(byte[] pixels, int first, int second)
        {
            return Math.Abs(pixels[first] - pixels[second]) + Math.Abs(pixels[first + 1] - pixels[second + 1]) +
                Math.Abs(pixels[first + 2] - pixels[second + 2]);
        }

        private static double FindOffset(double[] weights, double span)
        {
            if (span >= 1) return 0;
            var maxOffset = 1 - span;
            var centered = maxOffset / 2;
            var total = weights.Sum();
            if (total < .001) return centered;

            var bestOffset = centered;
            var bestScore = CropScore(weights, centered, span);
            for (int step = 0; step <= weights.Length; step++)
            {
                var offset = maxOffset * step / weights.Length;
                var score = CropScore(weights, offset, span) - Math.Abs(offset - centered) * total * .08;
                if (score <= bestScore) continue;
                bestScore = score;
                bestOffset = offset;
            }
            // Give strongly off-center subjects room on the side nearest the edge of the artwork.
            var direction = Math.Max(-1, Math.Min(1, (bestOffset - centered) / (maxOffset * .2)));
            var edgeRoom = Math.Min(span * .15, maxOffset * .06);
            return Math.Max(0, Math.Min(maxOffset, bestOffset + direction * edgeRoom));
        }

        private static double CropScore(double[] weights, double offset, double span)
        {
            // Details near the middle of a candidate crop count more than those at its edges.
            var center = offset + span / 2;
            var halfWidth = span / 2;
            var score = 0.0;
            for (int i = 0; i < weights.Length; i++)
            {
                var distance = ((i + .5) / weights.Length - center) / halfWidth;
                if (Math.Abs(distance) < 1) score += weights[i] * (1 - distance * distance);
            }
            return score;
        }
    }
}
