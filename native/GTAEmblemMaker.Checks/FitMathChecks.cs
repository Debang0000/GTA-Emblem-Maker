using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class FitMathChecks
    {
        internal static void Run(FitProfile profile)
        {
            var golden = Object(ReadGolden());
            var fixture = Fixture(Int(Object(golden["fixture"])["width"]), Int(Object(golden["fixture"])["height"]));
            Equal(String(Object(golden["fixture"])["rgbaSha256"]), Hash(fixture), "Task 6 fixture hash");

            CheckSchedules(profile);
            CheckRequests(golden, profile);
            var maps = CheckWeightsAndTotals(golden, fixture);
            CheckResponseMapping(golden);
            CheckSvgPixelCenters();
            CheckRasterAndComposite(golden, fixture, maps["uniform"]);
            CheckNumericEdges(golden);
            CheckStrokeGuide();
            CheckStructuralGuide();
        }

        private static void CheckSvgPixelCenters()
        {
            var rectangle = new FitCandidate(1, 0, CandidateShapeKind.RotatedRectangle, "rotated-rect", "rotated-rect", 3, 3, 1, 1, 255, 0, 0, 255, 0, 0, 0, 0);
            Check.Equal(4, CoverageCount(FitMath.Rasterize(rectangle, 8, 8)), "SVG pixel-center rectangle coverage");
        }

        private static void CheckStrokeGuide()
        {
            const int size = 9;
            var target = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var offset = (y * size + x) * 4;
                    var value = x < 4 ? (byte)0 : (byte)255;
                    target[offset] = value;
                    target[offset + 1] = value;
                    target[offset + 2] = value;
                    target[offset + 3] = 255;
                }
            }

            var guide = FitMath.BuildStrokeGuide(target, size, size);
            Check.Equal(size * size * 2, guide.SaliencyQ8.Length, "stroke guide saliency size");
            Check.Equal(size * size * 2, guide.TangentQ8.Length, "stroke guide tangent size");
            var strongest = 0;
            var strongestTangent = 0;
            for (var x = 2; x <= 5; x++)
            {
                var offset = (4 * size + x) * 2;
                var saliency = guide.SaliencyQ8[offset] | guide.SaliencyQ8[offset + 1] << 8;
                if (saliency <= strongest) continue;
                strongest = saliency;
                strongestTangent = guide.TangentQ8[offset] | guide.TangentQ8[offset + 1] << 8;
            }
            Check.True(strongest > 60000, "stroke guide detects hard edge");
            Check.True(Math.Abs(strongestTangent / 256.0 - 90) < 0.01, "stroke guide vertical tangent");
            Check.Equal(0, guide.SaliencyQ8[(4 * size + 1) * 2], "stroke guide flat region");

            Array.Clear(target, 0, target.Length);
            for (var y = 0; y < size; y++)
            {
                for (var x = 4; x < size; x++) target[(y * size + x) * 4 + 3] = 255;
            }
            guide = FitMath.BuildStrokeGuide(target, size, size);
            strongest = 0;
            for (var x = 2; x <= 5; x++)
            {
                var offset = (4 * size + x) * 2;
                strongest = Math.Max(strongest, guide.SaliencyQ8[offset] | guide.SaliencyQ8[offset + 1] << 8);
            }
            Check.True(strongest > 60000, "stroke guide detects transparent black edge");
            CheckMultiScaleStrokeGuide();
        }

        private static void CheckMultiScaleStrokeGuide()
        {
            const int width = 64;
            const int height = 32;
            var target = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    var longRegion = x >= 16 && x < 32;
                    var compactRegion = x >= 45 && x < 51 && y >= 13 && y < 19;
                    var value = longRegion || compactRegion ? (byte)255 : (byte)0;
                    target[offset] = value;
                    target[offset + 1] = value;
                    target[offset + 2] = value;
                    target[offset + 3] = 255;
                }
            }

            var guide = FitMath.BuildMultiScaleStrokeGuide(target, width, height, 32);
            var contourLong = SumQ8(guide.ContourSaliencyQ8, width, 0, 0, 32, 32);
            var contourCompact = SumQ8(guide.ContourSaliencyQ8, width, 32, 0, 32, 32);
            var detailLong = SumQ8(guide.DetailSaliencyQ8, width, 0, 0, 32, 32);
            var detailCompact = SumQ8(guide.DetailSaliencyQ8, width, 32, 0, 32, 32);
            Check.True(contourLong > 0 && contourCompact > 0, "multi-scale contour detects both regions");
            Check.True(detailLong > 0 && detailCompact > 0, "multi-scale detail detects both regions");
            Check.True(detailCompact / (double)detailLong > contourCompact / (double)contourLong, "multi-scale detail balances compact edge tile");
        }

        private static void CheckStructuralGuide()
        {
            const int size = 9;
            var target = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var offset = (y * size + x) * 4;
                    target[offset + 3] = x < 4 ? (byte)0 : (byte)255;
                }
            }

            var guide = FitMath.BuildStructuralGuide(target, size, size, 2);
            Check.Equal(size * size * 2, guide.DistanceQ8.Length, "structural guide distance size");
            Check.Equal(size * size * 2, guide.TangentQ8.Length, "structural guide tangent size");
            Check.Equal(0, ReadQ8(guide.DistanceQ8, 4 * size + 4), "structural guide edge distance");
            Check.Equal(256, ReadQ8(guide.DistanceQ8, 4 * size + 5), "structural guide adjacent distance");
            Check.Equal(362, ReadQ8(guide.DistanceQ8, 5), "structural guide diagonal Euclidean distance");
            Check.Equal(512, ReadQ8(guide.DistanceQ8, 4 * size + 7), "structural guide truncated distance");
            Check.Equal(90 * 256, ReadQ8(guide.TangentQ8, 4 * size + 5), "structural guide propagated tangent");

            Array.Clear(target, 0, target.Length);
            guide = FitMath.BuildStructuralGuide(target, size, size, 2);
            Check.Equal(512, ReadQ8(guide.DistanceQ8, 4 * size + 4), "structural guide empty distance");
            Check.Equal(0, ReadQ8(guide.TangentQ8, 4 * size + 4), "structural guide empty tangent");
        }

        private static int ReadQ8(byte[] values, int index)
        {
            var offset = index * 2;
            return values[offset] | values[offset + 1] << 8;
        }

        private static long SumQ8(byte[] values, int sourceWidth, int x, int y, int width, int height)
        {
            long total = 0;
            for (var yy = y; yy < y + height; yy++)
            {
                for (var xx = x; xx < x + width; xx++)
                {
                    var offset = (yy * sourceWidth + xx) * 2;
                    total += values[offset] | values[offset + 1] << 8;
                }
            }
            return total;
        }

        private static void CheckSchedules(FitProfile profile)
        {
            var stage = FitMath.ResolveStage(profile, "current-image-fit");
            Equal("rotated", FitMath.ShapeChoicesForLayer(stage, 1)[0], "layer 1 shape");
            Equal(1, FitMath.ShapeChoicesForLayer(stage, 801).Count, "perceptual layer shape count");
            Equal("rotated", FitMath.ShapeChoicesForLayer(stage, 1600)[0], "final layer shape");
            Equal("uniform", FitMath.WeightMapChoiceForLayer(stage, false, 1).WeightMapId, "opaque initial map");
            Equal("uniform", FitMath.WeightMapChoiceForLayer(stage, false, 1600).WeightMapId, "opaque final map");
            Equal("alpha-protect", FitMath.WeightMapChoiceForLayer(stage, true, 1).WeightMapId, "transparent initial map");
            Equal("alpha-protect", FitMath.WeightMapChoiceForLayer(stage, true, 1600).WeightMapId, "transparent final map");
            Check.Throws<ArgumentException>(() => FitMath.ResolveStage(profile, "missing"), "unknown stage rejection");
            Check.Throws<ArgumentOutOfRangeException>(() => FitMath.ShapeChoicesForLayer(stage, 0), "invalid shape layer rejection");
            Check.Throws<ArgumentOutOfRangeException>(() => FitMath.WeightMapChoiceForLayer(stage, false, 0), "invalid weight layer rejection");
        }

        private static void CheckRequests(Dictionary<string, object> golden, FitProfile profile)
        {
            var request = Object(golden["request"]);
            Equal(UInt(request["layer1Seed"]), CandidateGenerator.SeedForLayer(1), "layer 1 seed");
            Equal(UInt(request["layer1ShapeMask"]), CandidateGenerator.ShapeMask(new[] { "rotated" }), "layer 1 shape mask");
            Equal(UInt(request["layer501ShapeMask"]), CandidateGenerator.ShapeMask(new[] { "rotated", "rotated-triangle", "rotated-rect", "line-rect" }), "layer 501 shape mask");
            Equal(UInt(request["selectionMode"]), CandidateGenerator.SelectionMode("code-aware-proxy"), "selection mode");
            Equal(Int(request["deviceChunkRounds"]), Math.Min(16, CandidateGenerator.EarlyStopRounds), "device chunk rounds");
            Equal(4583, CandidateGenerator.CandidatesPerGroup, "candidates per group");
            Equal(16, CandidateGenerator.Multistart, "multistart");
            Equal(100, CandidateGenerator.Age, "age");
            Equal(8, CandidateGenerator.Fanout, "fanout");
            Equal(48, CandidateGenerator.EarlyStopRounds, "early stop rounds");
            Equal(5000, CandidateGenerator.MaxHillSteps, "max hill steps");
            Equal(2, profile.Stages[0].MinAxis, "minimum axis");
            Equal(128, CandidateGenerator.InitialAlpha, "initial alpha");
            Check.Throws<ArgumentException>(() => CandidateGenerator.ShapeMask(new[] { "unknown" }), "unknown shape mask rejection");
            Check.Throws<ArgumentException>(() => CandidateGenerator.SelectionMode("unknown"), "unknown selection mode rejection");
        }

        private static IReadOnlyDictionary<string, WeightMap> CheckWeightsAndTotals(Dictionary<string, object> golden, byte[] target)
        {
            var fixture = Object(golden["fixture"]);
            var width = Int(fixture["width"]);
            var height = Int(fixture["height"]);
            var maps = FitMath.BuildWeightMaps(target, width, height);
            var expectedMaps = Object(golden["weightMaps"]);
            foreach (var pair in expectedMaps)
            {
                Equal(String(Object(pair.Value)["q8Sha256"]), Hash(maps[pair.Key].Q8), pair.Key + " Q8 hash");
                Equal(pair.Key, maps[pair.Key].Id, pair.Key + " map ID");
            }
            Equal(4, maps.Count, "production weight map count");

            var opaque = FitMath.CreateInitialCurrent(target, false);
            var transparent = FitMath.CreateInitialCurrent(target, true);
            var totals = Object(golden["totals"]);
            Equal(Long(totals["opaqueInitial"]), FitMath.FullError(target, opaque), "opaque initial total");
            Equal(Long(totals["transparentInitial"]), FitMath.FullError(target, transparent), "transparent initial total");
            Equal(Long(totals["opaqueUniform"]), FitMath.WeightedFullError(target, opaque, maps["uniform"].Q8), "opaque uniform total");
            Equal(Long(totals["transparentAlphaProtect"]), FitMath.WeightedFullError(target, transparent, maps["alpha-protect"].Q8), "transparent alpha total");
            Check.Throws<ArgumentException>(() => FitMath.FullError(new byte[4], new byte[8]), "full error length rejection");
            Check.Throws<ArgumentException>(() => FitMath.WeightedFullError(new byte[4], new byte[4], new byte[4]), "weight length rejection");
            return maps;
        }

        private static void CheckResponseMapping(Dictionary<string, object> golden)
        {
            var resident = Object(golden["resident"]);
            CheckResident(Object(resident["command16Layer1"]), "rotated", "rotated", "ellipse");
            CheckResident(Object(resident["command17MixedOracle"]), "line-rect", "rotated-rect", "rectangle");

            Equal(CandidateShapeKind.RotatedEllipse, CandidateGenerator.ShapeKindFromCode(0), "ellipse kind alias");
            Equal(CandidateShapeKind.RotatedRectangle, CandidateGenerator.ShapeKindFromCode(1), "rectangle kind alias");
            Equal(CandidateShapeKind.RotatedTriangle, CandidateGenerator.ShapeKindFromCode(2), "triangle kind alias");
            Equal(CandidateShapeKind.LineRectangle, CandidateGenerator.ShapeKindFromCode(3), "line rectangle kind alias");
            Check.Throws<ArgumentOutOfRangeException>(() => CandidateGenerator.ShapeKindFromCode(4), "shape kind rejection");
        }

        private static void CheckResident(Dictionary<string, object> row, string poolFamily, string rasterShape, string exportShape)
        {
            var candidateRow = Object(row["candidate"]);
            var scoreRow = Object(row["score"]);
            var candidate = new CudaCandidate
            {
                CandidateId = UInt(candidateRow["candidateId"]),
                Cx = Int(candidateRow["cx"]),
                Cy = Int(candidateRow["cy"]),
                Rx = Int(candidateRow["rx"]),
                Ry = Int(candidateRow["ry"]),
                Alpha = Int(candidateRow["alpha"]),
                AngleDegrees = Single(candidateRow["angleDegrees"]),
                GroupId = UInt(candidateRow["group"])
            };
            var score = new CudaScore
            {
                CandidateId = UInt(scoreRow["candidateId"]),
                Red = Byte(scoreRow["r"]),
                Green = Byte(scoreRow["g"]),
                Blue = Byte(scoreRow["b"]),
                Alpha = Byte(scoreRow["a"]),
                Energy = Double(scoreRow["energy"]),
                OldErrorDelta = ULong(scoreRow["oldErrorDelta"]),
                NewErrorDelta = ULong(scoreRow["newErrorDelta"])
            };
            var mapped = CandidateGenerator.FromResidentResult(UInt(row["selectedShapeKind"]), candidate, score);
            Equal(candidate.CandidateId, mapped.CandidateId, poolFamily + " candidate ID");
            Equal(candidate.GroupId, mapped.Group, poolFamily + " group");
            Equal(poolFamily, mapped.PoolShapeFamily, poolFamily + " pool family");
            Equal(rasterShape, mapped.Shape, poolFamily + " raster shape");
            Equal(candidate.AngleDegrees, mapped.AngleDegrees, poolFamily + " float32 angle");
            Equal(score.Energy, mapped.Energy, poolFamily + " energy");
            Equal(score.OldErrorDelta, mapped.OldErrorDelta, poolFamily + " old delta");
            Equal(score.NewErrorDelta, mapped.NewErrorDelta, poolFamily + " new delta");
            var state = CandidateGenerator.ToShapeState(mapped);
            Equal(exportShape, state.Shape, poolFamily + " export shape");
            Equal(mapped.Cx, state.Cx, poolFamily + " state cx");
            Equal(mapped.Rx, state.Rx, poolFamily + " state rx");
        }

        private static void CheckRasterAndComposite(Dictionary<string, object> golden, byte[] target, WeightMap uniform)
        {
            var row = Object(Object(golden["resident"])["command16Layer1"]);
            var candidate = CandidateFromGolden(row);
            var raster = FitMath.Rasterize(candidate, 512, 512);
            var rasterGolden = Object(golden["raster"]);
            Equal(Int(rasterGolden["lineCount"]), CoverageCount(raster), "resident raster coverage count");
            Equal(String(rasterGolden["maskQ16Sha256"]), RasterHash(raster, 512, 512), "resident raster hash");
            var current = FitMath.CreateInitialCurrent(target, false);
            FitMath.ApplyCandidate(current, 512, candidate);
            var incrementalCurrent = FitMath.CreateInitialCurrent(target, false);
            var initialTotal = FitMath.WeightedFullError(target, incrementalCurrent, uniform.Q8);
            var evaluatedTotal = FitMath.EvaluateCandidateError(target, incrementalCurrent, 512, candidate, uniform.Q8, initialTotal);
            var incrementalTotal = FitMath.ApplyCandidateAndUpdateError(target, incrementalCurrent, 512, candidate, uniform.Q8, initialTotal);
            var post = Object(golden["postApply"]);
            Equal(String(post["currentRgbaSha256"]), Hash(current), "post composite hash");
            Equal(Hash(current), Hash(incrementalCurrent), "incremental composite parity");
            Equal(incrementalTotal, evaluatedTotal, "non-mutating objective parity");
            Equal(FitMath.WeightedFullError(target, incrementalCurrent, uniform.Q8), incrementalTotal, "incremental objective parity");
            Equal(Long(post["baseTotalError"]), FitMath.WeightedFullError(target, current, uniform.Q8), "post composite total");
        }

        private static void CheckNumericEdges(Dictionary<string, object> golden)
        {
            var edge = Object(golden["numericEdgeCases"]);
            var target = new byte[] { 0, 0, 0, 255, 1, 3, 5, 255 };
            Equal(String(edge["averageInitialRgbaSha256"]), Hash(FitMath.CreateInitialCurrent(target, false)), "truncated average hash");
            Equal(Long(edge["fullError"]), FitMath.FullError(target, FitMath.CreateInitialCurrent(target, false)), "edge full error");
            Equal(Long(edge["weightedTruncationTotal"]), FitMath.WeightedFullError(new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }, new byte[8], new byte[] { 1, 1, 1, 1 }), "per-pixel weighted truncation");
            Equal(Double(edge["energy"]), FitMath.EnergyFromTotal(1, 1, 1), "normalized energy");

            var aliases = Object(edge["rasterAliases"]);
            foreach (var pair in aliases)
            {
                var candidate = new FitCandidate(1, 0, KindForShape(pair.Key), pair.Key == "line-rect" ? "rotated-rect" : pair.Key, pair.Key, 2, 3, 2, 1, 1, 2, 3, 128, 30.0000007f, 0, 0, 0);
                var lines = FitMath.Rasterize(candidate, 8, 8);
                var expected = Object(pair.Value);
                Equal(Int(expected["lineCount"]), CoverageCount(lines), pair.Key + " edge raster coverage count");
                Equal(String(expected["maskQ16Sha256"]), RasterHash(lines, 8, 8), pair.Key + " edge raster hash");
            }
            Equal(String(Object(aliases["rotated-rect"])["maskQ16Sha256"]), String(Object(aliases["line-rect"])["maskQ16Sha256"]), "rectangle raster aliases");
            Check.Throws<ArgumentOutOfRangeException>(() => FitMath.EnergyFromTotal(-1, 1, 1), "negative energy total rejection");
        }

        private static FitCandidate CandidateFromGolden(Dictionary<string, object> row)
        {
            var c = Object(row["candidate"]);
            var s = Object(row["score"]);
            var kind = CandidateGenerator.ShapeKindFromCode(UInt(row["selectedShapeKind"]));
            return new FitCandidate(UInt(c["candidateId"]), UInt(c["group"]), kind, "rotated", "rotated", Int(c["cx"]), Int(c["cy"]), Int(c["rx"]), Int(c["ry"]), Byte(s["r"]), Byte(s["g"]), Byte(s["b"]), Byte(s["a"]), Single(c["angleDegrees"]), Double(s["energy"]), ULong(s["oldErrorDelta"]), ULong(s["newErrorDelta"]));
        }

        private static CandidateShapeKind KindForShape(string shape)
        {
            if (shape == "rotated-rect") return CandidateShapeKind.RotatedRectangle;
            if (shape == "rotated-triangle") return CandidateShapeKind.RotatedTriangle;
            if (shape == "line-rect") return CandidateShapeKind.LineRectangle;
            if (shape == "rotated") return CandidateShapeKind.RotatedEllipse;
            throw new ArgumentException("Unknown check shape.", "shape");
        }

        private static byte[] Fixture(int width, int height)
        {
            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    rgba[offset] = (byte)((x * 17 + y * 3) & 255);
                    rgba[offset + 1] = (byte)((x * 5 + y * 11) & 255);
                    rgba[offset + 2] = (byte)((x * 13 + y * 7) & 255);
                    rgba[offset + 3] = (byte)((x + y) % 7 == 0 ? 96 : 255);
                }
            }
            return rgba;
        }

        private static string RasterHash(IReadOnlyList<RasterLine> lines, int width, int height)
        {
            var mask = new byte[width * height * 2];
            foreach (var line in lines)
            {
                for (var x = line.X1; x <= line.X2; x++)
                {
                    var offset = (line.Y * width + x) * 2;
                    mask[offset] = (byte)line.Alpha;
                    mask[offset + 1] = (byte)(line.Alpha >> 8);
                }
            }
            return Hash(mask);
        }

        private static int CoverageCount(IReadOnlyList<RasterLine> lines)
        {
            var count = 0;
            for (var index = 0; index < lines.Count; index++) count += lines[index].X2 - lines[index].X1 + 1;
            return count;
        }

        private static object ReadGolden()
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (folder != null)
            {
                var file = Path.Combine(folder.FullName, "native", "GTAEmblemMaker.Checks", "Golden", "fit-math.json");
                if (File.Exists(file))
                {
                    Type serializerType = null;
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name == "System.Web.Extensions") serializerType = assembly.GetType("System.Web.Script.Serialization.JavaScriptSerializer", true);
                    }
                    if (serializerType == null) throw new InvalidOperationException("System.Web.Extensions is not loaded.");
                    var serializer = Activator.CreateInstance(serializerType);
                    return serializerType.GetMethod("DeserializeObject", BindingFlags.Instance | BindingFlags.Public).Invoke(serializer, new object[] { File.ReadAllText(file) });
                }
                folder = folder.Parent;
            }
            throw new FileNotFoundException("Task 6 golden not found.");
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static Dictionary<string, object> Object(object value) { return (Dictionary<string, object>)value; }
        private static int Int(object value) { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        private static uint UInt(object value) { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
        private static long Long(object value) { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        private static ulong ULong(object value) { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        private static byte Byte(object value) { return Convert.ToByte(value, CultureInfo.InvariantCulture); }
        private static float Single(object value) { return Convert.ToSingle(value, CultureInfo.InvariantCulture); }
        private static double Double(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        private static string String(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static void Equal<T>(T expected, T actual, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
        }
    }
}
