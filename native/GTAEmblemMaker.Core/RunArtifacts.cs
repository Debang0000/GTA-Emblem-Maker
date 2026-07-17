using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace GTAEmblemMaker.Core
{
    public sealed class RunArtifactSet
    {
        public string RunFolder { get; private set; }
        public string PreviewPng { get; private set; }
        public string PayloadPreviewPng { get; private set; }
        public string CanonicalTargetPng { get; private set; }
        public string ShapesJson { get; private set; }
        public string WeightMapQ8 { get; private set; }
        public string PayloadSvg { get; private set; }
        public string ImportCode { get; private set; }
        public string MetricsJson { get; private set; }
        public string ProfileJson { get; private set; }
        public string TraceJson { get; private set; }

        internal RunArtifactSet(string folder)
        {
            RunFolder = folder;
            PreviewPng = Path.Combine(folder, "preview.png");
            PayloadPreviewPng = Path.Combine(folder, "payload-preview.png");
            CanonicalTargetPng = Path.Combine(folder, "canonical-target.png");
            ShapesJson = Path.Combine(folder, "shapes.json");
            WeightMapQ8 = Path.Combine(folder, "weight-map.q8");
            PayloadSvg = Path.Combine(folder, "payload.svg");
            ImportCode = Path.Combine(folder, "import.js");
            MetricsJson = Path.Combine(folder, "metrics.json");
            ProfileJson = Path.Combine(folder, "profile.json");
            TraceJson = Path.Combine(folder, "trace.json");
        }
    }

    public static class RunArtifacts
    {
        public static RunArtifactSet Write(FitRequest request, FitResult result)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTAEmblemMaker", "runs");
            return Write(request, result, root);
        }

        internal static RunArtifactSet Write(FitRequest request, FitResult result, string rootFolder)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (result == null) throw new ArgumentNullException("result");
            if (String.IsNullOrWhiteSpace(rootFolder)) throw new ArgumentException("Run root folder is required.", "rootFolder");
            Directory.CreateDirectory(rootFolder);
            var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + request.Profile.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var temporary = Path.Combine(rootFolder, ".tmp-" + name);
            var final = Path.Combine(rootFolder, name);
            Directory.CreateDirectory(temporary);
            try
            {
                WritePng(Path.Combine(temporary, "canonical-target.png"), request.Source.CanonicalRgba);
                WritePng(Path.Combine(temporary, "preview.png"), result.CurrentRgba);
                WritePng(Path.Combine(temporary, "payload-preview.png"), RenderPayloadPreview(result.Shapes, result.Payload));
                WriteShapes(Path.Combine(temporary, "shapes.json"), result.Shapes);
                File.WriteAllBytes(Path.Combine(temporary, "weight-map.q8"), FitMath.BuildWeightMaps(request.Source.CanonicalRgba, 512, 512)[result.WeightMapId].Q8);
                File.WriteAllText(Path.Combine(temporary, "payload.svg"), result.Payload.Svg, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(temporary, "import.js"), result.Payload.ConsoleCode, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(temporary, "profile.json"), request.Profile.SourceJson, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(temporary, "metrics.json"), Serialize(Metrics(request, result)), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(temporary, "trace.json"), Serialize(Trace(result.Trace)), new UTF8Encoding(false));
                Directory.Move(temporary, final);
                return new RunArtifactSet(final);
            }
            catch
            {
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch (Exception) { }
                throw;
            }
        }

        private static Dictionary<string, object> Metrics(FitRequest request, FitResult result)
        {
            var stage = FitMath.ResolveStage(request.Profile, "current-image-fit");
            var counters = result.PerformanceCounters;
            return new Dictionary<string, object>
            {
                { "engineVersion", EngineInfo.Version },
                { "profileId", request.Profile.Id },
                { "profileSchemaVersion", request.Profile.SchemaVersion },
                { "profileSha256", Hash(Encoding.UTF8.GetBytes(request.Profile.SourceJson)) },
                { "sourceTransparent", request.Source.IsTransparent },
                { "completedLayers", result.CompletedLayers },
                { "maximumLayers", stage.MaxLayers },
                { "budget", stage.Budget },
                { "generatedCodeLength", result.Payload.GeneratedCodeLength },
                { "budgetReached", result.BudgetReached },
                { "baseTotalError", result.BaseTotalError.ToString(CultureInfo.InvariantCulture) },
                { "weightMapId", result.WeightMapId },
                { "perceptualBackend", result.PerceptualBackend ?? "none" },
                { "perceptualModel", stage.PerceptualRerank == null ? "none" : stage.PerceptualRerank.Model },
                { "wallMilliseconds", result.WallMilliseconds },
                { "gpuExchangeCount", counters == null ? 0 : counters.GpuExchangeCount },
                { "gpuCommandCount", counters == null ? 0 : counters.GpuCommandCount },
                { "candidatesEvaluated", counters == null ? 0 : counters.CandidatesEvaluated },
                { "averageBatchSize", counters == null ? 0 : counters.AverageBatchSize },
                { "hostToDeviceBytes", counters == null ? 0 : counters.HostToDeviceBytes },
                { "hostDeviceSynchronizationCount", counters == null ? 0 : counters.HostDeviceSynchronizationCount },
                { "catalogAtlasUploadCount", counters == null ? 0 : counters.CatalogAtlasUploadCount },
                { "residentCatalogScoreCommandCount", counters == null ? 0 : counters.ResidentCatalogScoreCommandCount },
                { "residentCatalogGpuKernelCount", counters == null ? 0 : counters.ResidentCatalogGpuKernelCount },
                { "residentCatalogCandidatesEvaluated", counters == null ? 0 : counters.ResidentCatalogCandidatesEvaluated },
                { "residentCatalogSynchronizationCount", counters == null ? 0 : counters.ResidentCatalogSynchronizationCount },
                { "catalogHostToDeviceBytes", counters == null ? 0 : counters.CatalogHostToDeviceBytes }
            };
        }

        private static object[] Trace(IReadOnlyList<FitLayerTrace> trace)
        {
            var rows = new object[trace.Count];
            for (var index = 0; index < trace.Count; index++)
            {
                var row = trace[index];
                rows[index] = new Dictionary<string, object>
                {
                    { "layer", row.Layer },
                    { "candidateId", row.CandidateId },
                    { "shapeFamily", row.ShapeFamily },
                    { "weightMapId", row.WeightMapId },
                    { "generatedCodeLength", row.GeneratedCodeLength },
                    { "baseTotalError", row.BaseTotalError.ToString(CultureInfo.InvariantCulture) },
                    { "selectedEnergy", row.SelectedEnergy },
                    { "serverMilliseconds", row.ServerMilliseconds },
                    { "perceptualRerankApplied", row.PerceptualRerankApplied },
                    { "perceptualChangedSelection", row.PerceptualChangedSelection },
                    { "perceptualScore", row.PerceptualScore },
                    { "perceptualMilliseconds", row.PerceptualMilliseconds }
                };
            }
            return rows;
        }

        private static object[] Shapes(IReadOnlyList<ShapeState> shapes)
        {
            var rows = new object[shapes.Count];
            for (var index = 0; index < shapes.Count; index++)
            {
                var shape = shapes[index];
                rows[index] = new Dictionary<string, object>
                {
                    { "shape", shape.Shape },
                    { "cx", shape.Cx },
                    { "cy", shape.Cy },
                    { "rx", shape.Rx },
                    { "ry", shape.Ry },
                    { "red", shape.Red },
                    { "green", shape.Green },
                    { "blue", shape.Blue },
                    { "alpha", shape.Alpha },
                    { "angleDegrees", shape.AngleDegrees }
                };
            }
            return rows;
        }

        private static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value) + Environment.NewLine;
        }

        internal static void WritePng(string path, byte[] rgba)
        {
            if (rgba == null || rgba.Length != 512 * 512 * 4) throw new ArgumentException("Preview must be a 512x512 RGBA buffer.", "rgba");
            var bgra = (byte[])rgba.Clone();
            for (var index = 0; index < bgra.Length; index += 4)
            {
                var red = bgra[index];
                bgra[index] = bgra[index + 2];
                bgra[index + 2] = red;
            }
            var bitmap = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Pbgra32, null, bgra, 512 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        public static byte[] RenderPayloadPreview(IReadOnlyList<ShapeState> shapes, RockstarPayload payload)
        {
            if (shapes == null) throw new ArgumentNullException("shapes");
            if (payload == null) throw new ArgumentNullException("payload");
            var info = new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var bitmap = new SKBitmap(info))
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
            {
                var paths = new Dictionary<string, SKPath>(StringComparer.Ordinal);
                try
                {
                    canvas.Clear(BackgroundColor(payload.BackgroundColor));
                    for (var index = 0; index < shapes.Count; index++)
                    {
                        var state = shapes[index];
                        var export = GTAEmblemMaker.Core.Shapes.ToExportShape(state);
                        var values = RockstarExporter.MatrixValues(export);
                        var matrix = new SKMatrix
                        {
                            ScaleX = (float)values.A,
                            SkewX = (float)values.C,
                            TransX = (float)values.E,
                            SkewY = (float)values.B,
                            ScaleY = (float)values.D,
                            TransY = (float)values.F,
                            Persp2 = 1
                        };
                        paint.Color = new SKColor((byte)state.Red, (byte)state.Green, (byte)state.Blue, (byte)export.Alpha);
                        SKPath path;
                        if (!paths.TryGetValue(export.Definition.Slug, out path))
                        {
                            path = SKPath.ParseSvgPathData(export.Definition.Path);
                            paths.Add(export.Definition.Slug, path);
                        }
                        canvas.Save();
                        canvas.Concat(matrix);
                        canvas.DrawPath(path, paint);
                        canvas.Restore();
                    }
                    canvas.Flush();
                    var rgba = new byte[info.BytesSize];
                    Marshal.Copy(bitmap.GetPixels(), rgba, 0, rgba.Length);
                    return rgba;
                }
                finally
                {
                    foreach (var path in paths.Values) path.Dispose();
                }
            }
        }

        private static SKColor BackgroundColor(string value)
        {
            if (value == "#transparent") return SKColors.Transparent;
            return new SKColor(
                Convert.ToByte(value.Substring(1, 2), 16),
                Convert.ToByte(value.Substring(3, 2), 16),
                Convert.ToByte(value.Substring(5, 2), 16),
                255);
        }

        internal static void WriteShapes(string path, IReadOnlyList<ShapeState> shapes)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Shape path is required.", "path");
            if (shapes == null) throw new ArgumentNullException("shapes");
            File.WriteAllText(path, Serialize(Shapes(shapes)), new UTF8Encoding(false));
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
