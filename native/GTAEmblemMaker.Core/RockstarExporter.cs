using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace GTAEmblemMaker.Core
{
    public sealed class RockstarPayload
    {
        public string Svg { get; private set; }
        public string ConsoleCode { get; private set; }
        public int GeneratedCodeLength { get; private set; }
        public int ClipboardCodeLength { get; private set; }
        public string BackgroundColor { get; private set; }

        internal RockstarPayload(string svg, string consoleCode, int generatedCodeLength, string backgroundColor)
        {
            Svg = svg;
            ConsoleCode = consoleCode;
            GeneratedCodeLength = generatedCodeLength;
            ClipboardCodeLength = consoleCode.Length;
            BackgroundColor = backgroundColor;
        }
    }

    public static class RockstarExporter
    {
        private const int CanvasSize = 512;
        private const string SaveRequest = "var request = new XMLHttpRequest;request.open(\"POST\",\"/emblems/save\",!0),request.onreadystatechange=function(){if(request.readyState==XMLHttpRequest.DONE){var a=JSON.parse(request.responseText);200==a.Status?window.location.href=\"https://socialclub.rockstargames.com/emblems/edit/\"+a.EmblemId:a.Message?alert(a.Message):alert(a.Error.Message)}},request.setRequestHeader(\"Content-Type\",\"application/json\"),request.setRequestHeader(\"__RequestVerificationToken\",document.getElementsByName(\"__RequestVerificationToken\")[0].value),request.setRequestHeader(\"X-Requested-With\",\"XMLHttpRequest\"),request.send(JSON.stringify({\"crewId\":\"0\",\"emblemId\":\"\",\"parentId\":\"\",\"svgData\":svgData,\"layerData\":layerData,\"hash\":document.getElementById(\"editorField-hash\").value}));";

        public static RockstarPayload Build(IReadOnlyList<ShapeState> states, bool transparent)
        {
            return Build(states, transparent, 255, 255, 255, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        internal static RockstarPayload Build(IReadOnlyList<ShapeState> states, bool transparent, long timestamp)
        {
            return Build(states, transparent, 255, 255, 255, timestamp);
        }

        public static RockstarPayload Build(IReadOnlyList<ShapeState> states, bool transparent, int opaqueRed, int opaqueGreen, int opaqueBlue)
        {
            return Build(states, transparent, opaqueRed, opaqueGreen, opaqueBlue, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        internal static RockstarPayload Build(IReadOnlyList<ShapeState> states, bool transparent, int opaqueRed, int opaqueGreen, int opaqueBlue, long timestamp)
        {
            if (states == null) throw new ArgumentNullException("states");
            var builder = CreateBuilder(transparent, opaqueRed, opaqueGreen, opaqueBlue, timestamp);
            for (var index = 0; index < states.Count; index++) builder.Add(states[index]);
            return builder.Build();
        }

        internal static IncrementalBuilder CreateBuilder(bool transparent, int opaqueRed, int opaqueGreen, int opaqueBlue, long timestamp)
        {
            return new IncrementalBuilder(transparent, opaqueRed, opaqueGreen, opaqueBlue, timestamp);
        }

        private static string BackgroundLayer(string color)
        {
            return "{\"id\":\"background\",\"name\":\"Background\",\"type\":\"square\",\"y\":0,\"x\":0,\"scaleY\":100,\"scaleX\":100,\"invertedY\":false,\"invertedX\":false,\"rotation\":0,\"opacity\":100,\"index\":0,\"color\":\"" + color + "\",\"isFilled\":true,\"internal\":true,\"locked\":false,\"tBold\":false,\"tItalic\":false,\"fontFamily\":null,\"borderColor\":\"#a1a1a1\",\"borderSize\":0,\"gradientStyle\":\"Fill\",\"slug\":\"rectangles/square\",\"width\":512,\"height\":512}";
        }

        private static string PathLayer(string id, int index, ExportShape shape)
        {
            var values = MatrixValues(shape);
            return "{\"id\":\"" + id + "\",\"name\":\"" + shape.Definition.Name + "\",\"type\":\"path\",\"y\":" + FormatNumber(shape.Y) + ",\"x\":" + FormatNumber(shape.X) + ",\"scaleY\":" + FormatNumber(values.ScaleY * 100) + ",\"scaleX\":" + FormatNumber(values.ScaleX * 100) + ",\"invertedY\":false,\"invertedX\":false,\"rotation\":" + FormatNumber(shape.Rotation) + ",\"opacity\":" + FormatNumber(Math.Max(0, Math.Min(255, shape.Alpha)) / 255.0 * 100) + ",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"color\":\"" + shape.Color + "\",\"isFilled\":true,\"internal\":false,\"locked\":false,\"tBold\":false,\"tItalic\":false,\"fontFamily\":null,\"borderColor\":\"#a1a1a1\",\"borderSize\":0,\"gradientStyle\":\"Fill\",\"slug\":\"" + shape.Definition.Slug + "\",\"width\":" + FormatNumber(shape.Definition.Width) + ",\"height\":" + FormatNumber(shape.Definition.Height) + "}";
        }

        private static string Matrix(ExportShape shape)
        {
            var values = MatrixValues(shape);
            return "matrix(" + FormatNumber(values.A) + "," + FormatNumber(values.B) + "," + FormatNumber(values.C) + "," + FormatNumber(values.D) + "," + FormatNumber(values.E) + "," + FormatNumber(values.F) + ")";
        }

        internal static MatrixState MatrixValues(ExportShape shape)
        {
            var scaleX = shape.Width / shape.Definition.Width;
            var scaleY = shape.Height / shape.Definition.Height;
            var radians = shape.Rotation * Math.PI / 180;
            var a = Round5(Math.Cos(radians) * scaleX);
            var b = Round5(Math.Sin(radians) * scaleX);
            var c = Round5(-Math.Sin(radians) * scaleY);
            var d = Round5(Math.Cos(radians) * scaleY);
            var e = Round5(shape.X + shape.Width / 2 - a * shape.Definition.Width / 2 - c * shape.Definition.Height / 2);
            var f = Round5(shape.Y + shape.Height / 2 - b * shape.Definition.Width / 2 - d * shape.Definition.Height / 2);
            return new MatrixState(scaleX, scaleY, a, b, c, d, e, f);
        }

        private static double Round5(double value)
        {
            return (double)ToFixedInteger(value) / 100000;
        }

        internal static string FormatNumber(double value)
        {
            var scaled = ToFixedInteger(value);
            if (scaled.IsZero) return "0";
            var sign = scaled.Sign < 0 ? "-" : "";
            var absolute = BigInteger.Abs(scaled);
            var whole = BigInteger.DivRem(absolute, 100000, out var fraction);
            if (fraction.IsZero) return sign + whole.ToString(CultureInfo.InvariantCulture);
            var decimals = fraction.ToString("D5", CultureInfo.InvariantCulture).TrimEnd('0');
            return sign + whole.ToString(CultureInfo.InvariantCulture) + "." + decimals;
        }

        private static BigInteger ToFixedInteger(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentOutOfRangeException("value");
            var bits = BitConverter.DoubleToInt64Bits(value);
            var negative = bits < 0;
            var exponentBits = (int)((bits >> 52) & 0x7ff);
            var significand = new BigInteger(bits & 0x000fffffffffffffL);
            var exponent = exponentBits == 0 ? -1074 : exponentBits - 1075;
            if (exponentBits != 0) significand += BigInteger.One << 52;
            var numerator = significand * 100000;
            if (exponent >= 0) return (negative ? -numerator : numerator) << exponent;
            var denominator = BigInteger.One << -exponent;
            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
            if (!negative && remainder * 2 >= denominator) quotient += BigInteger.One;
            if (negative && remainder * 2 > denominator) quotient += BigInteger.One;
            return negative ? -quotient : quotient;
        }

        private static string RgbHex(int red, int green, int blue)
        {
            return "#" + red.ToString("x2", CultureInfo.InvariantCulture) + green.ToString("x2", CultureInfo.InvariantCulture) + blue.ToString("x2", CultureInfo.InvariantCulture);
        }

        private static void ValidateByte(int value, string name)
        {
            if (value < 0 || value > 255) throw new ArgumentOutOfRangeException(name);
        }

        internal sealed class IncrementalBuilder
        {
            private const string SvgSuffix = "</svg>";
            private const string CodePrefix = "(async()=>{let b=Uint8Array.from(atob(\"";
            private const string DecodeCode = "\"),c=>c.charCodeAt()),s=await new Response(new Blob([b]).stream().pipeThrough(new DecompressionStream(\"gzip\"))).text(),p=s.indexOf(\"\\0\"),svgData=btoa(s.slice(0,p)),layerData=btoa(s.slice(p+1));";
            private const string CodeSuffix = "})()";
            private const string BudgetSvgPrefix = "var svgData = \"";
            private const string BudgetLayerPrefix = "\";\n\nvar layerData = \"";
            private const string BudgetSuffix = "\";\n\n";

            private readonly string backgroundColor;
            private readonly long timestamp;
            private readonly string svgPrefix;
            private readonly StringBuilder paths = new StringBuilder();
            private readonly StringBuilder layers = new StringBuilder();
            private int count;

            internal IncrementalBuilder(bool transparent, int opaqueRed, int opaqueGreen, int opaqueBlue, long timestamp)
            {
                ValidateByte(opaqueRed, "opaqueRed");
                ValidateByte(opaqueGreen, "opaqueGreen");
                ValidateByte(opaqueBlue, "opaqueBlue");
                this.timestamp = timestamp;
                backgroundColor = transparent ? "#transparent" : RgbHex(opaqueRed, opaqueGreen, opaqueBlue);
                svgPrefix = "<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"512\" height=\"512\"><defs></defs><rect x=\"0\" y=\"0\" width=\"512\" height=\"512\" rx=\"0\" ry=\"0\" fill=\"" + (transparent ? "none" : backgroundColor) + "\" stroke=\"#a1a1a1\" fill-opacity=\"1\" stroke-opacity=\"0\" stroke-width=\"0\" stroke-miterlimit=\"10\"></rect>";
                layers.Append(BackgroundLayer(backgroundColor));
            }

            internal int Count { get { return count; } }
            internal int BudgetCodeLength { get { return CalculateBudgetCodeLength(); } }

            internal void Add(ShapeState state)
            {
                Append(state);
            }

            internal bool TryAdd(ShapeState state, int budget)
            {
                if (state == null) throw new ArgumentException("State entries cannot be null.", "state");
                if (budget < 1) throw new ArgumentOutOfRangeException("budget");
                var pathLength = paths.Length;
                var layerLength = layers.Length;
                Append(state);
                if (CalculateBudgetCodeLength() <= budget) return true;
                count--;
                paths.Length = pathLength;
                layers.Length = layerLength;
                return false;
            }

            internal RockstarPayload Build()
            {
                var svg = CurrentSvg();
                var consoleCode = CodePrefix + CompressPayload(svg, CurrentLayerJson()) + DecodeCode + SaveRequest + CodeSuffix;
                return new RockstarPayload(svg, consoleCode, CalculateBudgetCodeLength(), backgroundColor);
            }

            private void Append(ShapeState state)
            {
                if (state == null) throw new ArgumentException("State entries cannot be null.", "state");
                var shape = Shapes.ToExportShape(state);
                paths.Append("<path fill=\"").Append(shape.Color).Append('\"');
                if (shape.Alpha < 255) paths.Append(" fill-opacity=\"").Append(FormatNumber(shape.Alpha / 255.0)).Append('\"');
                paths.Append(" d=\"").Append(shape.Definition.Path).Append("\" transform=\"").Append(Matrix(shape)).Append("\"></path>");
                layers.Append(',').Append(PathLayer("s" + timestamp.ToString(CultureInfo.InvariantCulture) + count.ToString(CultureInfo.InvariantCulture), count, shape));
                count++;
            }

            private int CalculateBudgetCodeLength()
            {
                var svgLength = checked(svgPrefix.Length + paths.Length + SvgSuffix.Length);
                var layerLength = checked(layers.Length + 2);
                return checked(BudgetSvgPrefix.Length + Base64Length(svgLength) + BudgetLayerPrefix.Length + Base64Length(layerLength) + BudgetSuffix.Length + SaveRequest.Length);
            }

            private string CurrentSvg()
            {
                return svgPrefix + paths + SvgSuffix;
            }

            private string CurrentLayerJson()
            {
                return "[" + layers + "]";
            }

            private static string CompressPayload(string svg, string layerJson)
            {
                var data = Encoding.UTF8.GetBytes(svg + "\0" + layerJson);
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(data, 0, data.Length);
                    return Convert.ToBase64String(output.ToArray());
                }
            }

            private static int Base64Length(int byteCount)
            {
                return checked(4 * ((byteCount + 2) / 3));
            }
        }

        internal sealed class MatrixState
        {
            public double ScaleX { get; private set; }
            public double ScaleY { get; private set; }
            public double A { get; private set; }
            public double B { get; private set; }
            public double C { get; private set; }
            public double D { get; private set; }
            public double E { get; private set; }
            public double F { get; private set; }

            public MatrixState(double scaleX, double scaleY, double a, double b, double c, double d, double e, double f)
            {
                ScaleX = scaleX;
                ScaleY = scaleY;
                A = a;
                B = b;
                C = c;
                D = d;
                E = e;
                F = f;
            }
        }
    }
}
