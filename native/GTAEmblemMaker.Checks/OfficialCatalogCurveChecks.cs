using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class OfficialCatalogCurveChecks
    {
        internal static void Run()
        {
            var expectedHashes = new Dictionary<string, string>
            {
                { "curves/01", "2F224F95C85ED22D3B136925E653492F9039A763CE04BDB0CB2448148E6EAE4A" },
                { "curves/07", "98CADFDF281A5E653B8A5D8FF689AB6163357A4454DC209EEF044ED1217AA6B9" },
                { "curves/61", "C8818FEC772D947B891AD0716B6B68515DED5D05AFE56AEC7DDC021AF80B8DE3" },
                { "curves/62", "C8921C1C3A73385DC3D0DF1AD67239E840521083FDE68F92B0D8799B291A33D4" },
                { "curves/66", "A084A44B174C2148EB8DAC64FD018650C95B4D2F3924E2361703686FE04881FF" },
                { "curves/67", "06F70D917FD904F9B2A2C0E16470B203879CCD0DCE10A19293827E5323AEA43B" },
                { "curves/71", "772173565EFC62EA768EA78588B15EA1D8390B1C2E24799DF55688FD7030A9D9" },
                { "curves/72", "D345BA46562B7AF3233F9E9247F36D186544CAAD9342AA0F8B149FD7566A3820" },
                { "curves/73", "C549606EA35593632278A9FFAD989A7BD1A94E306F98228D4071EDB14B419A27" },
            };
            var expectedDimensions = new Dictionary<string, double[]>
            {
                { "curves/01", new[] { 300.0, 26.17475 } },
                { "curves/07", new[] { 300.0, 51.42924685800673 } },
                { "curves/61", new[] { 300.0, 292.82 } },
                { "curves/62", new[] { 300.0, 292.947 } },
                { "curves/66", new[] { 150.903, 300.0 } },
                { "curves/67", new[] { 151.797, 300.0 } },
                { "curves/71", new[] { 300.0, 64.65605911362775 } },
                { "curves/72", new[] { 300.0, 71.51928451486023 } },
                { "curves/73", new[] { 300.0, 77.90408259281718 } },
            };

            Check.Equal(expectedHashes.Count, OfficialCatalog.CurveBasis.Count, "selected curve basis count");
            foreach (var definition in OfficialCatalog.CurveBasis)
            {
                Check.True(expectedHashes.ContainsKey(definition.Slug), "selected curve slug");
                Check.Equal(expectedHashes[definition.Slug], Sha256(definition.Path), definition.Slug + " exact path");
                Check.Equal(expectedDimensions[definition.Slug][0], definition.Width, definition.Slug + " intrinsic width");
                Check.Equal(expectedDimensions[definition.Slug][1], definition.Height, definition.Slug + " intrinsic height");

                var identifier = OfficialCatalog.ShapeIdentifier(definition.Slug);
                var state = new ShapeState(identifier, 256, 320, 45, 8, 75, 62, 50, 220, 5);
                var empty = RockstarExporter.Build(new ShapeState[0], true, 1700000000000);
                var curvePayload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
                Check.True(curvePayload.Svg.Contains(definition.Path), definition.Slug + " exact SVG path");
                Check.True(curvePayload.GeneratedCodeLength > empty.GeneratedCodeLength, definition.Slug + " exact budget cost");
                Check.Equal("#transparent", curvePayload.BackgroundColor, definition.Slug + " transparency");
                Check.True(HasVisiblePixel(RunArtifacts.RenderPayloadPreview(new[] { state }, curvePayload)), definition.Slug + " exact preview");
            }

            var shape = new ShapeState("catalog-curve-71", 256, 320, 45, 8, 75, 62, 50, 220, 5);
            var payload = RockstarExporter.Build(new[] { shape }, true, 1700000000000);
            Check.True(payload.Svg.Contains(OfficialCatalog.CurveBasis[6].Path), "curve 71 exact SVG path");
            Check.True(payload.ConsoleCode.Length > 0, "curve payload generated");
            Check.Equal("#transparent", payload.BackgroundColor, "curve payload transparency");
            var preview = RunArtifacts.RenderPayloadPreview(new[] { shape }, payload);
            Check.Equal(512 * 512 * 4, preview.Length, "curve preview size");
            Check.True(HasVisiblePixel(preview), "curve preview is visible");

            var acceptedSubFour = new ShapeState("catalog-curve-01", 419, 448, 2, 2, 0, 0, 0, 170, 22.763635635375977);
            var acceptedSubFourPayload = RockstarExporter.Build(new[] { acceptedSubFour }, true, 255, 255, 255, 1700000000000, true, 2);
            Check.True(
                acceptedSubFourPayload.Svg.Contains("transform=\"matrix(0.0123,0.0052,-0.0591,0.1409,417.9296,445.3819)\""),
                "catalog sub-four axes preserve accepted exact matrix");

            CheckProductionPreviewPreservesLegacyRotation();
        }

        private static void CheckProductionPreviewPreservesLegacyRotation()
        {
            var catalog = new ShapeState("catalog-curve-61", 30, 30, 8, 8, 0, 0, 0, 1, 0);
            var rotated = new ShapeState("ellipse", 256, 256, 96, 18, 255, 255, 255, 255, 37);
            var axisAligned = new ShapeState("ellipse", 256, 256, 96, 18, 255, 255, 255, 255, 0);
            var rotatedShapes = new[] { rotated, catalog };
            var axisAlignedShapes = new[] { axisAligned, catalog };
            var rotatedPreview = RunArtifacts.RenderPayloadPreview(rotatedShapes, RockstarExporter.Build(rotatedShapes, true, 255, 255, 255, 1700000000000, true, 2));
            var axisAlignedPreview = RunArtifacts.RenderPayloadPreview(axisAlignedShapes, RockstarExporter.Build(axisAlignedShapes, true, 255, 255, 255, 1700000000000, true, 2));
            Check.False(AreEqual(rotatedPreview, axisAlignedPreview), "catalog production preview preserves rotated legacy ellipse");
        }

        private static bool AreEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }

        private static string Sha256(string value)
        {
            using (var algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
            }
        }

        private static bool HasVisiblePixel(byte[] rgba)
        {
            for (var index = 3; index < rgba.Length; index += 4) if (rgba[index] != 0) return true;
            return false;
        }
    }
}
