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
                var entries = CatalogMaskAtlas.Build();
                foreach (var entry in entries) CheckEntry(scorer, entry);
                CheckResidentBatch(scorer, entries);
            }
        }

        private static void CheckResidentBatch(CudaScorerClient scorer, IReadOnlyList<CatalogMaskAtlasEntry> entries)
        {
            scorer.SetCatalogAtlasesAsync(entries, CancellationToken.None).GetAwaiter().GetResult();
            var first = new[] { new CudaCatalogCandidate { CandidateId = 31, Cx = 256, Cy = 256, Rx = 110, Ry = 90, Alpha = 255, AngleDegrees = 17 } };
            var second = new[] { new CudaCatalogCandidate { CandidateId = 32, Cx = 240, Cy = 220, Rx = 70, Ry = 55, Alpha = 220, AngleDegrees = 41 } };
            var expectedFirst = scorer.ScoreCatalogAsync(entries[0], first, true, CancellationToken.None).GetAwaiter().GetResult().Scores[0];
            var expectedSecond = scorer.ScoreCatalogAsync(entries[1], second, true, CancellationToken.None).GetAwaiter().GetResult().Scores[0];
            var actual = scorer.ScoreResidentCatalogAsync(new[] { new CudaCatalogBatch(0, first), new CudaCatalogBatch(1, second) }, true, CancellationToken.None).GetAwaiter().GetResult().Scores;
            CheckScore(expectedFirst, actual[0], "resident catalog first identity");
            CheckScore(expectedSecond, actual[1], "resident catalog second identity");
            scorer.SetCatalogAtlasesAsync(entries, CancellationToken.None).GetAwaiter().GetResult();
            var counters = scorer.PerformanceCounters;
            Check.Equal(1L, counters.CatalogAtlasUploadCount, "resident catalog uploads immutable atlases once");
            Check.Equal(1L, counters.ResidentCatalogScoreCommandCount, "resident catalog unified score command count");
            Check.Equal(2L, counters.ResidentCatalogCandidatesEvaluated, "resident catalog unified candidate count");
            Check.Equal(2L, counters.ResidentCatalogGpuKernelCount, "resident catalog per-identity GPU kernels");
            Check.Equal(1L, counters.ResidentCatalogSynchronizationCount, "resident catalog unified synchronization count");
        }

        private static void CheckScore(CudaScore expected, CudaScore actual, string name)
        {
            Check.Equal((int)expected.CandidateId, (int)actual.CandidateId, name + " candidate ID");
            Check.Equal((int)expected.Red, (int)actual.Red, name + " red");
            Check.Equal((int)expected.Green, (int)actual.Green, name + " green");
            Check.Equal((int)expected.Blue, (int)actual.Blue, name + " blue");
            Check.Equal((int)expected.Alpha, (int)actual.Alpha, name + " alpha");
            Check.Equal(expected.Energy, actual.Energy, name + " energy");
            Check.Equal((long)expected.OldErrorDelta, (long)actual.OldErrorDelta, name + " old error");
            Check.Equal((long)expected.NewErrorDelta, (long)actual.NewErrorDelta, name + " new error");
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
