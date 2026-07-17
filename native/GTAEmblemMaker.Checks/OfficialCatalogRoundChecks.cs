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
            CheckRound(OfficialCatalog.RoundBasis[0], "rounds/08", 211.872, 300.001, "7271704954196577C40DFD8546F28E1F68961D735C371E07F4FB239A26AE97FB");
            CheckRound(OfficialCatalog.RoundBasis[1], "rounds/18", 262.57, 300, "008AD95B4ED946D790F82B64ACADFE74C2CB7C8BC709B60CA8F36CD3B1062444");
        }

        private static void CheckRound(ShapeDefinition definition, string slug, double width, double height, string hash)
        {
            Check.Equal(slug, definition.Slug, slug + " slug");
            Check.Equal(width, definition.Width, slug + " width");
            Check.Equal(height, definition.Height, slug + " height");
            using (var algorithm = SHA256.Create()) Check.Equal(hash, BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(definition.Path))).Replace("-", ""), slug + " path");
            var state = new ShapeState(OfficialCatalog.ShapeIdentifier(slug), 256, 256, 60, 80, 100, 120, 140, 220, 0);
            var payload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
            Check.Equal("#transparent", payload.BackgroundColor, slug + " transparency");
            Check.True(payload.Svg.Contains(definition.Path), slug + " exact SVG");
            Check.True(HasVisiblePixel(RunArtifacts.RenderPayloadPreview(new[] { state }, payload)), slug + " exact preview");
        }

        private static bool HasVisiblePixel(byte[] rgba)
        {
            for (var index = 3; index < rgba.Length; index += 4) if (rgba[index] != 0) return true;
            return false;
        }
    }
}
