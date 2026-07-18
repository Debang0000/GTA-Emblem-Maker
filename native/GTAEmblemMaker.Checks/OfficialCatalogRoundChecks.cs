using System;
using System.Security.Cryptography;
using System.Text;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class OfficialCatalogRoundChecks
    {
        internal static void Run()
        {
            Check.Equal(2, OfficialCatalog.RoundBasis.Count, "selected round basis count");
            CheckRound(OfficialCatalog.RoundBasis[0], "rounds/08", 211.872, 300.001, "7271704954196577C40DFD8546F28E1F68961D735C371E07F4FB239A26AE97FB", 212, 300, "M211.871,186.514c0-160.721-105.938-185.515-105.938-185.515S0,25.793,0,186.514l0.164,0.045C0.131,187.719,0,188.861,0,190.035C0,251.326,47.424,301,105.934,301c58.501,0,105.938-49.674,105.938-110.965c0-1.174-0.137-2.315-0.168-3.477L211.871,186.514z");
            CheckRound(OfficialCatalog.RoundBasis[1], "rounds/18", 262.57, 300, "008AD95B4ED946D790F82B64ACADFE74C2CB7C8BC709B60CA8F36CD3B1062444", 263, 300, "M136.3,0h-10.025H0c0,0,22.953,300,131.282,300C239.617,300,262.57,0,262.57,0H136.3z");
        }

        private static void CheckRound(ShapeDefinition definition, string slug, double width, double height, string hash, double exportWidth, double exportHeight, string exportPath)
        {
            Check.Equal(slug, definition.Slug, slug + " slug");
            Check.Equal(width, definition.Width, slug + " width");
            Check.Equal(height, definition.Height, slug + " height");
            using (var algorithm = SHA256.Create()) Check.Equal(hash, BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(definition.Path))).Replace("-", ""), slug + " path");
            var identifier = OfficialCatalog.ShapeIdentifier(slug);
            ShapeDefinition exportDefinition;
            Check.True(OfficialCatalog.TryGetExportDefinition(identifier, out exportDefinition), slug + " export definition");
            Check.Equal(exportWidth, exportDefinition.Width, slug + " Rockstar width");
            Check.Equal(exportHeight, exportDefinition.Height, slug + " Rockstar height");
            Check.Equal(exportPath, exportDefinition.Path, slug + " Rockstar path");
            var state = new ShapeState(identifier, 256, 256, 60, 80, 100, 120, 140, 220, 0);
            var payload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
            Check.Equal("#transparent", payload.BackgroundColor, slug + " transparency");
            Check.True(payload.Svg.Contains(exportDefinition.Path), slug + " exact SVG");
            var layer = Program.DecodeLayers(payload.ConsoleCode)[1];
            Check.Equal(exportDefinition.Width, Convert.ToDouble(layer["width"]), slug + " Rockstar layer width");
            Check.Equal(exportDefinition.Height, Convert.ToDouble(layer["height"]), slug + " Rockstar layer height");
            Check.True(HasVisiblePixel(RunArtifacts.RenderPayloadPreview(new[] { state }, payload)), slug + " exact preview");
        }

        private static bool HasVisiblePixel(byte[] rgba)
        {
            for (var index = 3; index < rgba.Length; index += 4) if (rgba[index] != 0) return true;
            return false;
        }
    }
}
