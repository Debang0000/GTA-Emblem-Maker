using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GTAEmblemMaker.Core
{
    internal sealed class WeightMap
    {
        internal string Id { get; private set; }
        internal byte[] Q8 { get; private set; }

        internal WeightMap(string id, byte[] q8)
        {
            Id = id;
            Q8 = q8;
        }
    }

    internal sealed class StrokeGuide
    {
        internal byte[] SaliencyQ8 { get; private set; }
        internal byte[] TangentQ8 { get; private set; }

        internal StrokeGuide(byte[] saliencyQ8, byte[] tangentQ8)
        {
            SaliencyQ8 = saliencyQ8;
            TangentQ8 = tangentQ8;
        }
    }

    internal sealed class MultiScaleStrokeGuide
    {
        internal byte[] DetailSaliencyQ8 { get; private set; }
        internal byte[] ContourSaliencyQ8 { get; private set; }
        internal byte[] TangentQ8 { get; private set; }

        internal MultiScaleStrokeGuide(byte[] detailSaliencyQ8, byte[] contourSaliencyQ8, byte[] tangentQ8)
        {
            DetailSaliencyQ8 = detailSaliencyQ8;
            ContourSaliencyQ8 = contourSaliencyQ8;
            TangentQ8 = tangentQ8;
        }
    }

    internal sealed class StructuralGuide
    {
        internal byte[] DistanceQ8 { get; private set; }
        internal byte[] TangentQ8 { get; private set; }

        internal StructuralGuide(byte[] distanceQ8, byte[] tangentQ8)
        {
            DistanceQ8 = distanceQ8;
            TangentQ8 = tangentQ8;
        }
    }

    internal sealed class StrokeEdgeData
    {
        internal double[] Thinned { get; private set; }
        internal double[] Gx { get; private set; }
        internal double[] Gy { get; private set; }

        internal StrokeEdgeData(double[] thinned, double[] gx, double[] gy)
        {
            Thinned = thinned;
            Gx = gx;
            Gy = gy;
        }
    }

    internal struct RasterLine
    {
        internal int Y { get; private set; }
        internal int X1 { get; private set; }
        internal int X2 { get; private set; }
        internal ushort Alpha { get; private set; }

        internal RasterLine(int y, int x1, int x2, ushort alpha)
        {
            Y = y;
            X1 = x1;
            X2 = x2;
            Alpha = alpha;
        }
    }

    internal static class FitMath
    {
        private const double EdgeEpsilon = 1e-7;
        private const double CoordinateScale = 1000000;

        internal static FitStage ResolveStage(FitProfile profile, string stageId)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            for (var index = 0; index < profile.Stages.Count; index++)
            {
                if (profile.Stages[index].Id == stageId) return profile.Stages[index];
            }
            throw new ArgumentException("Unknown fitting stage.", "stageId");
        }

        internal static IReadOnlyList<string> ShapeChoicesForLayer(FitStage stage, int layer)
        {
            if (stage == null) throw new ArgumentNullException("stage");
            if (layer < 1) throw new ArgumentOutOfRangeException("layer");
            ShapeChoice current = null;
            for (var index = 0; index < stage.ShapeChoicesByLayer.Count; index++)
            {
                if (stage.ShapeChoicesByLayer[index].FromLayer <= layer) current = stage.ShapeChoicesByLayer[index];
            }
            if (current == null) throw new ArgumentException("No shape schedule applies to the layer.", "layer");
            return current.Shapes;
        }

        internal static WeightMapChoice WeightMapChoiceForLayer(FitStage stage, bool transparent, int layer)
        {
            if (stage == null) throw new ArgumentNullException("stage");
            if (layer < 1) throw new ArgumentOutOfRangeException("layer");
            var schedule = transparent ? stage.TransparentWeightMapSchedule : stage.OpaqueWeightMapSchedule;
            WeightMapChoice current = null;
            for (var index = 0; index < schedule.Count; index++)
            {
                if (schedule[index].FromLayer <= layer) current = schedule[index];
            }
            if (current == null) throw new ArgumentException("No weight map schedule applies to the layer.", "layer");
            return current;
        }

        internal static IReadOnlyDictionary<string, WeightMap> BuildWeightMaps(byte[] targetRgba, int width, int height)
        {
            ValidateImage(targetRgba, width, height, "targetRgba");
            var edge = NormalizedSobelEdge(targetRgba, width, height);
            var softEdge = SoftenMap(edge, width, height);
            var laplacian = NormalizedLaplacianHighpass(targetRgba, width, height);
            var softLaplacian = SoftenMap(laplacian, width, height);
            var count = checked(width * height);
            var uniform = new double[count];
            var alphaProtect = new double[count];
            var edgePlusLaplacian = new double[count];
            var combined = new double[count];
            for (var pixel = 0; pixel < count; pixel++)
            {
                uniform[pixel] = 1;
                var alpha = targetRgba[pixel * 4 + 3] / 255.0;
                var transparent = 1 - alpha;
                alphaProtect[pixel] = 1 + 31 * transparent * transparent;
                edgePlusLaplacian[pixel] = Math.Min(3, 1 + softEdge[pixel] + softLaplacian[pixel]);
                combined[pixel] = Math.Max(edgePlusLaplacian[pixel], alphaProtect[pixel]);
            }

            var maps = new Dictionary<string, WeightMap>(StringComparer.Ordinal)
            {
                { "uniform", MakeWeightMap("uniform", uniform) },
                { "alpha-protect", MakeWeightMap("alpha-protect", alphaProtect) },
                { "edge-plus-laplacian", MakeWeightMap("edge-plus-laplacian", edgePlusLaplacian) },
                { "edge-plus-laplacian-alpha-protect", MakeWeightMap("edge-plus-laplacian-alpha-protect", combined) }
            };
            return new ReadOnlyDictionary<string, WeightMap>(maps);
        }

        internal static bool IsStrokeLayer(FitStage stage, int layer)
        {
            if (stage == null) throw new ArgumentNullException("stage");
            if (layer < 1) throw new ArgumentOutOfRangeException("layer");
            var search = stage.StrokeSearch;
            if (search == null || layer < search.FromLayer) return false;
            if (layer >= search.FinalFromLayer) return (layer - search.FinalFromLayer) % search.FinalEvery == 0;
            return (layer - search.FromLayer) % search.Every == 0;
        }

        internal static bool IsContourStrokeLayer(FitStage stage, int layer)
        {
            if (!IsStrokeLayer(stage, layer)) return false;
            var search = stage.StrokeSearch;
            if (!search.IsMultiScale) return false;
            if (layer >= search.FinalFromLayer) return (layer - search.FinalFromLayer) % search.FinalContourEvery == 0;
            return (layer - search.FromLayer) % search.ContourEvery == 0;
        }

        internal static StrokeGuide BuildStrokeGuide(byte[] targetRgba, int width, int height)
        {
            ValidateImage(targetRgba, width, height, "targetRgba");
            var edge = BuildStrokeEdgeData(targetRgba, width, height);
            return new StrokeGuide(QuantizeSaliency(edge.Thinned), QuantizeTangent(edge.Gx, edge.Gy));
        }

        internal static MultiScaleStrokeGuide BuildMultiScaleStrokeGuide(byte[] targetRgba, int width, int height, int tileSize)
        {
            ValidateImage(targetRgba, width, height, "targetRgba");
            if (tileSize < 1) throw new ArgumentOutOfRangeException("tileSize");
            var edge = BuildStrokeEdgeData(targetRgba, width, height);
            var detail = BuildDetailSaliency(edge.Thinned, width, height, tileSize);
            return new MultiScaleStrokeGuide(QuantizeSaliency(detail), QuantizeSaliency(edge.Thinned), QuantizeTangent(edge.Gx, edge.Gy));
        }

        internal static StructuralGuide BuildStructuralGuide(byte[] targetRgba, int width, int height, int distanceLimit)
        {
            ValidateImage(targetRgba, width, height, "targetRgba");
            if (distanceLimit < 1 || distanceLimit > 32) throw new ArgumentOutOfRangeException("distanceLimit");
            var edge = BuildStrokeEdgeData(targetRgba, width, height);
            var sourceTangent = QuantizeTangent(edge.Gx, edge.Gy);
            var maximumSquared = checked(distanceLimit * distanceLimit);
            var squared = new int[edge.Thinned.Length];
            var nearestTangent = new ushort[edge.Thinned.Length];
            for (var index = 0; index < squared.Length; index++) squared[index] = maximumSquared;

            for (var edgeY = 1; edgeY < height - 1; edgeY++)
            {
                for (var edgeX = 1; edgeX < width - 1; edgeX++)
                {
                    var edgeIndex = edgeY * width + edgeX;
                    if (edge.Thinned[edgeIndex] <= EdgeEpsilon) continue;
                    var tangentOffset = edgeIndex * 2;
                    var tangent = (ushort)(sourceTangent[tangentOffset] | sourceTangent[tangentOffset + 1] << 8);
                    var minY = Math.Max(0, edgeY - distanceLimit);
                    var maxY = Math.Min(height - 1, edgeY + distanceLimit);
                    var minX = Math.Max(0, edgeX - distanceLimit);
                    var maxX = Math.Min(width - 1, edgeX + distanceLimit);
                    for (var y = minY; y <= maxY; y++)
                    {
                        var dy = y - edgeY;
                        for (var x = minX; x <= maxX; x++)
                        {
                            var dx = x - edgeX;
                            var distanceSquared = dx * dx + dy * dy;
                            var index = y * width + x;
                            if (distanceSquared >= squared[index]) continue;
                            squared[index] = distanceSquared;
                            nearestTangent[index] = tangent;
                        }
                    }
                }
            }

            var distanceQ8 = new byte[checked(squared.Length * 2)];
            var tangentQ8 = new byte[distanceQ8.Length];
            for (var index = 0; index < squared.Length; index++)
            {
                WriteUInt16(distanceQ8, index * 2, (int)Math.Round(Math.Sqrt(squared[index]) * 256));
                WriteUInt16(tangentQ8, index * 2, nearestTangent[index]);
            }
            return new StructuralGuide(distanceQ8, tangentQ8);
        }

        private static double[] BuildDetailSaliency(double[] contour, int width, int height, int tileSize)
        {
            var detail = (double[])contour.Clone();
            for (var tileY = 0; tileY < height; tileY += tileSize)
            {
                for (var tileX = 0; tileX < width; tileX += tileSize)
                {
                    var maxY = Math.Min(height, tileY + tileSize);
                    var maxX = Math.Min(width, tileX + tileSize);
                    var total = 0.0;
                    for (var y = tileY; y < maxY; y++)
                    {
                        for (var x = tileX; x < maxX; x++) total += detail[y * width + x];
                    }
                    if (total == 0) continue;
                    var divisor = Math.Sqrt(total);
                    for (var y = tileY; y < maxY; y++)
                    {
                        for (var x = tileX; x < maxX; x++) detail[y * width + x] /= divisor;
                    }
                }
            }
            return detail;
        }

        private static StrokeEdgeData BuildStrokeEdgeData(byte[] targetRgba, int width, int height)
        {
            var gray = StrokeSignal(targetRgba, width, height);
            var gx = new double[gray.Length];
            var gy = new double[gray.Length];
            var magnitude = new double[gray.Length];
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    gx[index] = -gray[index - width - 1] + gray[index - width + 1] - 2 * gray[index - 1] + 2 * gray[index + 1] - gray[index + width - 1] + gray[index + width + 1];
                    gy[index] = -gray[index - width - 1] - 2 * gray[index - width] - gray[index - width + 1] + gray[index + width - 1] + 2 * gray[index + width] + gray[index + width + 1];
                    magnitude[index] = Math.Sqrt(gx[index] * gx[index] + gy[index] * gy[index]);
                }
            }

            var thinned = new double[gray.Length];
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    var angle = Math.Atan2(gy[index], gx[index]) * 180 / Math.PI;
                    if (angle < 0) angle += 180;
                    int first;
                    int second;
                    if (angle < 22.5 || angle >= 157.5) { first = index - 1; second = index + 1; }
                    else if (angle < 67.5) { first = index - width + 1; second = index + width - 1; }
                    else if (angle < 112.5) { first = index - width; second = index + width; }
                    else { first = index - width - 1; second = index + width + 1; }
                    if (magnitude[index] < magnitude[first] || magnitude[index] < magnitude[second]) continue;
                    thinned[index] = magnitude[index];
                }
            }
            return new StrokeEdgeData(thinned, gx, gy);
        }

        private static byte[] QuantizeSaliency(double[] values)
        {
            var maximum = 0.0;
            for (var index = 0; index < values.Length; index++) maximum = Math.Max(maximum, values[index]);
            var result = new byte[checked(values.Length * 2)];
            for (var index = 0; index < values.Length; index++)
            {
                var encoded = maximum == 0 ? 0 : (int)Math.Round(values[index] / maximum * UInt16.MaxValue);
                WriteUInt16(result, index * 2, encoded);
            }
            return result;
        }

        private static byte[] QuantizeTangent(double[] gx, double[] gy)
        {
            var result = new byte[checked(gx.Length * 2)];
            for (var index = 0; index < gx.Length; index++)
            {
                var tangentDegrees = (Math.Atan2(gy[index], gx[index]) * 180 / Math.PI + 90) % 180;
                if (tangentDegrees < 0) tangentDegrees += 180;
                WriteUInt16(result, index * 2, (int)Math.Round(tangentDegrees * 256));
            }
            return result;
        }

        internal static byte[] CreateInitialCurrent(byte[] targetRgba, bool transparent)
        {
            if (targetRgba == null) throw new ArgumentNullException("targetRgba");
            if (targetRgba.Length == 0 || targetRgba.Length % 4 != 0) throw new ArgumentException("RGBA data must contain complete pixels.", "targetRgba");
            var current = new byte[targetRgba.Length];
            if (transparent) return current;
            long red = 0;
            long green = 0;
            long blue = 0;
            for (var offset = 0; offset < targetRgba.Length; offset += 4)
            {
                red += targetRgba[offset];
                green += targetRgba[offset + 1];
                blue += targetRgba[offset + 2];
            }
            var pixels = targetRgba.Length / 4;
            var averageRed = (byte)(red / pixels);
            var averageGreen = (byte)(green / pixels);
            var averageBlue = (byte)(blue / pixels);
            for (var offset = 0; offset < current.Length; offset += 4)
            {
                current[offset] = averageRed;
                current[offset + 1] = averageGreen;
                current[offset + 2] = averageBlue;
                current[offset + 3] = 255;
            }
            return current;
        }

        internal static long FullError(byte[] targetRgba, byte[] currentRgba)
        {
            RequireEqualRgba(targetRgba, currentRgba);
            long total = 0;
            for (var offset = 0; offset < targetRgba.Length; offset++)
            {
                var difference = targetRgba[offset] - currentRgba[offset];
                total += difference * difference;
            }
            return total;
        }

        internal static long WeightedFullError(byte[] targetRgba, byte[] currentRgba, byte[] weightsQ8)
        {
            RequireEqualRgba(targetRgba, currentRgba);
            if (weightsQ8 == null) throw new ArgumentNullException("weightsQ8");
            if (weightsQ8.Length != targetRgba.Length / 2) throw new ArgumentException("Weight map length does not match the image.", "weightsQ8");
            long total = 0;
            for (int offset = 0, pixel = 0; offset < targetRgba.Length; offset += 4, pixel++)
            {
                long error = 0;
                for (var channel = 0; channel < 4; channel++)
                {
                    var difference = targetRgba[offset + channel] - currentRgba[offset + channel];
                    error += difference * difference;
                }
                var weightOffset = pixel * 2;
                var weight = weightsQ8[weightOffset] | weightsQ8[weightOffset + 1] << 8;
                total += error * weight / 256;
            }
            return total;
        }

        internal static double EnergyFromTotal(long total, int width, int height)
        {
            if (total < 0) throw new ArgumentOutOfRangeException("total");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");
            return Math.Sqrt(total / ((double)width * height * 4)) / 255;
        }

        internal static IReadOnlyList<RasterLine> Rasterize(FitCandidate candidate, int width, int height)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");
            if (candidate.Kind == CandidateShapeKind.OfficialCatalog) return RasterizeCatalog(candidate, width, height);
            var lines = new List<RasterLine>();
            var theta = candidate.AngleDegrees * Math.PI / 180;
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            var extentX = (int)Math.Ceiling(Math.Abs(candidate.Rx * cos) + Math.Abs(candidate.Ry * sin)) + 1;
            var extentY = (int)Math.Ceiling(Math.Abs(candidate.Rx * sin) + Math.Abs(candidate.Ry * cos)) + 1;
            var minX = Math.Max(0, candidate.Cx - extentX);
            var maxX = Math.Min(width - 1, candidate.Cx + extentX);
            var minY = Math.Max(0, candidate.Cy - extentY);
            var maxY = Math.Min(height - 1, candidate.Cy + extentY);
            for (var y = minY; y <= maxY; y++)
            {
                var runStart = -1;
                for (var x = minX; x <= maxX; x++)
                {
                    bool contains;
                    if (candidate.Kind == CandidateShapeKind.RotatedTriangle) contains = RotatedTriangleContains(candidate, x, y, cos, sin);
                    else if (candidate.Kind == CandidateShapeKind.RotatedRectangle || candidate.Kind == CandidateShapeKind.LineRectangle) contains = RotatedRectangleContains(candidate, x, y, cos, sin);
                    else if (candidate.Kind == CandidateShapeKind.RotatedEllipse) contains = RotatedEllipseContains(candidate, x, y, cos, sin);
                    else throw new ArgumentOutOfRangeException("candidate");
                    if (contains && runStart < 0) runStart = x;
                    if (!contains && runStart >= 0)
                    {
                        lines.Add(new RasterLine(y, runStart, x - 1, UInt16.MaxValue));
                        runStart = -1;
                    }
                }
                if (runStart >= 0) lines.Add(new RasterLine(y, runStart, maxX, UInt16.MaxValue));
            }
            return lines;
        }

        internal static IReadOnlyList<RasterLine> RasterizeCompatibilityCandidate(FitCandidate candidate, int width, int height)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");
            return RasterizeCompatibility(candidate, width, height);
        }

        private static IReadOnlyList<RasterLine> RasterizeCatalog(FitCandidate candidate, int width, int height)
        {
            if (width != 512 || height != 512) throw new ArgumentException("Official catalog rasterization requires the Rockstar 512x512 canvas.");
            var entry = CatalogMaskAtlas.Find(candidate.Shape);
            var state = CandidateGenerator.ToShapeState(candidate);
            var alpha = CatalogMaskAtlas.RenderBinaryAlpha(entry, state);
            var lines = new List<RasterLine>();
            for (var y = 0; y < height; y++)
            {
                var runStart = -1;
                for (var x = 0; x < width; x++)
                {
                    var filled = alpha[y * width + x] >= 128;
                    if (filled && runStart < 0) runStart = x;
                    if (!filled && runStart >= 0)
                    {
                        lines.Add(new RasterLine(y, runStart, x - 1, UInt16.MaxValue));
                        runStart = -1;
                    }
                }
                if (runStart >= 0) lines.Add(new RasterLine(y, runStart, width - 1, UInt16.MaxValue));
            }
            return lines;
        }

        internal static void ApplyCandidate(byte[] currentRgba, int width, FitCandidate candidate)
        {
            if (currentRgba == null) throw new ArgumentNullException("currentRgba");
            if (width <= 0 || currentRgba.Length == 0 || currentRgba.Length % (width * 4) != 0) throw new ArgumentException("Current RGBA dimensions are invalid.", "currentRgba");
            ApplyCandidateCore(null, currentRgba, width, candidate, null, 0, false);
        }

        internal static void ApplyCompatibilityCandidate(byte[] currentRgba, int width, FitCandidate candidate)
        {
            if (currentRgba == null) throw new ArgumentNullException("currentRgba");
            if (width <= 0 || currentRgba.Length == 0 || currentRgba.Length % (width * 4) != 0) throw new ArgumentException("Current RGBA dimensions are invalid.", "currentRgba");
            ApplyCandidateCore(null, currentRgba, width, candidate, null, 0, true);
        }

        internal static long ApplyCandidateAndUpdateError(byte[] targetRgba, byte[] currentRgba, int width, FitCandidate candidate, byte[] weightsQ8, long baseTotalError)
        {
            RequireEqualRgba(targetRgba, currentRgba);
            if (width <= 0 || currentRgba.Length % (width * 4) != 0) throw new ArgumentException("Current RGBA dimensions are invalid.", "currentRgba");
            if (weightsQ8 == null) throw new ArgumentNullException("weightsQ8");
            if (weightsQ8.Length != currentRgba.Length / 2) throw new ArgumentException("Weight map length does not match the image.", "weightsQ8");
            if (baseTotalError < 0) throw new ArgumentOutOfRangeException("baseTotalError");
            return ApplyCandidateCore(targetRgba, currentRgba, width, candidate, weightsQ8, baseTotalError, false);
        }

        internal static long ApplyCompatibilityCandidateAndUpdateError(byte[] targetRgba, byte[] currentRgba, int width, FitCandidate candidate, byte[] weightsQ8, long baseTotalError)
        {
            RequireEqualRgba(targetRgba, currentRgba);
            if (width <= 0 || currentRgba.Length % (width * 4) != 0) throw new ArgumentException("Current RGBA dimensions are invalid.", "currentRgba");
            if (weightsQ8 == null) throw new ArgumentNullException("weightsQ8");
            if (weightsQ8.Length != currentRgba.Length / 2) throw new ArgumentException("Weight map length does not match the image.", "weightsQ8");
            if (baseTotalError < 0) throw new ArgumentOutOfRangeException("baseTotalError");
            return ApplyCandidateCore(targetRgba, currentRgba, width, candidate, weightsQ8, baseTotalError, true);
        }

        internal static long EvaluateCandidateError(byte[] targetRgba, byte[] currentRgba, int width, FitCandidate candidate, byte[] weightsQ8, long baseTotalError)
        {
            RequireEqualRgba(targetRgba, currentRgba);
            if (width <= 0 || currentRgba.Length % (width * 4) != 0) throw new ArgumentException("Current RGBA dimensions are invalid.", "currentRgba");
            if (weightsQ8 == null) throw new ArgumentNullException("weightsQ8");
            if (weightsQ8.Length != currentRgba.Length / 2) throw new ArgumentException("Weight map length does not match the image.", "weightsQ8");
            if (baseTotalError < 0) throw new ArgumentOutOfRangeException("baseTotalError");

            var lines = Rasterize(candidate, width, currentRgba.Length / (width * 4));
            const long maximum = UInt16.MaxValue;
            var sourceAlpha = candidate.Alpha * 0x101L;
            var sourceRed = candidate.Red * 0x101L * candidate.Alpha / 255;
            var sourceGreen = candidate.Green * 0x101L * candidate.Alpha / 255;
            var sourceBlue = candidate.Blue * 0x101L * candidate.Alpha / 255;
            var total = baseTotalError;
            foreach (var line in lines)
            {
                var blend = (maximum - sourceAlpha * line.Alpha / maximum) * 0x101L;
                for (var x = line.X1; x <= line.X2; x++)
                {
                    var offset = (line.Y * width + x) * 4;
                    total -= WeightedPixelError(targetRgba, currentRgba, weightsQ8, offset);
                    total += WeightedPixelError(
                        targetRgba,
                        weightsQ8,
                        offset,
                        Blend(currentRgba[offset], blend, sourceRed, line.Alpha),
                        Blend(currentRgba[offset + 1], blend, sourceGreen, line.Alpha),
                        Blend(currentRgba[offset + 2], blend, sourceBlue, line.Alpha),
                        Blend(currentRgba[offset + 3], blend, sourceAlpha, line.Alpha));
                }
            }
            return total;
        }

        internal static byte[] CropRgba(byte[] rgba, int sourceWidth, int x, int y, int width, int height)
        {
            if (rgba == null) throw new ArgumentNullException("rgba");
            if (sourceWidth <= 0 || rgba.Length == 0 || rgba.Length % (sourceWidth * 4) != 0) throw new ArgumentException("RGBA source dimensions are invalid.", "rgba");
            var sourceHeight = rgba.Length / (sourceWidth * 4);
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > sourceWidth || y + height > sourceHeight) throw new ArgumentOutOfRangeException("x");
            var crop = new byte[checked(width * height * 4)];
            for (var row = 0; row < height; row++) Buffer.BlockCopy(rgba, ((y + row) * sourceWidth + x) * 4, crop, row * width * 4, width * 4);
            return crop;
        }

        internal static byte[] CropAfterCandidate(byte[] currentRgba, int sourceWidth, FitCandidate candidate, IReadOnlyList<RasterLine> lines, int x, int y, int width, int height)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (lines == null) throw new ArgumentNullException("lines");
            var crop = CropRgba(currentRgba, sourceWidth, x, y, width, height);
            const long maximum = UInt16.MaxValue;
            var sourceAlpha = candidate.Alpha * 0x101L;
            var sourceRed = candidate.Red * 0x101L * candidate.Alpha / 255;
            var sourceGreen = candidate.Green * 0x101L * candidate.Alpha / 255;
            var sourceBlue = candidate.Blue * 0x101L * candidate.Alpha / 255;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.Y < y || line.Y >= y + height) continue;
                var first = Math.Max(line.X1, x);
                var last = Math.Min(line.X2, x + width - 1);
                if (first > last) continue;
                var blend = (maximum - sourceAlpha * line.Alpha / maximum) * 0x101L;
                for (var sourceX = first; sourceX <= last; sourceX++)
                {
                    var offset = ((line.Y - y) * width + sourceX - x) * 4;
                    crop[offset] = Blend(crop[offset], blend, sourceRed, line.Alpha);
                    crop[offset + 1] = Blend(crop[offset + 1], blend, sourceGreen, line.Alpha);
                    crop[offset + 2] = Blend(crop[offset + 2], blend, sourceBlue, line.Alpha);
                    crop[offset + 3] = Blend(crop[offset + 3], blend, sourceAlpha, line.Alpha);
                }
            }
            return crop;
        }

        private static long ApplyCandidateCore(byte[] targetRgba, byte[] currentRgba, int width, FitCandidate candidate, byte[] weightsQ8, long baseTotalError, bool compatibilityGeometry)
        {
            var height = currentRgba.Length / (width * 4);
            var lines = compatibilityGeometry ? RasterizeCompatibility(candidate, width, height) : Rasterize(candidate, width, height);
            const long maximum = UInt16.MaxValue;
            var sourceAlpha = candidate.Alpha * 0x101L;
            var sourceRed = candidate.Red * 0x101L * candidate.Alpha / 255;
            var sourceGreen = candidate.Green * 0x101L * candidate.Alpha / 255;
            var sourceBlue = candidate.Blue * 0x101L * candidate.Alpha / 255;
            var total = baseTotalError;
            foreach (var line in lines)
            {
                var blend = (maximum - sourceAlpha * line.Alpha / maximum) * 0x101L;
                for (var x = line.X1; x <= line.X2; x++)
                {
                    var offset = (line.Y * width + x) * 4;
                    if (targetRgba != null) total -= WeightedPixelError(targetRgba, currentRgba, weightsQ8, offset);
                    currentRgba[offset] = Blend(currentRgba[offset], blend, sourceRed, line.Alpha);
                    currentRgba[offset + 1] = Blend(currentRgba[offset + 1], blend, sourceGreen, line.Alpha);
                    currentRgba[offset + 2] = Blend(currentRgba[offset + 2], blend, sourceBlue, line.Alpha);
                    currentRgba[offset + 3] = Blend(currentRgba[offset + 3], blend, sourceAlpha, line.Alpha);
                    if (targetRgba != null) total += WeightedPixelError(targetRgba, currentRgba, weightsQ8, offset);
                }
            }
            return total;
        }

        private static IReadOnlyList<RasterLine> RasterizeCompatibility(FitCandidate candidate, int width, int height)
        {
            if (candidate.Kind == CandidateShapeKind.OfficialCatalog) return RasterizeCatalog(candidate, width, height);
            var lines = new List<RasterLine>();
            var theta = candidate.AngleDegrees * Math.PI / 180;
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            var extentX = (int)Math.Ceiling(Math.Abs(candidate.Rx * cos) + Math.Abs(candidate.Ry * sin)) + 1;
            var extentY = (int)Math.Ceiling(Math.Abs(candidate.Rx * sin) + Math.Abs(candidate.Ry * cos)) + 1;
            var minX = Math.Max(0, candidate.Cx - extentX);
            var maxX = Math.Min(width - 1, candidate.Cx + extentX);
            var minY = Math.Max(0, candidate.Cy - extentY);
            var maxY = Math.Min(height - 1, candidate.Cy + extentY);
            for (var y = minY; y <= maxY; y++)
            {
                var runStart = -1;
                for (var x = minX; x <= maxX; x++)
                {
                    bool contains;
                    if (candidate.Kind == CandidateShapeKind.RotatedTriangle) contains = CompatibilityTriangleContains(candidate, x, y, cos, sin);
                    else if (candidate.Kind == CandidateShapeKind.RotatedRectangle || candidate.Kind == CandidateShapeKind.LineRectangle) contains = CompatibilityRectangleContains(candidate, x, y, cos, sin);
                    else if (candidate.Kind == CandidateShapeKind.RotatedEllipse) contains = CompatibilityEllipseContains(candidate, x, y, cos, sin);
                    else throw new ArgumentOutOfRangeException("candidate");
                    if (contains && runStart < 0) runStart = x;
                    if (!contains && runStart >= 0)
                    {
                        lines.Add(new RasterLine(y, runStart, x - 1, UInt16.MaxValue));
                        runStart = -1;
                    }
                }
                if (runStart >= 0) lines.Add(new RasterLine(y, runStart, maxX, UInt16.MaxValue));
            }
            return lines;
        }

        private static byte Blend(byte current, long blend, long source, ushort coverage)
        {
            return (byte)(((current * blend + source * coverage) / UInt16.MaxValue >> 8) & 255);
        }

        private static long WeightedPixelError(byte[] target, byte[] current, byte[] weights, int offset)
        {
            long error = 0;
            for (var channel = 0; channel < 4; channel++)
            {
                var difference = target[offset + channel] - current[offset + channel];
                error += difference * difference;
            }
            var weightOffset = offset / 2;
            var weight = weights[weightOffset] | weights[weightOffset + 1] << 8;
            return error * weight / 256;
        }

        private static long WeightedPixelError(byte[] target, byte[] weights, int offset, byte red, byte green, byte blue, byte alpha)
        {
            var redDifference = target[offset] - red;
            var greenDifference = target[offset + 1] - green;
            var blueDifference = target[offset + 2] - blue;
            var alphaDifference = target[offset + 3] - alpha;
            var error = redDifference * redDifference + greenDifference * greenDifference + blueDifference * blueDifference + alphaDifference * alphaDifference;
            var weightOffset = offset / 2;
            var weight = weights[weightOffset] | weights[weightOffset + 1] << 8;
            return (long)error * weight / 256;
        }

        private static bool RotatedEllipseContains(FitCandidate candidate, int x, int y, double cos, double sin)
        {
            double localX;
            double localY;
            LocalCoordinates(candidate, x, y, cos, sin, out localX, out localY);
            var ry = Math.Max(1, candidate.Ry);
            var yEpsilon = Math.Abs(sin) > 1e-12 ? EdgeEpsilon : 0;
            if (Math.Abs(localY) >= ry + yEpsilon) return false;
            var aspect = candidate.Rx / (double)ry;
            var span = (int)(Math.Sqrt(ry * ry - localY * localY) * aspect);
            return Math.Abs(localX) <= span + EdgeEpsilon;
        }

        private static bool RotatedRectangleContains(FitCandidate candidate, int x, int y, double cos, double sin)
        {
            double localX;
            double localY;
            LocalCoordinates(candidate, x, y, cos, sin, out localX, out localY);
            return Math.Abs(localX) <= candidate.Rx + EdgeEpsilon && Math.Abs(localY) <= candidate.Ry + EdgeEpsilon;
        }

        private static bool RotatedTriangleContains(FitCandidate candidate, int x, int y, double cos, double sin)
        {
            double localX;
            double localY;
            LocalCoordinates(candidate, x, y, cos, sin, out localX, out localY);
            var ry = Math.Max(1, candidate.Ry);
            if (localY < -ry - EdgeEpsilon || localY > ry + EdgeEpsilon) return false;
            var position = (localY + ry) / (2 * ry);
            return Math.Abs(localX) <= candidate.Rx * position + EdgeEpsilon;
        }

        private static void LocalCoordinates(FitCandidate candidate, int x, int y, double cos, double sin, out double localX, out double localY)
        {
            var dx = x + 0.5 - candidate.Cx;
            var dy = y + 0.5 - candidate.Cy;
            localX = JsRound((cos * dx + sin * dy) * CoordinateScale) / CoordinateScale;
            localY = JsRound((-sin * dx + cos * dy) * CoordinateScale) / CoordinateScale;
        }

        private static bool CompatibilityEllipseContains(FitCandidate candidate, int x, int y, double cos, double sin)
        {
            double localX;
            double localY;
            CompatibilityLocalCoordinates(candidate, x, y, cos, sin, out localX, out localY);
            var ry = Math.Max(1, candidate.Ry);
            var yEpsilon = Math.Abs(sin) > 1e-12 ? EdgeEpsilon : 0;
            if (Math.Abs(localY) >= ry + yEpsilon) return false;
            var aspect = candidate.Rx / (double)ry;
            var span = (int)Math.Floor(Math.Sqrt(ry * ry - localY * localY) * aspect);
            return Math.Abs(localX) <= span + EdgeEpsilon;
        }

        private static bool CompatibilityRectangleContains(FitCandidate candidate, int x, int y, double cos, double sin)
        {
            double localX;
            double localY;
            CompatibilityLocalCoordinates(candidate, x, y, cos, sin, out localX, out localY);
            return Math.Abs(localX) <= candidate.Rx + EdgeEpsilon && Math.Abs(localY) <= candidate.Ry + EdgeEpsilon;
        }

        private static bool CompatibilityTriangleContains(FitCandidate candidate, int x, int y, double cos, double sin)
        {
            double localX;
            double localY;
            CompatibilityLocalCoordinates(candidate, x, y, cos, sin, out localX, out localY);
            var ry = Math.Max(1, candidate.Ry);
            if (localY < -ry - EdgeEpsilon || localY > ry + EdgeEpsilon) return false;
            var position = (localY + ry) / (2 * ry);
            return Math.Abs(localX) <= candidate.Rx * position + EdgeEpsilon;
        }

        private static void CompatibilityLocalCoordinates(FitCandidate candidate, int x, int y, double cos, double sin, out double localX, out double localY)
        {
            var dx = x - candidate.Cx;
            var dy = y - candidate.Cy;
            localX = JsRound((cos * dx + sin * dy) * CoordinateScale) / CoordinateScale;
            localY = JsRound((-sin * dx + cos * dy) * CoordinateScale) / CoordinateScale;
        }

        private static WeightMap MakeWeightMap(string id, double[] values)
        {
            var q8 = new byte[values.Length * 2];
            for (var index = 0; index < values.Length; index++)
            {
                var value = Math.Max(1.0 / 256, values[index]);
                var encoded = Math.Max(1, Math.Min(UInt16.MaxValue, (int)JsRound(value * 256)));
                q8[index * 2] = (byte)encoded;
                q8[index * 2 + 1] = (byte)(encoded >> 8);
            }
            return new WeightMap(id, q8);
        }

        private static double[] NormalizedSobelEdge(byte[] target, int width, int height)
        {
            var gray = Grayscale(target, width, height);
            var edge = new double[gray.Length];
            var maximum = 0.0;
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    var gx = -gray[index - width - 1] + gray[index - width + 1] + -2 * gray[index - 1] + 2 * gray[index + 1] + -gray[index + width - 1] + gray[index + width + 1];
                    var gy = -gray[index - width - 1] - 2 * gray[index - width] - gray[index - width + 1] + gray[index + width - 1] + 2 * gray[index + width] + gray[index + width + 1];
                    var value = Math.Sqrt(gx * gx + gy * gy);
                    edge[index] = value;
                    maximum = Math.Max(maximum, value);
                }
            }
            if (maximum > 0)
            {
                for (var index = 0; index < edge.Length; index++) edge[index] /= maximum;
            }
            return edge;
        }

        private static double[] NormalizedLaplacianHighpass(byte[] target, int width, int height)
        {
            var gray = Grayscale(target, width, height);
            var raw = new double[gray.Length];
            var nonzero = new List<double>();
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    var value = Math.Abs(gray[index] * 4 - gray[index - 1] - gray[index + 1] - gray[index - width] - gray[index + width]);
                    raw[index] = value;
                    if (value > 0) nonzero.Add(value);
                }
            }
            nonzero.Sort();
            var scale = nonzero.Count == 0 ? 1 : nonzero[Math.Min(nonzero.Count - 1, (int)Math.Floor(nonzero.Count * 0.99))];
            if (scale == 0) scale = 1;
            for (var index = 0; index < raw.Length; index++) raw[index] = Math.Min(1, raw[index] / scale);
            return raw;
        }

        private static double[] Grayscale(byte[] target, int width, int height)
        {
            var gray = new double[width * height];
            for (var index = 0; index < gray.Length; index++)
            {
                var offset = index * 4;
                gray[index] = 0.299 * target[offset] + 0.587 * target[offset + 1] + 0.114 * target[offset + 2];
            }
            return gray;
        }

        private static double[] StrokeSignal(byte[] target, int width, int height)
        {
            var signal = new double[width * height];
            for (var index = 0; index < signal.Length; index++)
            {
                var offset = index * 4;
                var luminance = 0.299 * target[offset] + 0.587 * target[offset + 1] + 0.114 * target[offset + 2];
                signal[index] = target[offset + 3] * (luminance + 255) / 255;
            }
            return signal;
        }

        private static double[] SoftenMap(double[] values, int width, int height)
        {
            var softened = new double[values.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var total = 0.0;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var xx = x + dx;
                            var yy = y + dy;
                            if (xx < 0 || xx >= width || yy < 0 || yy >= height) continue;
                            total += values[yy * width + xx];
                            count++;
                        }
                    }
                    softened[y * width + x] = total / count;
                }
            }
            return softened;
        }

        private static double JsRound(double value)
        {
            return Math.Floor(value + 0.5);
        }

        private static void WriteUInt16(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }

        private static void ValidateImage(byte[] rgba, int width, int height, string name)
        {
            if (rgba == null) throw new ArgumentNullException(name);
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");
            if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA length does not match image dimensions.", name);
        }

        private static void RequireEqualRgba(byte[] target, byte[] current)
        {
            if (target == null) throw new ArgumentNullException("targetRgba");
            if (current == null) throw new ArgumentNullException("currentRgba");
            if (target.Length == 0 || target.Length % 4 != 0 || target.Length != current.Length) throw new ArgumentException("Target and current RGBA buffers must contain equal complete pixels.");
        }
    }
}
