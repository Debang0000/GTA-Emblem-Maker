using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GTAEmblemMaker.Core
{
    internal static class OfficialCatalog
    {
        private static readonly ShapeDefinition Curve01 = new ShapeDefinition(
            "curves/01", "01", 300, 26.17475,
            "M250.001,12.743C184.536,30.652,115.463,30.652,49.998999999999995,12.743C33.333,8.495,16.667,4.248,0,0C98.196,26.864,201.805,26.864,300,0C283.333,4.248,266.668,8.495,250.001,12.743Z",
            "300", "26.17475");
        private static readonly ShapeDefinition Curve07 = new ShapeDefinition(
            "curves/07", "07", 300, 51.42924685800673,
            "M199.832,22.064C270.575,3.968,293.619,24.42,300,37.412C298.902,36.682,289.001,-3.264000000000003,237.096,0.2149999999999963C147.904,6.191,78.629,78.317,0,14.736C42.801,82.858,136.28,38.319,199.832,22.064Z",
            "300", "51.42924685800673");
        private static readonly ShapeDefinition Curve61 = new ShapeDefinition(
            "curves/61", "61", 300, 292.82,
            "M0,292.82H3.683C7.618,132.729,138.979,3.683,300,3.683V0C136.946,0,3.935,130.697,0,292.82Z",
            "300", "292.82");
        private static readonly ShapeDefinition Curve62 = new ShapeDefinition(
            "curves/62", "62", 300, 292.947,
            "M294.674,0C217.345,0,144.236,29.851,88.818,84.054C33.501,138.16,2.004,210.41,0.133,287.488L0,292.947H14.205L14.333,287.752C18.041,136.917,143.8,14.204,294.674,14.204H300V0H294.674Z",
            "300", "292.947");
        private static readonly ShapeDefinition Curve66 = new ShapeDefinition(
            "curves/66", "66", 150.903, 300,
            "M150,3.194H150.903V0H150C67.289,0,0,67.29,0,150C0,232.709,67.289,300,150,300H150.903V296.806H150C69.051,296.806,3.194,230.947,3.194,150C3.194,69.051,69.051,3.194,150,3.194Z",
            "150.903", "300");
        private static readonly ShapeDefinition Curve67 = new ShapeDefinition(
            "curves/67", "67", 151.797, 300,
            "M150,4.972H151.797V0H150C67.29,0,0,67.29,0,150S67.29,300,150,300H151.797V295.028H150C70.032,295.028,4.972,229.968,4.972,150C4.972,70.031,70.032,4.972,150,4.972Z",
            "151.797", "300");
        private static readonly ShapeDefinition Curve71 = new ShapeDefinition(
            "curves/71", "71", 300, 64.65605911362775,
            "M0,59.159L1.864,61.022999999999996C84.871,-18.001,216.65,-16.83,298.136,64.656L300,62.792C217.485,-19.722,84.035,-20.893,0,59.159Z",
            "300", "64.65605911362775");
        private static readonly ShapeDefinition Curve72 = new ShapeDefinition(
            "curves/72", "72", 300, 71.51928451486023,
            "M297.305,61.639C258.182,22.515,206.089,0.628,150.626,0.013C95.264,-0.6,42.773,20.02,2.829,58.07L0,60.765L7.187,67.952L9.88,65.389C88.071,-9.05,213.784,-7.509,290.118,68.825L292.813,71.519L300,64.333L297.305,61.639Z",
            "300", "71.51928451486023");
        private static readonly ShapeDefinition Curve73 = new ShapeDefinition(
            "curves/73", "73", 300, 77.90408259281718,
            "M294.798,60.565C256.355,22.122,205.172,0.617,150.677,0.013C96.282,-0.59,44.709,19.67,5.457,57.062L0,62.26L12.139,74.399L17.336,69.45C91.366,-1.028,210.391,0.432,282.661,72.702L287.862,77.904L300,65.767L294.798,60.565Z",
            "300", "77.90408259281718");
        private static readonly ShapeDefinition Round08 = new ShapeDefinition(
            "rounds/08", "08", 211.872, 300.001,
            "M211.871,186.514C211.871,25.793000000000006,105.933,0.9990000000000236,105.933,0.9990000000000236S0,25.793,0,186.514L0.164,186.559C0.131,187.719,0,188.861,0,190.035C0,251.326,47.424,301,105.934,301C164.435,301,211.872,251.326,211.872,190.035C211.872,188.861,211.735,187.72,211.704,186.558L211.871,186.514Z",
            "211.872", "300.001");
        private static readonly ShapeDefinition Round18 = new ShapeDefinition(
            "rounds/18", "18", 262.57, 300,
            "M136.3,0H126.275H0C0,0,22.953,300,131.282,300C239.617,300,262.57,0,262.57,0H136.3Z",
            "262.57", "300");

        private static readonly ReadOnlyCollection<ShapeDefinition> ExportDefinitions = Array.AsReadOnly(new[]
        {
            new ShapeDefinition("curves/01", "01", 300, 26.17475, "M250.001,12.743C184.536,30.652,115.463,30.652,49.999,12.743C33.333,8.495,16.667,4.248,0,0C98.196,26.864,201.805,26.864,300,0C283.333,4.248,266.668,8.495,250.001,12.743Z", "300", "26.17"),
            new ShapeDefinition("curves/07", "07", 300, 51.42924685800673, "M199.832,22.064C270.575,3.968,293.619,24.42,300,37.412C298.902,36.682,289.001,-3.264,237.096,0.215C147.904,6.191,78.629,78.317,0,14.736C42.801,82.858,136.28,38.319,199.832,22.064Z", "300", "51.43"),
            new ShapeDefinition("curves/61", "61", 300, 292.82, "M0,292.82H3.683C7.618,132.729,138.979,3.683,300,3.683V0C136.946,0,3.935,130.697,0,292.82Z", "300", "292.82"),
            new ShapeDefinition("curves/62", "62", 300, 292.947, "M294.674,0C217.345,0,144.236,29.851,88.818,84.054C33.501,138.16,2.004,210.41,0.133,287.488L0,292.947H14.205L14.333,287.752C18.041,136.917,143.8,14.204,294.674,14.204H300V0H294.674Z", "300", "292.95"),
            new ShapeDefinition("curves/66", "66", 150.903, 300, "M150,3.194H150.903V0H150C67.289,0,0,67.29,0,150C0,232.709,67.289,300,150,300H150.903V296.806H150C69.051,296.806,3.194,230.947,3.194,150C3.194,69.051,69.051,3.194,150,3.194Z", "150.9", "300"),
            new ShapeDefinition("curves/67", "67", 151.797, 300, "M150,4.972H151.797V0H150C67.29,0,0,67.29,0,150S67.29,300,150,300H151.797V295.028H150C70.032,295.028,4.972,229.968,4.972,150C4.972,70.031,70.032,4.972,150,4.972Z", "151.8", "300"),
            new ShapeDefinition("curves/71", "71", 300, 64.65605911362775, "M0,59.159L1.864,61.023C84.871,-18.001,216.65,-16.83,298.136,64.656L300,62.792C217.485,-19.722,84.035,-20.893,0,59.159Z", "300", "64.66"),
            new ShapeDefinition("curves/72", "72", 300, 71.51928451486023, "M297.305,61.639C258.182,22.515,206.089,0.628,150.626,0.013C95.264,-0.6,42.773,20.02,2.829,58.07L0,60.765L7.187,67.952L9.88,65.389C88.071,-9.05,213.784,-7.509,290.118,68.825L292.813,71.519L300,64.333L297.305,61.639Z", "300", "71.52"),
            new ShapeDefinition("curves/73", "73", 300, 77.90408259281718, "M294.798,60.565C256.355,22.122,205.172,0.617,150.677,0.013C96.282,-0.59,44.709,19.67,5.457,57.062L0,62.26L12.139,74.399L17.336,69.45C91.366,-1.028,210.391,0.432,282.661,72.702L287.862,77.904L300,65.767L294.798,60.565Z", "300", "77.9"),
            new ShapeDefinition("rounds/08", "08", 211.872, 300.001, "M211.871,186.514C211.871,25.793,105.933,0.999,105.933,0.999S0,25.793,0,186.514L0.164,186.559C0.131,187.719,0,188.861,0,190.035C0,251.326,47.424,301,105.934,301C164.435,301,211.872,251.326,211.872,190.035C211.872,188.861,211.735,187.72,211.704,186.558L211.871,186.514Z", "211.87", "300"),
            new ShapeDefinition("rounds/18", "18", 262.57, 300, "M136.3,0H126.275H0C0,0,22.953,300,131.282,300C239.617,300,262.57,0,262.57,0H136.3Z", "262.57", "300"),
        });

        internal static readonly ReadOnlyCollection<ShapeDefinition> CurveBasis = Array.AsReadOnly(new[]
        {
            Curve01,
            Curve07,
            Curve61,
            Curve62,
            Curve66,
            Curve67,
            Curve71,
            Curve72,
            Curve73,
        });
        internal static readonly ReadOnlyCollection<ShapeDefinition> RoundBasis = Array.AsReadOnly(new[] { Round08, Round18 });

        private static readonly Dictionary<string, ShapeDefinition> ByIdentifier = CreateRegistry();
        private static readonly Dictionary<string, ShapeDefinition> ByExportIdentifier = CreateExportRegistry();

        internal static string ShapeIdentifier(string slug)
        {
            if (slug == null) throw new ArgumentNullException("slug");
            if (slug.StartsWith("curves/", StringComparison.Ordinal)) return "catalog-curve-" + slug.Substring(7);
            if (slug.StartsWith("rounds/", StringComparison.Ordinal)) return "catalog-round-" + slug.Substring(7);
            throw new ArgumentException("Unsupported official catalog family.", "slug");
        }

        internal static bool TryGetDefinition(string identifier, out ShapeDefinition definition)
        {
            return ByIdentifier.TryGetValue(identifier, out definition);
        }

        internal static bool TryGetExportDefinition(string identifier, out ShapeDefinition definition)
        {
            return ByExportIdentifier.TryGetValue(identifier, out definition);
        }

        private static Dictionary<string, ShapeDefinition> CreateRegistry()
        {
            var registry = new Dictionary<string, ShapeDefinition>(StringComparer.Ordinal);
            foreach (var definition in CurveBasis) registry.Add(ShapeIdentifier(definition.Slug), definition);
            foreach (var definition in RoundBasis) registry.Add(ShapeIdentifier(definition.Slug), definition);
            return registry;
        }

        private static Dictionary<string, ShapeDefinition> CreateExportRegistry()
        {
            var registry = new Dictionary<string, ShapeDefinition>(StringComparer.Ordinal);
            foreach (var definition in ExportDefinitions) registry.Add(ShapeIdentifier(definition.Slug), definition);
            return registry;
        }
    }
}
