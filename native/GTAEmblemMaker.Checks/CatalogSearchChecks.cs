using System.Collections.Generic;
using System.Threading;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class CatalogSearchChecks
    {
        internal static void Run(string scorerPath)
        {
            var target = new byte[512 * 512 * 4];
            for (var y = 160; y < 352; y++)
                for (var x = 160; x < 352; x++)
                {
                    var offset = (y * 512 + x) * 4;
                    target[offset] = 220;
                    target[offset + 1] = 180;
                    target[offset + 2] = 140;
                    target[offset + 3] = 255;
                }
            var current = new byte[target.Length];
            var weights = new byte[512 * 512 * 2];
            for (var index = 0; index < weights.Length; index += 2) weights[index + 1] = 1;
            using (var scorer = CudaScorerClient.Start(scorerPath, 512, 512, target, current))
            {
                scorer.SetWeightMapAsync(weights, CancellationToken.None).GetAwaiter().GetResult();
                var config = new CatalogSearch(1, 4, new List<string> { "catalog-curve-61" });
                var selection = CatalogCandidateSearch.SelectAsync(scorer, config, 1, 2, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(selection.Best.Kind == CandidateShapeKind.OfficialCatalog, "catalog search candidate kind");
                Check.Equal("catalog-curve-61", selection.Best.Shape, "catalog search identity");
                Check.Equal(16, selection.Candidates.Count, "catalog search group finalists");
                var before = FitMath.WeightedFullError(target, current, weights);
                var after = FitMath.ApplyCandidateAndUpdateError(target, current, 512, selection.Best, weights, before);
                Check.True(after < before, "catalog search improves exact current raster");
                var state = CandidateGenerator.ToShapeState(selection.Best);
                var payload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
                Check.True(payload.GeneratedCodeLength <= 1250000, "catalog search payload budget");
                Check.Equal("#transparent", payload.BackgroundColor, "catalog search transparency");
            }
        }

        internal static void CheckPerceptualPool(FitProfile profile)
        {
            var candidates = new List<FitCandidate>();
            for (var index = 0; index < 16; index++)
            {
                candidates.Add(new FitCandidate((uint)index, 0, CandidateShapeKind.RotatedEllipse, "rotated", "rotated", 256, 256, 20, 20, 0, 0, 0, 255, 0, index, 0, 0));
                candidates.Add(new FitCandidate((uint)index, 0, CandidateShapeKind.OfficialCatalog, "catalog-curve-61", "catalog-curve-61", 256, 256, 20, 20, 0, 0, 0, 255, 0, index + 0.5, 0, 0));
            }
            var selected = PerceptualReranker.SelectCandidates(candidates, profile.Stages[0].PerceptualRerank);
            Check.True(selected.Exists(candidate => candidate.Kind == CandidateShapeKind.OfficialCatalog), "catalog candidates enter AlexNet rerank pool");
        }
    }
}
