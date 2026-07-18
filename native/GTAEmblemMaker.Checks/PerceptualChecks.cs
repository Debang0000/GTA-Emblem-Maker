using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class PerceptualChecks
    {
        internal static void MeasureBatch(FitProfile profile)
        {
            const int count = 192;
            const int size = 256;
            var config = profile.Stages[0].PerceptualRerank;
            var model = RepositoryFile("third_party", "lpips-winml", "model", config.Model + ".onnx");
            var items = new PerceptualBatchItem[count];
            var candidates = new byte[count][];
            var targets = new byte[count][];
            var sharedTargets = new byte[4][];
            for (var tile = 0; tile < sharedTargets.Length; tile++) sharedTargets[tile] = Pattern(size, 17 + tile * 13);
            for (var index = 0; index < count; index++)
            {
                items[index] = new PerceptualBatchItem(index.ToString(), "tile-" + (index % 4));
                candidates[index] = Pattern(size, 31 + index);
                targets[index] = sharedTargets[index % 4];
            }

            var clock = Stopwatch.StartNew();
            using (var client = PerceptualClient.Start(Path.GetDirectoryName(model), config, CancellationToken.None))
            {
                var startupMilliseconds = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var scores = client.ScoreBatchAsync(items, candidates, targets, size, size, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("model={0} size={1} batch={2} startupMs={3:0.0} scoreMs={4:0.0} scores={5}", config.Model, client.ModelSize, config.BatchSize, startupMilliseconds, clock.Elapsed.TotalMilliseconds, scores.Count);
            }
        }

        internal static void Run(FitProfile profile)
        {
            Check.Throws<DirectoryNotFoundException>(() => PerceptualClient.Start("missing", profile.Stages[0].PerceptualRerank, CancellationToken.None), "perceptual model folder");
            var model = RepositoryFile("third_party", "lpips-winml", "model", profile.Stages[0].PerceptualRerank.Model + ".onnx");
            if (!File.Exists(model))
            {
                Console.WriteLine("SKIP perceptual integration: model is absent.");
                return;
            }

            const int size = 4;
            var candidate = new byte[size * size * 4];
            var target = new byte[candidate.Length];
            for (var index = 0; index < candidate.Length; index++)
            {
                candidate[index] = (byte)(index * 3);
                target[index] = (byte)(index * 5);
            }
            var items = new[] { new PerceptualBatchItem("candidate-1", "tile-1") };
            using (var client = PerceptualClient.Start(Path.GetDirectoryName(model), profile.Stages[0].PerceptualRerank, CancellationToken.None))
            {
                Check.Equal("lpips-directml", client.BackendName, "perceptual backend identity");
                Check.Equal(profile.Stages[0].PerceptualRerank.Model, client.ModelName, "perceptual model identity");
                var first = client.ScoreBatchAsync(items, new[] { candidate }, new[] { target }, size, size, CancellationToken.None).GetAwaiter().GetResult();
                var second = client.ScoreBatchAsync(items, new[] { candidate }, new[] { target }, size, size, CancellationToken.None).GetAwaiter().GetResult();
                Check.Equal(1, first.Count, "perceptual score count");
                Check.True(first[0].Score > 0, "perceptual positive score");
                Check.Equal(first[0].Score, second[0].Score, "perceptual target-cache score parity");
                Check.Equal("candidate-1", first[0].CandidateId, "perceptual candidate identity");
                CheckReranker(client, profile.Stages[0].PerceptualRerank);
            }
        }

        internal static void CheckNativeEdgeBackend(FitProfile profile)
        {
            var config = profile.Stages[0].PerceptualRerank;
            Check.Equal("native-edge-detail", config.Backend, "catalog accepted perceptual backend");
            const int size = 3;
            var candidate = new byte[size * size * 4];
            var target = new byte[candidate.Length];
            var right = (1 * size + 2) * 4;
            target[right] = target[right + 1] = target[right + 2] = 255;
            var items = new[] { new PerceptualBatchItem("edge", "tile") };
            using (var client = PerceptualClient.Start(null, config, CancellationToken.None))
            {
                Check.Equal("native-edge-detail", client.BackendName, "catalog runtime perceptual backend");
                var scores = client.ScoreBatchAsync(items, new[] { candidate }, new[] { target }, size, size, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(Math.Abs(0.35 - scores[0].Score) < 1e-12, "native edge accepted scorer formula");
            }
        }

        private static void CheckReranker(PerceptualClient client, PerceptualRerank config)
        {
            Check.True(config.FirstRerankLayer > 1, "perceptual schedule has a pre-rerank phase");
            Check.True(!PerceptualReranker.ShouldRerank(config, config.FirstRerankLayer - 1, 1600), "perceptual schedule before start");
            Check.True(PerceptualReranker.ShouldRerank(config, config.FirstRerankLayer, 1600), "perceptual schedule start");
            var target = new byte[512 * 512 * 4];
            var current = new byte[target.Length];
            for (var index = 0; index < target.Length; index += 4)
            {
                var pixel = index / 4;
                target[index] = (byte)(pixel * 3);
                target[index + 1] = (byte)(pixel * 5);
                target[index + 2] = (byte)(pixel * 7);
                target[index + 3] = 255;
                current[index] = current[index + 1] = current[index + 2] = current[index + 3] = 255;
            }
            var chains = new List<CudaChainResult>();
            for (uint kind = 0; kind < 4; kind++)
            {
                var id = 100 + kind;
                chains.Add(new CudaChainResult
                {
                    ShapeKind = kind,
                    Candidate = new CudaCandidate { CandidateId = id, GroupId = kind, Cx = 128 + (int)kind * 64, Cy = 256, Rx = 30, Ry = 20, Alpha = 128, AngleDegrees = kind * 20 },
                    Score = new CudaScore { CandidateId = id, Red = (byte)(40 + kind * 30), Green = (byte)(80 + kind * 20), Blue = (byte)(120 + kind * 10), Alpha = 128, Energy = 0.1 + kind }
                });
            }
            var mseBest = CandidateGenerator.FromResidentResult(0, chains[0].Candidate, chains[0].Score);
            var selection = PerceptualReranker.SelectAsync(client, config, target, current, chains, mseBest, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(selection.Candidate.CandidateId >= 100 && selection.Candidate.CandidateId <= 103, "perceptual rerank candidate");
            Check.True(selection.Score >= 0 && !Double.IsInfinity(selection.Score), "perceptual rerank score");
        }

        private static string RepositoryFile(params string[] parts)
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (folder != null)
            {
                var pathParts = new string[parts.Length + 1];
                pathParts[0] = folder.FullName;
                Array.Copy(parts, 0, pathParts, 1, parts.Length);
                var candidate = Path.Combine(pathParts);
                if (File.Exists(candidate)) return candidate;
                folder = folder.Parent;
            }
            return Path.Combine(parts);
        }

        private static byte[] Pattern(int size, int seed)
        {
            var rgba = new byte[size * size * 4];
            for (var index = 0; index < rgba.Length; index += 4)
            {
                var pixel = index / 4;
                rgba[index] = (byte)(pixel * 3 + seed);
                rgba[index + 1] = (byte)(pixel * 5 + seed * 2);
                rgba[index + 2] = (byte)(pixel * 7 + seed * 3);
                rgba[index + 3] = 255;
            }
            return rgba;
        }
    }
}
