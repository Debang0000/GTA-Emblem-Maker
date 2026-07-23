using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class FittingEngineChecks
    {
        internal static void Run(FitProfile perceptualProfile, FitProfile beamProfile, FitProfile cleanLogoProfile)
        {
            var beamBest = BeamFitter.BestIndices(new long[] { 9, 3, 3, 7 }, 2);
            Check.Equal(1, beamBest[0], "beam best index");
            Check.Equal(2, beamBest[1], "beam stable tie index");
            Check.False(PipelineEngine.CanShareBeam(beamProfile, perceptualProfile), "beam result sharing rejects greedy");
            CheckDispatch(perceptualProfile);
            CheckCleanLogoDispatch(cleanLogoProfile);
            CheckLayerOptimizer();
            var scorer = RepositoryFile("third_party", "cuda-scorer", "bin", "cuda-scorer.exe");
            if (!File.Exists(scorer))
            {
                Console.WriteLine("SKIP fitting integration: third_party\\cuda-scorer\\bin\\cuda-scorer.exe is absent.");
                return;
            }

            var folder = Path.Combine(Path.GetTempPath(), "GTAEmblemMaker-FittingCheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var opaque = LoadFixture(Path.Combine(folder, "opaque.png"), false);
                var transparent = LoadFixture(Path.Combine(folder, "transparent.png"), true);
                CheckRun(perceptualProfile, scorer, opaque, false, "f5d7fb4366bc5eabfa6835e61b3b6315719e1e38b3d44b103fff323709410dbd", "917c81060fcb9279dbedc3ddb39097e324a5ca582bf38028a3f96cd0aedb1ac7", null);
                CheckRun(perceptualProfile, scorer, transparent, true, "1919df54919f41b935792812875baf91188d3dd09fb94e321c6c51cf18205b11", "e821ce57fb70818b77cd5d469cf3b823fe62f3e97bf64c416ac362aaf6f60532", Path.Combine(folder, "runs"));
                CheckBeamRun(beamProfile, perceptualProfile, scorer, transparent);
                CheckCleanLogoRun(cleanLogoProfile, scorer, Path.Combine(folder, "clean-runs"));
                CheckBudget(perceptualProfile, scorer, opaque);
                CheckCancellation(perceptualProfile, scorer, opaque);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static void CheckBeamRun(FitProfile beamProfile, FitProfile greedyProfile, string scorer, SourceImage source)
        {
            var beamRequest = new FitRequest(beamProfile, source, scorer) { LayerLimit = 3, Timestamp = 1700000000000 };
            var progress = new List<FitProgress>();
            var beam = PipelineEngine.RunAsync(beamRequest, new InlineProgress<FitProgress>(progress.Add), CancellationToken.None).GetAwaiter().GetResult();
            var greedyRequest = new FitRequest(greedyProfile, source, scorer) { LayerLimit = 3, Timestamp = 1700000000000 };
            var greedy = FittingEngine.RunAsync(greedyRequest, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(3, beam.CompletedLayers, "beam completed layers");
            Check.True(progress.Count >= 2, "beam progress previews");
            Check.True(beam.BaseTotalError <= greedy.BaseTotalError, "beam non-regression against greedy");
            Check.True(beam.Payload.GeneratedCodeLength <= beamProfile.Stages[0].Budget, "beam payload budget");
            var weights = FitMath.BuildWeightMaps(source.CanonicalRgba, 512, 512)[beam.WeightMapId].Q8;
            Check.Equal(FitMath.WeightedFullError(source.CanonicalRgba, beam.CurrentRgba, weights), beam.BaseTotalError, "beam exact error parity");
            Check.Equal("#transparent", beam.Payload.BackgroundColor, "beam transparent background");
        }

        private static void CheckCleanLogoRun(FitProfile profile, string scorer, string artifactRoot)
        {
            var transparent = new byte[512 * 512 * 4];
            var targetState = new ShapeState("ellipse", 256, 256, 72, 48, 36, 148, 224, 255, 23);
            var source = SourceImage.FromCanonical(RunArtifacts.RenderShapeOnto(transparent, targetState, 4));
            var request = new FitRequest(profile, source, scorer) { LayerLimit = 2, Timestamp = 1700000000000 };
            var result = FittingEngine.RunAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(result.CompletedLayers >= 1 && result.CompletedLayers <= 2, "clean logo completes safe layers or stops early");
            Check.Equal(255, result.Shapes[0].Alpha, "clean logo committed opaque layer");
            var exact = RunArtifacts.RenderPayloadPreview(result.Shapes, result.Payload);
            Check.Equal(Hash(exact), Hash(result.CurrentRgba), "clean logo current uses exact payload replay");
            Check.True(result.Payload.GeneratedCodeLength <= profile.Stages[0].Budget, "clean logo payload budget");
            Check.True(result.CleanLogoMetrics != null, "clean logo metrics available");
            var artifacts = RunArtifacts.Write(request, result, artifactRoot);
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(artifacts.MetricsJson)) as Dictionary<string, object>;
            Check.True(root != null && root.ContainsKey("cleanLogo"), "clean logo metrics object");
            var clean = root["cleanLogo"] as Dictionary<string, object>;
            Check.True(clean != null, "clean logo metrics dictionary");
            Check.Equal(result.CleanLogoMetrics.SupportRejectedLayers, Convert.ToInt32(clean["supportRejectedLayers"]), "clean logo support rejection artifact metric");
            Check.Equal(result.CleanLogoMetrics.LocalRegressionRejectedLayers, Convert.ToInt32(clean["localRegressionRejectedLayers"]), "clean logo local rejection artifact metric");
            Check.Equal(result.CleanLogoMetrics.ColorSnappedLayers, Convert.ToInt32(clean["colorSnappedLayers"]), "clean logo color snap artifact metric");
            Check.Equal(result.CleanLogoMetrics.EdgeRemovedLayers, Convert.ToInt32(clean["edgeRemovedLayers"]), "clean logo edge removal artifact metric");
            Check.Equal(0, Convert.ToInt32(clean["finalSupportViolationPixels"]), "clean logo artifact support invariant");
            Check.Equal(0, Convert.ToInt32(clean["finalUnsupportedEdgePixels"]), "clean logo artifact edge invariant");
            Check.Equal(Hash(File.ReadAllBytes(artifacts.PreviewPng)), Hash(File.ReadAllBytes(artifacts.PayloadPreviewPng)), "clean logo artifact previews use audited sequence");
            var reloadedPreview = ReadPngAsPremultipliedRgba(artifacts.PreviewPng);
            Check.Equal(0, MaximumChannelDifference(result.CurrentRgba, reloadedPreview, 3, 4), "clean logo preview alpha parity");
            Check.True(MaximumChannelDifference(result.CurrentRgba, reloadedPreview, 0, 1) <= 1, "clean logo preview color quantization");
            result.CleanLogoMetrics.FinalSupportViolationPixels = 1;
            Check.Throws<InvalidOperationException>(() => RunArtifacts.Write(request, result, Path.Combine(artifactRoot, "invalid")), "clean logo invalid artifacts rejected");
            result.CleanLogoMetrics.FinalSupportViolationPixels = 0;
        }

        private static void CheckLayerOptimizer()
        {
            var windowTarget = new byte[512 * 512 * 4];
            var windowCurrent = new byte[windowTarget.Length];
            for (var y = 144; y < 240; y++)
            {
                for (var x = 96; x < 192; x++) windowCurrent[(y * 512 + x) * 4] = 255;
            }
            var windows = ExactPairRefiner.RankWindows(windowTarget, windowCurrent, 96, 48, 1);
            Check.Equal(96, windows[0].X, "exact pair highest-error window x");
            Check.Equal(144, windows[0].Y, "exact pair highest-error window y");

            var keep = LayerOptimizer.KeepIndices(new ulong[] { 10, 2, 2, 9 }, 2);
            Check.Equal(2, keep.Length, "layer optimizer keep count");
            Check.Equal(0, keep[0], "layer optimizer strongest index");
            Check.Equal(3, keep[1], "layer optimizer second strongest index");

            var state = new ShapeState("ellipse", 8, 8, 4, 3, 20, 40, 60, 128, 15);
            var initial = new byte[16 * 16 * 4];
            var replayed = LayerOptimizer.RebuildCurrent(initial, new[] { state }, 16);
            var expected = (byte[])initial.Clone();
            FitMath.ApplyCandidate(expected, 16, CandidateGenerator.FromShapeState(state));
            Check.Equal(Hash(expected), Hash(replayed), "layer optimizer replay parity");
            Check.True(!Object.ReferenceEquals(initial, replayed), "layer optimizer returns new buffer");

            var red = new ShapeState("rectangle", 8, 8, 8, 8, 255, 0, 0, 255, 0);
            var blue = new ShapeState("rectangle", 8, 8, 8, 8, 0, 0, 255, 255, 0);
            var target = LayerOptimizer.RebuildCurrent(initial, new[] { blue }, 16);
            var weights = FitMath.BuildWeightMaps(target, 16, 16)["uniform"].Q8;
            var exactKeep = LayerOptimizer.KeepIndicesByRemoval(target, initial, new[] { red, blue }, weights, 16, new ulong[] { 100, 10 }, 1, 2, CancellationToken.None);
            Check.Equal(1, exactKeep.Length, "exact removal keep count");
            Check.Equal(1, exactKeep[0], "exact removal removes obscured historical winner");
            var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Check.Throws<OperationCanceledException>(() => LayerOptimizer.KeepIndicesByRemoval(target, initial, new[] { red, blue }, weights, 16, new ulong[] { 100, 10 }, 1, 2, canceled.Token), "exact removal cancellation");
            canceled.Dispose();

            var green = new ShapeState("rectangle", 8, 8, 4, 4, 0, 255, 0, 255, 0);
            var weakest = LayerOptimizer.WeakestStates(new[] { red, blue, green }, new ulong[] { 100, 10, 20 }, 2);
            Check.Equal(2, weakest.Count, "replacement weakest state count");
            Check.True(Object.ReferenceEquals(blue, weakest[0]), "replacement weakest state first");
            Check.True(Object.ReferenceEquals(green, weakest[1]), "replacement weakest state second");

            var localTarget = new byte[16 * 16 * 4];
            var localBaseline = (byte[])localTarget.Clone();
            for (var x = 0; x < 4; x++) localBaseline[x * 4] = 100;
            var locallyWorse = (byte[])localTarget.Clone();
            locallyWorse[(12 * 16 + 12) * 4] = 150;
            var locallyBetter = (byte[])localBaseline.Clone();
            locallyBetter[0] = 0;
            locallyBetter[4] = 0;
            var localWeights = FitMath.BuildWeightMaps(localTarget, 16, 16)["uniform"].Q8;
            var baselineError = FitMath.WeightedFullError(localTarget, localBaseline, localWeights);
            var worseError = FitMath.WeightedFullError(localTarget, locallyWorse, localWeights);
            var betterError = FitMath.WeightedFullError(localTarget, locallyBetter, localWeights);
            Check.True(worseError < baselineError, "replacement globally better fixture");
            Check.False(LayerOptimizer.AllowsReplacement(localTarget, localBaseline, baselineError, locallyWorse, worseError, localWeights, 16, 8, 0), "replacement rejects local detail loss");
            Check.True(LayerOptimizer.AllowsReplacement(localTarget, localBaseline, baselineError, locallyBetter, betterError, localWeights, 16, 8, 0), "replacement accepts local non-regression");
        }

        private static void CheckDispatch(FitProfile profile)
        {
            var stage = FitMath.ResolveStage(profile, "current-image-fit");
            var beforeRerank = FittingEngine.CreateSelectRequest(stage, 800, FitMath.ShapeChoicesForLayer(stage, 800));
            Check.Equal((int)CudaSelectLayerMode.RotatedDeviceChunk, (int)beforeRerank.Mode, "perceptual pre-rerank dispatch");
            var rerank = FittingEngine.CreateSelectRequest(stage, 801, FitMath.ShapeChoicesForLayer(stage, 801));
            Check.Equal((int)CudaSelectLayerMode.MixedDeviceChunk, (int)rerank.Mode, "perceptual rerank dispatch");
            Check.Equal(1, (int)rerank.ShapeMask, "perceptual ellipse shape mask");
            Check.True(rerank.MutateAlpha, "perceptual mutates alpha");
            Check.Equal(1, (int)rerank.MinAlpha, "perceptual minimum alpha");
            Check.Equal(255, (int)rerank.MaxAlpha, "perceptual maximum alpha");
        }

        private static void CheckCleanLogoDispatch(FitProfile profile)
        {
            var stage = FitMath.ResolveStage(profile, "current-image-fit");
            var request = FittingEngine.CreateSelectRequest(stage, 1, FitMath.ShapeChoicesForLayer(stage, 1), fixedOpaqueAlpha: true);
            Check.Equal((int)CudaSelectLayerMode.MixedDeviceChunk, (int)request.Mode, "clean logo mixed dispatch");
            Check.False(request.MutateAlpha, "clean logo fixed alpha");
            Check.Equal(255, (int)request.MinAlpha, "clean logo minimum alpha");
            Check.Equal(255, (int)request.MaxAlpha, "clean logo maximum alpha");
            Check.Equal(255, (int)request.InitialAlpha, "clean logo initial alpha");
        }

        private static void CheckRun(FitProfile profile, string scorer, SourceImage source, bool transparent, string expectedTraceHash, string expectedCurrentHash, string artifactRoot)
        {
            var request = new FitRequest(profile, source, scorer) { LayerLimit = 3, Timestamp = 1700000000000 };
            var progress = new List<FitProgress>();
            var result = FittingEngine.RunAsync(request, new InlineProgress<FitProgress>(progress.Add), CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(3, result.CompletedLayers, "fitting completed layers");
            Check.Equal(3, result.Shapes.Count, "fitting shape count");
            Check.Equal(3, result.Trace.Count, "fitting trace count");
            Check.Equal(3, progress.Count, "fitting progress count");
            Check.True(progress[0].PreviewRgba != null, "first progress preview");
            Check.True(progress[2].PreviewRgba != null, "final progress preview");
            Check.Equal(512 * 512 * 4, progress[0].PreviewRgba.Length, "progress preview size");
            Check.True(!result.BudgetReached, "fitting budget not reached");
            Check.True(result.Payload.GeneratedCodeLength <= profile.Stages[0].Budget, "fitting payload budget");
            var maps = FitMath.BuildWeightMaps(source.CanonicalRgba, 512, 512);
            Check.Equal(FitMath.WeightedFullError(source.CanonicalRgba, result.CurrentRgba, maps[result.WeightMapId].Q8), result.BaseTotalError, "fitting delta total parity");
            if (transparent) Check.Equal("#transparent", result.Payload.BackgroundColor, "transparent fitting background");
            else Check.True(result.Payload.BackgroundColor != "#transparent", "opaque fitting background");
            Check.True(transparent ? result.Payload.Svg.Contains("fill=\"none\"") : !result.Payload.Svg.Contains("fill=\"none\""), "fitting SVG background");
            if (transparent)
            {
                Check.True(HasAlpha(result.CurrentRgba, 0), "transparent fitting alpha zero");
                Check.True(HasPartialAlpha(result.CurrentRgba), "transparent fitting partial alpha");
            }
            Check.Equal(expectedTraceHash + "|" + expectedCurrentHash, TraceHash(result.Trace) + "|" + Hash(result.CurrentRgba), "fitting output hashes");
            if (artifactRoot != null) CheckArtifacts(request, result, artifactRoot);
        }

        private static void CheckArtifacts(FitRequest request, FitResult result, string root)
        {
            var artifacts = RunArtifacts.Write(request, result, root);
            Check.Equal(10, Directory.GetFiles(artifacts.RunFolder).Length, "run artifact file count");
            Check.True(File.Exists(artifacts.PreviewPng), "run preview PNG");
            Check.True(File.Exists(artifacts.PayloadPreviewPng), "run payload preview PNG");
            Check.True(Hash(File.ReadAllBytes(artifacts.PreviewPng)) != Hash(File.ReadAllBytes(artifacts.PayloadPreviewPng)), "payload preview uses Rockstar rasterization");
            Check.True(File.Exists(artifacts.CanonicalTargetPng), "run canonical target PNG");
            Check.True(File.Exists(artifacts.ShapesJson), "run shapes JSON");
            Check.True(File.Exists(artifacts.WeightMapQ8), "run weight map Q8");
            Check.True(File.ReadAllText(artifacts.PayloadSvg).Contains("fill=\"none\""), "run transparent SVG");
            Check.Equal(result.Payload.ConsoleCode, File.ReadAllText(artifacts.ImportCode), "run import code");
            Check.Equal(request.Profile.SourceJson, File.ReadAllText(artifacts.ProfileJson), "run profile snapshot");
            Check.True(File.ReadAllText(artifacts.MetricsJson).Contains("profileSha256"), "run profile hash metadata");
            Check.False(File.ReadAllText(artifacts.MetricsJson).Contains("\"cleanLogo\""), "legacy metrics exclude clean logo object");
            Check.True(File.ReadAllText(artifacts.TraceJson).Contains("perceptualRerankApplied"), "run trace metadata");
            Check.True(File.ReadAllText(artifacts.ShapesJson).Contains("\"angleDegrees\""), "run shape state metadata");
            var expectedWeights = FitMath.BuildWeightMaps(request.Source.CanonicalRgba, 512, 512)[result.WeightMapId].Q8;
            Check.Equal(Hash(expectedWeights), Hash(File.ReadAllBytes(artifacts.WeightMapQ8)), "run weight map parity");
            Check.True(SourceImage.Load(artifacts.PreviewPng).IsTransparent, "run preview transparency");
            var reloadedTarget = ReadPngAsPremultipliedRgba(artifacts.CanonicalTargetPng);
            Check.Equal(0, MaximumChannelDifference(request.Source.CanonicalRgba, reloadedTarget, 3, 4), "run canonical target alpha");
            var colorDifference = MaximumChannelDifference(request.Source.CanonicalRgba, reloadedTarget, 0, 1);
            Check.True(colorDifference <= 1, "run canonical target color quantization max=" + colorDifference);
            Check.Equal(0, Directory.GetDirectories(root, ".tmp-*", SearchOption.TopDirectoryOnly).Length, "run temporary cleanup");
        }

        private static byte[] ReadPngAsPremultipliedRgba(string path)
        {
            BitmapSource source;
            using (var stream = File.OpenRead(path))
            {
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var red = pixels[index];
                pixels[index] = pixels[index + 2];
                pixels[index + 2] = red;
            }
            return pixels;
        }

        private static int MaximumChannelDifference(byte[] expected, byte[] actual, int start, int step)
        {
            if (expected.Length != actual.Length) throw new InvalidOperationException("Pixel buffer lengths differ.");
            var maximum = 0;
            for (var index = start; index < expected.Length; index += step)
            {
                maximum = Math.Max(maximum, Math.Abs(expected[index] - actual[index]));
            }
            return maximum;
        }

        private static void CheckBudget(FitProfile profile, string scorer, SourceImage source)
        {
            var initial = FitMath.CreateInitialCurrent(source.CanonicalRgba, false);
            var basePayload = RockstarExporter.CreateBuilder(false, initial[0], initial[1], initial[2], 1700000000000).Build().GeneratedCodeLength;
            var request = new FitRequest(profile, source, scorer) { LayerLimit = 1, BudgetLimit = basePayload, Timestamp = 1700000000000 };
            var result = FittingEngine.RunAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(result.BudgetReached, "fitting budget reached");
            Check.Equal(0, result.CompletedLayers, "fitting budget rollback layers");
            Check.True(result.Payload.GeneratedCodeLength <= basePayload, "fitting budget rollback payload");
        }

        private static void CheckCancellation(FitProfile profile, string scorer, SourceImage source)
        {
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => FittingEngine.RunAsync(new FitRequest(profile, source, scorer), null, cancellation.Token).GetAwaiter().GetResult(), "fitting pre-cancellation");
            cancellation.Dispose();
        }

        private static SourceImage LoadFixture(string path, bool transparent)
        {
            const int size = 32;
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var offset = (y * size + x) * 4;
                    pixels[offset] = (byte)((x * 11 + y * 3) & 255);
                    pixels[offset + 1] = (byte)((x * 5 + y * 13) & 255);
                    pixels[offset + 2] = (byte)((x * 17 + y * 7) & 255);
                    pixels[offset + 3] = transparent ? (byte)(((x + y) % 7 == 0) ? 0 : 160) : (byte)255;
                }
            }
            var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return SourceImage.Load(path);
        }

        private static string TraceHash(IReadOnlyList<FitLayerTrace> trace)
        {
            var text = new StringBuilder();
            for (var index = 0; index < trace.Count; index++)
            {
                var row = trace[index];
                text.Append(row.Layer).Append('|').Append(row.CandidateId).Append('|').Append(row.ShapeFamily).Append('|')
                    .Append(row.WeightMapId).Append('|').Append(row.GeneratedCodeLength).Append('|').Append(row.BaseTotalError).Append('|')
                    .Append(row.SelectedEnergy.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            return Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static bool HasAlpha(byte[] rgba, byte alpha)
        {
            for (var index = 3; index < rgba.Length; index += 4) if (rgba[index] == alpha) return true;
            return false;
        }

        private static bool HasPartialAlpha(byte[] rgba)
        {
            for (var index = 3; index < rgba.Length; index += 4) if (rgba[index] > 0 && rgba[index] < 255) return true;
            return false;
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

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> report;
            internal InlineProgress(Action<T> report) { this.report = report; }
            public void Report(T value) { report(value); }
        }
    }
}
