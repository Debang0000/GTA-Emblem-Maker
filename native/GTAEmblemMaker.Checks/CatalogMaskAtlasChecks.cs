using System;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class CatalogMaskAtlasChecks
    {
        internal static void Run()
        {
            var atlas = CatalogMaskAtlas.Build();
            Check.Equal(11, atlas.Count, "catalog atlas entry count");
            for (var index = 0; index < atlas.Count; index++)
            {
                var entry = atlas[index];
                Check.Equal(CatalogMaskAtlas.DefaultSize * CatalogMaskAtlas.DefaultSize, entry.Mask.Length, entry.Slug + " atlas size");
                Check.True(entry.MaxX > entry.MinX && entry.MaxY > entry.MinY, entry.Slug + " path bounds");
                Check.True(HasCoverage(entry.Mask), entry.Slug + " atlas coverage");
                Check.True(entry.Identifier.StartsWith("catalog-"), entry.Slug + " catalog identifier");
                var parity = MeasurePayloadParity(entry);
                Check.True(parity.IntersectionOverUnion >= 0.45, entry.Slug + " atlas payload IoU " + parity.IntersectionOverUnion.ToString("0.000"));
                Check.True(parity.DisagreementPixels <= 1024, entry.Slug + " atlas payload disagreement " + parity.DisagreementPixels);
            }
            CheckAnisotropicQuarterArc(atlas);
        }

        private static bool HasCoverage(byte[] mask)
        {
            for (var index = 0; index < mask.Length; index++) if (mask[index] != 0) return true;
            return false;
        }

        private static PayloadParity MeasurePayloadParity(CatalogMaskAtlasEntry entry)
        {
            var state = new ShapeState(entry.Identifier, 256, 256, 110, 90, 255, 255, 255, 255, 17);
            var atlas = CatalogMaskAtlas.RenderBinaryAlpha(entry, state);
            var payload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
            var exact = RunArtifacts.RenderPayloadPreview(new[] { state }, payload);
            var intersection = 0;
            var union = 0;
            var disagreement = 0;
            for (var index = 0; index < atlas.Length; index++)
            {
                var atlasFilled = atlas[index] >= 128;
                var exactFilled = exact[index * 4 + 3] >= 128;
                if (atlasFilled && exactFilled) intersection++;
                if (atlasFilled || exactFilled) union++;
                if (atlasFilled != exactFilled) disagreement++;
            }
            return new PayloadParity(union == 0 ? 0 : (double)intersection / union, disagreement);
        }

        private static void CheckAnisotropicQuarterArc(System.Collections.Generic.IReadOnlyList<CatalogMaskAtlasEntry> entries)
        {
            CatalogMaskAtlasEntry entry = null;
            foreach (var candidate in entries) if (candidate.Slug == "curves/61") entry = candidate;
            Check.True(entry != null, "curves/61 atlas entry");
            ShapeDefinition exportDefinition;
            Check.True(OfficialCatalog.TryGetExportDefinition(entry.Identifier, out exportDefinition), "curves/61 export definition");

            var defaultState = new ShapeState(entry.Identifier, 256, 256.41, 150, 146.41, 0, 0, 0, 255, 0);
            var defaultMatrix = RockstarExporter.MatrixValues(Shapes.ToExportShape(defaultState));
            Check.True(Math.Abs(1 - defaultMatrix.ScaleX) <= 0.000001, "curves/61 default scaleX");
            Check.True(Math.Abs(1 - defaultMatrix.ScaleY) <= 0.000001, "curves/61 default scaleY");
            Check.True(Math.Abs(106 - defaultMatrix.E) <= 0.000001, "curves/61 default matrix e");
            Check.True(Math.Abs(110 - defaultMatrix.F) <= 0.000001, "curves/61 default matrix f");

            var state = new ShapeState(entry.Identifier, 256, 256.41, 150, entry.IntrinsicHeight * 0.35 / 2, 0, 0, 0, 255, 31);
            var matrix = RockstarExporter.MatrixValues(Shapes.ToExportShape(state));
            var payload = RockstarExporter.Build(new[] { state }, true, 1700000000000);
            Check.True(Math.Abs(1 - matrix.ScaleX) <= 0.000001, "curves/61 anisotropic scaleX");
            Check.True(Math.Abs(0.35 - matrix.ScaleY) <= 0.000001, "curves/61 anisotropic scaleY");
            Check.True(Math.Abs(0.8572 - matrix.A) <= 0.00005, "curves/61 official matrix a");
            Check.True(Math.Abs(0.515 - matrix.B) <= 0.00005, "curves/61 official matrix b");
            Check.True(Math.Abs(-0.1803 - matrix.C) <= 0.00005, "curves/61 official matrix c");
            Check.True(Math.Abs(0.3 - matrix.D) <= 0.00005, "curves/61 official matrix d");
            Check.True(Math.Abs(153.8173 - matrix.E) <= 0.00005, "curves/61 official matrix e");
            Check.True(Math.Abs(135.23 - matrix.F) <= 0.00005, "curves/61 official matrix f");
            Check.True(payload.Svg.Contains("d=\"" + exportDefinition.Path + "\""), "curves/61 exact filled path export");
            Check.True(payload.Svg.Contains("matrix(0.8572,0.515,-0.1803,0.3,153.8173,135.23)"), "curves/61 official live SVG matrix");
            var layer = Program.DecodeLayers(payload.ConsoleCode)[1];
            Check.True(Math.Abs(106 - Convert.ToDouble(layer["x"])) <= 0.000001, "curves/61 official layerData x");
            Check.True(Math.Abs(110 - Convert.ToDouble(layer["y"])) <= 0.000001, "curves/61 official layerData y");
            Check.True(Math.Abs(100 - Convert.ToDouble(layer["scaleX"])) <= 0.000001, "curves/61 layerData scaleX");
            Check.True(Math.Abs(35 - Convert.ToDouble(layer["scaleY"])) <= 0.000001, "curves/61 layerData scaleY");
            Check.True(Math.Abs(31 - Convert.ToDouble(layer["rotation"])) <= 0.000001, "curves/61 layerData rotation");

            var atlasAlpha = CatalogMaskAtlas.RenderBinaryAlpha(entry, state);
            var exactRgba = RunArtifacts.RenderPayloadPreview(new[] { state }, payload);
            var atlasThickness = QuarterArcThickness(atlasAlpha, matrix);
            var exactThickness = QuarterArcThickness(ExtractAlpha(exactRgba), matrix);
            for (var index = 0; index < exactThickness.Length; index++)
            {
                Check.True(Math.Abs(atlasThickness[index] - exactThickness[index]) <= 1.5, "curves/61 anisotropic thickness parity " + index);
            }
            Check.True(exactThickness[0] >= exactThickness[2] * 1.5, "curves/61 anisotropic end-to-end thickness variation " + String.Join(",", exactThickness));
        }

        private static byte[] ExtractAlpha(byte[] rgba)
        {
            var alpha = new byte[512 * 512];
            for (var index = 0; index < alpha.Length; index++) alpha[index] = rgba[index * 4 + 3];
            return alpha;
        }

        private static double[] QuarterArcThickness(byte[] alpha, RockstarExporter.MatrixState matrix)
        {
            var result = new double[3];
            var angles = new[] { 195.0, 225.0, 255.0 };
            for (var index = 0; index < angles.Length; index++)
            {
                var radians = angles[index] * Math.PI / 180;
                var normalX = Math.Cos(radians);
                var normalY = Math.Sin(radians);
                result[index] = MeasureThickness(alpha, matrix, 300 + 296 * normalX, 300 + 296 * normalY, normalX, normalY);
            }
            return result;
        }

        private static double MeasureThickness(byte[] alpha, RockstarExporter.MatrixState matrix, double pathX, double pathY, double normalX, double normalY)
        {
            var centerX = matrix.A * pathX + matrix.C * pathY + matrix.E;
            var centerY = matrix.B * pathX + matrix.D * pathY + matrix.F;
            var determinant = matrix.A * matrix.D - matrix.B * matrix.C;
            var transformedNormalX = (matrix.D * normalX - matrix.B * normalY) / determinant;
            var transformedNormalY = (-matrix.C * normalX + matrix.A * normalY) / determinant;
            var length = Math.Sqrt(transformedNormalX * transformedNormalX + transformedNormalY * transformedNormalY);
            transformedNormalX /= length;
            transformedNormalY /= length;
            var first = Double.NaN;
            var last = Double.NaN;
            for (var offset = -12.0; offset <= 12.0; offset += 0.25)
            {
                var x = (int)Math.Round(centerX + transformedNormalX * offset);
                var y = (int)Math.Round(centerY + transformedNormalY * offset);
                if (x < 0 || x >= 512 || y < 0 || y >= 512 || alpha[y * 512 + x] < 128) continue;
                if (Double.IsNaN(first)) first = offset;
                last = offset;
            }
            return Double.IsNaN(first) ? 0 : last - first + 0.25;
        }

        private sealed class PayloadParity
        {
            internal double IntersectionOverUnion { get; private set; }
            internal int DisagreementPixels { get; private set; }

            internal PayloadParity(double intersectionOverUnion, int disagreementPixels)
            {
                IntersectionOverUnion = intersectionOverUnion;
                DisagreementPixels = disagreementPixels;
            }
        }
    }
}
