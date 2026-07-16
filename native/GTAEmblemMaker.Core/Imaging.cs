using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SkiaSharp;

[assembly: InternalsVisibleTo("GTAEmblemMaker.Checks")]

namespace GTAEmblemMaker.Core
{
    public sealed class SourceImage
    {
        private const long MaxDecodedPixels = 64000000;
        private const int MaxDimension = 8192;
        private const int CanvasSize = 512;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsTransparent { get; private set; }
        public byte[] CanonicalRgba { get; private set; }

        private SourceImage(int width, int height, bool isTransparent, byte[] canonicalRgba)
        {
            Width = width;
            Height = height;
            IsTransparent = isTransparent;
            CanonicalRgba = canonicalRgba;
        }

        public static SourceImage Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Image path is required.", "path");

            using (var source = SKBitmap.Decode(Path.GetFullPath(path)))
            using (var colorSpace = SKColorSpace.CreateSrgb())
            {
                if (source == null) throw new InvalidDataException("Image could not be decoded.");
                var width = source.Width;
                var height = source.Height;
                GetPixelByteCount(width, height);
                if (width > MaxDimension || height > MaxDimension) throw new InvalidDataException("Image dimensions exceed 8192 pixels.");

                var sourceRgba = ReadUnpremultiplied(source, colorSpace);
                var isTransparent = HasTransparency(sourceRgba);
                var side = Math.Max(width, height);
                var surfaceInfo = new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
                using (var square = SKSurface.Create(surfaceInfo))
                {
                    if (square == null) throw new InvalidOperationException("Cannot allocate the image staging surface.");
                    square.Canvas.Clear(SKColors.Transparent);
                    using (var image = SKImage.FromBitmap(source))
                    {
                        square.Canvas.DrawImage(image, (side - width) / 2.0f, (side - height) / 2.0f, new SKSamplingOptions(SKFilterMode.Linear));
                    }
                    square.Canvas.Flush();

                    using (var staged = square.Snapshot())
                    using (var target = SKSurface.Create(new SKImageInfo(CanvasSize, CanvasSize, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace)))
                    {
                        if (target == null) throw new InvalidOperationException("Cannot allocate the canonical image surface.");
                        target.Canvas.Clear(SKColors.Transparent);
                        target.Canvas.DrawImage(staged, new SKRect(0, 0, CanvasSize, CanvasSize), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        target.Canvas.Flush();
                        using (var canonical = target.Snapshot()) return new SourceImage(width, height, isTransparent, Premultiply(ReadUnpremultiplied(canonical, colorSpace)));
                    }
                }
            }
        }

        internal static int GetPixelByteCount(int width, int height)
        {
            var pixels = (long)width * height;
            if (width <= 0 || height <= 0) throw new InvalidDataException("Image dimensions must be positive.");
            if (pixels > MaxDecodedPixels) throw new InvalidDataException("Image exceeds the 64 million pixel limit.");
            return checked((int)(pixels * 4));
        }

        private static byte[] ReadUnpremultiplied(SKBitmap source, SKColorSpace colorSpace)
        {
            using (var image = SKImage.FromBitmap(source)) return ReadUnpremultiplied(image, colorSpace);
        }

        private static byte[] ReadUnpremultiplied(SKImage source, SKColorSpace colorSpace)
        {
            var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, colorSpace);
            using (var bitmap = new SKBitmap(info))
            {
                if (!source.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) throw new InvalidOperationException("Cannot read decoded image pixels.");
                var pixels = new byte[checked(source.Width * source.Height * 4)];
                Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
                return pixels;
            }
        }

        private static bool HasTransparency(byte[] rgba)
        {
            for (var index = 3; index < rgba.Length; index += 4) if (rgba[index] < 255) return true;
            return false;
        }

        private static byte[] Premultiply(byte[] rgba)
        {
            for (var index = 0; index < rgba.Length; index += 4)
            {
                var alpha = rgba[index + 3];
                if (alpha == 255) continue;
                rgba[index] = (byte)(rgba[index] * alpha / 255);
                rgba[index + 1] = (byte)(rgba[index + 1] * alpha / 255);
                rgba[index + 2] = (byte)(rgba[index + 2] * alpha / 255);
            }
            return rgba;
        }
    }
}
