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
    }
}
