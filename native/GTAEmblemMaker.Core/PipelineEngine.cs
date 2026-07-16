using System;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    public static class PipelineEngine
    {
        public static async Task<FitResult> RunAsync(FitRequest request, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Profile.Pipeline.Runner == "greedy") return await FittingEngine.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
            var beam = await BeamFitter.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
            if (request.Profile.Pipeline.Runner == "beam") return beam;
            return await ExactPairRefiner.RunAsync(request, beam, progress, cancellationToken).ConfigureAwait(false);
        }

        public static Task<FitResult> RunBeamAsync(FitRequest request, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            return BeamFitter.RunAsync(request, progress, cancellationToken);
        }

        public static Task<FitResult> RefinePairAsync(FitRequest request, FitResult beam, IProgress<FitProgress> progress, CancellationToken cancellationToken)
        {
            return ExactPairRefiner.RunAsync(request, beam, progress, cancellationToken);
        }

        public static bool CanShareBeam(FitProfile first, FitProfile second)
        {
            if (first == null || second == null) return false;
            var leftPipeline = first.Pipeline;
            var rightPipeline = second.Pipeline;
            if ((leftPipeline.Runner != "beam" && leftPipeline.Runner != "beam-pair") || (rightPipeline.Runner != "beam" && rightPipeline.Runner != "beam-pair")) return false;
            if (leftPipeline.BeamWidth != rightPipeline.BeamWidth || leftPipeline.BranchFactor != rightPipeline.BranchFactor) return false;
            var left = FitMath.ResolveStage(first, "current-image-fit");
            var right = FitMath.ResolveStage(second, "current-image-fit");
            if (left.MaxLayers != right.MaxLayers || left.Budget != right.Budget || left.MinAxis != right.MinAxis || left.ResidentSelectLayer != right.ResidentSelectLayer || left.ResidentSelection != right.ResidentSelection || left.ResidentDeviceChunk != right.ResidentDeviceChunk || left.ResidentDeviceChunkRounds != right.ResidentDeviceChunkRounds) return false;
            if (left.ShapeChoicesByLayer.Count != right.ShapeChoicesByLayer.Count || left.OpaqueWeightMapSchedule.Count != right.OpaqueWeightMapSchedule.Count || left.TransparentWeightMapSchedule.Count != right.TransparentWeightMapSchedule.Count) return false;
            for (var index = 0; index < left.ShapeChoicesByLayer.Count; index++)
            {
                if (left.ShapeChoicesByLayer[index].FromLayer != right.ShapeChoicesByLayer[index].FromLayer || left.ShapeChoicesByLayer[index].Shapes.Count != right.ShapeChoicesByLayer[index].Shapes.Count) return false;
                for (var shape = 0; shape < left.ShapeChoicesByLayer[index].Shapes.Count; shape++) if (left.ShapeChoicesByLayer[index].Shapes[shape] != right.ShapeChoicesByLayer[index].Shapes[shape]) return false;
            }
            return SameWeights(left.OpaqueWeightMapSchedule, right.OpaqueWeightMapSchedule) && SameWeights(left.TransparentWeightMapSchedule, right.TransparentWeightMapSchedule);
        }

        private static bool SameWeights(System.Collections.Generic.IReadOnlyList<WeightMapChoice> left, System.Collections.Generic.IReadOnlyList<WeightMapChoice> right)
        {
            for (var index = 0; index < left.Count; index++)
            {
                if (left[index].FromLayer != right[index].FromLayer || left[index].WeightMapId != right[index].WeightMapId) return false;
            }
            return true;
        }
    }
}
