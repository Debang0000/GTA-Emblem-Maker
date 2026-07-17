using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Web.Script.Serialization;

namespace GTAEmblemMaker.Core
{
    public sealed class ProfileValidationException : Exception
    {
        public ProfileValidationException(string message) : base(message) { }
    }

    public sealed class ProfileCatalog
    {
        public IReadOnlyList<FitProfile> Profiles { get; private set; }
        public FitProfile Default { get; private set; }

        private ProfileCatalog(List<FitProfile> profiles, FitProfile defaultProfile)
        {
            Profiles = new ReadOnlyCollection<FitProfile>(profiles);
            Default = defaultProfile;
        }

        public static ProfileCatalog Load(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new ProfileValidationException("Profile folder does not exist.");

            var paths = new List<string>(Directory.GetFiles(folder, "*.json"));
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            if (paths.Count == 0) throw new ProfileValidationException("Profile folder contains no JSON files.");

            var profiles = new List<FitProfile>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FitProfile defaultProfile = null;
            foreach (var path in paths)
            {
                var profile = ReadProfile(path);
                if (!ids.Add(profile.Id)) throw new ProfileValidationException("Duplicate profile ID: " + profile.Id);
                if (profile.IsDefault)
                {
                    if (defaultProfile != null) throw new ProfileValidationException("Multiple default profiles are not allowed.");
                    defaultProfile = profile;
                }
                profiles.Add(profile);
            }

            if (defaultProfile == null) throw new ProfileValidationException("Exactly one default profile is required.");
            return new ProfileCatalog(profiles, defaultProfile);
        }

        private static FitProfile ReadProfile(string path)
        {
            Dictionary<string, object> root;
            string sourceJson;
            try
            {
                sourceJson = File.ReadAllText(path);
                root = new JavaScriptSerializer().DeserializeObject(sourceJson) as Dictionary<string, object>;
            }
            catch (Exception exception)
            {
                throw new ProfileValidationException("Invalid profile JSON in " + Path.GetFileName(path) + ": " + exception.Message);
            }

            if (root == null) throw new ProfileValidationException("Profile root must be an object: " + Path.GetFileName(path));
            EnsureFieldsWithOptional(root, "profile", new[] { "pipeline" }, "schemaVersion", "id", "displayName", "isDefault", "minimumEngineVersion", "stages");

            var schemaVersion = RequiredInt(root, "schemaVersion", "profile");
            if (schemaVersion != 1) throw new ProfileValidationException("Unsupported schema version: " + schemaVersion);
            var id = RequiredString(root, "id", "profile");
            var displayName = RequiredString(root, "displayName", "profile");
            var minimumEngineVersion = RequiredString(root, "minimumEngineVersion", "profile");
            EnsureEngineCompatibility(minimumEngineVersion);

            var stageValues = RequiredArray(root, "stages", "profile");
            if (stageValues.Length == 0) throw new ProfileValidationException("Profile must contain at least one stage.");
            var stages = new List<FitStage>();
            var stageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in stageValues)
            {
                var stage = ReadStage(RequiredObject(value, "stage"));
                if (!stageIds.Add(stage.Id)) throw new ProfileValidationException("Duplicate stage ID: " + stage.Id);
                stages.Add(stage);
            }

            var pipeline = root.ContainsKey("pipeline")
                ? ReadPipeline(RequiredObject(root["pipeline"], "pipeline"))
                : PipelineSettings.Greedy;
            return new FitProfile(schemaVersion, id, displayName, RequiredBool(root, "isDefault", "profile"), minimumEngineVersion, stages, pipeline, sourceJson);
        }

        private static PipelineSettings ReadPipeline(Dictionary<string, object> value)
        {
            var optional = new[] { "beamWidth", "branchFactor", "windowSize", "windowStride", "windowCount", "selectedLayers", "exactLayers", "exactRounds", "pairCandidates" };
            EnsureFieldsWithOptional(value, "pipeline", optional, "runner");
            var runner = RequiredString(value, "runner", "pipeline");
            if (runner == "greedy")
            {
                if (value.Count != 1) throw new ProfileValidationException("Greedy pipeline does not accept beam or pair settings.");
                return PipelineSettings.Greedy;
            }
            if (runner != "beam" && runner != "beam-pair") throw new ProfileValidationException("Unknown pipeline runner: " + runner);
            RequirePipelineFields(value, "beamWidth", "branchFactor");
            var beamWidth = RequiredInt(value, "beamWidth", "pipeline");
            var branchFactor = RequiredInt(value, "branchFactor", "pipeline");
            if (beamWidth < 1 || beamWidth > 8 || branchFactor < 1 || branchFactor > 8) throw new ProfileValidationException("Beam width and branch factor must be between 1 and 8.");
            if (runner == "beam")
            {
                if (value.Count != 3) throw new ProfileValidationException("Beam pipeline does not accept pair settings.");
                return new PipelineSettings(runner, beamWidth, branchFactor, 0, 0, 0, 0, 0, 0, 0);
            }

            RequirePipelineFields(value, "windowSize", "windowStride", "windowCount", "selectedLayers", "exactLayers", "exactRounds", "pairCandidates");
            var windowSize = RequiredInt(value, "windowSize", "pipeline");
            var windowStride = RequiredInt(value, "windowStride", "pipeline");
            var windowCount = RequiredInt(value, "windowCount", "pipeline");
            var selectedLayers = RequiredInt(value, "selectedLayers", "pipeline");
            var exactLayers = RequiredInt(value, "exactLayers", "pipeline");
            var exactRounds = RequiredInt(value, "exactRounds", "pipeline");
            var pairCandidates = RequiredInt(value, "pairCandidates", "pipeline");
            if (windowSize < 8 || windowSize > 512 || windowStride < 1 || windowStride > windowSize || windowCount < 1 || selectedLayers < 2 || exactLayers < 2 || exactLayers > selectedLayers || exactRounds < 1 || pairCandidates < 1) throw new ProfileValidationException("Invalid exact pair pipeline settings.");
            return new PipelineSettings(runner, beamWidth, branchFactor, windowSize, windowStride, windowCount, selectedLayers, exactLayers, exactRounds, pairCandidates);
        }

        private static void RequirePipelineFields(Dictionary<string, object> value, params string[] fields)
        {
            for (var index = 0; index < fields.Length; index++)
            {
                if (!value.ContainsKey(fields[index])) throw new ProfileValidationException("Missing pipeline field: " + fields[index]);
            }
        }

        private static FitStage ReadStage(Dictionary<string, object> value)
        {
            EnsureFieldsWithOptional(value, "stage", new[] { "layerOptimization", "strokeSearch", "catalogSearch" }, "id", "maxLayers", "budget", "minAxis", "residentSelectLayer", "residentSelection", "residentDeviceChunk", "residentDeviceChunkRounds", "shapeChoicesByLayer", "opaqueWeightMapSchedule", "transparentWeightMapSchedule", "perceptualRerank");
            var id = RequiredString(value, "id", "stage");
            var maxLayers = RequiredInt(value, "maxLayers", "stage");
            var budget = RequiredInt(value, "budget", "stage");
            var minAxis = RequiredInt(value, "minAxis", "stage");
            var deviceChunkRounds = RequiredInt(value, "residentDeviceChunkRounds", "stage");
            if (maxLayers < 1 || budget < 1 || minAxis < 1 || deviceChunkRounds < 1) throw new ProfileValidationException("Stage numeric values must be positive.");

            var selection = RequiredString(value, "residentSelection", "stage");
            if (selection != "code-aware-proxy") throw new ProfileValidationException("Unknown resident selection: " + selection);

            return new FitStage(
                id,
                maxLayers,
                budget,
                minAxis,
                RequiredBool(value, "residentSelectLayer", "stage"),
                selection,
                RequiredBool(value, "residentDeviceChunk", "stage"),
                deviceChunkRounds,
                ReadShapeSchedule(RequiredArray(value, "shapeChoicesByLayer", "stage"), maxLayers),
                ReadWeightSchedule(RequiredArray(value, "opaqueWeightMapSchedule", "stage"), maxLayers),
                ReadWeightSchedule(RequiredArray(value, "transparentWeightMapSchedule", "stage"), maxLayers),
                ReadRerank(RequiredObject(value["perceptualRerank"], "perceptualRerank"), maxLayers),
                value.ContainsKey("layerOptimization")
                    ? ReadLayerOptimization(RequiredObject(value["layerOptimization"], "layerOptimization"))
                    : new LayerOptimization(0, 0, 2, 0, 64, 0.5),
                value.ContainsKey("strokeSearch")
                    ? ReadStrokeSearch(RequiredObject(value["strokeSearch"], "strokeSearch"), maxLayers, minAxis)
                    : null,
                value.ContainsKey("catalogSearch")
                    ? ReadCatalogSearch(RequiredObject(value["catalogSearch"], "catalogSearch"), maxLayers)
                    : null);
        }

        private static CatalogSearch ReadCatalogSearch(Dictionary<string, object> value, int maxLayers)
        {
            EnsureFields(value, "catalogSearch", "fromLayer", "candidatesPerGroup", "identities");
            var fromLayer = RequiredInt(value, "fromLayer", "catalogSearch");
            var candidatesPerGroup = RequiredInt(value, "candidatesPerGroup", "catalogSearch");
            if (fromLayer < 1 || fromLayer > maxLayers || candidatesPerGroup < 1 || candidatesPerGroup > 2048) throw new ProfileValidationException("Catalog search range is invalid.");
            var identities = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in RequiredArray(value, "identities", "catalogSearch"))
            {
                var identity = item as string;
                ShapeDefinition definition;
                if (identity == null || !OfficialCatalog.TryGetDefinition(identity, out definition) || !seen.Add(identity)) throw new ProfileValidationException("Invalid catalog search identity.");
                identities.Add(identity);
            }
            if (identities.Count == 0) throw new ProfileValidationException("Catalog search requires at least one identity.");
            return new CatalogSearch(fromLayer, candidatesPerGroup, identities);
        }

        private static StrokeSearch ReadStrokeSearch(Dictionary<string, object> value, int maxLayers, int stageMinAxis)
        {
            var multiScaleFields = new[] { "detailMinLength", "detailMaxLength", "contourMinLength", "contourMaxLength", "contourEvery", "finalContourEvery", "tileSize" };
            var optionalFields = new List<string>(multiScaleFields) { "structuralRefine" };
            EnsureFieldsWithOptional(value, "strokeSearch", optionalFields.ToArray(), "fromLayer", "every", "finalFromLayer", "finalEvery", "minAxis", "guideMode");
            var fromLayer = RequiredInt(value, "fromLayer", "strokeSearch");
            var every = RequiredInt(value, "every", "strokeSearch");
            var finalFromLayer = RequiredInt(value, "finalFromLayer", "strokeSearch");
            var finalEvery = RequiredInt(value, "finalEvery", "strokeSearch");
            var minAxis = RequiredInt(value, "minAxis", "strokeSearch");
            var guideMode = RequiredInt(value, "guideMode", "strokeSearch");
            if (fromLayer < 1 || fromLayer > maxLayers || finalFromLayer < fromLayer || finalFromLayer > maxLayers) throw new ProfileValidationException("Stroke search layer range is invalid.");
            if (every < 1 || finalEvery < 1) throw new ProfileValidationException("Stroke search cadence must be positive.");
            if (minAxis < 1 || minAxis > stageMinAxis) throw new ProfileValidationException("Stroke search minimum axis is invalid.");
            if (guideMode < 1 || guideMode > 2) throw new ProfileValidationException("Stroke search guide mode is invalid.");
            var hasMultiScale = false;
            for (var index = 0; index < multiScaleFields.Length; index++) hasMultiScale |= value.ContainsKey(multiScaleFields[index]);
            if (!hasMultiScale)
            {
                if (value.ContainsKey("structuralRefine")) throw new ProfileValidationException("Structural refinement requires multi-scale stroke search.");
                return new StrokeSearch(fromLayer, every, finalFromLayer, finalEvery, minAxis, guideMode, 0, 0, 0, 0, 0, 0, 0, null);
            }

            for (var index = 0; index < multiScaleFields.Length; index++)
            {
                if (!value.ContainsKey(multiScaleFields[index])) throw new ProfileValidationException("Multi-scale stroke search requires field: " + multiScaleFields[index]);
            }
            var detailMinLength = RequiredInt(value, "detailMinLength", "strokeSearch");
            var detailMaxLength = RequiredInt(value, "detailMaxLength", "strokeSearch");
            var contourMinLength = RequiredInt(value, "contourMinLength", "strokeSearch");
            var contourMaxLength = RequiredInt(value, "contourMaxLength", "strokeSearch");
            var contourEvery = RequiredInt(value, "contourEvery", "strokeSearch");
            var finalContourEvery = RequiredInt(value, "finalContourEvery", "strokeSearch");
            var tileSize = RequiredInt(value, "tileSize", "strokeSearch");
            if (detailMinLength < 1 || detailMaxLength < detailMinLength || contourMinLength < detailMaxLength || contourMaxLength < contourMinLength || contourMaxLength > 512) throw new ProfileValidationException("Multi-scale stroke length ranges are invalid.");
            if (contourEvery < every || contourEvery % every != 0 || finalContourEvery < finalEvery || finalContourEvery % finalEvery != 0) throw new ProfileValidationException("Contour cadence must be a multiple of stroke cadence.");
            if (tileSize < 8 || tileSize > 128) throw new ProfileValidationException("Stroke detail tile size must be between 8 and 128.");
            var structuralRefine = value.ContainsKey("structuralRefine")
                ? ReadStructuralRefine(RequiredObject(value["structuralRefine"], "structuralRefine"))
                : null;
            return new StrokeSearch(fromLayer, every, finalFromLayer, finalEvery, minAxis, guideMode, detailMinLength, detailMaxLength, contourMinLength, contourMaxLength, contourEvery, finalContourEvery, tileSize, structuralRefine);
        }

        private static StructuralRefine ReadStructuralRefine(Dictionary<string, object> value)
        {
            EnsureFields(value, "structuralRefine", "edgeDistanceWeight", "distanceLimit", "rounds", "maxPixelGainRegression");
            var edgeDistanceWeight = RequiredDouble(value, "edgeDistanceWeight", "structuralRefine");
            var distanceLimit = RequiredInt(value, "distanceLimit", "structuralRefine");
            var rounds = RequiredInt(value, "rounds", "structuralRefine");
            var maxPixelGainRegression = RequiredDouble(value, "maxPixelGainRegression", "structuralRefine");
            if (edgeDistanceWeight <= 0 || edgeDistanceWeight > 1) throw new ProfileValidationException("Structural edge distance weight must be greater than 0 and at most 1.");
            if (distanceLimit < 1 || distanceLimit > 32) throw new ProfileValidationException("Structural distance limit must be between 1 and 32.");
            if (rounds < 1 || rounds > 256) throw new ProfileValidationException("Structural refinement rounds must be between 1 and 256.");
            if (maxPixelGainRegression < 0 || maxPixelGainRegression > 1) throw new ProfileValidationException("Structural pixel gain regression must be between 0 and 1.");
            return new StructuralRefine(edgeDistanceWeight, distanceLimit, rounds, maxPixelGainRegression);
        }

        private static LayerOptimization ReadLayerOptimization(Dictionary<string, object> value)
        {
            EnsureFieldsWithOptional(value, "layerOptimization", new[] { "replacementPercent", "replacementTileSize", "maxTileRegressionPercent" }, "overfitPercent", "refinePercent", "rankingPoolMultiplier");
            var overfitPercent = RequiredInt(value, "overfitPercent", "layerOptimization");
            var refinePercent = RequiredInt(value, "refinePercent", "layerOptimization");
            var rankingPoolMultiplier = RequiredInt(value, "rankingPoolMultiplier", "layerOptimization");
            var replacementPercent = value.ContainsKey("replacementPercent") ? RequiredInt(value, "replacementPercent", "layerOptimization") : 0;
            var replacementTileSize = value.ContainsKey("replacementTileSize") ? RequiredInt(value, "replacementTileSize", "layerOptimization") : 64;
            var maxTileRegressionPercent = value.ContainsKey("maxTileRegressionPercent") ? RequiredDouble(value, "maxTileRegressionPercent", "layerOptimization") : 0.5;
            if (overfitPercent < 0 || overfitPercent > 100 || refinePercent < 0 || refinePercent > 100 || replacementPercent < 0 || replacementPercent > 100) throw new ProfileValidationException("Layer optimization percentages must be between 0 and 100.");
            if (rankingPoolMultiplier < 1 || rankingPoolMultiplier > 8) throw new ProfileValidationException("Layer ranking pool multiplier must be between 1 and 8.");
            if (replacementTileSize < 8 || replacementTileSize > 512) throw new ProfileValidationException("Layer replacement tile size must be between 8 and 512.");
            if (maxTileRegressionPercent < 0 || maxTileRegressionPercent > 100) throw new ProfileValidationException("Layer replacement tile regression must be between 0 and 100 percent.");
            if (overfitPercent == 0 && refinePercent != 0) throw new ProfileValidationException("Layer refinement requires overfitting to be enabled.");
            return new LayerOptimization(overfitPercent, refinePercent, rankingPoolMultiplier, replacementPercent, replacementTileSize, maxTileRegressionPercent);
        }

        private static List<ShapeChoice> ReadShapeSchedule(object[] values, int maxLayers)
        {
            var result = new List<ShapeChoice>();
            var previousLayer = 0;
            foreach (var value in values)
            {
                var entry = RequiredObject(value, "shapeChoicesByLayer entry");
                EnsureFields(entry, "shapeChoicesByLayer entry", "fromLayer", "shapes");
                var fromLayer = RequiredLayer(entry, "fromLayer", "shapeChoicesByLayer entry", maxLayers, previousLayer);
                var shapes = RequiredArray(entry, "shapes", "shapeChoicesByLayer entry");
                if (shapes.Length == 0) throw new ProfileValidationException("Shape schedule entry must contain shapes.");
                var parsedShapes = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var shapeValue in shapes)
                {
                    var shape = shapeValue as string;
                    if (shape == null || !KnownShapes.Contains(shape)) throw new ProfileValidationException("Unknown shape: " + (shape ?? "(non-string)"));
                    if (!seen.Add(shape)) throw new ProfileValidationException("Duplicate shape: " + shape);
                    parsedShapes.Add(shape);
                }
                result.Add(new ShapeChoice(fromLayer, parsedShapes));
                previousLayer = fromLayer;
            }
            RequireScheduleStartsAtOne(result.Count, result.Count == 0 ? 0 : result[0].FromLayer, "Shape");
            return result;
        }

        private static List<WeightMapChoice> ReadWeightSchedule(object[] values, int maxLayers)
        {
            var result = new List<WeightMapChoice>();
            var previousLayer = 0;
            foreach (var value in values)
            {
                var entry = RequiredObject(value, "weight map schedule entry");
                EnsureFields(entry, "weight map schedule entry", "fromLayer", "weightMapId");
                var fromLayer = RequiredLayer(entry, "fromLayer", "weight map schedule entry", maxLayers, previousLayer);
                var weightMapId = RequiredString(entry, "weightMapId", "weight map schedule entry");
                if (!KnownWeightMaps.Contains(weightMapId)) throw new ProfileValidationException("Unknown weight map: " + weightMapId);
                result.Add(new WeightMapChoice(fromLayer, weightMapId));
                previousLayer = fromLayer;
            }
            RequireScheduleStartsAtOne(result.Count, result.Count == 0 ? 0 : result[0].FromLayer, "Weight map");
            return result;
        }

        private static PerceptualRerank ReadRerank(Dictionary<string, object> value, int maxLayers)
        {
            EnsureFields(value, "perceptualRerank", "backend", "model", "batchSize", "shapeBalanced", "topK", "eachTopK", "firstRerankLayer", "tileSize", "tileStride", "maxTilesPerCandidate", "perceptualRankWeight", "middleEvery", "finalEvery", "batchFiles");
            var backend = RequiredString(value, "backend", "perceptualRerank");
            var model = RequiredString(value, "model", "perceptualRerank");
            if (backend != "lpips-directml") throw new ProfileValidationException("Unsupported perceptual rerank backend.");
            if (!String.Equals(Path.GetFileName(model), model, StringComparison.Ordinal) || model.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ProfileValidationException("Invalid perceptual model ID.");
            var topK = RequiredInt(value, "topK", "perceptualRerank");
            var batchSize = RequiredInt(value, "batchSize", "perceptualRerank");
            var eachTopK = RequiredInt(value, "eachTopK", "perceptualRerank");
            var firstRerankLayer = RequiredInt(value, "firstRerankLayer", "perceptualRerank");
            var tileSize = RequiredInt(value, "tileSize", "perceptualRerank");
            var tileStride = RequiredInt(value, "tileStride", "perceptualRerank");
            var maxTiles = RequiredInt(value, "maxTilesPerCandidate", "perceptualRerank");
            var middleEvery = RequiredInt(value, "middleEvery", "perceptualRerank");
            var finalEvery = RequiredInt(value, "finalEvery", "perceptualRerank");
            var rankWeight = RequiredDouble(value, "perceptualRankWeight", "perceptualRerank");
            if (topK < 1 || eachTopK < 1 || topK < eachTopK || batchSize < 1 || batchSize > 192 || firstRerankLayer < 1 || firstRerankLayer > maxLayers || tileSize < 1 || tileStride < 1 || maxTiles < 1 || middleEvery < 1 || finalEvery < 1 || rankWeight < 0 || rankWeight > 1) throw new ProfileValidationException("Invalid perceptual rerank range.");

            return new PerceptualRerank(backend, model, batchSize, RequiredBool(value, "shapeBalanced", "perceptualRerank"), topK, eachTopK, firstRerankLayer, tileSize, tileStride, maxTiles, rankWeight, middleEvery, finalEvery, RequiredBool(value, "batchFiles", "perceptualRerank"));
        }

        private static void EnsureEngineCompatibility(string minimumEngineVersion)
        {
            Version minimumVersion;
            Version engineVersion;
            if (!Version.TryParse(minimumEngineVersion, out minimumVersion) || !Version.TryParse(EngineInfo.Version, out engineVersion)) throw new ProfileValidationException("Invalid engine version.");
            if (minimumVersion > engineVersion) throw new ProfileValidationException("Profile requires engine version " + minimumEngineVersion + ".");
        }

        private static int RequiredLayer(Dictionary<string, object> value, string name, string context, int maxLayers, int previousLayer)
        {
            var layer = RequiredInt(value, name, context);
            if (layer < 1 || layer > maxLayers || layer <= previousLayer) throw new ProfileValidationException("Invalid " + context + " layer.");
            return layer;
        }

        private static void RequireScheduleStartsAtOne(int count, int firstLayer, string name)
        {
            if (count == 0 || firstLayer != 1) throw new ProfileValidationException(name + " schedule must start at layer 1.");
        }

        private static void EnsureFields(Dictionary<string, object> value, string context, params string[] fields)
        {
            var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
            foreach (var key in value.Keys)
            {
                if (!allowed.Contains(key)) throw new ProfileValidationException("Unknown " + context + " field: " + key);
            }
            foreach (var field in fields)
            {
                if (!value.ContainsKey(field)) throw new ProfileValidationException("Missing " + context + " field: " + field);
            }
        }

        private static void EnsureFieldsWithOptional(Dictionary<string, object> value, string context, string[] optionalFields, params string[] requiredFields)
        {
            var allowed = new HashSet<string>(requiredFields, StringComparer.Ordinal);
            for (var index = 0; index < optionalFields.Length; index++) allowed.Add(optionalFields[index]);
            foreach (var key in value.Keys)
            {
                if (!allowed.Contains(key)) throw new ProfileValidationException("Unknown " + context + " field: " + key);
            }
            foreach (var field in requiredFields)
            {
                if (!value.ContainsKey(field)) throw new ProfileValidationException("Missing " + context + " field: " + field);
            }
        }

        private static Dictionary<string, object> RequiredObject(object value, string context)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new ProfileValidationException(context + " must be an object.");
            return result;
        }

        private static object[] RequiredArray(Dictionary<string, object> value, string name, string context)
        {
            var result = value[name] as object[];
            if (result == null) throw new ProfileValidationException(context + "." + name + " must be an array.");
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name, string context)
        {
            var result = value[name] as string;
            if (String.IsNullOrWhiteSpace(result)) throw new ProfileValidationException(context + "." + name + " must be a non-empty string.");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name, string context)
        {
            if (!(value[name] is bool)) throw new ProfileValidationException(context + "." + name + " must be a Boolean.");
            return (bool)value[name];
        }

        private static int RequiredInt(Dictionary<string, object> value, string name, string context)
        {
            if (!(value[name] is int)) throw new ProfileValidationException(context + "." + name + " must be an integer.");
            return (int)value[name];
        }

        private static double RequiredDouble(Dictionary<string, object> value, string name, string context)
        {
            var number = value[name];
            if (number is int) return (int)number;
            if (number is decimal) return (double)(decimal)number;
            if (number is double) return (double)number;
            throw new ProfileValidationException(context + "." + name + " must be a number.");
        }

        private static readonly HashSet<string> KnownShapes = new HashSet<string>(new[] { "rotated", "rotated-triangle", "rotated-rect", "line-rect" }, StringComparer.Ordinal);
        private static readonly HashSet<string> KnownWeightMaps = new HashSet<string>(new[] { "uniform", "alpha-protect", "edge-plus-laplacian", "edge-plus-laplacian-alpha-protect" }, StringComparer.Ordinal);
    }

    public sealed class FitProfile
    {
        public int SchemaVersion { get; private set; }
        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public bool IsDefault { get; private set; }
        public string MinimumEngineVersion { get; private set; }
        public IReadOnlyList<FitStage> Stages { get; private set; }
        public PipelineSettings Pipeline { get; private set; }
        public string SourceJson { get; private set; }

        internal FitProfile(int schemaVersion, string id, string displayName, bool isDefault, string minimumEngineVersion, List<FitStage> stages, PipelineSettings pipeline, string sourceJson)
        {
            SchemaVersion = schemaVersion;
            Id = id;
            DisplayName = displayName;
            IsDefault = isDefault;
            MinimumEngineVersion = minimumEngineVersion;
            Stages = new ReadOnlyCollection<FitStage>(stages);
            Pipeline = pipeline;
            SourceJson = sourceJson;
        }
    }

    public sealed class PipelineSettings
    {
        public static readonly PipelineSettings Greedy = new PipelineSettings("greedy", 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public string Runner { get; private set; }
        public int BeamWidth { get; private set; }
        public int BranchFactor { get; private set; }
        public int WindowSize { get; private set; }
        public int WindowStride { get; private set; }
        public int WindowCount { get; private set; }
        public int SelectedLayers { get; private set; }
        public int ExactLayers { get; private set; }
        public int ExactRounds { get; private set; }
        public int PairCandidates { get; private set; }

        internal PipelineSettings(string runner, int beamWidth, int branchFactor, int windowSize, int windowStride, int windowCount, int selectedLayers, int exactLayers, int exactRounds, int pairCandidates)
        {
            Runner = runner;
            BeamWidth = beamWidth;
            BranchFactor = branchFactor;
            WindowSize = windowSize;
            WindowStride = windowStride;
            WindowCount = windowCount;
            SelectedLayers = selectedLayers;
            ExactLayers = exactLayers;
            ExactRounds = exactRounds;
            PairCandidates = pairCandidates;
        }
    }

    public sealed class FitStage
    {
        public string Id { get; private set; }
        public int MaxLayers { get; private set; }
        public int Budget { get; private set; }
        public int MinAxis { get; private set; }
        public bool ResidentSelectLayer { get; private set; }
        public string ResidentSelection { get; private set; }
        public bool ResidentDeviceChunk { get; private set; }
        public int ResidentDeviceChunkRounds { get; private set; }
        public IReadOnlyList<ShapeChoice> ShapeChoicesByLayer { get; private set; }
        public IReadOnlyList<WeightMapChoice> OpaqueWeightMapSchedule { get; private set; }
        public IReadOnlyList<WeightMapChoice> TransparentWeightMapSchedule { get; private set; }
        public PerceptualRerank PerceptualRerank { get; private set; }
        public LayerOptimization LayerOptimization { get; private set; }
        public StrokeSearch StrokeSearch { get; private set; }
        public CatalogSearch CatalogSearch { get; private set; }

        internal FitStage(string id, int maxLayers, int budget, int minAxis, bool residentSelectLayer, string residentSelection, bool residentDeviceChunk, int residentDeviceChunkRounds, List<ShapeChoice> shapeChoicesByLayer, List<WeightMapChoice> opaqueWeightMapSchedule, List<WeightMapChoice> transparentWeightMapSchedule, PerceptualRerank perceptualRerank, LayerOptimization layerOptimization, StrokeSearch strokeSearch, CatalogSearch catalogSearch)
        {
            Id = id;
            MaxLayers = maxLayers;
            Budget = budget;
            MinAxis = minAxis;
            ResidentSelectLayer = residentSelectLayer;
            ResidentSelection = residentSelection;
            ResidentDeviceChunk = residentDeviceChunk;
            ResidentDeviceChunkRounds = residentDeviceChunkRounds;
            ShapeChoicesByLayer = new ReadOnlyCollection<ShapeChoice>(shapeChoicesByLayer);
            OpaqueWeightMapSchedule = new ReadOnlyCollection<WeightMapChoice>(opaqueWeightMapSchedule);
            TransparentWeightMapSchedule = new ReadOnlyCollection<WeightMapChoice>(transparentWeightMapSchedule);
            PerceptualRerank = perceptualRerank;
            LayerOptimization = layerOptimization;
            StrokeSearch = strokeSearch;
            CatalogSearch = catalogSearch;
        }
    }

    public sealed class CatalogSearch
    {
        public int FromLayer { get; private set; }
        public int CandidatesPerGroup { get; private set; }
        public IReadOnlyList<string> Identities { get; private set; }

        internal CatalogSearch(int fromLayer, int candidatesPerGroup, List<string> identities)
        {
            FromLayer = fromLayer;
            CandidatesPerGroup = candidatesPerGroup;
            Identities = new ReadOnlyCollection<string>(identities);
        }
    }

    public sealed class StrokeSearch
    {
        public int FromLayer { get; private set; }
        public int Every { get; private set; }
        public int FinalFromLayer { get; private set; }
        public int FinalEvery { get; private set; }
        public int MinAxis { get; private set; }
        public int GuideMode { get; private set; }
        public int DetailMinLength { get; private set; }
        public int DetailMaxLength { get; private set; }
        public int ContourMinLength { get; private set; }
        public int ContourMaxLength { get; private set; }
        public int ContourEvery { get; private set; }
        public int FinalContourEvery { get; private set; }
        public int TileSize { get; private set; }
        public StructuralRefine StructuralRefine { get; private set; }
        public bool IsMultiScale { get { return DetailMaxLength > 0; } }

        internal StrokeSearch(int fromLayer, int every, int finalFromLayer, int finalEvery, int minAxis, int guideMode, int detailMinLength, int detailMaxLength, int contourMinLength, int contourMaxLength, int contourEvery, int finalContourEvery, int tileSize, StructuralRefine structuralRefine)
        {
            FromLayer = fromLayer;
            Every = every;
            FinalFromLayer = finalFromLayer;
            FinalEvery = finalEvery;
            MinAxis = minAxis;
            GuideMode = guideMode;
            DetailMinLength = detailMinLength;
            DetailMaxLength = detailMaxLength;
            ContourMinLength = contourMinLength;
            ContourMaxLength = contourMaxLength;
            ContourEvery = contourEvery;
            FinalContourEvery = finalContourEvery;
            TileSize = tileSize;
            StructuralRefine = structuralRefine;
        }
    }

    public sealed class StructuralRefine
    {
        public double EdgeDistanceWeight { get; private set; }
        public int DistanceLimit { get; private set; }
        public int Rounds { get; private set; }
        public double MaxPixelGainRegression { get; private set; }

        internal StructuralRefine(double edgeDistanceWeight, int distanceLimit, int rounds, double maxPixelGainRegression)
        {
            EdgeDistanceWeight = edgeDistanceWeight;
            DistanceLimit = distanceLimit;
            Rounds = rounds;
            MaxPixelGainRegression = maxPixelGainRegression;
        }
    }

    public sealed class LayerOptimization
    {
        public int OverfitPercent { get; private set; }
        public int RefinePercent { get; private set; }
        public int RankingPoolMultiplier { get; private set; }
        public int ReplacementPercent { get; private set; }
        public int ReplacementTileSize { get; private set; }
        public double MaxTileRegressionPercent { get; private set; }

        internal LayerOptimization(int overfitPercent, int refinePercent, int rankingPoolMultiplier, int replacementPercent, int replacementTileSize, double maxTileRegressionPercent)
        {
            OverfitPercent = overfitPercent;
            RefinePercent = refinePercent;
            RankingPoolMultiplier = rankingPoolMultiplier;
            ReplacementPercent = replacementPercent;
            ReplacementTileSize = replacementTileSize;
            MaxTileRegressionPercent = maxTileRegressionPercent;
        }
    }

    public sealed class ShapeChoice
    {
        public int FromLayer { get; private set; }
        public IReadOnlyList<string> Shapes { get; private set; }

        internal ShapeChoice(int fromLayer, List<string> shapes)
        {
            FromLayer = fromLayer;
            Shapes = new ReadOnlyCollection<string>(shapes);
        }
    }

    public sealed class WeightMapChoice
    {
        public int FromLayer { get; private set; }
        public string WeightMapId { get; private set; }

        internal WeightMapChoice(int fromLayer, string weightMapId)
        {
            FromLayer = fromLayer;
            WeightMapId = weightMapId;
        }
    }

    public sealed class PerceptualRerank
    {
        public string Backend { get; private set; }
        public string Model { get; private set; }
        public int BatchSize { get; private set; }
        public bool ShapeBalanced { get; private set; }
        public int TopK { get; private set; }
        public int EachTopK { get; private set; }
        public int FirstRerankLayer { get; private set; }
        public int TileSize { get; private set; }
        public int TileStride { get; private set; }
        public int MaxTilesPerCandidate { get; private set; }
        public double PerceptualRankWeight { get; private set; }
        public int MiddleEvery { get; private set; }
        public int FinalEvery { get; private set; }
        public bool BatchFiles { get; private set; }
        internal PerceptualRerank(string backend, string model, int batchSize, bool shapeBalanced, int topK, int eachTopK, int firstRerankLayer, int tileSize, int tileStride, int maxTilesPerCandidate, double perceptualRankWeight, int middleEvery, int finalEvery, bool batchFiles)
        {
            Backend = backend;
            Model = model;
            BatchSize = batchSize;
            ShapeBalanced = shapeBalanced;
            TopK = topK;
            EachTopK = eachTopK;
            FirstRerankLayer = firstRerankLayer;
            TileSize = tileSize;
            TileStride = tileStride;
            MaxTilesPerCandidate = maxTilesPerCandidate;
            PerceptualRankWeight = perceptualRankWeight;
            MiddleEvery = middleEvery;
            FinalEvery = finalEvery;
            BatchFiles = batchFiles;
        }
    }
}
