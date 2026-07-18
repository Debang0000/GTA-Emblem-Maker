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
            var expectedExportPaths = new Dictionary<string, string>
            {
                { "curves/01", "M250.001,12.743C184.536,30.652,115.463,30.652,49.999,12.743C33.333,8.495,16.667,4.248,0,0C98.196,26.864,201.805,26.864,300,0C283.333,4.248,266.668,8.495,250.001,12.743Z" },
                { "curves/07", "M199.832,22.064C270.575,3.968,293.619,24.42,300,37.412C298.902,36.682,289.001,-3.264,237.096,0.215C147.904,6.191,78.629,78.317,0,14.736C42.801,82.858,136.28,38.319,199.832,22.064Z" },
                { "curves/61", "M0,292.82H3.683C7.618,132.729,138.979,3.683,300,3.683V0C136.946,0,3.935,130.697,0,292.82Z" },
                { "curves/62", "M294.674,0C217.345,0,144.236,29.851,88.818,84.054C33.501,138.16,2.004,210.41,0.133,287.488L0,292.947H14.205L14.333,287.752C18.041,136.917,143.8,14.204,294.674,14.204H300V0H294.674Z" },
                { "curves/66", "M150,3.194H150.903V0H150C67.289,0,0,67.29,0,150C0,232.709,67.289,300,150,300H150.903V296.806H150C69.051,296.806,3.194,230.947,3.194,150C3.194,69.051,69.051,3.194,150,3.194Z" },
                { "curves/67", "M150,4.972H151.797V0H150C67.29,0,0,67.29,0,150S67.29,300,150,300H151.797V295.028H150C70.032,295.028,4.972,229.968,4.972,150C4.972,70.031,70.032,4.972,150,4.972Z" },
                { "curves/71", "M0,59.159L1.864,61.023C84.871,-18.001,216.65,-16.83,298.136,64.656L300,62.792C217.485,-19.722,84.035,-20.893,0,59.159Z" },
                { "curves/72", "M297.305,61.639C258.182,22.515,206.089,0.628,150.626,0.013C95.264,-0.6,42.773,20.02,2.829,58.07L0,60.765L7.187,67.952L9.88,65.389C88.071,-9.05,213.784,-7.509,290.118,68.825L292.813,71.519L300,64.333L297.305,61.639Z" },
                { "curves/73", "M294.798,60.565C256.355,22.122,205.172,0.617,150.677,0.013C96.282,-0.59,44.709,19.67,5.457,57.062L0,62.26L12.139,74.399L17.336,69.45C91.366,-1.028,210.391,0.432,282.661,72.702L287.862,77.904L300,65.767L294.798,60.565Z" },
            };
            var expectedLayerDimensions = new Dictionary<string, double[]>
            {
                { "curves/01", new[] { 300.0, 26.17 } },
                { "curves/07", new[] { 300.0, 51.43 } },
                { "curves/61", new[] { 300.0, 292.82 } },
                { "curves/62", new[] { 300.0, 292.95 } },
                { "curves/66", new[] { 150.9, 300.0 } },
                { "curves/67", new[] { 151.8, 300.0 } },
                { "curves/71", new[] { 300.0, 64.66 } },
                { "curves/72", new[] { 300.0, 71.52 } },
                { "curves/73", new[] { 300.0, 77.9 } },
            };

            Check.Equal(expectedHashes.Count, OfficialCatalog.CurveBasis.Count, "selected curve basis count");
            foreach (var definition in OfficialCatalog.CurveBasis)
            {
                Check.True(expectedHashes.ContainsKey(definition.Slug), "selected curve slug");
                Check.Equal(expectedHashes[definition.Slug], Sha256(definition.Path), definition.Slug + " exact path");
                Check.Equal(expectedDimensions[definition.Slug][0], definition.Width, definition.Slug + " intrinsic width");
                Check.Equal(expectedDimensions[definition.Slug][1], definition.Height, definition.Slug + " intrinsic height");

                var identifier = OfficialCatalog.ShapeIdentifier(definition.Slug);
                ShapeDefinition exportDefinition;
                Check.True(OfficialCatalog.TryGetExportDefinition(identifier, out exportDefinition), definition.Slug + " export definition");
                Check.Equal(expectedExportPaths[definition.Slug], exportDefinition.Path, definition.Slug + " Rockstar path");
                Check.Equal(expectedDimensions[definition.Slug][0], exportDefinition.Width, definition.Slug + " Rockstar measured width");
                Check.Equal(expectedDimensions[definition.Slug][1], exportDefinition.Height, definition.Slug + " Rockstar measured height");
                var state = new ShapeState(identifier, 256, 320, 45, 8, 75, 62, 50, 220, 5);
                var empty = RockstarExporter.Build(new ShapeState[0], true, 1700000000000);
                var curvePayload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
                Check.True(curvePayload.Svg.Contains(exportDefinition.Path), definition.Slug + " exact SVG path");
                var curveLayer = Program.DecodeLayers(curvePayload.ConsoleCode)[1];
                Check.Equal(expectedLayerDimensions[definition.Slug][0], Convert.ToDouble(curveLayer["width"]), definition.Slug + " Rockstar layer width");
                Check.Equal(expectedLayerDimensions[definition.Slug][1], Convert.ToDouble(curveLayer["height"]), definition.Slug + " Rockstar layer height");
                Check.True(curvePayload.GeneratedCodeLength > empty.GeneratedCodeLength, definition.Slug + " exact budget cost");
                Check.Equal("#transparent", curvePayload.BackgroundColor, definition.Slug + " transparency");
                Check.True(HasVisiblePixel(RunArtifacts.RenderPayloadPreview(new[] { state }, curvePayload)), definition.Slug + " exact preview");
            }

            var shape = new ShapeState("catalog-curve-71", 256, 320, 45, 8, 75, 62, 50, 220, 5);
            var payload = RockstarExporter.Build(new[] { shape }, true, 1700000000000);
            ShapeDefinition curve71Export;
            Check.True(OfficialCatalog.TryGetExportDefinition("catalog-curve-71", out curve71Export), "curve 71 export definition");
            Check.True(payload.Svg.Contains(curve71Export.Path), "curve 71 exact SVG path");
            Check.True(payload.ConsoleCode.Length > 0, "curve payload generated");
            Check.Equal("#transparent", payload.BackgroundColor, "curve payload transparency");
            var preview = RunArtifacts.RenderPayloadPreview(new[] { shape }, payload);
            Check.Equal(512 * 512 * 4, preview.Length, "curve preview size");
            Check.True(HasVisiblePixel(preview), "curve preview is visible");

            var acceptedSubFour = new ShapeState("catalog-curve-01", 419, 448, 2, 2, 0, 0, 0, 170, 22.763635635375977);
            var acceptedSubFourPayload = RockstarExporter.Build(new[] { acceptedSubFour }, true, 255, 255, 255, 1700000000000, true, 2);
            var acceptedSubFourLayer = Program.DecodeLayers(acceptedSubFourPayload.ConsoleCode)[1];
            Check.Equal(269.0, Convert.ToDouble(acceptedSubFourLayer["x"]), "catalog cleanup rounds x");
            Check.Equal(435.0, Convert.ToDouble(acceptedSubFourLayer["y"]), "catalog cleanup rounds y");
            Check.Equal(1.33, Convert.ToDouble(acceptedSubFourLayer["scaleX"]), "catalog cleanup rounds scaleX");
            Check.Equal(15.28, Convert.ToDouble(acceptedSubFourLayer["scaleY"]), "catalog cleanup rounds scaleY");
            Check.Equal(23.0, Convert.ToDouble(acceptedSubFourLayer["rotation"]), "catalog cleanup rounds rotation");
            Check.Equal(67.0, Convert.ToDouble(acceptedSubFourLayer["opacity"]), "catalog cleanup rounds opacity");
            Check.True(
                acceptedSubFourPayload.Svg.Contains("transform=\"matrix(0.0122,0.0052,-0.0597,0.1407,417.945,445.4671)\""),
                "catalog matrix uses cleaned layer values");

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
