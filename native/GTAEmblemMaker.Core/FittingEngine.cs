using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    public sealed class FitRequest
    {
        public FitProfile Profile { get; private set; }
        public SourceImage Source { get; private set; }
        public string CudaScorerPath { get; private set; }
        public string PerceptualModelFolder { get; private set; }

        internal int LayerLimit { get; set; }
        internal int BudgetLimit { get; set; }
        internal long Timestamp { get; set; }

        public FitRequest(FitProfile profile, SourceImage source, string cudaScorerPath)
            : this(profile, source, cudaScorerPath, null)
        {
        }

        public FitRequest(FitProfile profile, SourceImage source, string cudaScorerPath, string perceptualModelFolder)
        {
            Profile = profile ?? throw new ArgumentNullException("profile");
            Source = source ?? throw new ArgumentNullException("source");
            if (String.IsNullOrWhiteSpace(cudaScorerPath)) throw new ArgumentException("CUDA scorer path is required.", "cudaScorerPath");
            CudaScorerPath = cudaScorerPath;
            PerceptualModelFolder = perceptualModelFolder;
        }
    }

    public sealed class FitProgress
    {
        public int Layer { get; private set; }
        public int MaximumLayers { get; private set; }
        public int GeneratedCodeLength { get; private set; }
        public string ShapeFamily { get; private set; }
        public string WeightMapId { get; private set; }
        public double Energy { get; private set; }
        public byte[] PreviewRgba { get; private set; }

        internal FitProgress(int layer, int maximumLayers, int generatedCodeLength, string shapeFamily, string weightMapId, double energy, byte[] previewRgba)
        {
            Layer = layer;
            MaximumLayers = maximumLayers;
            GeneratedCodeLength = generatedCodeLength;
            ShapeFamily = shapeFamily;
            WeightMapId = weightMapId;
            Energy = energy;
            PreviewRgba = previewRgba;
        }
    }

    public sealed class FitLayerTrace
    {
        public int Layer { get; private set; }
        public uint CandidateId { get; private set; }
        public string ShapeFamily { get; private set; }
        public string WeightMapId { get; private set; }
        public int GeneratedCodeLength { get; private set; }
        public long BaseTotalError { get; private set; }
        public double SelectedEnergy { get; private set; }
        public double ServerMilliseconds { get; private set; }
        public bool PerceptualRerankApplied { get; private set; }
        public bool PerceptualChangedSelection { get; private set; }
        public double PerceptualScore { get; private set; }
        public double PerceptualMilliseconds { get; private set; }

        internal FitLayerTrace(int layer, uint candidateId, string shapeFamily, string weightMapId, int generatedCodeLength, long baseTotalError, double selectedEnergy, double serverMilliseconds, bool perceptualRerankApplied, bool perceptualChangedSelection, double perceptualScore, double perceptualMilliseconds)
        {
            Layer = layer;
            CandidateId = candidateId;
            ShapeFamily = shapeFamily;
            WeightMapId = weightMapId;
            GeneratedCodeLength = generatedCodeLength;
            BaseTotalError = baseTotalError;
            SelectedEnergy = selectedEnergy;
            ServerMilliseconds = serverMilliseconds;
            PerceptualRerankApplied = perceptualRerankApplied;
            PerceptualChangedSelection = perceptualChangedSelection;
            PerceptualScore = perceptualScore;
            PerceptualMilliseconds = perceptualMilliseconds;
        }
    }

    public sealed class FitResult
    {
        public IReadOnlyList<ShapeState> Shapes { get; private set; }
        public IReadOnlyList<FitLayerTrace> Trace { get; private set; }
        public byte[] CurrentRgba { get; private set; }
        public RockstarPayload Payload { get; private set; }
        public int CompletedLayers { get; private set; }
        public bool BudgetReached { get; private set; }
        public long BaseTotalError { get; private set; }
        public string WeightMapId { get; private set; }
        public string PerceptualBackend { get; private set; }
        public CudaPerformanceCounters PerformanceCounters { get; private set; }
        public double WallMilliseconds { get; private set; }

        internal FitResult(List<ShapeState> shapes, List<FitLayerTrace> trace, byte[] currentRgba, RockstarPayload payload, bool budgetReached, long baseTotalError, string weightMapId, string perceptualBackend)
            : this(shapes, trace, currentRgba, payload, budgetReached, baseTotalError, weightMapId, perceptualBackend, null, 0)
        {
        }

        internal FitResult(List<ShapeState> shapes, List<FitLayerTrace> trace, byte[] currentRgba, RockstarPayload payload, bool budgetReached, long baseTotalError, string weightMapId, string perceptualBackend, CudaPerformanceCounters performanceCounters, double wallMilliseconds)
        {
            Shapes = new ReadOnlyCollection<ShapeState>(shapes);
            Trace = new ReadOnlyCollection<FitLayerTrace>(trace);
            CurrentRgba = currentRgba;
            Payload = payload;
            CompletedLayers = shapes.Count;
            BudgetReached = budgetReached;
            BaseTotalError = baseTotalError;
            WeightMapId = weightMapId;
            PerceptualBackend = perceptualBackend;
            PerformanceCounters = performanceCounters;
            WallMilliseconds = wallMilliseconds;
        }
    }

    public static class FittingEngine
    {
        private const int Width = 512;
        private const int Height = 512;
        private const long PreviewIntervalMilliseconds = 2500;
        private static readonly string[] StrokeShapes = { "line-rect" };

        public static async Task<FitResult> RunAsync(FitRequest request, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            var runClock = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var stage = FitMath.ResolveStage(request.Profile, "current-image-fit");
            var compatibilityResident = request.Profile.Pipeline.Runner == "catalog-compatible";
            if (!stage.ResidentSelectLayer || !stage.ResidentDeviceChunk) throw new InvalidOperationException("The selected profile does not use the native resident fitting path.");
            if (request.Source.CanonicalRgba == null || request.Source.CanonicalRgba.Length != Width * Height * 4) throw new ArgumentException("The source image is not a canonical 512x512 RGBA image.", "request");

            var maximumLayers = request.LayerLimit > 0 ? request.LayerLimit : stage.MaxLayers;
            if (maximumLayers < 1 || maximumLayers > stage.MaxLayers) throw new ArgumentOutOfRangeException("request", "Layer limit is outside the profile bounds.");
            var budget = request.BudgetLimit > 0 ? request.BudgetLimit : stage.Budget;
            if (budget < 1 || budget > stage.Budget) throw new ArgumentOutOfRangeException("request", "Budget limit is outside the profile bounds.");

            var target = (byte[])request.Source.CanonicalRgba.Clone();
            var initialCurrent = FitMath.CreateInitialCurrent(target, request.Source.IsTransparent);
            var current = (byte[])initialCurrent.Clone();
            var backgroundRed = current[0];
            var backgroundGreen = current[1];
            var backgroundBlue = current[2];
            var maps = FitMath.BuildWeightMaps(target, Width, Height);
            var strokeGuide = stage.StrokeSearch != null && !stage.StrokeSearch.IsMultiScale ? FitMath.BuildStrokeGuide(target, Width, Height) : null;
            var multiScaleStrokeGuide = stage.StrokeSearch != null && stage.StrokeSearch.IsMultiScale ? FitMath.BuildMultiScaleStrokeGuide(target, Width, Height, stage.StrokeSearch.TileSize) : null;
            var structuralGuide = stage.StrokeSearch != null && stage.StrokeSearch.StructuralRefine != null ? FitMath.BuildStructuralGuide(target, Width, Height, stage.StrokeSearch.StructuralRefine.DistanceLimit) : null;
            var timestamp = request.Timestamp != 0 ? request.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var payloadBuilder = RockstarExporter.CreateBuilder(request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp);
            var overfitLayers = LayerOptimizer.PercentageCount(maximumLayers, stage.LayerOptimization.OverfitPercent);
            var refineLayers = LayerOptimizer.PercentageCount(maximumLayers, stage.LayerOptimization.RefinePercent);
            var firstPruneAt = checked(maximumLayers + overfitLayers);
            var totalAttempts = checked(firstPruneAt + refineLayers);
            var replacementAttempts = LayerOptimizer.PercentageCount(maximumLayers, stage.LayerOptimization.ReplacementPercent);
            var progressMaximum = checked(totalAttempts + replacementAttempts);
            var searchBudget = overfitLayers > 0 ? Int32.MaxValue : budget;
            var states = new List<ShapeState>(firstPruneAt);
            var trace = new List<FitLayerTrace>(firstPruneAt);
            var improvements = new List<ulong>(firstPruneAt);
            var activeChoice = FitMath.WeightMapChoiceForLayer(stage, request.Source.IsTransparent, 1);
            var activeMap = maps[activeChoice.WeightMapId];
            var baseTotalError = FitMath.WeightedFullError(target, current, activeMap.Q8);
            var budgetReached = false;
            var previewClock = Stopwatch.StartNew();
            var nextPreviewAt = 0L;
            var needsPerceptual = stage.PerceptualRerank != null && totalAttempts >= stage.PerceptualRerank.FirstRerankLayer;
            if (needsPerceptual && String.IsNullOrWhiteSpace(request.PerceptualModelFolder)) throw new ArgumentException("The selected profile requires its packaged perceptual model.", "request");
            var perceptual = needsPerceptual ? PerceptualClient.Start(request.PerceptualModelFolder, stage.PerceptualRerank, cancellationToken) : null;
            CudaPerformanceCounters performanceCounters = null;

            try
            {
                using (var client = CudaScorerClient.Start(request.CudaScorerPath, Width, Height, target, current, cancellationToken))
                {
                    await client.UpdateCurrentAsync(checked((ulong)baseTotalError), current, cancellationToken).ConfigureAwait(false);
                    await client.SetWeightMapAsync(activeMap.Q8, cancellationToken).ConfigureAwait(false);
                    if (strokeGuide != null) await client.SetStrokeGuideAsync(strokeGuide.SaliencyQ8, strokeGuide.TangentQ8, cancellationToken).ConfigureAwait(false);
                    if (multiScaleStrokeGuide != null) await client.SetMultiScaleStrokeGuideAsync(multiScaleStrokeGuide.DetailSaliencyQ8, multiScaleStrokeGuide.ContourSaliencyQ8, multiScaleStrokeGuide.TangentQ8, cancellationToken).ConfigureAwait(false);
                    if (structuralGuide != null) await client.SetStructuralGuideAsync(structuralGuide.DistanceQ8, structuralGuide.TangentQ8, cancellationToken).ConfigureAwait(false);

                    for (var layer = 1; layer <= totalAttempts; layer++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var choice = FitMath.WeightMapChoiceForLayer(stage, request.Source.IsTransparent, layer);
                        var weightMapChanged = !String.Equals(choice.WeightMapId, activeChoice.WeightMapId, StringComparison.Ordinal);
                        if (weightMapChanged)
                        {
                            activeChoice = choice;
                            activeMap = maps[choice.WeightMapId];
                        }
                        if (overfitLayers > 0 && layer == firstPruneAt + 1)
                        {
                            LayerOptimizer.RetainBestByRemoval(target, initialCurrent, states, trace, improvements, activeMap.Q8, Width, maximumLayers, stage.LayerOptimization.RankingPoolMultiplier, cancellationToken);
                            current = LayerOptimizer.RebuildCurrent(initialCurrent, states, Width);
                            payloadBuilder = RebuildPayload(states, request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budget);
                            baseTotalError = FitMath.WeightedFullError(target, current, activeMap.Q8);
                            await client.UpdateCurrentAsync(checked((ulong)baseTotalError), current, cancellationToken).ConfigureAwait(false);
                            if (weightMapChanged) await client.SetWeightMapAsync(activeMap.Q8, cancellationToken).ConfigureAwait(false);
                        }
                        else if (weightMapChanged)
                        {
                            baseTotalError = FitMath.WeightedFullError(target, current, activeMap.Q8);
                            await client.UpdateCurrentAsync(checked((ulong)baseTotalError), current, cancellationToken).ConfigureAwait(false);
                            await client.SetWeightMapAsync(activeMap.Q8, cancellationToken).ConfigureAwait(false);
                        }

                        var strokeLayer = FitMath.IsStrokeLayer(stage, layer);
                        var contourStrokeLayer = strokeLayer && FitMath.IsContourStrokeLayer(stage, layer);
                        var strokeScale = strokeLayer && stage.StrokeSearch.IsMultiScale ? (contourStrokeLayer ? 2u : 1u) : 0u;
                        var shapes = strokeLayer ? StrokeShapes : FitMath.ShapeChoicesForLayer(stage, layer);
                        var selectRequest = CreateSelectRequest(
                            stage,
                            layer,
                            shapes,
                            strokeLayer ? stage.StrokeSearch.MinAxis : 0,
                            strokeLayer ? (uint)stage.StrokeSearch.GuideMode : 0,
                            strokeScale,
                            strokeScale == 1 ? stage.StrokeSearch.DetailMinLength : strokeScale == 2 ? stage.StrokeSearch.ContourMinLength : 0,
                            strokeScale == 1 ? stage.StrokeSearch.DetailMaxLength : strokeScale == 2 ? stage.StrokeSearch.ContourMaxLength : 0,
                            compatibilityResident);
                        var mixed = selectRequest.IsMixed;
                        var selected = await client.SelectLayerAsync(selectRequest, cancellationToken).ConfigureAwait(false);
                        var shapeKind = mixed ? selected.SelectedShapeKind : 0;
                        var candidate = CandidateGenerator.FromResidentResult(shapeKind, selected.SelectedCandidate, selected.SelectedScore);
                        CatalogSelection catalogSelection = null;
                        if (stage.CatalogSearch != null && layer >= stage.CatalogSearch.FromLayer)
                        {
                            catalogSelection = await CatalogCandidateSearch.SelectAsync(client, stage.CatalogSearch, layer, stage.MinAxis, cancellationToken).ConfigureAwait(false);
                            candidate = ChooseLowestEnergyCandidate(candidate, catalogSelection.BestByIdentity);
                        }
                        PerceptualSelection perceptualSelection = null;
                        var perceptualMilliseconds = 0.0;
                        if (perceptual != null && mixed && selected.Chains.Count > 1 && PerceptualReranker.ShouldRerank(stage.PerceptualRerank, layer, stage.MaxLayers))
                        {
                            var perceptualClock = Stopwatch.StartNew();
                            perceptualSelection = await PerceptualReranker.SelectAsync(perceptual, stage.PerceptualRerank, target, current, selected.Chains, candidate, cancellationToken, catalogSelection == null ? null : catalogSelection.Candidates).ConfigureAwait(false);
                            perceptualClock.Stop();
                            perceptualMilliseconds = perceptualClock.Elapsed.TotalMilliseconds;
                            candidate = perceptualSelection.Candidate;
                        }
                        var state = CandidateGenerator.ToShapeState(candidate);
                        if (!payloadBuilder.TryAdd(state, searchBudget))
                        {
                            budgetReached = true;
                            break;
                        }

                        states.Add(state);
                        improvements.Add(candidate.OldErrorDelta >= candidate.NewErrorDelta ? candidate.OldErrorDelta - candidate.NewErrorDelta : 0);
                        baseTotalError = compatibilityResident
                            ? FitMath.ApplyCompatibilityCandidateAndUpdateError(target, current, Width, candidate, activeMap.Q8, baseTotalError)
                            : FitMath.ApplyCandidateAndUpdateError(target, current, Width, candidate, activeMap.Q8, baseTotalError);
                        trace.Add(new FitLayerTrace(layer, candidate.CandidateId, candidate.PoolShapeFamily, activeChoice.WeightMapId, payloadBuilder.BudgetCodeLength, baseTotalError, candidate.Energy, selected.ServerTotalMs + (catalogSelection == null ? 0 : catalogSelection.ServerMilliseconds), perceptualSelection != null, perceptualSelection != null && perceptualSelection.ChangedSelection, perceptualSelection == null ? 0 : perceptualSelection.Score, perceptualMilliseconds));
                        if (overfitLayers > 0 && layer == totalAttempts && states.Count > maximumLayers)
                        {
                            LayerOptimizer.RetainBestByRemoval(target, initialCurrent, states, trace, improvements, activeMap.Q8, Width, maximumLayers, stage.LayerOptimization.RankingPoolMultiplier, cancellationToken);
                            current = LayerOptimizer.RebuildCurrent(initialCurrent, states, Width);
                            payloadBuilder = RebuildPayload(states, request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budget);
                            baseTotalError = FitMath.WeightedFullError(target, current, activeMap.Q8);
                        }
                        else if (layer < totalAttempts)
                        {
                            await client.UpdateCurrentAsync(checked((ulong)baseTotalError), current, cancellationToken).ConfigureAwait(false);
                        }
                        if (progress != null)
                        {
                            var elapsed = previewClock.ElapsedMilliseconds;
                            var preview = elapsed >= nextPreviewAt || layer == totalAttempts ? (byte[])current.Clone() : null;
                            if (preview != null) nextPreviewAt = elapsed + PreviewIntervalMilliseconds;
                            progress.Report(new FitProgress(layer, progressMaximum, payloadBuilder.BudgetCodeLength, candidate.PoolShapeFamily, activeChoice.WeightMapId, FitMath.EnergyFromTotal(baseTotalError, Width, Height), preview));
                        }
                    }
                    if (overfitLayers > 0 && states.Count > maximumLayers)
                    {
                        LayerOptimizer.RetainBestByRemoval(target, initialCurrent, states, trace, improvements, activeMap.Q8, Width, maximumLayers, stage.LayerOptimization.RankingPoolMultiplier, cancellationToken);
                        current = LayerOptimizer.RebuildCurrent(initialCurrent, states, Width);
                        payloadBuilder = RebuildPayload(states, request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budget);
                        baseTotalError = FitMath.WeightedFullError(target, current, activeMap.Q8);
                    }
                    if (replacementAttempts > 0 && states.Count > 0)
                    {
                        replacementAttempts = Math.Min(replacementAttempts, states.Count);
                        var removalQueue = LayerOptimizer.WeakestStates(states, improvements, replacementAttempts);
                        var replacementShapes = FitMath.ShapeChoicesForLayer(stage, maximumLayers);
                        for (var attempt = 0; attempt < replacementAttempts; attempt++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var removedIndex = states.IndexOf(removalQueue[attempt]);
                            if (removedIndex < 0) throw new InvalidOperationException("Replacement layer is no longer present.");
                            var proposalStates = new List<ShapeState>(states.Count);
                            for (var index = 0; index < states.Count; index++) if (index != removedIndex) proposalStates.Add(states[index]);
                            var withoutRemoved = LayerOptimizer.RebuildCurrent(initialCurrent, proposalStates, Width);
                            var withoutRemovedError = FitMath.WeightedFullError(target, withoutRemoved, activeMap.Q8);
                            await client.UpdateCurrentAsync(checked((ulong)withoutRemovedError), withoutRemoved, cancellationToken).ConfigureAwait(false);

                            var replacementLayer = checked(totalAttempts + attempt + 1);
                            var selectRequest = CreateSelectRequest(stage, replacementLayer, replacementShapes, compatibilityResident: compatibilityResident);
                            var selected = await client.SelectLayerAsync(selectRequest, cancellationToken).ConfigureAwait(false);
                            var mixed = selectRequest.IsMixed;
                            var shapeKind = mixed ? selected.SelectedShapeKind : 0;
                            var candidate = CandidateGenerator.FromResidentResult(shapeKind, selected.SelectedCandidate, selected.SelectedScore);
                            PerceptualSelection perceptualSelection = null;
                            var perceptualMilliseconds = 0.0;
                            if (perceptual != null && mixed)
                            {
                                var perceptualClock = Stopwatch.StartNew();
                                perceptualSelection = await PerceptualReranker.SelectAsync(perceptual, stage.PerceptualRerank, target, withoutRemoved, selected.Chains, candidate, cancellationToken).ConfigureAwait(false);
                                perceptualClock.Stop();
                                perceptualMilliseconds = perceptualClock.Elapsed.TotalMilliseconds;
                                candidate = perceptualSelection.Candidate;
                            }

                            var replacementState = CandidateGenerator.ToShapeState(candidate);
                            proposalStates.Add(replacementState);
                            var proposalBuilder = TryRebuildPayload(proposalStates, request.Source.IsTransparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budget);
                            var proposalCurrent = (byte[])withoutRemoved.Clone();
                            var proposalError = compatibilityResident
                                ? FitMath.ApplyCompatibilityCandidateAndUpdateError(target, proposalCurrent, Width, candidate, activeMap.Q8, withoutRemovedError)
                                : FitMath.ApplyCandidateAndUpdateError(target, proposalCurrent, Width, candidate, activeMap.Q8, withoutRemovedError);
                            var accepted = proposalBuilder != null && LayerOptimizer.AllowsReplacement(target, current, baseTotalError, proposalCurrent, proposalError, activeMap.Q8, Width, stage.LayerOptimization.ReplacementTileSize, stage.LayerOptimization.MaxTileRegressionPercent);
                            if (accepted)
                            {
                                states.RemoveAt(removedIndex);
                                trace.RemoveAt(removedIndex);
                                improvements.RemoveAt(removedIndex);
                                states.Add(replacementState);
                                improvements.Add(checked((ulong)(baseTotalError - proposalError)));
                                current = proposalCurrent;
                                baseTotalError = proposalError;
                                payloadBuilder = proposalBuilder;
                            trace.Add(new FitLayerTrace(replacementLayer, candidate.CandidateId, candidate.PoolShapeFamily, activeChoice.WeightMapId, payloadBuilder.BudgetCodeLength, baseTotalError, candidate.Energy, selected.ServerTotalMs, perceptualSelection != null, perceptualSelection != null && perceptualSelection.ChangedSelection, perceptualSelection == null ? 0 : perceptualSelection.Score, perceptualMilliseconds));
                            }
                            await client.UpdateCurrentAsync(checked((ulong)baseTotalError), current, cancellationToken).ConfigureAwait(false);

                            if (progress != null)
                            {
                                var progressLayer = totalAttempts + attempt + 1;
                                var elapsed = previewClock.ElapsedMilliseconds;
                                var preview = elapsed >= nextPreviewAt || attempt == replacementAttempts - 1 ? (byte[])current.Clone() : null;
                                if (preview != null) nextPreviewAt = elapsed + PreviewIntervalMilliseconds;
                                progress.Report(new FitProgress(progressLayer, progressMaximum, payloadBuilder.BudgetCodeLength, candidate.PoolShapeFamily, activeChoice.WeightMapId, FitMath.EnergyFromTotal(baseTotalError, Width, Height), preview));
                            }
                        }
                    }
                    performanceCounters = client.PerformanceCounters;
                }
            }
            finally
            {
                if (perceptual != null) perceptual.Dispose();
            }

            runClock.Stop();
            return new FitResult(states, trace, current, payloadBuilder.Build(), budgetReached, baseTotalError, activeChoice.WeightMapId, perceptual == null ? null : perceptual.BackendName, performanceCounters, runClock.Elapsed.TotalMilliseconds);
        }

        internal static FitCandidate ChooseLowestEnergyCandidate(FitCandidate historical, IReadOnlyList<FitCandidate> catalogCandidates)
        {
            var best = historical;
            for (var index = 0; index < catalogCandidates.Count; index++)
            {
                var candidate = catalogCandidates[index];
                if (candidate.Energy < best.Energy) best = candidate;
            }
            return best;
        }

        private static RockstarExporter.IncrementalBuilder RebuildPayload(IReadOnlyList<ShapeState> states, bool transparent, int backgroundRed, int backgroundGreen, int backgroundBlue, long timestamp, int budget)
        {
            var builder = TryRebuildPayload(states, transparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp, budget);
            if (builder == null) throw new InvalidOperationException("Optimized layers exceed the code budget.");
            return builder;
        }

        private static RockstarExporter.IncrementalBuilder TryRebuildPayload(IReadOnlyList<ShapeState> states, bool transparent, int backgroundRed, int backgroundGreen, int backgroundBlue, long timestamp, int budget)
        {
            var builder = RockstarExporter.CreateBuilder(transparent, backgroundRed, backgroundGreen, backgroundBlue, timestamp);
            for (var index = 0; index < states.Count; index++)
            {
                if (!builder.TryAdd(states[index], budget)) return null;
            }
            return builder;
        }

        internal static CudaSelectLayerRequest CreateSelectRequest(FitStage stage, int layer, IReadOnlyList<string> shapes, int minAxis = 0, uint guideMode = 0, uint strokeScale = 0, int minLongAxis = 0, int maxLongAxis = 0, bool compatibilityResident = false)
        {
            if (stage == null) throw new ArgumentNullException("stage");
            var shapeMask = CandidateGenerator.ShapeMask(shapes);
            var mixed = shapeMask != 1 || PerceptualReranker.ShouldRerank(stage.PerceptualRerank, layer, stage.MaxLayers);
            var structural = mixed && strokeScale == 1 && stage.StrokeSearch != null ? stage.StrokeSearch.StructuralRefine : null;
            return new CudaSelectLayerRequest
            {
                Mode = mixed
                    ? (compatibilityResident ? CudaSelectLayerMode.MixedResident : CudaSelectLayerMode.MixedDeviceChunk)
                    : (compatibilityResident ? CudaSelectLayerMode.RotatedResident : CudaSelectLayerMode.RotatedDeviceChunk),
                CandidatesPerGroup = CandidateGenerator.CandidatesPerGroup,
                GroupCount = CandidateGenerator.Multistart,
                Age = CandidateGenerator.Age,
                Fanout = CandidateGenerator.Fanout,
                EarlyStopRounds = CandidateGenerator.EarlyStopRounds,
                MaxHillSteps = CandidateGenerator.MaxHillSteps,
                MinAxis = checked((uint)(minAxis > 0 ? minAxis : stage.MinAxis)),
                Layer = (uint)layer,
                Weighted = true,
                MutateAlpha = true,
                MinAlpha = 1,
                MaxAlpha = 255,
                InitialAlpha = CandidateGenerator.InitialAlpha,
                Seed = CandidateGenerator.SeedForLayer(layer),
                ShapeMask = mixed ? shapeMask : 0,
                SelectionMode = mixed ? CandidateGenerator.SelectionMode(stage.ResidentSelection) : 0,
                GuideMode = mixed ? guideMode : 0,
                StrokeScale = mixed ? strokeScale : 0,
                MinLongAxis = mixed ? checked((uint)minLongAxis) : 0,
                MaxLongAxis = mixed ? checked((uint)maxLongAxis) : 0,
                DeviceChunkRounds = compatibilityResident ? 0 : (uint)Math.Min(stage.ResidentDeviceChunkRounds, CandidateGenerator.EarlyStopRounds),
                StructuralEdgeWeightQ16 = structural == null ? 0 : checked((uint)Math.Round(structural.EdgeDistanceWeight * 65536)),
                StructuralDistanceLimit = structural == null ? 0 : checked((uint)structural.DistanceLimit),
                StructuralRounds = structural == null ? 0 : checked((uint)structural.Rounds),
                MaxPixelGainRegressionQ16 = structural == null ? 0 : checked((uint)Math.Round(structural.MaxPixelGainRegression * 65536))
            };
        }

    }
}
