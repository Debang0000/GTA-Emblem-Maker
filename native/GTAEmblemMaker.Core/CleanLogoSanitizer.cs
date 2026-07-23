using System;
using System.Collections.Generic;

namespace GTAEmblemMaker.Core
{
    public sealed class CleanLogoMetrics
    {
        public int SupportRejectedLayers { get; internal set; }
        public int LocalRegressionRejectedLayers { get; internal set; }
        public int ColorSnappedLayers { get; internal set; }
        public int EdgeRemovedLayers { get; internal set; }
        public int FinalSupportViolationPixels { get; internal set; }
        public int FinalUnsupportedEdgePixels { get; internal set; }
    }

    internal sealed class CleanLogoProposal
    {
        internal FitCandidate Candidate { get; private set; }
        internal ShapeState State { get; private set; }
        internal byte[] CurrentRgba { get; private set; }
        internal long CleanError { get; private set; }
        internal bool ColorSnapped { get; private set; }

        internal CleanLogoProposal(FitCandidate candidate, ShapeState state, byte[] currentRgba, long cleanError, bool colorSnapped)
        {
            Candidate = candidate;
            State = state;
            CurrentRgba = currentRgba;
            CleanError = cleanError;
            ColorSnapped = colorSnapped;
        }
    }

    internal sealed class CleanLogoAuditResult
    {
        internal IReadOnlyList<ShapeState> States { get; private set; }
        internal IReadOnlyList<int> RetainedIndices { get; private set; }
        internal byte[] CurrentRgba { get; private set; }

        internal CleanLogoAuditResult(List<ShapeState> states, List<int> retainedIndices, byte[] currentRgba)
        {
            States = states.ToArray();
            RetainedIndices = retainedIndices.ToArray();
            CurrentRgba = currentRgba;
        }
    }

    internal sealed class CleanLogoSanitizer
    {
        private const int Size = 512;
        private const int TileSize = 16;
        private const int EdgeThreshold = 24;
        private const int VisibleAlphaThreshold = 16;

        private readonly byte[] target;
        private readonly byte[] weightsQ8;
        private readonly bool transparent;
        private readonly int exportMinAxis;
        private readonly bool[] allowedAlphaSupport;
        private readonly bool[] allowedEdgeSupport;
        private readonly byte[] initialCurrent;
        private byte[] current;

        internal CleanLogoMetrics Metrics { get; private set; }
        internal long CurrentError { get; private set; }
        internal byte[] CurrentRgba { get { return current; } }

        internal CleanLogoSanitizer(byte[] targetRgba, byte[] currentRgba, byte[] weightsQ8, bool transparent, int exportMinAxis)
        {
            ValidateRgba(targetRgba, "targetRgba");
            ValidateRgba(currentRgba, "currentRgba");
            ValidateWeights(weightsQ8);
            target = (byte[])targetRgba.Clone();
            initialCurrent = (byte[])currentRgba.Clone();
            current = (byte[])initialCurrent.Clone();
            this.weightsQ8 = (byte[])weightsQ8.Clone();
            this.transparent = transparent;
            this.exportMinAxis = exportMinAxis;
            allowedAlphaSupport = BuildAllowedAlphaSupport(target);
            allowedEdgeSupport = BuildAllowedEdgeSupport(target);
            Metrics = new CleanLogoMetrics();
            CurrentError = CleanError(target, current, this.weightsQ8);
        }

        internal bool TrySelect(IReadOnlyList<FitCandidate> finalists, out CleanLogoProposal proposal)
        {
            if (finalists == null) throw new ArgumentNullException("finalists");
            var ordered = new List<RankedCandidate>(finalists.Count);
            for (var index = 0; index < finalists.Count; index++)
            {
                if (finalists[index] == null) throw new ArgumentException("Finalists cannot contain null.", "finalists");
                ordered.Add(new RankedCandidate(finalists[index], index));
            }
            ordered.Sort((left, right) =>
            {
                var energy = left.Candidate.Energy.CompareTo(right.Candidate.Energy);
                return energy != 0 ? energy : left.Index.CompareTo(right.Index);
            });

            for (var index = 0; index < ordered.Count; index++)
            {
                var candidate = ordered[index].Candidate;
                var original = CandidateGenerator.ToShapeState(candidate);
                var footprintState = new ShapeState(original.Shape, original.Cx, original.Cy, original.Rx, original.Ry, original.Red, original.Green, original.Blue, 255, original.AngleDegrees);
                var footprint = RunArtifacts.RenderShapeOnto(new byte[Size * Size * 4], footprintState, exportMinAxis);
                int red;
                int green;
                int blue;
                if (!TrySnapColor(footprint, candidate.Red, candidate.Green, candidate.Blue, out red, out green, out blue))
                {
                    Metrics.SupportRejectedLayers++;
                    continue;
                }

                var state = new ShapeState(original.Shape, original.Cx, original.Cy, original.Rx, original.Ry, red, green, blue, 255, original.AngleDegrees);
                var next = RunArtifacts.RenderShapeOnto(current, state, exportMinAxis);
                var supportViolations = transparent ? CountSupportViolations(next) : 0;
                if (supportViolations != 0)
                {
                    Metrics.SupportRejectedLayers++;
                    continue;
                }
                if (HasUnsupportedEdge(next, footprint))
                {
                    Metrics.SupportRejectedLayers++;
                    continue;
                }
                if (!AllowsLocalNonRegression(target, current, next, weightsQ8))
                {
                    Metrics.LocalRegressionRejectedLayers++;
                    continue;
                }

                proposal = new CleanLogoProposal(candidate, state, next, CleanError(target, next, weightsQ8), red != candidate.Red || green != candidate.Green || blue != candidate.Blue);
                return true;
            }

            proposal = null;
            return false;
        }

        internal void Commit(CleanLogoProposal proposal)
        {
            if (proposal == null) throw new ArgumentNullException("proposal");
            current = proposal.CurrentRgba;
            CurrentError = proposal.CleanError;
            if (proposal.ColorSnapped) Metrics.ColorSnappedLayers++;
        }

        internal CleanLogoAuditResult AuditFinal(IReadOnlyList<ShapeState> states)
        {
            if (states == null) throw new ArgumentNullException("states");
            var retainedStates = new List<ShapeState>(states.Count);
            var retainedIndices = new List<int>(states.Count);
            for (var index = 0; index < states.Count; index++)
            {
                if (states[index] == null) throw new ArgumentException("States cannot contain null.", "states");
                retainedStates.Add(states[index]);
                retainedIndices.Add(index);
            }

            while (true)
            {
                var replayed = Replay(retainedStates);
                var strongEdges = StrongEdgePixels(replayed);
                var violations = new bool[Size * Size];
                var supportViolationPixels = 0;
                var unsupportedEdgePixels = 0;
                for (var pixel = 0; pixel < violations.Length; pixel++)
                {
                    var supportViolation = transparent && !allowedAlphaSupport[pixel] && replayed[pixel * 4 + 3] >= VisibleAlphaThreshold;
                    var edgeViolation = strongEdges[pixel] && !allowedEdgeSupport[pixel];
                    if (supportViolation) supportViolationPixels++;
                    if (edgeViolation) unsupportedEdgePixels++;
                    violations[pixel] = supportViolation || edgeViolation;
                }
                Metrics.FinalSupportViolationPixels = supportViolationPixels;
                Metrics.FinalUnsupportedEdgePixels = unsupportedEdgePixels;
                if (supportViolationPixels == 0 && unsupportedEdgePixels == 0) return new CleanLogoAuditResult(retainedStates, retainedIndices, replayed);

                var removeAt = TopmostContributor(retainedStates, violations);
                if (removeAt < 0) throw new InvalidOperationException("Clean Logo final violations are not attributable to a retained layer.");
                retainedStates.RemoveAt(removeAt);
                retainedIndices.RemoveAt(removeAt);
                Metrics.EdgeRemovedLayers++;
            }
        }

        internal static bool[] BuildAllowedAlphaSupport(byte[] targetRgba)
        {
            ValidateRgba(targetRgba, "targetRgba");
            var support = new bool[Size * Size];
            for (var pixel = 0; pixel < support.Length; pixel++) support[pixel] = targetRgba[pixel * 4 + 3] > 0;
            return Dilate(support);
        }

        internal static bool[] BuildAllowedEdgeSupport(byte[] targetRgba)
        {
            ValidateRgba(targetRgba, "targetRgba");
            var support = Dilate(StrongEdgePixels(targetRgba));
            for (var pixel = 0; pixel < support.Length; pixel++)
            {
                if (targetRgba[pixel * 4 + 3] > 0) support[pixel] = true;
            }
            return support;
        }

        internal static long CleanError(byte[] targetRgba, byte[] currentRgba, byte[] weightsQ8)
        {
            ValidateRgba(targetRgba, "targetRgba");
            ValidateRgba(currentRgba, "currentRgba");
            ValidateWeights(weightsQ8);
            long total = 0;
            for (var pixel = 0; pixel < Size * Size; pixel++) total = checked(total + PixelError(targetRgba, currentRgba, weightsQ8, pixel));
            return total;
        }

        internal static bool AllowsLocalNonRegression(byte[] targetRgba, byte[] currentRgba, byte[] nextRgba, byte[] weightsQ8)
        {
            ValidateRgba(targetRgba, "targetRgba");
            ValidateRgba(currentRgba, "currentRgba");
            ValidateRgba(nextRgba, "nextRgba");
            ValidateWeights(weightsQ8);
            var tilesPerAxis = Size / TileSize;
            var currentTiles = new long[tilesPerAxis * tilesPerAxis];
            var nextTiles = new long[currentTiles.Length];
            long currentTotal = 0;
            long nextTotal = 0;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var pixel = y * Size + x;
                    var tile = y / TileSize * tilesPerAxis + x / TileSize;
                    var currentError = PixelError(targetRgba, currentRgba, weightsQ8, pixel);
                    var nextError = PixelError(targetRgba, nextRgba, weightsQ8, pixel);
                    currentTiles[tile] = checked(currentTiles[tile] + currentError);
                    nextTiles[tile] = checked(nextTiles[tile] + nextError);
                    currentTotal = checked(currentTotal + currentError);
                    nextTotal = checked(nextTotal + nextError);
                }
            }
            if (nextTotal >= currentTotal) return false;
            for (var tile = 0; tile < currentTiles.Length; tile++) if (nextTiles[tile] > currentTiles[tile]) return false;
            return true;
        }

        private bool TrySnapColor(byte[] footprint, int proposalRed, int proposalGreen, int proposalBlue, out int red, out int green, out int blue)
        {
            if (TrySnapColor(footprint, proposalRed, proposalGreen, proposalBlue, 128, out red, out green, out blue)) return true;
            return TrySnapColor(footprint, proposalRed, proposalGreen, proposalBlue, 1, out red, out green, out blue);
        }

        private bool TrySnapColor(byte[] footprint, int proposalRed, int proposalGreen, int proposalBlue, int minimumTargetAlpha, out int red, out int green, out int blue)
        {
            var found = false;
            var bestDistance = Int32.MaxValue;
            red = 0;
            green = 0;
            blue = 0;
            for (var pixel = 0; pixel < Size * Size; pixel++)
            {
                if (footprint[pixel * 4 + 3] == 0) continue;
                var offset = pixel * 4;
                var alpha = target[offset + 3];
                if (alpha < minimumTargetAlpha) continue;
                var sourceRed = Unpremultiply(target[offset], alpha);
                var sourceGreen = Unpremultiply(target[offset + 1], alpha);
                var sourceBlue = Unpremultiply(target[offset + 2], alpha);
                var redDifference = sourceRed - proposalRed;
                var greenDifference = sourceGreen - proposalGreen;
                var blueDifference = sourceBlue - proposalBlue;
                var distance = redDifference * redDifference + greenDifference * greenDifference + blueDifference * blueDifference;
                if (found && distance >= bestDistance) continue;
                found = true;
                bestDistance = distance;
                red = sourceRed;
                green = sourceGreen;
                blue = sourceBlue;
            }
            return found;
        }

        private int CountSupportViolations(byte[] rgba)
        {
            var count = 0;
            for (var pixel = 0; pixel < Size * Size; pixel++)
            {
                if (!allowedAlphaSupport[pixel] && rgba[pixel * 4 + 3] >= VisibleAlphaThreshold) count++;
            }
            return count;
        }

        private bool HasUnsupportedEdge(byte[] rgba, byte[] footprint)
        {
            var minimumX = Size;
            var minimumY = Size;
            var maximumX = -1;
            var maximumY = -1;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (footprint[(y * Size + x) * 4 + 3] == 0) continue;
                    minimumX = Math.Min(minimumX, x);
                    minimumY = Math.Min(minimumY, y);
                    maximumX = Math.Max(maximumX, x);
                    maximumY = Math.Max(maximumY, y);
                }
            }
            if (maximumX < 0) return true;
            minimumX = Math.Max(0, minimumX - 1);
            minimumY = Math.Max(0, minimumY - 1);
            maximumX = Math.Min(Size - 1, maximumX + 1);
            maximumY = Math.Min(Size - 1, maximumY + 1);
            for (var y = minimumY; y <= maximumY; y++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    var pixel = y * Size + x;
                    if (!allowedEdgeSupport[pixel] && IsStrongEdgeAt(rgba, x, y)) return true;
                }
            }
            return false;
        }

        private byte[] Replay(IReadOnlyList<ShapeState> states)
        {
            var replayed = (byte[])initialCurrent.Clone();
            for (var index = 0; index < states.Count; index++) replayed = RunArtifacts.RenderShapeOnto(replayed, states[index], exportMinAxis);
            return replayed;
        }

        private int TopmostContributor(IReadOnlyList<ShapeState> states, bool[] violations)
        {
            var transparentBase = new byte[Size * Size * 4];
            for (var index = states.Count - 1; index >= 0; index--)
            {
                var footprint = RunArtifacts.RenderShapeOnto(transparentBase, states[index], exportMinAxis);
                for (var pixel = 0; pixel < violations.Length; pixel++)
                {
                    if (violations[pixel] && footprint[pixel * 4 + 3] > 0) return index;
                }
            }
            return -1;
        }

        private static long PixelError(byte[] targetRgba, byte[] currentRgba, byte[] weightsQ8, int pixel)
        {
            var offset = pixel * 4;
            var red = targetRgba[offset] - currentRgba[offset];
            var green = targetRgba[offset + 1] - currentRgba[offset + 1];
            var blue = targetRgba[offset + 2] - currentRgba[offset + 2];
            var alpha = targetRgba[offset + 3] - currentRgba[offset + 3];
            var targetAlpha = targetRgba[offset + 3];
            var rgbError = (long)red * red + (long)green * green + (long)blue * blue;
            var alphaError = (long)alpha * alpha;
            var channelError = checked(targetAlpha * rgbError + (1020L - 3L * targetAlpha) * alphaError);
            var weightOffset = pixel * 2;
            var weight = weightsQ8[weightOffset] | weightsQ8[weightOffset + 1] << 8;
            return checked(channelError * weight / (255L * 256L));
        }

        private static bool[] StrongEdgePixels(byte[] rgba)
        {
            var result = new bool[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var pixel = y * Size + x;
                    result[pixel] = IsStrongEdgeAt(rgba, x, y);
                }
            }
            return result;
        }

        private static bool IsStrongEdgeAt(byte[] rgba, int x, int y)
        {
            var pixelOffset = (y * Size + x) * 4;
            for (var deltaY = -1; deltaY <= 1; deltaY++)
            {
                var neighborY = y + deltaY;
                if (neighborY < 0 || neighborY >= Size) continue;
                for (var deltaX = -1; deltaX <= 1; deltaX++)
                {
                    if (deltaX == 0 && deltaY == 0) continue;
                    var neighborX = x + deltaX;
                    if (neighborX < 0 || neighborX >= Size) continue;
                    if (StrongDifference(rgba, pixelOffset, (neighborY * Size + neighborX) * 4)) return true;
                }
            }
            return false;
        }

        private static bool StrongDifference(byte[] rgba, int left, int right)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                if (Math.Abs(rgba[left + channel] - rgba[right + channel]) >= EdgeThreshold) return true;
            }
            return false;
        }

        private static bool[] Dilate(bool[] source)
        {
            var result = new bool[source.Length];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (!source[y * Size + x]) continue;
                    for (var deltaY = -1; deltaY <= 1; deltaY++)
                    {
                        var neighborY = y + deltaY;
                        if (neighborY < 0 || neighborY >= Size) continue;
                        for (var deltaX = -1; deltaX <= 1; deltaX++)
                        {
                            var neighborX = x + deltaX;
                            if (neighborX >= 0 && neighborX < Size) result[neighborY * Size + neighborX] = true;
                        }
                    }
                }
            }
            return result;
        }

        private static int Unpremultiply(int channel, int alpha)
        {
            return Math.Min(255, (channel * 255 + alpha / 2) / alpha);
        }

        private static void ValidateRgba(byte[] rgba, string name)
        {
            if (rgba == null || rgba.Length != Size * Size * 4) throw new ArgumentException("Image must be a 512x512 RGBA buffer.", name);
        }

        private static void ValidateWeights(byte[] weightsQ8)
        {
            if (weightsQ8 == null || weightsQ8.Length != Size * Size * 2) throw new ArgumentException("Weight map must contain one Q8 value per pixel.", "weightsQ8");
        }

        private sealed class RankedCandidate
        {
            internal FitCandidate Candidate { get; private set; }
            internal int Index { get; private set; }

            internal RankedCandidate(FitCandidate candidate, int index)
            {
                Candidate = candidate;
                Index = index;
            }
        }
    }
}
