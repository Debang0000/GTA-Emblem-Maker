using System;
using System.Globalization;

namespace GTAEmblemMaker.Core
{
    public sealed class ShapeState
    {
        public string Shape { get; private set; }
        public double Cx { get; private set; }
        public double Cy { get; private set; }
        public double Rx { get; private set; }
        public double Ry { get; private set; }
        public int Red { get; private set; }
        public int Green { get; private set; }
        public int Blue { get; private set; }
        public int Alpha { get; private set; }
        public double AngleDegrees { get; private set; }

        public ShapeState(string shape, double cx, double cy, double rx, double ry, int red, int green, int blue, int alpha, double angleDegrees)
        {
            if (!Shapes.IsKnownShape(shape)) throw new ArgumentException("Unknown shape.", "shape");
            ValidateCanvasValue(cx, "cx");
            ValidateCanvasValue(cy, "cy");
            ValidateRadius(rx, "rx");
            ValidateRadius(ry, "ry");
            if (!IsFinite(angleDegrees) || angleDegrees < 0 || angleDegrees > 180) throw new ArgumentOutOfRangeException("angleDegrees");
            ValidateByte(red, "red");
            ValidateByte(green, "green");
            ValidateByte(blue, "blue");
            ValidateByte(alpha, "alpha");
            Shape = shape;
            Cx = cx;
            Cy = cy;
            Rx = rx;
            Ry = ry;
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
            AngleDegrees = angleDegrees;
        }

        private static void ValidateCanvasValue(double value, string name)
        {
            if (!IsFinite(value) || value < 0 || value > 512) throw new ArgumentOutOfRangeException(name);
        }

        private static void ValidateRadius(double value, string name)
        {
            if (!IsFinite(value) || value < 0 || value > 512) throw new ArgumentOutOfRangeException(name);
        }

        private static void ValidateByte(int value, string name)
        {
            if (value < 0 || value > 255) throw new ArgumentOutOfRangeException(name);
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }

    internal sealed class ExportShape
    {
        public ShapeDefinition Definition { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }
        public int Alpha { get; private set; }
        public double Rotation { get; private set; }
        public string Color { get; private set; }
        public bool UsesIntrinsicAnchor { get; private set; }

        public ExportShape(ShapeDefinition definition, double x, double y, double width, double height, int alpha, double rotation, string color, bool usesIntrinsicAnchor)
        {
            Definition = definition;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Alpha = alpha;
            Rotation = rotation;
            Color = color;
            UsesIntrinsicAnchor = usesIntrinsicAnchor;
        }
    }

    internal sealed class ShapeDefinition
    {
        public string Slug { get; private set; }
        public string Name { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }
        public string Path { get; private set; }

        public ShapeDefinition(string slug, string name, double width, double height, string path)
        {
            Slug = slug;
            Name = name;
            Width = width;
            Height = height;
            Path = path;
        }
    }

    internal static class Shapes
    {
        internal const double MinEllipseAxis = 4;

        internal static readonly ShapeDefinition Rectangle21 = new ShapeDefinition(
            "rectangles/21", "21", 66.47, 300,
            "M0,0H66.437V300H0V148.125C0,148.125,0.04,147.744,0,147.625C-0.063,147.437,0,147.062,0,147.062V0Z");
        internal static readonly ShapeDefinition Round01 = new ShapeDefinition(
            "rounds/01", "01", 300, 299.99,
            "M300,149.988C300,232.831,232.835,299.985,149.997,299.985C67.154,299.985,0,232.831,0,149.988C0,67.15,67.154,0,149.997,0C232.835,0,300,67.15,300,149.988Z");
        internal static readonly ShapeDefinition Angle01 = new ShapeDefinition(
            "angles/01", "01", 299.71, 300,
            "M148.842,0L84.523,129.637C84.523,129.637,84.531,129.782,84.426,129.83C84.382,129.851,84.339,130.006,84.339,130.006L0,300H299.706L148.842,0Z");

        internal static ExportShape ToExportShape(ShapeState state)
        {
            ShapeDefinition definition;
            var usesIntrinsicAnchor = OfficialCatalog.TryGetDefinition(state.Shape, out definition);
            if (!usesIntrinsicAnchor) definition = DefinitionFor(state.Shape);
            var rx = usesIntrinsicAnchor ? state.Rx : Math.Max(state.Rx, MinEllipseAxis);
            var ry = usesIntrinsicAnchor ? state.Ry : Math.Max(state.Ry, MinEllipseAxis);
            var alpha = state.Alpha;
            if (definition == Round01 && (rx != state.Rx || ry != state.Ry))
            {
                alpha = Math.Max(1, ClampByte((int)Math.Floor(state.Alpha * state.Rx * state.Ry / (rx * ry) + 0.5)));
            }

            return new ExportShape(definition, state.Cx - rx, state.Cy - ry, rx * 2, ry * 2, alpha, state.AngleDegrees, RgbHex(state.Red, state.Green, state.Blue), usesIntrinsicAnchor);
        }

        private static ShapeDefinition DefinitionFor(string shape)
        {
            if (shape == "rotated-triangle" || shape == "triangle") return Angle01;
            if (shape == "rotated-rect" || shape == "rectangle" || shape == "line-rect") return Rectangle21;
            return Round01;
        }

        internal static bool IsKnownShape(string shape)
        {
            ShapeDefinition definition;
            return shape == "rotated" || shape == "ellipse" || shape == "circle" || shape == "round" || shape == "rotated-triangle" || shape == "triangle" || shape == "rotated-rect" || shape == "rectangle" || shape == "line-rect" || OfficialCatalog.TryGetDefinition(shape, out definition);
        }

        private static string RgbHex(int red, int green, int blue)
        {
            return "#" + ClampByte(red).ToString("x2", CultureInfo.InvariantCulture) + ClampByte(green).ToString("x2", CultureInfo.InvariantCulture) + ClampByte(blue).ToString("x2", CultureInfo.InvariantCulture);
        }

        private static int ClampByte(int value)
        {
            return Math.Max(0, Math.Min(255, value));
        }
    }
}
