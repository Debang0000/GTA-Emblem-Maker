using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    internal static class ExactPairRefiner
    {
        private const int CanvasSize = 512;

        internal static Task<FitResult> RunAsync(FitRequest request, FitResult seed, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (seed == null) throw new ArgumentNullException("seed");
            return Task.Run(() => Run(request, seed, progress, cancellationToken), cancellationToken);
        }

        internal static IReadOnlyList<RefineWindow> RankWindows(byte[] target, byte[] current, int windowSize, int stride, int count)
        {
            if (target == null || current == null || target.Length != CanvasSize * CanvasSize * 4 || current.Length != target.Length) throw new ArgumentException("Window ranking requires matching 512x512 RGBA buffers.");
            if (windowSize < 8 || windowSize > CanvasSize || stride < 1 || stride > windowSize || count < 1) throw new ArgumentOutOfRangeException("windowSize");
            var starts = WindowStarts(windowSize, stride);
            var ranked = new List<WindowScore>(starts.Count * starts.Count);
            for (var yIndex = 0; yIndex < starts.Count; yIndex++)
            {
                for (var xIndex = 0; xIndex < starts.Count; xIndex++)
                {
                    var x = starts[xIndex];
                    var y = starts[yIndex];
                    long score = 0;
                    for (var row = y; row < y + windowSize; row++)
                    {
                        for (var column = x; column < x + windowSize; column++)
                        {
                            var offset = (row * CanvasSize + column) * 4;
                            for (var channel = 0; channel < 4; channel++)
                            {
                                var difference = target[offset + channel] - current[offset + channel];
                                score += difference * difference;
                            }
                        }
                    }
                    ranked.Add(new WindowScore(new RefineWindow(x, y, windowSize, windowSize), score));
                }
            }
            ranked.Sort((left, right) =>
            {
                var scoreOrder = right.Score.CompareTo(left.Score);
                if (scoreOrder != 0) return scoreOrder;
                var yOrder = left.Window.Y.CompareTo(right.Window.Y);
                return yOrder != 0 ? yOrder : left.Window.X.CompareTo(right.Window.X);
            });

            var selected = new List<RefineWindow>(count);
            for (var index = 0; index < ranked.Count && selected.Count < count; index++)
            {
                var window = ranked[index].Window;
                var overlaps = false;
                for (var existing = 0; existing < selected.Count; existing++) overlaps |= IntersectionOverUnion(window, selected[existing]) > 0.35;
                if (!overlaps) selected.Add(window);
            }
            return selected;
        }

        private static FitResult Run(FitRequest request, FitResult seed, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            var settings = request.Profile.Pipeline;
            if (settings.Runner != "beam-pair") throw new InvalidOperationException("The selected profile does not use exact pair refinement.");
            var stage = FitMath.ResolveStage(request.Profile, "current-image-fit");
            var windows = RankWindows(request.Source.CanonicalRgba, seed.CurrentRgba, settings.WindowSize, settings.WindowStride, settings.WindowCount);
            var shapes = new List<ShapeState>(seed.Shapes);
            var current = (byte[])seed.CurrentRgba.Clone();
            var totalError = seed.BaseTotalError;
            var timestamp = request.Timestamp != 0 ? request.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var initial = FitMath.CreateInitialCurrent(request.Source.CanonicalRgba, request.Source.IsTransparent);

            for (var windowIndex = 0; windowIndex < windows.Count; windowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selected = SelectOverlappingLayers(shapes, windows[windowIndex], settings.SelectedLayers);
                if (selected.Count >= settings.ExactLayers)
                {
                    var crop = OptimizationCrop(shapes, selected, windows[windowIndex]);
                    var exact = SelectVisibleLayers(shapes, selected, crop, settings.ExactLayers);
                    var refined = RefineExact(request.Source, shapes, request.Profile, exact, settings.ExactRounds, settings.PairCandidates, cancellationToken);
                    var payload = RockstarExporter.Build(refined.Shapes, request.Source.IsTransparent, initial[0], initial[1], initial[2], timestamp);
                    if (refined.BaseTotalError < totalError && payload.GeneratedCodeLength <= stage.Budget && LocalError(request.Source.CanonicalRgba, refined.CurrentRgba, windows[windowIndex]) < LocalError(request.Source.CanonicalRgba, current, windows[windowIndex]))
                    {
                        shapes = refined.Shapes;
                        current = refined.CurrentRgba;
                        totalError = refined.BaseTotalError;
                    }
                }
                if (progress != null)
                {
                    progress.Report(new FitProgress(seed.CompletedLayers, seed.CompletedLayers, seed.Payload.GeneratedCodeLength, "exact pair " + (windowIndex + 1) + "/" + windows.Count, seed.WeightMapId, FitMath.EnergyFromTotal(totalError, CanvasSize, CanvasSize), (byte[])current.Clone()));
                }
            }

            var finalPayload = RockstarExporter.Build(shapes, request.Source.IsTransparent, initial[0], initial[1], initial[2], timestamp);
            return new FitResult(shapes, new List<FitLayerTrace>(seed.Trace), current, finalPayload, seed.BudgetReached, totalError, seed.WeightMapId, seed.PerceptualBackend);
        }

        internal static ExactRefineResult RefineExact(SourceImage source, IReadOnlyList<ShapeState> inputShapes, FitProfile profile, IReadOnlyList<int> selectedIndices, int rounds, int pairCandidates, CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (inputShapes == null) throw new ArgumentNullException("inputShapes");
            if (profile == null) throw new ArgumentNullException("profile");
            if (selectedIndices == null || selectedIndices.Count == 0) throw new ArgumentException("Selected indices are required.", "selectedIndices");
            if (rounds <= 0 || pairCandidates < 0) throw new ArgumentOutOfRangeException("rounds");
            var selected = new SortedSet<int>();
            for (var index = 0; index < selectedIndices.Count; index++)
            {
                var value = selectedIndices[index];
                if (value < 0 || value >= inputShapes.Count || !selected.Add(value)) throw new ArgumentOutOfRangeException("selectedIndices");
            }

            var shapes = new List<ShapeState>(inputShapes);
            var stage = FitMath.ResolveStage(profile, "current-image-fit");
            var weightMapId = FitMath.WeightMapChoiceForLayer(stage, source.IsTransparent, Math.Max(1, shapes.Count)).WeightMapId;
            var weights = FitMath.BuildWeightMaps(source.CanonicalRgba, CanvasSize, CanvasSize)[weightMapId].Q8;
            var initial = FitMath.CreateInitialCurrent(source.CanonicalRgba, source.IsTransparent);
            var current = LayerOptimizer.RebuildCurrent(initial, shapes, CanvasSize);
            var totalError = FitMath.WeightedFullError(source.CanonicalRgba, current, weights);

            for (var round = 0; round < rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prefixes = BuildPrefixes(initial, shapes, selected);
                var accepted = 0;
                var descending = new List<int>(selected);
                descending.Reverse();
                for (var selectedIndex = 0; selectedIndex < descending.Count; selectedIndex++)
                {
                    var shapeIndex = descending[selectedIndex];
                    ShapeState bestShape = null;
                    byte[] bestCurrent = null;
                    var bestError = totalError;
                    foreach (var candidate in Mutations(shapes[shapeIndex]))
                    {
                        var candidateCurrent = ReplaySuffix(prefixes[shapeIndex], shapes, shapeIndex, candidate);
                        var candidateError = FitMath.WeightedFullError(source.CanonicalRgba, candidateCurrent, weights);
                        if (candidateError >= bestError) continue;
                        bestShape = candidate;
                        bestCurrent = candidateCurrent;
                        bestError = candidateError;
                    }
                    if (bestShape == null) continue;
                    shapes[shapeIndex] = bestShape;
                    current = bestCurrent;
                    totalError = bestError;
                    accepted++;
                }

                if (pairCandidates > 0 && selected.Count > 1)
                {
                    var pairPrefixes = BuildPrefixes(initial, shapes, selected);
                    var candidatePool = BuildCandidatePool(source.CanonicalRgba, weights, shapes, selected, pairPrefixes, pairCandidates, cancellationToken);
                    ShapeState bestFirstShape = null;
                    ShapeState bestSecondShape = null;
                    byte[] bestPairCurrent = null;
                    var bestFirstIndex = -1;
                    var bestSecondIndex = -1;
                    var bestPairError = totalError;
                    var ascending = new List<int>(selected);
                    for (var firstPosition = 0; firstPosition < ascending.Count - 1; firstPosition++)
                    {
                        var firstIndex = ascending[firstPosition];
                        for (var secondPosition = firstPosition + 1; secondPosition < ascending.Count; secondPosition++)
                        {
                            var secondIndex = ascending[secondPosition];
                            foreach (var firstCandidate in candidatePool[firstIndex])
                            {
                                foreach (var secondCandidate in candidatePool[secondIndex])
                                {
                                    var candidateCurrent = ReplaySuffixPair(pairPrefixes[firstIndex], shapes, firstIndex, firstCandidate.Item3, secondIndex, secondCandidate.Item3);
                                    var candidateError = FitMath.WeightedFullError(source.CanonicalRgba, candidateCurrent, weights);
                                    if (candidateError >= bestPairError) continue;
                                    bestFirstIndex = firstIndex;
                                    bestSecondIndex = secondIndex;
                                    bestFirstShape = firstCandidate.Item3;
                                    bestSecondShape = secondCandidate.Item3;
                                    bestPairCurrent = candidateCurrent;
                                    bestPairError = candidateError;
                                }
                            }
                        }
                    }
                    if (bestFirstShape != null)
                    {
                        shapes[bestFirstIndex] = bestFirstShape;
                        shapes[bestSecondIndex] = bestSecondShape;
                        current = bestPairCurrent;
                        totalError = bestPairError;
                        accepted += 2;
                    }
                }
                if (accepted == 0) break;
            }
            return new ExactRefineResult(shapes, current, totalError);
        }

        private static Dictionary<int, List<Tuple<long, int, ShapeState>>> BuildCandidatePool(byte[] target, byte[] weights, IReadOnlyList<ShapeState> shapes, SortedSet<int> selected, Dictionary<int, byte[]> prefixes, int count, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, List<Tuple<long, int, ShapeState>>>(selected.Count);
            foreach (var shapeIndex in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scored = new List<Tuple<long, int, ShapeState>>();
                var order = 0;
                foreach (var candidate in Mutations(shapes[shapeIndex]))
                {
                    var candidateCurrent = ReplaySuffix(prefixes[shapeIndex], shapes, shapeIndex, candidate);
                    scored.Add(Tuple.Create(FitMath.WeightedFullError(target, candidateCurrent, weights), order++, candidate));
                }
                scored.Sort((left, right) => left.Item1 != right.Item1 ? left.Item1.CompareTo(right.Item1) : left.Item2.CompareTo(right.Item2));
                if (scored.Count > count) scored.RemoveRange(count, scored.Count - count);
                result.Add(shapeIndex, scored);
            }
            return result;
        }

        private static List<int> SelectOverlappingLayers(IReadOnlyList<ShapeState> shapes, RefineWindow window, int count)
        {
            var selected = new List<int>();
            for (var index = 0; index < shapes.Count; index++)
            {
                if (Overlaps(shapes[index], window)) selected.Add(index);
            }
            if (selected.Count > count) selected.RemoveRange(0, selected.Count - count);
            return selected;
        }

        private static List<int> SelectVisibleLayers(IReadOnlyList<ShapeState> shapes, IReadOnlyList<int> selected, RefineWindow crop, int count)
        {
            var selectedSet = new HashSet<int>(selected);
            var scores = new Dictionary<int, double>();
            var transmittance = new double[crop.Width * crop.Height];
            for (var index = 0; index < transmittance.Length; index++) transmittance[index] = 1;
            for (var shapeIndex = shapes.Count - 1; shapeIndex >= 0; shapeIndex--)
            {
                var shape = shapes[shapeIndex];
                if (!Overlaps(shape, crop)) continue;
                var alpha = shape.Alpha / 255.0;
                var lines = FitMath.Rasterize(CandidateGenerator.FromShapeState(shape), CanvasSize, CanvasSize);
                double influence = 0;
                for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var line = lines[lineIndex];
                    if (line.Y < crop.Y || line.Y >= crop.Y + crop.Height) continue;
                    var first = Math.Max(line.X1, crop.X);
                    var last = Math.Min(line.X2, crop.X + crop.Width - 1);
                    for (var x = first; x <= last; x++)
                    {
                        var offset = (line.Y - crop.Y) * crop.Width + x - crop.X;
                        if (selectedSet.Contains(shapeIndex)) influence += alpha * transmittance[offset];
                        transmittance[offset] *= 1 - alpha;
                    }
                }
                if (selectedSet.Contains(shapeIndex)) scores.Add(shapeIndex, influence);
            }
            var ranked = new List<int>(selected);
            ranked.Sort((left, right) =>
            {
                var scoreOrder = scores[right].CompareTo(scores[left]);
                return scoreOrder != 0 ? scoreOrder : right.CompareTo(left);
            });
            if (ranked.Count > count) ranked.RemoveRange(count, ranked.Count - count);
            return ranked;
        }

        private static RefineWindow OptimizationCrop(IReadOnlyList<ShapeState> shapes, IReadOnlyList<int> selected, RefineWindow core)
        {
            var minX = (double)core.X;
            var minY = (double)core.Y;
            var maxX = (double)(core.X + core.Width);
            var maxY = (double)(core.Y + core.Height);
            for (var index = 0; index < selected.Count; index++)
            {
                var shape = shapes[selected[index]];
                var angle = shape.AngleDegrees * Math.PI / 180;
                var extentX = Math.Abs(shape.Rx * Math.Cos(angle)) + Math.Abs(shape.Ry * Math.Sin(angle));
                var extentY = Math.Abs(shape.Rx * Math.Sin(angle)) + Math.Abs(shape.Ry * Math.Cos(angle));
                minX = Math.Min(minX, shape.Cx - extentX);
                minY = Math.Min(minY, shape.Cy - extentY);
                maxX = Math.Max(maxX, shape.Cx + extentX);
                maxY = Math.Max(maxY, shape.Cy + extentY);
            }
            var x = Math.Max(0, (int)Math.Floor(minX - 8));
            var y = Math.Max(0, (int)Math.Floor(minY - 8));
            var right = Math.Min(CanvasSize, (int)Math.Ceiling(maxX + 8));
            var bottom = Math.Min(CanvasSize, (int)Math.Ceiling(maxY + 8));
            return new RefineWindow(x, y, right - x, bottom - y);
        }

        private static bool Overlaps(ShapeState shape, RefineWindow window)
        {
            var angle = shape.AngleDegrees * Math.PI / 180;
            var extentX = Math.Abs(shape.Rx * Math.Cos(angle)) + Math.Abs(shape.Ry * Math.Sin(angle)) + 1;
            var extentY = Math.Abs(shape.Rx * Math.Sin(angle)) + Math.Abs(shape.Ry * Math.Cos(angle)) + 1;
            return shape.Cx + extentX >= window.X && shape.Cx - extentX < window.X + window.Width && shape.Cy + extentY >= window.Y && shape.Cy - extentY < window.Y + window.Height;
        }

        private static long LocalError(byte[] target, byte[] current, RefineWindow window)
        {
            long result = 0;
            for (var y = window.Y; y < window.Y + window.Height; y++)
            {
                for (var x = window.X; x < window.X + window.Width; x++)
                {
                    var offset = (y * CanvasSize + x) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var difference = target[offset + channel] - current[offset + channel];
                        result += difference * difference;
                    }
                }
            }
            return result;
        }

        private static List<int> WindowStarts(int windowSize, int stride)
        {
            var starts = new List<int>();
            for (var value = 0; value <= CanvasSize - windowSize; value += stride) starts.Add(value);
            var final = CanvasSize - windowSize;
            if (starts[starts.Count - 1] != final) starts.Add(final);
            return starts;
        }

        private static double IntersectionOverUnion(RefineWindow first, RefineWindow second)
        {
            var overlapWidth = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
            var overlapHeight = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
            var overlap = overlapWidth * overlapHeight;
            return overlap / (double)(first.Width * first.Height + second.Width * second.Height - overlap);
        }

        private static Dictionary<int, byte[]> BuildPrefixes(byte[] initial, IReadOnlyList<ShapeState> shapes, SortedSet<int> selected)
        {
            var prefixes = new Dictionary<int, byte[]>(selected.Count);
            var current = (byte[])initial.Clone();
            for (var index = 0; index < shapes.Count; index++)
            {
                if (selected.Contains(index)) prefixes.Add(index, (byte[])current.Clone());
                FitMath.ApplyCandidate(current, CanvasSize, CandidateGenerator.FromShapeState(shapes[index]));
            }
            return prefixes;
        }

        private static byte[] ReplaySuffix(byte[] prefix, IReadOnlyList<ShapeState> shapes, int shapeIndex, ShapeState candidate)
        {
            var current = (byte[])prefix.Clone();
            FitMath.ApplyCandidate(current, CanvasSize, CandidateGenerator.FromShapeState(candidate));
            for (var index = shapeIndex + 1; index < shapes.Count; index++) FitMath.ApplyCandidate(current, CanvasSize, CandidateGenerator.FromShapeState(shapes[index]));
            return current;
        }

        private static byte[] ReplaySuffixPair(byte[] prefix, IReadOnlyList<ShapeState> shapes, int firstIndex, ShapeState firstCandidate, int secondIndex, ShapeState secondCandidate)
        {
            var current = (byte[])prefix.Clone();
            FitMath.ApplyCandidate(current, CanvasSize, CandidateGenerator.FromShapeState(firstCandidate));
            for (var index = firstIndex + 1; index < shapes.Count; index++) FitMath.ApplyCandidate(current, CanvasSize, CandidateGenerator.FromShapeState(index == secondIndex ? secondCandidate : shapes[index]));
            return current;
        }

        private static IEnumerable<ShapeState> Mutations(ShapeState shape)
        {
            yield return Replace(shape, cx: Clamp(shape.Cx - 1, 0, 512));
            yield return Replace(shape, cx: Clamp(shape.Cx + 1, 0, 512));
            yield return Replace(shape, cy: Clamp(shape.Cy - 1, 0, 512));
            yield return Replace(shape, cy: Clamp(shape.Cy + 1, 0, 512));
            yield return Replace(shape, rx: Clamp(shape.Rx - 1, 0, 512));
            yield return Replace(shape, rx: Clamp(shape.Rx + 1, 0, 512));
            yield return Replace(shape, ry: Clamp(shape.Ry - 1, 0, 512));
            yield return Replace(shape, ry: Clamp(shape.Ry + 1, 0, 512));
            yield return Replace(shape, angle: Clamp(shape.AngleDegrees - 2, 0, 180));
            yield return Replace(shape, angle: Clamp(shape.AngleDegrees - 0.5, 0, 180));
            yield return Replace(shape, angle: Clamp(shape.AngleDegrees + 0.5, 0, 180));
            yield return Replace(shape, angle: Clamp(shape.AngleDegrees + 2, 0, 180));
            yield return Replace(shape, red: Clamp(shape.Red - 2, 0, 255));
            yield return Replace(shape, red: Clamp(shape.Red + 2, 0, 255));
            yield return Replace(shape, green: Clamp(shape.Green - 2, 0, 255));
            yield return Replace(shape, green: Clamp(shape.Green + 2, 0, 255));
            yield return Replace(shape, blue: Clamp(shape.Blue - 2, 0, 255));
            yield return Replace(shape, blue: Clamp(shape.Blue + 2, 0, 255));
            yield return Replace(shape, alpha: Clamp(shape.Alpha - 2, 0, 255));
            yield return Replace(shape, alpha: Clamp(shape.Alpha + 2, 0, 255));
        }

        private static ShapeState Replace(ShapeState shape, double? cx = null, double? cy = null, double? rx = null, double? ry = null, double? angle = null, int? red = null, int? green = null, int? blue = null, int? alpha = null)
        {
            return new ShapeState(shape.Shape, cx ?? shape.Cx, cy ?? shape.Cy, rx ?? shape.Rx, ry ?? shape.Ry, red ?? shape.Red, green ?? shape.Green, blue ?? shape.Blue, alpha ?? shape.Alpha, angle ?? shape.AngleDegrees);
        }

        private static double Clamp(double value, double minimum, double maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
        private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }

        internal sealed class RefineWindow
        {
            internal int X { get; private set; }
            internal int Y { get; private set; }
            internal int Width { get; private set; }
            internal int Height { get; private set; }

            internal RefineWindow(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }

        internal sealed class ExactRefineResult
        {
            internal List<ShapeState> Shapes { get; private set; }
            internal byte[] CurrentRgba { get; private set; }
            internal long BaseTotalError { get; private set; }

            internal ExactRefineResult(List<ShapeState> shapes, byte[] currentRgba, long baseTotalError)
            {
                Shapes = shapes;
                CurrentRgba = currentRgba;
                BaseTotalError = baseTotalError;
            }
        }

        private sealed class WindowScore
        {
            internal RefineWindow Window { get; private set; }
            internal long Score { get; private set; }

            internal WindowScore(RefineWindow window, long score)
            {
                Window = window;
                Score = score;
            }
        }
    }
}
