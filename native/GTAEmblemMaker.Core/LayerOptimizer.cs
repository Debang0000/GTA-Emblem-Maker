using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    internal static class LayerOptimizer
    {
        internal static int PercentageCount(int layerCount, int percent)
        {
            if (layerCount < 0) throw new ArgumentOutOfRangeException("layerCount");
            if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            return checked((int)Math.Ceiling(layerCount * percent / 100.0));
        }

        internal static int[] KeepIndices(IReadOnlyList<ulong> improvements, int retainCount)
        {
            if (improvements == null) throw new ArgumentNullException("improvements");
            if (retainCount < 0 || retainCount > improvements.Count) throw new ArgumentOutOfRangeException("retainCount");

            var ranked = new List<int>(improvements.Count);
            for (var index = 0; index < improvements.Count; index++) ranked.Add(index);
            ranked.Sort((left, right) =>
            {
                var byImprovement = improvements[right].CompareTo(improvements[left]);
                return byImprovement != 0 ? byImprovement : left.CompareTo(right);
            });
            if (ranked.Count > retainCount) ranked.RemoveRange(retainCount, ranked.Count - retainCount);
            ranked.Sort();
            return ranked.ToArray();
        }

        internal static IReadOnlyList<ShapeState> WeakestStates(IReadOnlyList<ShapeState> states, IReadOnlyList<ulong> improvements, int count)
        {
            if (states == null) throw new ArgumentNullException("states");
            if (improvements == null) throw new ArgumentNullException("improvements");
            if (states.Count != improvements.Count) throw new ArgumentException("Layer collections must have equal lengths.");
            if (count < 0 || count > states.Count) throw new ArgumentOutOfRangeException("count");
            var ranked = new List<int>(states.Count);
            for (var index = 0; index < states.Count; index++) ranked.Add(index);
            ranked.Sort((left, right) =>
            {
                var byImprovement = improvements[left].CompareTo(improvements[right]);
                return byImprovement != 0 ? byImprovement : left.CompareTo(right);
            });
            var result = new List<ShapeState>(count);
            for (var index = 0; index < count; index++) result.Add(states[ranked[index]]);
            return result;
        }

        internal static bool AllowsReplacement(byte[] targetRgba, byte[] baselineRgba, long baselineError, byte[] proposalRgba, long proposalError, byte[] weightsQ8, int width, int tileSize, double maxTileRegressionPercent)
        {
            if (targetRgba == null) throw new ArgumentNullException("targetRgba");
            if (baselineRgba == null) throw new ArgumentNullException("baselineRgba");
            if (proposalRgba == null) throw new ArgumentNullException("proposalRgba");
            if (weightsQ8 == null) throw new ArgumentNullException("weightsQ8");
            if (targetRgba.Length == 0 || targetRgba.Length != baselineRgba.Length || targetRgba.Length != proposalRgba.Length || targetRgba.Length % 4 != 0) throw new ArgumentException("RGBA buffers must have equal nonzero lengths.");
            if (width <= 0 || targetRgba.Length % checked(width * 4) != 0) throw new ArgumentOutOfRangeException("width");
            if (weightsQ8.Length != targetRgba.Length / 2) throw new ArgumentException("Weight map length does not match the image.", "weightsQ8");
            if (baselineError < 0 || proposalError < 0) throw new ArgumentOutOfRangeException("baselineError");
            if (tileSize < 1) throw new ArgumentOutOfRangeException("tileSize");
            if (Double.IsNaN(maxTileRegressionPercent) || Double.IsInfinity(maxTileRegressionPercent) || maxTileRegressionPercent < 0 || maxTileRegressionPercent > 100) throw new ArgumentOutOfRangeException("maxTileRegressionPercent");
            if (proposalError >= baselineError) return false;

            var height = targetRgba.Length / (width * 4);
            var tileWidth = Math.Min(tileSize, width);
            var tileHeight = Math.Min(tileSize, height);
            var stride = Math.Max(1, tileSize / 2);
            for (var y = 0; ; y = Math.Min(y + stride, height - tileHeight))
            {
                for (var x = 0; ; x = Math.Min(x + stride, width - tileWidth))
                {
                    var baselineTile = WeightedRegionError(targetRgba, baselineRgba, weightsQ8, width, x, y, tileWidth, tileHeight);
                    var proposalTile = WeightedRegionError(targetRgba, proposalRgba, weightsQ8, width, x, y, tileWidth, tileHeight);
                    var allowedRegression = (long)Math.Ceiling(baselineTile * maxTileRegressionPercent / 100.0);
                    if (proposalTile > baselineTile + allowedRegression) return false;
                    if (x == width - tileWidth) break;
                }
                if (y == height - tileHeight) break;
            }
            return true;
        }

        private static long WeightedRegionError(byte[] targetRgba, byte[] currentRgba, byte[] weightsQ8, int imageWidth, int x, int y, int width, int height)
        {
            long total = 0;
            for (var sourceY = y; sourceY < y + height; sourceY++)
            {
                for (var sourceX = x; sourceX < x + width; sourceX++)
                {
                    var pixel = sourceY * imageWidth + sourceX;
                    var offset = pixel * 4;
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
            }
            return total;
        }

        internal static int[] KeepIndicesByRemoval(byte[] targetRgba, byte[] initialRgba, IReadOnlyList<ShapeState> states, byte[] weightsQ8, int width, IReadOnlyList<ulong> improvements, int retainCount, int poolMultiplier, CancellationToken cancellationToken)
        {
            if (targetRgba == null) throw new ArgumentNullException("targetRgba");
            if (initialRgba == null) throw new ArgumentNullException("initialRgba");
            if (states == null) throw new ArgumentNullException("states");
            if (weightsQ8 == null) throw new ArgumentNullException("weightsQ8");
            if (improvements == null) throw new ArgumentNullException("improvements");
            if (states.Count != improvements.Count) throw new ArgumentException("Layer collections must have equal lengths.");
            if (retainCount < 0 || retainCount > states.Count) throw new ArgumentOutOfRangeException("retainCount");
            if (poolMultiplier < 1) throw new ArgumentOutOfRangeException("poolMultiplier");
            cancellationToken.ThrowIfCancellationRequested();
            if (retainCount == states.Count) return KeepIndices(improvements, retainCount);

            var removeCount = states.Count - retainCount;
            var poolCount = Math.Min(states.Count, checked(removeCount * poolMultiplier));
            var candidates = new List<int>(states.Count);
            for (var index = 0; index < states.Count; index++) candidates.Add(index);
            candidates.Sort((left, right) =>
            {
                var byImprovement = improvements[left].CompareTo(improvements[right]);
                return byImprovement != 0 ? byImprovement : left.CompareTo(right);
            });
            if (candidates.Count > poolCount) candidates.RemoveRange(poolCount, candidates.Count - poolCount);

            var replayCandidates = new FitCandidate[states.Count];
            for (var index = 0; index < states.Count; index++) replayCandidates[index] = CandidateGenerator.FromShapeState(states[index]);
            var removalErrors = new long[states.Count];
            var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.For(0, candidates.Count, parallelOptions, candidateIndex =>
            {
                var removed = candidates[candidateIndex];
                var current = (byte[])initialRgba.Clone();
                for (var layer = 0; layer < states.Count; layer++)
                {
                    if ((layer & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (layer != removed) FitMath.ApplyCandidate(current, width, replayCandidates[layer]);
                }
                removalErrors[removed] = FitMath.WeightedFullError(targetRgba, current, weightsQ8);
            });

            candidates.Sort((left, right) =>
            {
                var leftError = removalErrors[left];
                var rightError = removalErrors[right];
                var byError = leftError.CompareTo(rightError);
                return byError != 0 ? byError : left.CompareTo(right);
            });
            var removedIndices = new HashSet<int>();
            for (var index = 0; index < removeCount; index++) removedIndices.Add(candidates[index]);
            var keep = new List<int>(retainCount);
            for (var index = 0; index < states.Count; index++) if (!removedIndices.Contains(index)) keep.Add(index);
            return keep.ToArray();
        }

        internal static void RetainBestByRemoval(byte[] targetRgba, byte[] initialRgba, List<ShapeState> states, List<FitLayerTrace> trace, List<ulong> improvements, byte[] weightsQ8, int width, int retainCount, int poolMultiplier, CancellationToken cancellationToken)
        {
            var keep = KeepIndicesByRemoval(targetRgba, initialRgba, states, weightsQ8, width, improvements, retainCount, poolMultiplier, cancellationToken);
            RetainIndices(states, trace, improvements, keep);
        }

        private static void RetainIndices(List<ShapeState> states, List<FitLayerTrace> trace, List<ulong> improvements, IReadOnlyList<int> keep)
        {
            if (states.Count != trace.Count || states.Count != improvements.Count) throw new ArgumentException("Layer collections must have equal lengths.");
            var retainedStates = new List<ShapeState>(keep.Count);
            var retainedTrace = new List<FitLayerTrace>(keep.Count);
            var retainedImprovements = new List<ulong>(keep.Count);
            for (var index = 0; index < keep.Count; index++)
            {
                retainedStates.Add(states[keep[index]]);
                retainedTrace.Add(trace[keep[index]]);
                retainedImprovements.Add(improvements[keep[index]]);
            }
            states.Clear();
            states.AddRange(retainedStates);
            trace.Clear();
            trace.AddRange(retainedTrace);
            improvements.Clear();
            improvements.AddRange(retainedImprovements);
        }

        internal static byte[] RebuildCurrent(byte[] initialRgba, IReadOnlyList<ShapeState> states, int width)
        {
            if (initialRgba == null) throw new ArgumentNullException("initialRgba");
            if (states == null) throw new ArgumentNullException("states");
            if (width <= 0 || initialRgba.Length == 0 || initialRgba.Length % checked(width * 4) != 0) throw new ArgumentException("Initial RGBA data does not match the width.");

            var current = (byte[])initialRgba.Clone();
            for (var index = 0; index < states.Count; index++)
            {
                if (states[index] == null) throw new ArgumentException("Layer state cannot be null.", "states");
                FitMath.ApplyCandidate(current, width, CandidateGenerator.FromShapeState(states[index]));
            }
            return current;
        }
    }
}
