using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--canonical-hash")
            {
                using (var sha256 = SHA256.Create()) Console.WriteLine(ToHex(sha256.ComputeHash(SourceImage.Load(args[1]).CanonicalRgba)));
                return;
            }
            if (args.Length == 2 && args[0] == "--catalog-canonical-check")
            {
                using (var sha256 = SHA256.Create())
                {
                    Check.Equal("80319d17544318d0ba44647cc3fdf10410b2f08605966aac282d2c91d97eb720", ToHex(sha256.ComputeHash(SourceImage.Load(args[1]).CanonicalRgba)), "native canonical RGBA diagnostic hash");
                }
                return;
            }
            if (args.Length == 3 && args[0] == "--canonical-output")
            {
                File.WriteAllBytes(args[2], SourceImage.Load(args[1]).CanonicalRgba);
                return;
            }
            if (args.Length == 3 && args[0] == "--catalog-final-parity")
            {
                RunCatalogFinalParity(args[1], args[2]);
                return;
            }
            if (args.Length == 4 && args[0] == "--catalog-prefix-check")
            {
                CatalogCompatibilityChecks.Run(args[1], args[2], args[3]);
                return;
            }
            if ((args.Length == 5 || args.Length == 7) && args[0] == "--catalog-checkpoint-check")
            {
                CatalogCompatibilityChecks.RunCatalogCheckpoint(args[1], args[2], args[3], args[4], args.Length == 7 ? Int32.Parse(args[5]) : 501, args.Length == 7 ? args[6] : null);
                return;
            }
            if (args.Length == 2 && args[0] == "--perceptual-check")
            {
                PerceptualChecks.Run(ProfileCatalog.Load(args[1]).Default);
                return;
            }
            if (args.Length == 2 && args[0] == "--perceptual-batch-check")
            {
                PerceptualChecks.MeasureBatch(ProfileCatalog.Load(args[1]).Default);
                return;
            }
            if (args.Length >= 4 && args.Length <= 7 && args[0] == "--formal-fit-canonical")
            {
                RunFormalFit(args, true);
                return;
            }
            if (args.Length > 0)
            {
                RunFormalFit(args, false);
                return;
            }
            Check.Equal("1.1.2", EngineInfo.Version, "engine version");

            var catalog = ProfileCatalog.Load(ProfileFolder());
            Check.Equal(3, catalog.Profiles.Count, "production profile count");
            Check.Equal("v1-beam-clean", catalog.Default.Id, "default profile");
            Check.Equal("Beam Clean", catalog.Default.DisplayName, "default profile display name");
            foreach (var profile in catalog.Profiles)
            {
                foreach (var stage in profile.Stages) Check.Equal(1270000, stage.Budget, profile.Id + " production budget");
                if (profile.Id == "v1-catalog-quality") Check.Equal("1.1.2", profile.MinimumEngineVersion, "catalog minimum engine version");
            }
            Check.Equal("beam", catalog.Default.Pipeline.Runner, "default pipeline runner");

            var validProfile = File.ReadAllText(Path.Combine(ProfileFolder(), "v1-beam-clean.json"));
            RejectProfile("{\"unknown\":true}", "unknown top-level field");
            RejectProfile(validProfile.Replace("  \"displayName\": \"Beam Clean\",\r\n", "").Replace("  \"displayName\": \"Beam Clean\",\n", ""), "missing display name");
            RejectProfile(validProfile.Replace("\"displayName\"", "\"displayLabel\""), "unknown display name field");
            RejectProfile(validProfile.Replace("\"id\": \"current-image-fit\",", "\"id\": \"current-image-fit\", \"unknownStageField\": true,"), "unknown stage field");
            RejectProfile(validProfile.Replace("\"beamWidth\": 2", "\"beamWidth\": 0"), "invalid beam width");
            RejectProfile(validProfile.Replace("\"runner\": \"beam\"", "\"runner\": \"unknown\""), "unknown pipeline runner");

            CheckImages();
            CheckRockstarExport();
            CheckRockstarExportReviewCases();
            BundleLauncherChecks.Run();
            OfficialCatalogCurveChecks.Run();
            OfficialCatalogRoundChecks.Run();
            CatalogMaskAtlasChecks.Run();
            CatalogGpuChecks.Run(Path.Combine(ProjectRoot(), "third_party", "cuda-scorer", "bin", "cuda-scorer.exe"));
            CatalogSearchChecks.Run(Path.Combine(ProjectRoot(), "third_party", "cuda-scorer", "bin", "cuda-scorer.exe"));
            FitMathChecks.Run(catalog.Default);
            CudaProtocolChecks.Run();
            PerceptualChecks.Run(catalog.Default);
            FitProfile beamCleanProfile = null;
            FitProfile perceptualProfile = null;
            FitProfile catalogProfile = null;
            for (var profileIndex = 0; profileIndex < catalog.Profiles.Count; profileIndex++)
            {
                if (catalog.Profiles[profileIndex].Id == "v1-beam-clean") beamCleanProfile = catalog.Profiles[profileIndex];
                if (catalog.Profiles[profileIndex].Id == "v1-perceptual") perceptualProfile = catalog.Profiles[profileIndex];
                if (catalog.Profiles[profileIndex].Id == "v1-catalog-quality") catalogProfile = catalog.Profiles[profileIndex];
            }
            Check.True(beamCleanProfile != null, "beam clean profile available");
            Check.Equal("beam", beamCleanProfile.Pipeline.Runner, "beam clean runner");
            Check.Equal(2, beamCleanProfile.Pipeline.BranchFactor, "beam clean branch factor");
            Check.True(perceptualProfile != null, "perceptual profile available");
            Check.Equal("greedy", perceptualProfile.Pipeline.Runner, "perceptual runner");
            Check.True(catalogProfile != null, "official catalog profile available");
            Check.Equal("catalog-compatible", catalogProfile.Pipeline.Runner, "official catalog runner");
            Check.Equal(11, catalogProfile.Stages[0].CatalogSearch.Identities.Count, "official catalog identity count");
            Check.Equal(501, catalogProfile.Stages[0].CatalogSearch.FromLayer, "official catalog first layer");
            Check.Equal(512, catalogProfile.Stages[0].CatalogSearch.CandidatesPerGroup, "official catalog candidate quota");
            Check.Equal(1001, catalogProfile.Stages[0].PerceptualRerank.FirstRerankLayer, "official catalog perceptual boundary");
            PerceptualChecks.CheckNativeEdgeBackend(catalogProfile);
            CatalogSearchChecks.CheckPerceptualPool(catalogProfile);
            FittingEngineChecks.Run(perceptualProfile, beamCleanProfile);
        }

        private static string ToHex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++) result.Append(bytes[index].ToString("x2"));
            return result.ToString();
        }

        private static void RunFormalFit(string[] args, bool canonical)
        {
            var minimum = canonical ? 4 : 3;
            var maximum = canonical ? 7 : 6;
            var command = canonical ? "--formal-fit-canonical" : "--formal-fit";
            if (args.Length < minimum || args.Length > maximum || args[0] != command) throw new ArgumentException("Usage: " + command + " <image-or-rgba> <output-root> " + (canonical ? "<timestamp> " : "") + "[layer-limit] [profile-folder] [profile-id]");
            var layerIndex = canonical ? 4 : 3;
            var profileFolderIndex = canonical ? 5 : 4;
            var profileIdIndex = canonical ? 6 : 5;
            var catalog = ProfileCatalog.Load(args.Length > profileFolderIndex ? args[profileFolderIndex] : ProfileFolder());
            var profile = catalog.Default;
            if (args.Length > profileIdIndex)
            {
                profile = null;
                for (var index = 0; index < catalog.Profiles.Count; index++)
                {
                    if (catalog.Profiles[index].Id == args[profileIdIndex]) profile = catalog.Profiles[index];
                }
                if (profile == null) throw new ArgumentException("Unknown profile ID: " + args[profileIdIndex], "args");
            }
            var source = canonical ? SourceImage.FromCanonical(File.ReadAllBytes(args[1])) : SourceImage.Load(args[1]);
            var root = ProjectRoot();
            var request = new FitRequest(
                profile,
                source,
                Path.Combine(root, "third_party", "cuda-scorer", "bin", "cuda-scorer.exe"),
                Path.Combine(root, "third_party", "lpips-winml", "model"));
            if (canonical) request.Timestamp = Int64.Parse(args[3]);
            if (args.Length > layerIndex) request.LayerLimit = Int32.Parse(args[layerIndex]);
            var progress = new DirectProgress<FitProgress>(value =>
            {
                if (value.Layer == 1 || value.Layer % 25 == 0) Console.WriteLine(value.Layer + "/" + value.MaximumLayers + " code=" + value.GeneratedCodeLength + " energy=" + value.Energy.ToString("0.000000"));
            });
            var result = PipelineEngine.RunAsync(request, progress, CancellationToken.None).GetAwaiter().GetResult();
            var artifacts = RunArtifacts.Write(request, result, args[2]);
            Console.WriteLine("RunFolder=" + artifacts.RunFolder);
        }

        private static void CheckRockstarExport()
        {
            var states = new[]
            {
                new ShapeState("ellipse", 25, 40, 15, 20, 17, 34, 51, 128, 15),
                new ShapeState("triangle", 85, 100, 35, 40, 68, 85, 102, 255, 0),
                new ShapeState("rectangle", 145, 160, 55, 60, 119, 136, 153, 200, 0)
            };
            const long timestamp = 1700000000000;

            var transparent = RockstarExporter.Build(states, true, timestamp);
            Check.Equal("#transparent", transparent.BackgroundColor, "transparent background metadata");
            Check.True(transparent.Svg.Contains("fill=\"none\""), "transparent SVG fill none");
            Check.True(transparent.ConsoleCode.Contains("DecompressionStream(\"gzip\")"), "gzip payload decoder");
            Check.True(transparent.ConsoleCode.Contains("/emblems/save"), "Rockstar save request");
            Check.Equal(4226, transparent.GeneratedCodeLength, "transparent server budget length");
            Check.True(transparent.ClipboardCodeLength < 2500, "transparent compressed JavaScript length");
            Check.Equal(transparent.ConsoleCode.Length, transparent.ClipboardCodeLength, "transparent clipboard code length");

            var transparentLayers = DecodeLayers(transparent.ConsoleCode);
            Check.Equal(4, transparentLayers.Length, "transparent layer count");
            Check.Equal("#transparent", LayerValue(transparentLayers[0], "color"), "transparent layer color");
            Check.Equal("rounds/01", LayerValue(transparentLayers[1], "slug"), "ellipse Rockstar slug");
            Check.Equal("angles/01", LayerValue(transparentLayers[2], "slug"), "triangle Rockstar slug");
            Check.Equal("rectangles/21", LayerValue(transparentLayers[3], "slug"), "rectangle Rockstar slug");
            Check.Equal("s17000000000000", LayerValue(transparentLayers[1], "id"), "ellipse 13-digit layer ID");
            Check.Equal(20, LayerNumber(transparentLayers[1], "y"), "ellipse y");
            Check.Equal(10, LayerNumber(transparentLayers[1], "x"), "ellipse x");
            Check.Equal(13.33378, LayerNumber(transparentLayers[1], "scaleY"), "ellipse scaleY");
            Check.Equal(10, LayerNumber(transparentLayers[1], "scaleX"), "ellipse scaleX");
            Check.Equal(50.19608, LayerNumber(transparentLayers[1], "opacity"), "ellipse opacity");
            Check.Equal(26.66667, LayerNumber(transparentLayers[2], "scaleY"), "triangle scaleY");
            Check.Equal(23.35591, LayerNumber(transparentLayers[2], "scaleX"), "triangle scaleX");
            Check.Equal(40, LayerNumber(transparentLayers[3], "scaleY"), "rectangle scaleY");
            Check.Equal(165.48819, LayerNumber(transparentLayers[3], "scaleX"), "rectangle scaleX");
            Check.Equal(78.43137, LayerNumber(transparentLayers[3], "opacity"), "rectangle opacity");
            Check.True(transparent.Svg.Contains("matrix(0.09659,0.02588,-0.03451,0.12879,15.68783,16.80014)"), "ellipse matrix");
            Check.True(transparent.Svg.Contains("matrix(0.23356,0,0,0.26667,49.99987,59.9995)"), "triangle matrix");
            Check.True(transparent.Svg.Contains("matrix(1.65488,0,0,0.4,90.00006,100)"), "rectangle matrix");

            var opaque = RockstarExporter.Build(states, false, timestamp);
            Check.Equal("#ffffff", opaque.BackgroundColor, "opaque background metadata");
            Check.True(opaque.Svg.Contains("fill=\"#ffffff\""), "opaque SVG fill");
            Check.Equal(4222, opaque.GeneratedCodeLength, "opaque server budget length");
            Check.True(opaque.ClipboardCodeLength < 2500, "opaque compressed JavaScript length");
            Check.Equal(opaque.ConsoleCode.Length, opaque.ClipboardCodeLength, "opaque clipboard code length");
            Check.Equal("#ffffff", LayerValue(DecodeLayers(opaque.ConsoleCode)[0], "color"), "opaque layer color");

            var compensated = RockstarExporter.Build(new[]
            {
                new ShapeState("ellipse", 20, 30, 1, 2, 1, 2, 3, 200, 0)
            }, true, timestamp);
            Check.Equal(9.80392, LayerNumber(DecodeLayers(compensated.ConsoleCode)[1], "opacity"), "minimum ellipse axis alpha compensation");
        }

        private static void CheckRockstarExportReviewCases()
        {
            const long timestamp = 1700000000000;
            var state = new ShapeState("rotated", 256, 256, 16, 8, 1, 2, 3, 128, 10.000005);
            var opaque = RockstarExporter.Build(new[] { state }, false, 12, 34, 56, timestamp);
            Check.Equal("#0c2238", opaque.BackgroundColor, "opaque RGB background metadata");
            Check.True(opaque.Svg.Contains("fill=\"#0c2238\""), "opaque RGB SVG fill");
            Check.Equal("#0c2238", LayerValue(DecodeLayers(opaque.ConsoleCode)[0], "color"), "opaque RGB layer color");
            Check.Equal("10", RockstarExporter.FormatNumber(10.000005), "JavaScript toFixed positive boundary");
            Check.Equal("10.00001", RockstarExporter.FormatNumber(10.000015), "JavaScript toFixed next boundary");
            Check.Equal("-10", RockstarExporter.FormatNumber(-10.000005), "JavaScript toFixed negative boundary");
            Check.Equal("-0.00001", RockstarExporter.FormatNumber(-0.000005), "JavaScript toFixed negative small boundary");

            var primary = new[]
            {
                new ShapeState("ellipse", 25, 40, 15, 20, 17, 34, 51, 128, 15),
                new ShapeState("triangle", 85, 100, 35, 40, 68, 85, 102, 255, 0),
                new ShapeState("rectangle", 145, 160, 55, 60, 119, 136, 153, 200, 0)
            };
            var compressed = RockstarExporter.Build(primary, true, timestamp);
            Check.True(compressed.ConsoleCode.Contains("DecompressionStream(\"gzip\")"), "compressed Rockstar payload");
            Check.True(compressed.ClipboardCodeLength < 2500, "compressed Rockstar payload length");
            CompareGolden("export-transparent.js.txt", RockstarExporter.Build(primary, true, timestamp));
            CompareGolden("export-opaque.js.txt", RockstarExporter.Build(primary, false, 12, 34, 56, timestamp));

            var incremental = RockstarExporter.CreateBuilder(true, 255, 255, 255, timestamp);
            for (var index = 0; index < primary.Length; index++)
            {
                incremental.Add(primary[index]);
                var prefix = new ShapeState[index + 1];
                Array.Copy(primary, prefix, prefix.Length);
                Check.Equal(RockstarExporter.Build(prefix, true, timestamp).GeneratedCodeLength, incremental.BudgetCodeLength, "incremental budget length " + (index + 1));
            }
            Check.Equal(RockstarExporter.Build(primary, true, timestamp).ConsoleCode, incremental.Build().ConsoleCode, "incremental payload parity");
            var budgetGuard = RockstarExporter.CreateBuilder(true, 255, 255, 255, timestamp);
            budgetGuard.Add(primary[0]);
            var acceptedLength = budgetGuard.BudgetCodeLength;
            Check.True(!budgetGuard.TryAdd(primary[1], acceptedLength), "incremental budget rejection");
            Check.Equal(1, budgetGuard.Count, "incremental budget rollback count");
            Check.Equal(acceptedLength, budgetGuard.Build().GeneratedCodeLength, "incremental budget rollback length");

            foreach (var alias in new[] { "rotated", "ellipse", "circle", "round" })
            {
                Check.Equal("rounds/01", LayerValue(DecodeLayers(RockstarExporter.Build(new[] { new ShapeState(alias, 32, 32, 8, 8, 1, 2, 3, 255, 0) }, true, timestamp).ConsoleCode)[1], "slug"), alias + " ellipse alias");
            }
            foreach (var alias in new[] { "triangle", "rotated-triangle" })
            {
                Check.Equal("angles/01", LayerValue(DecodeLayers(RockstarExporter.Build(new[] { new ShapeState(alias, 32, 32, 8, 8, 1, 2, 3, 255, 0) }, true, timestamp).ConsoleCode)[1], "slug"), alias + " triangle alias");
            }
            foreach (var alias in new[] { "rectangle", "rotated-rect", "line-rect" })
            {
                Check.Equal("rectangles/21", LayerValue(DecodeLayers(RockstarExporter.Build(new[] { new ShapeState(alias, 32, 32, 8, 8, 1, 2, 3, 255, 0) }, true, timestamp).ConsoleCode)[1], "slug"), alias + " rectangle alias");
            }

            foreach (var alpha in new[] { 0, 1, 254, 255 })
            {
                var compensated = RockstarExporter.Build(new[] { new ShapeState("ellipse", 32, 32, 1, 2, 1, 2, 3, alpha, 0) }, true, timestamp);
                var expectedOpacity = alpha < 2 ? 0.39216 : 12.54902;
                Check.Equal(expectedOpacity, LayerNumber(DecodeLayers(compensated.ConsoleCode)[1], "opacity"), "minimum ellipse alpha boundary " + alpha);
            }
            var empty = RockstarExporter.Build(new ShapeState[0], true, timestamp);
            Check.Equal(1, DecodeLayers(empty.ConsoleCode).Length, "empty payload background only");
            Check.Equal(empty.ConsoleCode, RockstarExporter.Build(new ShapeState[0], true, timestamp).ConsoleCode, "repeat deterministic console code");

            Check.Throws<ArgumentNullException>(() => RockstarExporter.Build(null, true, timestamp), "null state list");
            Check.Throws<ArgumentException>(() => RockstarExporter.Build(new ShapeState[] { null }, true, timestamp), "null state entry");
            Check.Throws<ArgumentException>(() => new ShapeState("unknown", 1, 1, 1, 1, 1, 2, 3, 4, 0), "unknown shape");
            Check.Throws<ArgumentOutOfRangeException>(() => new ShapeState("ellipse", Double.NaN, 1, 1, 1, 1, 2, 3, 4, 0), "non-finite center");
            Check.Throws<ArgumentOutOfRangeException>(() => new ShapeState("ellipse", 1, 1, -1, 1, 1, 2, 3, 4, 0), "negative radius");
            Check.Throws<ArgumentOutOfRangeException>(() => new ShapeState("ellipse", 1, 1, 1, 1, 1, 2, 3, 256, 0), "invalid alpha");
            Check.Throws<ArgumentOutOfRangeException>(() => new ShapeState("ellipse", 1, 1, 1, 1, 1, 2, 3, 4, -0.00001), "negative rotation");
            Check.Equal(180.0, new ShapeState("ellipse", 1, 1, 1, 1, 1, 2, 3, 4, 180).AngleDegrees, "float32 wrapped rotation boundary");
            Check.Throws<ArgumentOutOfRangeException>(() => new ShapeState("ellipse", 1, 1, 1, 1, 1, 2, 3, 4, 180.00001), "rotation above wrapped domain");
            Check.Throws<ArgumentOutOfRangeException>(() => new ShapeState("ellipse", 1, 1, 1, 1, 1, 2, 3, 4, 1e21), "out-of-domain rotation");
        }

        private static void CompareGolden(string fileName, RockstarPayload actual)
        {
            var expectedConsoleCode = File.ReadAllText(Path.Combine(ProjectRoot(), "native", "GTAEmblemMaker.Checks", "Golden", fileName));
            if (expectedConsoleCode.EndsWith("\n", StringComparison.Ordinal)) expectedConsoleCode = expectedConsoleCode.Substring(0, expectedConsoleCode.Length - 1);
            Check.Equal(expectedConsoleCode, actual.ConsoleCode, fileName + " console code");
            Check.Equal(DecodeSvg(expectedConsoleCode), actual.Svg, fileName + " SVG");
            Check.Equal(DecodeLayerJson(expectedConsoleCode), DecodeLayerJson(actual.ConsoleCode), fileName + " decoded layer JSON");
        }

        private static string DecodeSvg(string consoleCode)
        {
            return DecodePayload(consoleCode)[0];
        }

        private static string DecodeLayerJson(string consoleCode)
        {
            return DecodePayload(consoleCode)[1];
        }

        private static string[] DecodePayload(string consoleCode)
        {
            if (!consoleCode.Contains("DecompressionStream(\"gzip\")")) return DecodeManualPayload(consoleCode);
            const string prefix = "atob(\"";
            var start = consoleCode.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            var end = consoleCode.IndexOf("\")", start, StringComparison.Ordinal);
            var compressed = Convert.FromBase64String(consoleCode.Substring(start, end - start));
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray()).Split(new[] { '\0' }, 2);
            }
        }

        private static string[] DecodeManualPayload(string consoleCode)
        {
            const string svgPrefix = "var svgData = \"";
            const string layerPrefix = "var layerData = \"";
            var svgBase64 = ExtractQuoted(consoleCode, svgPrefix);
            var layerBase64 = ExtractQuoted(consoleCode, layerPrefix);
            return new[]
            {
                Encoding.UTF8.GetString(Convert.FromBase64String(svgBase64)),
                Encoding.UTF8.GetString(Convert.FromBase64String(layerBase64))
            };
        }

        private static string ExtractQuoted(string value, string prefix)
        {
            var start = value.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Missing payload section: " + prefix);
            start += prefix.Length;
            var end = value.IndexOf("\";", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("Missing payload terminator: " + prefix);
            return value.Substring(start, end - start);
        }

        internal static Dictionary<string, object>[] DecodeLayers(string consoleCode)
        {
            var json = DecodeLayerJson(consoleCode);
            Type serializerType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                serializerType = assembly.GetType("System.Web.Script.Serialization.JavaScriptSerializer", false);
                if (serializerType != null) break;
            }
            if (serializerType == null) throw new InvalidOperationException("JavaScriptSerializer is unavailable.");
            var serializer = Activator.CreateInstance(serializerType);
            var values = (object[])serializerType.GetMethod("DeserializeObject").Invoke(serializer, new object[] { json });
            var layers = new Dictionary<string, object>[values.Length];
            for (var index = 0; index < values.Length; index++) layers[index] = (Dictionary<string, object>)values[index];
            return layers;
        }

        private static void RunCatalogFinalParity(string selectedCandidatesPath, string acceptedFolder)
        {
            var states = CatalogCompatibilityChecks.LoadAcceptedStates(selectedCandidatesPath);
            var replayCandidates = CatalogCompatibilityChecks.LoadAcceptedReplayCandidates(selectedCandidatesPath);
            Check.Equal(1345, states.Count, "catalog compatibility accepted layer count");
            var importPath = Path.Combine(acceptedFolder, "manual-import-snippet.js");
            if (!File.Exists(importPath)) importPath = Path.Combine(acceptedFolder, "final-generated-code.txt");
            var svgPath = Path.Combine(acceptedFolder, "payload.svg");
            var previewPath = Path.Combine(acceptedFolder, "payload-preview.rgba");
            var expectedImport = File.ReadAllText(importPath);
            var expectedLayers = DecodeLayers(expectedImport);
            var expectedIds = ExtractLayerIds(expectedLayers);
            var timestampText = LayerValue(expectedLayers[1], "id");
            var timestamp = Int64.Parse(timestampText.Substring(1, timestampText.Length - 2));
            var expectedSvg = File.ReadAllText(svgPath);
            var expectedPreview = File.ReadAllBytes(previewPath);
            var payload = RockstarExporter.Build(states, true, 255, 255, 255, timestamp, true, 2, expectedIds);
            var actualPreview = RenderHistoricalCatalogPreviewAlias(replayCandidates, payload);
            var actualLayerJson = DecodeLayerJson(payload.ConsoleCode);
            var expectedLayerJson = DecodeLayerJson(expectedImport);
            var actualLayers = DecodeLayers(payload.ConsoleCode);
            var layerFieldDifference = DescribeLayerFieldDifference(expectedLayers, actualLayers);

            Check.Equal("#transparent", payload.BackgroundColor, "catalog compatibility transparency");
            Check.Equal(payload.ConsoleCode.Length, payload.GeneratedCodeLength, "catalog compatibility import length");
            if (payload.GeneratedCodeLength != 1249106) throw new InvalidOperationException("catalog compatibility generated length: expected 1249106, got " + payload.GeneratedCodeLength + " | expected import " + expectedImport.Length + " actual import " + payload.ConsoleCode.Length + " | expected layers " + expectedLayerJson.Length + " actual layers " + actualLayerJson.Length + " | svg " + DescribeDifference(expectedSvg, payload.Svg) + " | layers " + DescribeDifference(expectedLayerJson, actualLayerJson) + " | layer fields " + layerFieldDifference);
            Check.Equal(expectedImport, payload.ConsoleCode, "catalog compatibility import parity");
            Check.Equal(expectedSvg, payload.Svg, "catalog compatibility SVG parity");
            Check.Equal(expectedLayerJson, actualLayerJson, "catalog compatibility layer JSON parity");
            Check.SequenceEqual(expectedPreview, actualPreview, "catalog compatibility payload render parity");

            var layer22 = new ShapeState[22];
            for (var index = 0; index < layer22.Length; index++) layer22[index] = states[index];
            var layer22Payload = RockstarExporter.Build(layer22, true, 255, 255, 255, timestamp, true, 2, expectedIds);
            Check.Equal(21898, layer22Payload.GeneratedCodeLength, "catalog compatibility layer 22 serialized length");

            Console.WriteLine("layers=" + states.Count);
            Console.WriteLine("generatedCodeLength=" + payload.GeneratedCodeLength);
            Console.WriteLine("importSha256=" + HashText(payload.ConsoleCode));
            Console.WriteLine("svgSha256=" + HashText(payload.Svg));
            Console.WriteLine("payloadPreviewSha256=" + HashBytes(actualPreview));
            Console.WriteLine("layer22GeneratedCodeLength=" + layer22Payload.GeneratedCodeLength);
        }

        private static byte[] RenderHistoricalCatalogPreviewAlias(IReadOnlyList<FitCandidate> candidates, RockstarPayload payload)
        {
            var rgba = new byte[512 * 512 * 4];
            if (payload.BackgroundColor != "#transparent")
            {
                var red = Convert.ToByte(payload.BackgroundColor.Substring(1, 2), 16);
                var green = Convert.ToByte(payload.BackgroundColor.Substring(3, 2), 16);
                var blue = Convert.ToByte(payload.BackgroundColor.Substring(5, 2), 16);
                for (var offset = 0; offset < rgba.Length; offset += 4)
                {
                    rgba[offset] = red;
                    rgba[offset + 1] = green;
                    rgba[offset + 2] = blue;
                    rgba[offset + 3] = 255;
                }
            }
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (candidate.Shape == "ellipse" || candidate.Shape == "circle" || candidate.Shape == "round")
                    candidate = new FitCandidate(candidate.CandidateId, candidate.Group, candidate.Kind, candidate.Shape, candidate.PoolShapeFamily, candidate.Cx, candidate.Cy, candidate.Rx, candidate.Ry, candidate.Red, candidate.Green, candidate.Blue, candidate.Alpha, 0, candidate.Energy, candidate.OldErrorDelta, candidate.NewErrorDelta);
                FitMath.ApplyCompatibilityCandidate(rgba, 512, candidate);
            }
            return rgba;
        }

        private static string LayerValue(Dictionary<string, object> layer, string name)
        {
            return (string)layer[name];
        }

        private static string[] ExtractLayerIds(Dictionary<string, object>[] layers)
        {
            var ids = new string[Math.Max(0, layers.Length - 1)];
            for (var index = 1; index < layers.Length; index++) ids[index - 1] = LayerValue(layers[index], "id");
            return ids;
        }

        private static double LayerNumber(Dictionary<string, object> layer, string name)
        {
            return Convert.ToDouble(layer[name]);
        }

        private static void CheckImages()
        {
            Check.Equal(256000000, SourceImage.GetPixelByteCount(8000, 8000), "maximum decoded pixel byte count");
            Check.Throws<InvalidDataException>(() => SourceImage.GetPixelByteCount(8000, 8001), "decoded pixel limit");
            Check.Throws<InvalidDataException>(() => SourceImage.GetPixelByteCount(0, 1), "nonpositive decoded dimensions");

            var folder = Path.Combine(Path.GetTempPath(), "GTAEmblemMaker-ImageCheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var transparentPath = Path.Combine(folder, "transparent.png");
                var partialPath = Path.Combine(folder, "partial.png");
                var opaquePath = Path.Combine(folder, "opaque-checker.png");
                var wideOpaquePath = Path.Combine(folder, "wide-opaque-checker.png");
                WritePng(transparentPath, 2, 2, new byte[]
                {
                    10, 20, 30, 0, 40, 50, 60, 0,
                    70, 80, 90, 0, 100, 110, 120, 0
                });
                WritePng(partialPath, 2, 2, new byte[]
                {
                    64, 128, 192, 128, 64, 128, 192, 128,
                    64, 128, 192, 128, 64, 128, 192, 128
                });
                WritePng(opaquePath, 2, 2, new byte[]
                {
                    7, 23, 101, 255, 11, 29, 103, 255,
                    11, 29, 103, 255, 7, 23, 101, 255
                });
                WritePng(wideOpaquePath, 3, 1, new byte[]
                {
                    7, 23, 101, 255, 11, 29, 103, 255, 7, 23, 101, 255
                });

                var transparent = SourceImage.Load(transparentPath);
                Check.Equal(2, transparent.Width, "transparent source width");
                Check.Equal(2, transparent.Height, "transparent source height");
                Check.True(transparent.IsTransparent, "transparent source alpha");
                Check.Equal(512 * 512 * 4, transparent.CanonicalRgba.Length, "transparent canonical size");
                for (var offset = 0; offset < transparent.CanonicalRgba.Length; offset += 4)
                {
                    Check.Equal(0, transparent.CanonicalRgba[offset], "transparent premultiplied red");
                    Check.Equal(0, transparent.CanonicalRgba[offset + 1], "transparent premultiplied green");
                    Check.Equal(0, transparent.CanonicalRgba[offset + 2], "transparent premultiplied blue");
                    Check.Equal(0, transparent.CanonicalRgba[offset + 3], "transparent canonical alpha");
                }

                var partial = SourceImage.Load(partialPath);
                Check.True(partial.IsTransparent, "partial source alpha");
                Check.Equal(512 * 512 * 4, partial.CanonicalRgba.Length, "partial canonical size");
                var partialPixel = (256 * 512 + 256) * 4;
                Check.Equal(95, partial.CanonicalRgba[partialPixel], "partial premultiplied red");
                Check.Equal(64, partial.CanonicalRgba[partialPixel + 1], "partial premultiplied green");
                Check.Equal(32, partial.CanonicalRgba[partialPixel + 2], "partial premultiplied blue");
                Check.Equal(128, partial.CanonicalRgba[partialPixel + 3], "partial canonical alpha");

                var opaque = SourceImage.Load(opaquePath);
                Check.False(opaque.IsTransparent, "opaque checker alpha");
                Check.Equal(512 * 512 * 4, opaque.CanonicalRgba.Length, "opaque canonical size");
                var opaquePixel = (64 * 512 + 64) * 4;
                Check.Equal(101, opaque.CanonicalRgba[opaquePixel], "canonical red channel order");
                Check.Equal(23, opaque.CanonicalRgba[opaquePixel + 1], "canonical green channel order");
                Check.Equal(7, opaque.CanonicalRgba[opaquePixel + 2], "canonical blue channel order");
                Check.Equal(255, opaque.CanonicalRgba[opaquePixel + 3], "canonical alpha channel order");

                var wideOpaque = SourceImage.Load(wideOpaquePath);
                Check.Equal(3, wideOpaque.Width, "wide source width");
                Check.Equal(1, wideOpaque.Height, "wide source height");
                Check.False(wideOpaque.IsTransparent, "opaque checker remains opaque after square padding");
                Check.Equal(512 * 512 * 4, wideOpaque.CanonicalRgba.Length, "wide canonical size");
                Check.SequenceEqual(wideOpaque.CanonicalRgba, SourceImage.Load(wideOpaquePath).CanonicalRgba, "deterministic canonical image");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static void WritePng(string path, int width, int height, byte[] pixels)
        {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        private static void RejectProfile(string json, string name)
        {
            var folder = Path.Combine(Path.GetTempPath(), "GTAEmblemMaker-ProfileCheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "invalid.json"), json);
                Check.Throws<ProfileValidationException>(() => ProfileCatalog.Load(folder), name);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static string ProfileFolder()
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (folder != null)
            {
                var candidate = Path.Combine(folder.FullName, "profiles");
                if (Directory.Exists(candidate)) return candidate;
                folder = folder.Parent;
            }

            throw new InvalidOperationException("profiles folder not found");
        }

        private static string ProjectRoot()
        {
            return Directory.GetParent(ProfileFolder()).FullName;
        }

        private static string HashText(string value)
        {
            return HashBytes(Encoding.UTF8.GetBytes(value));
        }

        private static string HashBytes(byte[] bytes)
        {
            using (var sha256 = SHA256.Create()) return ToHex(sha256.ComputeHash(bytes));
        }

        private static string DescribeDifference(string expected, string actual)
        {
            var length = Math.Min(expected.Length, actual.Length);
            for (var index = 0; index < length; index++)
            {
                if (expected[index] != actual[index]) return "first diff @" + index + " expected `" + Snippet(expected, index) + "` actual `" + Snippet(actual, index) + "`";
            }
            if (expected.Length != actual.Length) return "same prefix, length expected " + expected.Length + " actual " + actual.Length;
            return "identical";
        }

        private static string DescribeLayerFieldDifference(Dictionary<string, object>[] expected, Dictionary<string, object>[] actual)
        {
            if (expected.Length != actual.Length) return "layer count expected " + expected.Length + " actual " + actual.Length;
            for (var index = 0; index < expected.Length; index++)
            {
                foreach (var pair in expected[index])
                {
                    if (pair.Key == "id") continue;
                    object actualValue;
                    if (!actual[index].TryGetValue(pair.Key, out actualValue)) return "layer " + index + " missing field " + pair.Key;
                    var expectedText = Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture);
                    var actualText = Convert.ToString(actualValue, System.Globalization.CultureInfo.InvariantCulture);
                    if (!String.Equals(expectedText, actualText, StringComparison.Ordinal)) return "layer " + index + " field " + pair.Key + " expected `" + expectedText + "` actual `" + actualText + "`";
                }
            }
            return "only id differs";
        }

        private static string Snippet(string value, int index)
        {
            var start = Math.Max(0, index - 32);
            var end = Math.Min(value.Length, index + 32);
            return value.Substring(start, end - start).Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private sealed class DirectProgress<T> : IProgress<T>
        {
            private readonly Action<T> report;
            internal DirectProgress(Action<T> report) { this.report = report; }
            public void Report(T value) { report(value); }
        }
    }

    internal static class Check
    {
        public static void Equal(string expected, string actual, string name)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
            }
        }

        public static void Equal(int expected, int actual, string name)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
            }
        }

        public static void Equal(double expected, double actual, string name)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
            }
        }

        public static void True(bool actual, string name)
        {
            if (!actual)
            {
                throw new InvalidOperationException(name + ": expected true");
            }
        }

        public static void False(bool actual, string name)
        {
            if (actual)
            {
                throw new InvalidOperationException(name + ": expected false");
            }
        }

        public static void SequenceEqual(byte[] expected, byte[] actual, string name)
        {
            if (expected.Length != actual.Length)
            {
                throw new InvalidOperationException(name + ": length differs");
            }

            for (var index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                {
                    throw new InvalidOperationException(name + ": differs at " + index + ", expected " + expected[index] + ", actual " + actual[index]);
                }
            }
        }

        public static void Throws<TException>(Action action, string name) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(name + ": expected " + typeof(TException).Name);
        }
    }
}
