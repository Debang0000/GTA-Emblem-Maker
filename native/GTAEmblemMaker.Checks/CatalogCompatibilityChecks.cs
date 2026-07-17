using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class CatalogCompatibilityChecks
    {
        internal static void Run(string canonicalRgbaPath, string profileFolder, string scorerPath)
        {
            var expected = new JavaScriptSerializer().Deserialize<ExpectedState[]>(
                File.ReadAllText(GoldenFile("catalog-quality-trace-prefix.json")));
            var source = SourceImage.FromCanonical(File.ReadAllBytes(canonicalRgbaPath));
            var profile = FindProfile(ProfileCatalog.Load(profileFolder).Profiles, "v1-catalog-quality");
            var request = new FitRequest(profile, source, scorerPath)
            {
                LayerLimit = expected.Length,
                Timestamp = 1700000000000
            };
            var result = FittingEngine.RunAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();

            Check.Equal(expected.Length, result.Shapes.Count, "catalog compatibility prefix length");
            for (var index = 0; index < expected.Length; index++)
            {
                var row = expected[index];
                var trace = result.Trace[index];
                var shape = result.Shapes[index];
                var name = "catalog compatibility layer " + row.Layer;
                Check.Equal(row.Layer, trace.Layer, name + " layer");
                Check.Equal(checked((int)row.CandidateId), checked((int)trace.CandidateId), name + " candidate ID");
                Check.Equal(row.ShapeFamily, trace.ShapeFamily, name + " shape family");
                Check.Equal(row.WeightMapId, trace.WeightMapId, name + " weight map");
                Check.Equal(row.Energy, trace.SelectedEnergy, name + " energy");
                Check.Equal(row.Cx, shape.Cx, name + " center x");
                Check.Equal(row.Cy, shape.Cy, name + " center y");
                Check.Equal(row.Rx, shape.Rx, name + " radius x");
                Check.Equal(row.Ry, shape.Ry, name + " radius y");
                Check.Equal(row.Red, shape.Red, name + " red");
                Check.Equal(row.Green, shape.Green, name + " green");
                Check.Equal(row.Blue, shape.Blue, name + " blue");
                Check.Equal(row.Alpha, shape.Alpha, name + " alpha");
                Check.Equal(row.AngleDegrees, shape.AngleDegrees, name + " angle");
            }
        }

        internal static void RunCatalogCheckpoint(string canonicalRgbaPath, string selectedCandidatesPath, string profileFolder, string scorerPath)
        {
            var accepted = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<AcceptedCandidate[]>(File.ReadAllText(selectedCandidatesPath));
            if (accepted.Length < 501) throw new InvalidDataException("The accepted trace does not contain layer 501.");
            var source = SourceImage.FromCanonical(File.ReadAllBytes(canonicalRgbaPath));
            var profile = FindProfile(ProfileCatalog.Load(profileFolder).Profiles, "v1-catalog-quality");
            var stage = FitMath.ResolveStage(profile, "current-image-fit");
            var target = (byte[])source.CanonicalRgba.Clone();
            var current = FitMath.CreateInitialCurrent(target, source.IsTransparent);
            var maps = FitMath.BuildWeightMaps(target, 512, 512);
            var choice = FitMath.WeightMapChoiceForLayer(stage, source.IsTransparent, 500);
            var weights = maps[choice.WeightMapId].Q8;
            var baseTotalError = FitMath.WeightedFullError(target, current, weights);
            for (var index = 0; index < 500; index++)
            {
                var candidate = accepted[index].ToCandidate();
                baseTotalError = FitMath.ApplyCompatibilityCandidateAndUpdateError(target, current, 512, candidate, weights, baseTotalError);
            }
            Check.Equal(accepted[499].Energy, FitMath.EnergyFromTotal(baseTotalError, 512, 512), "accepted layer 500 replay energy");

            using (var scorer = CudaScorerClient.Start(scorerPath, 512, 512, target, current))
            {
                scorer.UpdateCurrentAsync(checked((ulong)baseTotalError), current, CancellationToken.None).GetAwaiter().GetResult();
                scorer.SetWeightMapAsync(weights, CancellationToken.None).GetAwaiter().GetResult();
                var shapes = FitMath.ShapeChoicesForLayer(stage, 501);
                var request = FittingEngine.CreateSelectRequest(stage, 501, shapes, compatibilityResident: true);
                var selected = scorer.SelectLayerAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                var historical = CandidateGenerator.FromResidentResult(selected.SelectedShapeKind, selected.SelectedCandidate, selected.SelectedScore);
                var catalog = CatalogCandidateSearch.SelectAsync(scorer, stage.CatalogSearch, 501, stage.MinAxis, CancellationToken.None).GetAwaiter().GetResult();
                var actual = FittingEngine.ChooseLowestEnergyCandidate(historical, catalog.BestByIdentity);
                accepted[500].CheckCandidate(actual, "catalog compatibility layer 501");
            }
        }

        private static FitProfile FindProfile(IReadOnlyList<FitProfile> profiles, string id)
        {
            for (var index = 0; index < profiles.Count; index++) if (profiles[index].Id == id) return profiles[index];
            throw new InvalidOperationException("Required profile is missing: " + id);
        }

        private static string GoldenFile(string name)
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (folder != null)
            {
                var path = Path.Combine(folder.FullName, "native", "GTAEmblemMaker.Checks", "Golden", name);
                if (File.Exists(path)) return path;
                folder = folder.Parent;
            }
            throw new FileNotFoundException("Golden file was not found.", name);
        }

        private sealed class ExpectedState
        {
            public int Layer { get; set; }
            public uint CandidateId { get; set; }
            public string ShapeFamily { get; set; }
            public string WeightMapId { get; set; }
            public double Cx { get; set; }
            public double Cy { get; set; }
            public double Rx { get; set; }
            public double Ry { get; set; }
            public int Red { get; set; }
            public int Green { get; set; }
            public int Blue { get; set; }
            public int Alpha { get; set; }
            public double AngleDegrees { get; set; }
            public double Energy { get; set; }
        }

        private sealed class AcceptedCandidate
        {
            public int Layer { get; set; }
            public string Shape { get; set; }
            public string PoolShapeFamily { get; set; }
            public uint CandidateId { get; set; }
            public uint Group { get; set; }
            public int Cx { get; set; }
            public int Cy { get; set; }
            public int Rx { get; set; }
            public int Ry { get; set; }
            public int Alpha { get; set; }
            public int R { get; set; }
            public int G { get; set; }
            public int B { get; set; }
            public float AngleDegrees { get; set; }
            public double Energy { get; set; }
            public ulong OldErrorDelta { get; set; }
            public ulong NewErrorDelta { get; set; }

            internal FitCandidate ToCandidate()
            {
                var kind = CandidateGenerator.ShapeKindFromName(PoolShapeFamily == "line-rect" ? "line-rect" : Shape);
                return new FitCandidate(CandidateId, Group, kind, Shape, PoolShapeFamily, Cx, Cy, Rx, Ry, checked((byte)R), checked((byte)G), checked((byte)B), checked((byte)Alpha), AngleDegrees, Energy, OldErrorDelta, NewErrorDelta);
            }

            internal void CheckCandidate(FitCandidate actual, string name)
            {
                Check.Equal(checked((int)CandidateId), checked((int)actual.CandidateId), name + " candidate ID");
                Check.Equal(PoolShapeFamily, actual.PoolShapeFamily, name + " shape family");
                Check.Equal(Cx, actual.Cx, name + " center x");
                Check.Equal(Cy, actual.Cy, name + " center y");
                Check.Equal(Rx, actual.Rx, name + " radius x");
                Check.Equal(Ry, actual.Ry, name + " radius y");
                Check.Equal(R, (int)actual.Red, name + " red");
                Check.Equal(G, (int)actual.Green, name + " green");
                Check.Equal(B, (int)actual.Blue, name + " blue");
                Check.Equal(Alpha, (int)actual.Alpha, name + " alpha");
                Check.Equal((double)AngleDegrees, (double)actual.AngleDegrees, name + " angle");
                Check.Equal(Energy, actual.Energy, name + " energy");
            }
        }
    }
}
