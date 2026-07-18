using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GTAEmblemMaker.Core
{
    internal sealed class PerceptualBatchItem
    {
        internal string CandidateId { get; private set; }
        internal string TargetKey { get; private set; }

        internal PerceptualBatchItem(string candidateId, string targetKey)
        {
            if (String.IsNullOrWhiteSpace(candidateId)) throw new ArgumentException("Candidate ID is required.", "candidateId");
            if (String.IsNullOrWhiteSpace(targetKey)) throw new ArgumentException("Target key is required.", "targetKey");
            CandidateId = candidateId;
            TargetKey = targetKey;
        }
    }

    internal sealed class PerceptualScore
    {
        internal string CandidateId { get; private set; }
        internal string TileKey { get; private set; }
        internal double Score { get; private set; }

        internal PerceptualScore(string candidateId, string tileKey, double score)
        {
            CandidateId = candidateId;
            TileKey = tileKey;
            Score = score;
        }
    }

    internal sealed class PerceptualClient : IDisposable
    {
        private readonly InferenceSession session;
        private readonly int batchSize;
        private readonly SemaphoreSlim access = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, float[]> targetCache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private int disposed;

        private PerceptualClient(InferenceSession session, string backendName, string modelName, int modelSize, int batchSize)
        {
            this.session = session;
            this.batchSize = batchSize;
            BackendName = backendName;
            ModelName = modelName;
            ModelSize = modelSize;
        }

        internal string BackendName { get; private set; }
        internal string ModelName { get; private set; }
        internal int ModelSize { get; private set; }

        internal static PerceptualClient Start(string modelFolder, PerceptualRerank config, CancellationToken cancellationToken)
        {
            if (config == null) throw new ArgumentNullException("config");
            cancellationToken.ThrowIfCancellationRequested();
            if (config.Backend == "native-edge-detail") return new PerceptualClient(null, config.Backend, config.Model, config.TileSize, config.BatchSize);
            if (String.IsNullOrWhiteSpace(modelFolder) || !Directory.Exists(modelFolder)) throw new DirectoryNotFoundException("Perceptual model folder was not found.");
            var modelPath = Path.Combine(modelFolder, config.Model + ".onnx");
            if (!File.Exists(modelPath)) throw new FileNotFoundException("Perceptual model was not found.", modelPath);
            using (var options = new SessionOptions())
            {
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(modelPath, options);
                try
                {
                    NodeMetadata input;
                    if (!session.InputMetadata.TryGetValue("candidate", out input) || input.Dimensions.Length != 4 || input.Dimensions[2] < 1 || input.Dimensions[2] != input.Dimensions[3]) throw new InvalidDataException("Perceptual model candidate input must be square NCHW.");
                    return new PerceptualClient(session, config.Backend, config.Model, input.Dimensions[2], config.BatchSize);
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }
        }

        internal async Task<IReadOnlyList<PerceptualScore>> ScoreBatchAsync(IReadOnlyList<PerceptualBatchItem> items, IReadOnlyList<byte[]> candidates, IReadOnlyList<byte[]> targets, int width, int height, CancellationToken cancellationToken)
        {
            ValidateBatch(items, candidates, targets, width, height);
            await access.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("PerceptualClient");
                var scores = new List<PerceptualScore>(items.Count);
                if (session == null)
                {
                    for (var index = 0; index < items.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        scores.Add(new PerceptualScore(items[index].CandidateId, items[index].TargetKey, EdgeDetailScore(candidates[index], targets[index], width, height)));
                    }
                    return new ReadOnlyCollection<PerceptualScore>(scores);
                }
                var valuesPerImage = checked(3 * ModelSize * ModelSize);
                for (var start = 0; start < items.Count; start += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(batchSize, items.Count - start);
                    var candidateTensor = new float[checked(count * valuesPerImage)];
                    var targetTensor = new float[candidateTensor.Length];
                    for (var index = 0; index < count; index++)
                    {
                        var sourceIndex = start + index;
                        AppendTensor(candidates[sourceIndex], width, height, index, candidateTensor);
                        float[] target;
                        if (!targetCache.TryGetValue(items[sourceIndex].TargetKey, out target))
                        {
                            target = new float[valuesPerImage];
                            AppendTensor(targets[sourceIndex], width, height, 0, target);
                            targetCache.Add(items[sourceIndex].TargetKey, target);
                        }
                        Array.Copy(target, 0, targetTensor, index * valuesPerImage, valuesPerImage);
                    }
                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("candidate", new DenseTensor<float>(candidateTensor, new[] { count, 3, ModelSize, ModelSize })),
                        NamedOnnxValue.CreateFromTensor("target", new DenseTensor<float>(targetTensor, new[] { count, 3, ModelSize, ModelSize }))
                    };
                    using (var output = session.Run(inputs))
                    {
                        var distances = output.First(value => value.Name == "distance").AsEnumerable<float>().ToArray();
                        if (distances.Length != count) throw new InvalidDataException("Perceptual score count does not match the request.");
                        for (var index = 0; index < count; index++)
                        {
                            var score = distances[index];
                            if (Single.IsNaN(score) || Single.IsInfinity(score) || score < 0) throw new InvalidDataException("Perceptual score is invalid.");
                            var sourceIndex = start + index;
                            scores.Add(new PerceptualScore(items[sourceIndex].CandidateId, items[sourceIndex].TargetKey, score));
                        }
                    }
                }
                return new ReadOnlyCollection<PerceptualScore>(scores);
            }
            finally
            {
                access.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (session != null) session.Dispose();
            access.Dispose();
        }

        private static double EdgeDetailScore(byte[] candidate, byte[] target, int width, int height)
        {
            if (width < 3 || height < 3) return ColorMse(candidate, target);
            var colorError = 0.0;
            var edgeError = 0.0;
            for (var y = 1; y < height - 1; y++)
                for (var x = 1; x < width - 1; x++)
                {
                    var offset = (y * width + x) * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var delta = (double)candidate[offset + channel] - target[offset + channel];
                        colorError += delta * delta;
                    }
                    var gradientDelta = LuminanceGradient(candidate, width, x, y) - LuminanceGradient(target, width, x, y);
                    edgeError += gradientDelta * gradientDelta;
                }
            var pixels = (double)((width - 2) * (height - 2));
            return colorError / (pixels * 3 * 255 * 255) + 0.35 * edgeError / (pixels * 255 * 255);
        }

        private static double ColorMse(byte[] candidate, byte[] target)
        {
            var total = 0.0;
            for (var offset = 0; offset + 2 < candidate.Length && offset + 2 < target.Length; offset += 4)
                for (var channel = 0; channel < 3; channel++)
                {
                    var delta = (double)candidate[offset + channel] - target[offset + channel];
                    total += delta * delta;
                }
            return total / ((candidate.Length / 4.0) * 3 * 255 * 255);
        }

        private static double LuminanceGradient(byte[] pixels, int width, int x, int y)
        {
            return Math.Abs(Luminance(pixels, width, x + 1, y) - Luminance(pixels, width, x - 1, y))
                + Math.Abs(Luminance(pixels, width, x, y + 1) - Luminance(pixels, width, x, y - 1));
        }

        private static double Luminance(byte[] pixels, int width, int x, int y)
        {
            var offset = (y * width + x) * 4;
            return 0.2126 * pixels[offset] + 0.7152 * pixels[offset + 1] + 0.0722 * pixels[offset + 2];
        }

        private void AppendTensor(byte[] rgba, int width, int height, int batchIndex, float[] tensor)
        {
            for (var y = 0; y < ModelSize; y++)
            {
                var sourceY = (y + 0.5f) * height / ModelSize - 0.5f;
                var y0 = (int)Math.Floor(sourceY);
                var y1 = y0 + 1;
                var fy = sourceY - y0;
                for (var x = 0; x < ModelSize; x++)
                {
                    var sourceX = (x + 0.5f) * width / ModelSize - 0.5f;
                    var x0 = (int)Math.Floor(sourceX);
                    var x1 = x0 + 1;
                    var fx = sourceX - x0;
                    for (var component = 0; component < 3; component++)
                    {
                        var top = Channel(rgba, width, height, x0, y0, component) * (1 - fx) + Channel(rgba, width, height, x1, y0, component) * fx;
                        var bottom = Channel(rgba, width, height, x0, y1, component) * (1 - fx) + Channel(rgba, width, height, x1, y1, component) * fx;
                        var output = ((batchIndex * 3 + component) * ModelSize + y) * ModelSize + x;
                        tensor[output] = (top * (1 - fy) + bottom * fy) / 127.5f - 1.0f;
                    }
                }
            }
        }

        private static float Channel(byte[] rgba, int width, int height, int x, int y, int component)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(0, Math.Min(height - 1, y));
            return rgba[(y * width + x) * 4 + component];
        }

        private static void ValidateBatch(IReadOnlyList<PerceptualBatchItem> items, IReadOnlyList<byte[]> candidates, IReadOnlyList<byte[]> targets, int width, int height)
        {
            if (items == null) throw new ArgumentNullException("items");
            if (candidates == null) throw new ArgumentNullException("candidates");
            if (targets == null) throw new ArgumentNullException("targets");
            if (items.Count == 0 || items.Count != candidates.Count || items.Count != targets.Count) throw new ArgumentException("Perceptual batch counts must be equal and nonzero.");
            if (width <= 0 || height <= 0 || width > 512 || height > 512) throw new ArgumentOutOfRangeException("width");
            var bytes = checked(width * height * 4);
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] == null || candidates[index] == null || targets[index] == null) throw new ArgumentException("Perceptual batch entries cannot be null.");
                if (candidates[index].Length != bytes || targets[index].Length != bytes) throw new ArgumentException("Perceptual tile length does not match its dimensions.");
            }
        }

    }

    internal sealed class PerceptualSelection
    {
        internal FitCandidate Candidate { get; private set; }
        internal bool ChangedSelection { get; private set; }
        internal double Score { get; private set; }

        internal PerceptualSelection(FitCandidate candidate, bool changedSelection, double score)
        {
            Candidate = candidate;
            ChangedSelection = changedSelection;
            Score = score;
        }
    }

    internal static class PerceptualReranker
    {
        private const int CanvasSize = 512;
        private static readonly string[] ShapeFamilies = { "rotated", "rotated-triangle", "rotated-rect", "line-rect" };

        internal static bool ShouldRerank(PerceptualRerank config, int layer, int maximumLayers)
        {
            if (config == null || layer < config.FirstRerankLayer) return false;
            var every = layer > (int)Math.Floor(maximumLayers * 0.8) ? config.FinalEvery : config.MiddleEvery;
            return layer % Math.Max(1, every) == 0;
        }

        internal static async Task<PerceptualSelection> SelectAsync(PerceptualClient client, PerceptualRerank config, byte[] target, byte[] current, IReadOnlyList<CudaChainResult> chains, FitCandidate mseBest, CancellationToken cancellationToken, IReadOnlyList<FitCandidate> extraCandidates = null, bool compatibilityGeometry = false)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (config == null) throw new ArgumentNullException("config");
            if (chains == null) throw new ArgumentNullException("chains");
            if (mseBest == null) throw new ArgumentNullException("mseBest");
            var all = new List<FitCandidate>(chains.Count);
            for (var index = 0; index < chains.Count; index++)
            {
                var chain = chains[index];
                all.Add(CandidateGenerator.FromResidentResult(chain.ShapeKind, chain.Candidate, chain.Score));
            }
            if (extraCandidates != null) all.AddRange(extraCandidates);
            var candidates = SelectCandidates(all, config);
            if (candidates.Count == 0) return new PerceptualSelection(mseBest, false, Double.PositiveInfinity);

            var items = new List<PerceptualBatchItem>();
            var candidateTiles = new List<byte[]>();
            var targetTiles = new List<byte[]>();
            var targetCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var lines = compatibilityGeometry
                    ? FitMath.RasterizeCompatibilityCandidate(candidate, CanvasSize, CanvasSize)
                    : FitMath.Rasterize(candidate, CanvasSize, CanvasSize);
                var tiles = TilesForCandidate(lines, config);
                var candidateKey = CandidateKey(candidate);
                for (var tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
                {
                    var tile = tiles[tileIndex];
                    var tileKey = CanvasSize.ToString(CultureInfo.InvariantCulture) + ":" + tile.X + ":" + tile.Y + ":" + tile.Size + ":" + tile.Size;
                    byte[] targetTile;
                    if (!targetCache.TryGetValue(tileKey, out targetTile))
                    {
                        targetTile = FitMath.CropRgba(target, CanvasSize, tile.X, tile.Y, tile.Size, tile.Size);
                        targetCache.Add(tileKey, targetTile);
                    }
                    items.Add(new PerceptualBatchItem(candidateKey, tileKey));
                    candidateTiles.Add(FitMath.CropAfterCandidate(current, CanvasSize, candidate, lines, tile.X, tile.Y, tile.Size, tile.Size));
                    targetTiles.Add(targetTile);
                }
            }

            var scores = await client.ScoreBatchAsync(items, candidateTiles, targetTiles, Math.Min(CanvasSize, config.TileSize), Math.Min(CanvasSize, config.TileSize), cancellationToken).ConfigureAwait(false);
            var totals = new Dictionary<string, ScoreTotal>(StringComparer.Ordinal);
            for (var index = 0; index < scores.Count; index++)
            {
                ScoreTotal total;
                if (!totals.TryGetValue(scores[index].CandidateId, out total)) total = new ScoreTotal();
                total.Total += scores[index].Score;
                total.Count++;
                totals[scores[index].CandidateId] = total;
            }

            var mseRanks = Ranks(candidates, candidate => candidate.Energy);
            var perceptualRanks = Ranks(candidates, candidate => MeanScore(totals, candidate));
            var selected = candidates
                .OrderBy(candidate => mseRanks[candidate] + config.PerceptualRankWeight * perceptualRanks[candidate])
                .ThenBy(candidate => candidate.Energy)
                .ThenBy(candidate => candidate.CandidateId)
                .First();
            var changedSelection = CandidateKey(selected) != CandidateKey(mseBest);
            var score = MeanScore(totals, selected);
            return new PerceptualSelection(MapAcceptedPoolFamily(selected, mseBest), changedSelection, score);
        }

        internal static FitCandidate MapAcceptedPoolFamily(FitCandidate selected, FitCandidate mseBest)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            if (mseBest == null) throw new ArgumentNullException("mseBest");
            if (selected.Kind != CandidateShapeKind.OfficialCatalog) return selected;
            return new FitCandidate(selected.CandidateId, selected.Group, selected.Kind, selected.Shape, mseBest.PoolShapeFamily,
                selected.Cx, selected.Cy, selected.Rx, selected.Ry, selected.Red, selected.Green, selected.Blue, selected.Alpha,
                selected.AngleDegrees, selected.Energy, selected.OldErrorDelta, selected.NewErrorDelta);
        }

        internal static List<FitCandidate> SelectCandidates(List<FitCandidate> all, PerceptualRerank config)
        {
            var sorted = all.OrderBy(candidate => candidate.Energy).ThenBy(candidate => candidate.PoolShapeFamily, StringComparer.Ordinal).ThenBy(candidate => candidate.CandidateId).ToList();
            if (!config.ShapeBalanced) return sorted.Take(config.TopK).ToList();
            var selected = new List<FitCandidate>(config.TopK);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var familyIndex = 0; familyIndex < ShapeFamilies.Length; familyIndex++)
            {
                var family = ShapeFamilies[familyIndex];
                foreach (var candidate in sorted.Where(item => item.PoolShapeFamily == family).Take(config.EachTopK))
                {
                    if (keys.Add(CandidateKey(candidate))) selected.Add(candidate);
                }
            }
            var catalogFamilies = sorted.Select(candidate => candidate.PoolShapeFamily).Where(family => !ShapeFamilies.Contains(family)).Distinct(StringComparer.Ordinal).OrderBy(family => family, StringComparer.Ordinal).ToList();
            foreach (var family in catalogFamilies)
            {
                foreach (var candidate in sorted.Where(item => item.PoolShapeFamily == family).Take(config.EachTopK))
                {
                    if (keys.Add(CandidateKey(candidate))) selected.Add(candidate);
                }
            }
            return catalogFamilies.Count == 0
                ? selected.Take(config.TopK).ToList()
                : selected.OrderBy(candidate => candidate.Energy).ThenBy(candidate => candidate.PoolShapeFamily, StringComparer.Ordinal).ThenBy(candidate => candidate.CandidateId).Take(config.TopK).ToList();
        }

        private static Dictionary<FitCandidate, int> Ranks(List<FitCandidate> candidates, Func<FitCandidate, double> score)
        {
            var sorted = candidates.OrderBy(score).ThenBy(candidate => candidates.IndexOf(candidate)).ToList();
            var ranks = new Dictionary<FitCandidate, int>();
            for (var index = 0; index < sorted.Count; index++) ranks.Add(sorted[index], index + 1);
            return ranks;
        }

        private static double MeanScore(Dictionary<string, ScoreTotal> totals, FitCandidate candidate)
        {
            ScoreTotal total;
            return totals.TryGetValue(CandidateKey(candidate), out total) && total.Count > 0 ? total.Total / total.Count : Double.PositiveInfinity;
        }

        private static string CandidateKey(FitCandidate candidate)
        {
            return candidate.PoolShapeFamily + ":" + candidate.CandidateId.ToString(CultureInfo.InvariantCulture);
        }

        private static List<RerankTile> TilesForCandidate(IReadOnlyList<RasterLine> lines, PerceptualRerank config)
        {
            var tileSize = Math.Min(CanvasSize, config.TileSize);
            var positions = GridPositions(CanvasSize, tileSize, config.TileStride);
            var tiles = new List<RerankTile>();
            for (var yIndex = 0; yIndex < positions.Count; yIndex++)
                for (var xIndex = 0; xIndex < positions.Count; xIndex++)
                    tiles.Add(new RerankTile(positions[xIndex], positions[yIndex], tileSize));
            if (lines.Count == 0) return new List<RerankTile> { tiles[0] };
            var minX = lines.Min(line => line.X1);
            var maxX = lines.Max(line => line.X2);
            var minY = lines.Min(line => line.Y);
            var maxY = lines.Max(line => line.Y);
            return tiles.Select(tile => new { Tile = tile, Overlap = tile.Overlap(minX, minY, maxX, maxY) })
                .Where(item => item.Overlap > 0)
                .OrderByDescending(item => item.Overlap)
                .ThenBy(item => item.Tile.Y)
                .ThenBy(item => item.Tile.X)
                .Take(config.MaxTilesPerCandidate)
                .Select(item => item.Tile)
                .ToList();
        }

        private static List<int> GridPositions(int size, int tileSize, int stride)
        {
            if (tileSize >= size) return new List<int> { 0 };
            var positions = new List<int>();
            for (var value = 0; value <= size - tileSize; value += Math.Max(1, stride)) positions.Add(value);
            var last = size - tileSize;
            if (positions[positions.Count - 1] != last) positions.Add(last);
            return positions;
        }

        private struct ScoreTotal
        {
            internal double Total;
            internal int Count;
        }

        private sealed class RerankTile
        {
            internal int X { get; private set; }
            internal int Y { get; private set; }
            internal int Size { get; private set; }

            internal RerankTile(int x, int y, int size)
            {
                X = x;
                Y = y;
                Size = size;
            }

            internal int Overlap(int minX, int minY, int maxX, int maxY)
            {
                var x1 = Math.Max(X, minX);
                var y1 = Math.Max(Y, minY);
                var x2 = Math.Min(X + Size - 1, maxX);
                var y2 = Math.Min(Y + Size - 1, maxY);
                return x2 < x1 || y2 < y1 ? 0 : (x2 - x1 + 1) * (y2 - y1 + 1);
            }
        }
    }
}
