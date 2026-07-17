using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GTAEmblemMaker.Core
{
    internal sealed class CatalogMaskAtlasEntry
    {
        internal string Identifier { get; private set; }
        internal string Slug { get; private set; }
        internal string Path { get; private set; }
        internal double IntrinsicWidth { get; private set; }
        internal double IntrinsicHeight { get; private set; }
        internal double MinX { get; private set; }
        internal double MinY { get; private set; }
        internal double MaxX { get; private set; }
        internal double MaxY { get; private set; }
        internal int Size { get; private set; }
        internal byte[] Mask { get; private set; }

        internal CatalogMaskAtlasEntry(string identifier, ShapeDefinition definition, SKRect bounds, int size, byte[] mask)
        {
            Identifier = identifier;
            Slug = definition.Slug;
            Path = definition.Path;
            IntrinsicWidth = definition.Width;
            IntrinsicHeight = definition.Height;
            MinX = bounds.Left;
            MinY = bounds.Top;
            MaxX = bounds.Right;
            MaxY = bounds.Bottom;
            Size = size;
            Mask = mask;
        }
    }

    internal static class CatalogMaskAtlas
    {
        internal const int DefaultSize = 512;
        private static readonly Lazy<IReadOnlyList<CatalogMaskAtlasEntry>> DefaultEntries = new Lazy<IReadOnlyList<CatalogMaskAtlasEntry>>(() => BuildEntries(DefaultSize));

        internal static CatalogMaskAtlasEntry Find(string identifier)
        {
            foreach (var entry in Build()) if (String.Equals(entry.Identifier, identifier, StringComparison.Ordinal)) return entry;
            throw new ArgumentException("Unknown official catalog identifier.", "identifier");
        }

        internal static IReadOnlyList<CatalogMaskAtlasEntry> Build(int size = DefaultSize)
        {
            if (size < 32 || size > 1024) throw new ArgumentOutOfRangeException("size");
            return size == DefaultSize ? DefaultEntries.Value : BuildEntries(size);
        }

        private static IReadOnlyList<CatalogMaskAtlasEntry> BuildEntries(int size)
        {
            var definitions = new List<ShapeDefinition>();
            definitions.AddRange(OfficialCatalog.CurveBasis);
            definitions.AddRange(OfficialCatalog.RoundBasis);
            var entries = new List<CatalogMaskAtlasEntry>(definitions.Count);
            for (var index = 0; index < definitions.Count; index++) entries.Add(BuildEntry(definitions[index], size));
            return new ReadOnlyCollection<CatalogMaskAtlasEntry>(entries);
        }

        internal static byte[] RenderBinaryAlpha(CatalogMaskAtlasEntry entry, ShapeState state)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (state == null || state.Shape != entry.Identifier) throw new ArgumentException("State must use the atlas identity.", "state");
            var alpha = new byte[512 * 512];
            var radians = state.AngleDegrees * Math.PI / 180;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            for (var y = 0; y < 512; y++)
            {
                for (var x = 0; x < 512; x++)
                {
                    var dx = x - state.Cx;
                    var dy = y - state.Cy;
                    var localX = cosine * dx + sine * dy;
                    var localY = -sine * dx + cosine * dy;
                    var pathX = (localX / (2 * state.Rx) + 0.5) * entry.IntrinsicWidth;
                    var pathY = (localY / (2 * state.Ry) + 0.5) * entry.IntrinsicHeight;
                    if (pathX < entry.MinX || pathX > entry.MaxX || pathY < entry.MinY || pathY > entry.MaxY) continue;
                    var atlasX = Math.Min(entry.Size - 1, Math.Max(0, (int)Math.Floor((pathX - entry.MinX) / (entry.MaxX - entry.MinX) * entry.Size)));
                    var atlasY = Math.Min(entry.Size - 1, Math.Max(0, (int)Math.Floor((pathY - entry.MinY) / (entry.MaxY - entry.MinY) * entry.Size)));
                    if (entry.Mask[atlasY * entry.Size + atlasX] >= 128) alpha[y * 512 + x] = 255;
                }
            }
            return alpha;
        }

        private static CatalogMaskAtlasEntry BuildEntry(ShapeDefinition definition, int size)
        {
            using (var path = SKPath.ParseSvgPathData(definition.Path))
            {
                var bounds = path.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException(definition.Slug + " has empty path bounds.");
                var info = new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul);
                using (var bitmap = new SKBitmap(info))
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill })
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.SetMatrix(new SKMatrix
                    {
                        ScaleX = size / bounds.Width,
                        ScaleY = size / bounds.Height,
                        TransX = -bounds.Left * size / bounds.Width,
                        TransY = -bounds.Top * size / bounds.Height,
                        Persp2 = 1
                    });
                    canvas.DrawPath(path, paint);
                    canvas.Flush();
                    var mask = new byte[size * size];
                    Marshal.Copy(bitmap.GetPixels(), mask, 0, mask.Length);
                    return new CatalogMaskAtlasEntry(OfficialCatalog.ShapeIdentifier(definition.Slug), definition, bounds, size, mask);
                }
            }
        }
    }
}
