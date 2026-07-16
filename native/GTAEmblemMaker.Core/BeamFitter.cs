using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    internal static class BeamFitter
    {
        private const int CanvasSize = 512;
        private const long PreviewIntervalMilliseconds = 2500;

        internal static Task<FitResult> RunAsync(FitRequest request, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Task.Run(() => Run(request, progress, cancellationToken), cancellationToken);
        }

        internal static int[] BestIndices(long[] errors, int count)
        {
            if (errors == null) throw new ArgumentNullException("errors");
            if (count < 1 || count > errors.Length) throw new ArgumentOutOfRangeException("count");
            var indices = new int[errors.Length];
            for (var index = 0; index < indices.Length; index++) indices[index] = index;
            Array.Sort(indices, (left, right) =>
            {
                var order = errors[left].CompareTo(errors[right]);
                return order != 0 ? order : left.CompareTo(right);
            });
            Array.Resize(ref indices, count);
            return indices;
        }

        private static FitResult Run(FitRequest request, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            var settings = request.Profile.Pipeline;
            if (settings.Runner != "beam" && settings.Runner != "beam-pair") throw new InvalidOperationException("The selected profile does not use beam search.");
            var stage = FitMath.ResolveStage(request.Profile, "current-image-fit");
            var maximumLayers = request.LayerLimit > 0 ? request.LayerLimit : stage.MaxLayers;
            var budget = request.BudgetLimit > 0 ? request.BudgetLimit : stage.Budget;
            Validate(stage, request.Source.IsTransparent, maximumLayers, budget);

            var target = (byte[])request.Source.CanonicalRgba.Clone();
            var initial = FitMath.CreateInitialCurrent(target, request.Source.IsTransparent);
            var mapChoice = FitMath.WeightMapChoiceForLayer(stage, request.Source.IsTransparent, 1);
            var weights = FitMath.BuildWeightMaps(target, CanvasSize, CanvasSize)[mapChoice.WeightMapId].Q8;
            var initialError = FitMath.WeightedFullError(target, initial, weights);
            var timestamp = request.Timestamp != 0 ? request.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var backgroundRed = initial[0];
            var backgroundGreen = initial[1];
            var backgroundBlue = initial[2];
            var beam = new List<BeamState> { new BeamState(null, initial, initialError, NewBuilder(request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp).BudgetCodeLength) };
            var previewClock = Stopwatch.StartNew();
            var budgetReached = false;

            using (var client = CudaScorerClient.Start(request.CudaScorerPath, CanvasSize, CanvasSize, target, initial, cancellationToken))
            {
                client.SetWeightMapAsync(weights, cancellationToken).GetAwaiter().GetResult();
                for (var layer = 1; layer <= maximumLayers; layer++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expansions = new List<BeamExpansion>(beam.Count * settings.BranchFactor);
                    for (var beamIndex = 0; beamIndex < beam.Count; beamIndex++)
                    {
                        var parent = beam[beamIndex];
                        client.UpdateCurrentAsync(checked((ulong)parent.Error), parent.Current, cancellationToken).GetAwaiter().GetResult();
                        var selected = client.SelectLayerAsync(BeamRequest(stage, layer), cancellationToken).GetAwaiter().GetResult();
                        AddExpansions(expansions, parent, selected, settings.BranchFactor, target, weights);
                    }

                    var expansionErrors = new long[expansions.Count];
                    for (var index = 0; index < expansions.Count; index++) expansionErrors[index] = expansions[index].Error;
                    var ranked = BestIndices(expansionErrors, expansionErrors.Length);
                    var next = new List<BeamState>(settings.BeamWidth);
                    for (var rank = 0; rank < ranked.Length && next.Count < settings.BeamWidth; rank++)
                    {
                        var expansion = expansions[ranked[rank]];
                        int codeLength;
                        if (!FitsBudget(expansion.Parent.Node, expansion.Candidate, request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budget, out codeLength)) continue;
                        var current = (byte[])expansion.Parent.Current.Clone();
                        var exactError = FitMath.ApplyCandidateAndUpdateError(target, current, CanvasSize, expansion.Candidate, weights, expansion.Parent.Error);
                        if (exactError != expansion.Error) throw new InvalidOperationException("CUDA beam error delta does not match the CPU rasterizer.");
                        next.Add(new BeamState(new BeamNode(expansion.Parent.Node, expansion.Candidate, exactError, expansion.ServerMilliseconds), current, exactError, codeLength));
                    }

                    if (next.Count == 0)
                    {
                        budgetReached = true;
                        break;
                    }
                    beam = next;
                    if (progress != null && (layer == 1 || layer == maximumLayers || previewClock.ElapsedMilliseconds >= PreviewIntervalMilliseconds))
                    {
                        progress.Report(new FitProgress(layer, maximumLayers, beam[0].CodeLength, "beam", mapChoice.WeightMapId, FitMath.EnergyFromTotal(beam[0].Error, CanvasSize, CanvasSize), (byte[])beam[0].Current.Clone()));
                        previewClock.Restart();
                    }
                }
            }

            var best = beam[BestIndices(Errors(beam), 1)[0]];
            return BuildResult(request, best, mapChoice.WeightMapId, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budgetReached);
        }

        private static void AddExpansions(List<BeamExpansion> expansions, BeamState parent, CudaSelectLayerResult selected, int branchFactor, byte[] target, byte[] weights)
        {
            if (selected.Chains == null || selected.Chains.Count == 0) throw new InvalidOperationException("Beam search requires CUDA candidate chains.");
            var unique = new List<FitCandidate>(selected.Chains.Count);
            var errors = new List<long>(selected.Chains.Count);
            for (var index = 0; index < selected.Chains.Count; index++)
            {
                var chain = selected.Chains[index];
                var candidate = CandidateGenerator.FromResidentResult(chain.ShapeKind, chain.Candidate, chain.Score);
                if (candidate.Kind != CandidateShapeKind.RotatedEllipse) throw new InvalidOperationException("Beam search received a non-ellipse candidate.");
                var duplicate = false;
                for (var existing = 0; existing < unique.Count; existing++) duplicate |= SameCandidate(unique[existing], candidate);
                if (duplicate) continue;
                unique.Add(candidate);
                errors.Add(FitMath.EvaluateCandidateError(target, parent.Current, CanvasSize, candidate, weights, parent.Error));
            }
            if (unique.Count == 0) throw new InvalidOperationException("CUDA returned no unique beam candidates.");
            var ranked = BestIndices(errors.ToArray(), Math.Min(branchFactor, unique.Count));
            for (var rank = 0; rank < ranked.Length; rank++)
            {
                var index = ranked[rank];
                expansions.Add(new BeamExpansion(parent, unique[index], errors[index], selected.ServerTotalMs));
            }
        }

        private static CudaSelectLayerRequest BeamRequest(FitStage stage, int layer)
        {
            var request = FittingEngine.CreateSelectRequest(stage, layer, new[] { "rotated" });
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 1;
            request.SelectionMode = CandidateGenerator.SelectionMode(stage.ResidentSelection);
            return request;
        }

        private static bool SameCandidate(FitCandidate left, FitCandidate right)
        {
            return left.Cx == right.Cx && left.Cy == right.Cy && left.Rx == right.Rx && left.Ry == right.Ry && left.Red == right.Red && left.Green == right.Green && left.Blue == right.Blue && left.Alpha == right.Alpha && left.AngleDegrees == right.AngleDegrees;
        }

        private static bool FitsBudget(BeamNode parent, FitCandidate candidate, bool transparent, byte red, byte green, byte blue, long timestamp, int budget, out int codeLength)
        {
            var builder = NewBuilder(transparent, red, green, blue, timestamp);
            var nodes = NodesInOrder(parent);
            for (var index = 0; index < nodes.Count; index++) builder.Add(CandidateGenerator.ToShapeState(nodes[index].Candidate));
            if (!builder.TryAdd(CandidateGenerator.ToShapeState(candidate), budget))
            {
                codeLength = builder.BudgetCodeLength;
                return false;
            }
            codeLength = builder.BudgetCodeLength;
            return true;
        }

        private static RockstarExporter.IncrementalBuilder NewBuilder(bool transparent, byte red, byte green, byte blue, long timestamp)
        {
            return RockstarExporter.CreateBuilder(transparent, red, green, blue, timestamp);
        }

        private static FitResult BuildResult(FitRequest request, BeamState best, string weightMapId, byte red, byte green, byte blue, long timestamp, bool budgetReached)
        {
            var nodes = NodesInOrder(best.Node);
            var states = new List<ShapeState>(nodes.Count);
            var trace = new List<FitLayerTrace>(nodes.Count);
            var builder = NewBuilder(request.Source.IsTransparent, red, green, blue, timestamp);
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                var state = CandidateGenerator.ToShapeState(node.Candidate);
                builder.Add(state);
                states.Add(state);
                trace.Add(new FitLayerTrace(index + 1, node.Candidate.CandidateId, node.Candidate.PoolShapeFamily, weightMapId, builder.BudgetCodeLength, node.Error, node.Candidate.Energy, node.ServerMilliseconds, false, false, 0, 0));
            }
            return new FitResult(states, trace, best.Current, builder.Build(), budgetReached, best.Error, weightMapId, null);
        }

        private static List<BeamNode> NodesInOrder(BeamNode node)
        {
            var nodes = new List<BeamNode>();
            while (node != null)
            {
                nodes.Add(node);
                node = node.Parent;
            }
            nodes.Reverse();
            return nodes;
        }

        private static long[] Errors(List<BeamState> beam)
        {
            var errors = new long[beam.Count];
            for (var index = 0; index < beam.Count; index++) errors[index] = beam[index].Error;
            return errors;
        }

        private static void Validate(FitStage stage, bool transparent, int maximumLayers, int budget)
        {
            if (maximumLayers < 1 || maximumLayers > stage.MaxLayers) throw new ArgumentOutOfRangeException("maximumLayers");
            if (budget < 1 || budget > stage.Budget) throw new ArgumentOutOfRangeException("budget");
            if (!stage.ResidentSelectLayer || !stage.ResidentDeviceChunk) throw new InvalidOperationException("Beam search requires the resident CUDA fitting path.");
            if (stage.LayerOptimization.OverfitPercent != 0 || stage.LayerOptimization.RefinePercent != 0 || stage.LayerOptimization.ReplacementPercent != 0 || stage.StrokeSearch != null) throw new InvalidOperationException("Beam search does not support layer or stroke post-processing.");
            var weightMapId = FitMath.WeightMapChoiceForLayer(stage, transparent, 1).WeightMapId;
            for (var layer = 1; layer <= maximumLayers; layer++)
            {
                if (CandidateGenerator.ShapeMask(FitMath.ShapeChoicesForLayer(stage, layer)) != 1) throw new InvalidOperationException("Beam search supports ellipse-only profiles.");
                if (FitMath.WeightMapChoiceForLayer(stage, transparent, layer).WeightMapId != weightMapId) throw new InvalidOperationException("Beam search requires one fixed weight map.");
            }
        }

        private sealed class BeamNode
        {
            internal BeamNode Parent { get; private set; }
            internal FitCandidate Candidate { get; private set; }
            internal long Error { get; private set; }
            internal double ServerMilliseconds { get; private set; }

            internal BeamNode(BeamNode parent, FitCandidate candidate, long error, double serverMilliseconds)
            {
                Parent = parent;
                Candidate = candidate;
                Error = error;
                ServerMilliseconds = serverMilliseconds;
            }
        }

        private sealed class BeamState
        {
            internal BeamNode Node { get; private set; }
            internal byte[] Current { get; private set; }
            internal long Error { get; private set; }
            internal int CodeLength { get; private set; }

            internal BeamState(BeamNode node, byte[] current, long error, int codeLength)
            {
                Node = node;
                Current = current;
                Error = error;
                CodeLength = codeLength;
            }
        }

        private sealed class BeamExpansion
        {
            internal BeamState Parent { get; private set; }
            internal FitCandidate Candidate { get; private set; }
            internal long Error { get; private set; }
            internal double ServerMilliseconds { get; private set; }

            internal BeamExpansion(BeamState parent, FitCandidate candidate, long error, double serverMilliseconds)
            {
                Parent = parent;
                Candidate = candidate;
                Error = error;
                ServerMilliseconds = serverMilliseconds;
            }
        }
    }
}
