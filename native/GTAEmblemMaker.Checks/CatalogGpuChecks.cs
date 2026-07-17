using System;
using System.Collections.Generic;
using System.Threading;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class CatalogGpuChecks
    {
        internal static void Run(string scorerPath)
        {
            var target = new byte[512 * 512 * 4];
            for (var index = 0; index < target.Length; index++) target[index] = 255;
            var current = new byte[target.Length];
            using (var scorer = CudaScorerClient.Start(scorerPath, 512, 512, target, current))
            {
                var weights = new byte[512 * 512 * 2];
                for (var index = 0; index < weights.Length; index += 2) weights[index + 1] = 1;
                scorer.SetWeightMapAsync(weights, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var entry in CatalogMaskAtlas.Build()) CheckEntry(scorer, entry);
            }
        }

        private static void CheckEntry(CudaScorerClient scorer, CatalogMaskAtlasEntry entry)
        {
            var state = new ShapeState(entry.Identifier, 256, 256, 110, 90, 255, 255, 255, 255, 17);
            var alpha = CatalogMaskAtlas.RenderBinaryAlpha(entry, state);
            ulong covered = 0;
            for (var index = 0; index < alpha.Length; index++) if (alpha[index] >= 128) covered++;
            var candidates = new List<CudaCatalogCandidate>
            {
                new CudaCatalogCandidate { CandidateId = 7, Cx = state.Cx, Cy = state.Cy, Rx = state.Rx, Ry = state.Ry, Alpha = state.Alpha, AngleDegrees = (float)state.AngleDegrees }
            };
            var unweighted = scorer.ScoreCatalogAsync(entry, candidates, false, CancellationToken.None).GetAwaiter().GetResult().Scores[0];
            var weighted = scorer.ScoreCatalogAsync(entry, candidates, true, CancellationToken.None).GetAwaiter().GetResult().Scores[0];
            var expectedOldError = covered * 4UL * 255UL * 255UL;
            Check.Equal((long)expectedOldError, (long)unweighted.OldErrorDelta, entry.Slug + " GPU coverage error");
            Check.Equal((long)expectedOldError, (long)weighted.OldErrorDelta, entry.Slug + " weighted GPU coverage error");
            Check.Equal(0L, (long)unweighted.NewErrorDelta, entry.Slug + " GPU replacement error");
            Check.Equal(255, (int)unweighted.Red, entry.Slug + " GPU fitted red");
            Check.Equal(255, (int)unweighted.Green, entry.Slug + " GPU fitted green");
            Check.Equal(255, (int)unweighted.Blue, entry.Slug + " GPU fitted blue");
        }
    }
}
