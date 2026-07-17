using System;
using System.Collections.Generic;

namespace GTAEmblemMaker.Core
{
    internal enum CandidateShapeKind : uint
    {
        RotatedEllipse = 0,
        RotatedRectangle = 1,
        RotatedTriangle = 2,
        LineRectangle = 3,
        OfficialCatalog = 4
    }

    internal sealed class FitCandidate
    {
        internal uint CandidateId { get; private set; }
        internal uint Group { get; private set; }
        internal CandidateShapeKind Kind { get; private set; }
        internal string Shape { get; private set; }
        internal string PoolShapeFamily { get; private set; }
        internal int Cx { get; private set; }
        internal int Cy { get; private set; }
        internal int Rx { get; private set; }
        internal int Ry { get; private set; }
        internal byte Red { get; private set; }
        internal byte Green { get; private set; }
        internal byte Blue { get; private set; }
        internal byte Alpha { get; private set; }
        internal float AngleDegrees { get; private set; }
        internal double Energy { get; private set; }
        internal ulong OldErrorDelta { get; private set; }
        internal ulong NewErrorDelta { get; private set; }

        internal FitCandidate(uint candidateId, uint group, CandidateShapeKind kind, string shape, string poolShapeFamily, int cx, int cy, int rx, int ry, byte red, byte green, byte blue, byte alpha, float angleDegrees, double energy, ulong oldErrorDelta, ulong newErrorDelta)
        {
            CandidateId = candidateId;
            Group = group;
            Kind = kind;
            Shape = shape;
            PoolShapeFamily = poolShapeFamily;
            Cx = cx;
            Cy = cy;
            Rx = rx;
            Ry = ry;
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
            AngleDegrees = angleDegrees;
            Energy = energy;
            OldErrorDelta = oldErrorDelta;
            NewErrorDelta = newErrorDelta;
        }
    }

    internal static class CandidateGenerator
    {
        internal const int CandidatesPerGroup = 4583;
        internal const int Multistart = 16;
        internal const int Age = 100;
        internal const int Fanout = 8;
        internal const int EarlyStopRounds = 48;
        internal const int MaxHillSteps = 5000;
        internal const int InitialAlpha = 128;

        internal static uint SeedForLayer(int layer)
        {
            if (layer < 1) throw new ArgumentOutOfRangeException("layer");
            return unchecked((uint)(23456 + CandidatesPerGroup * 17 + Multistart * 101 + layer));
        }

        internal static uint ShapeMask(IReadOnlyList<string> shapes)
        {
            if (shapes == null) throw new ArgumentNullException("shapes");
            if (shapes.Count == 0) throw new ArgumentException("At least one shape is required.", "shapes");
            uint mask = 0;
            for (var index = 0; index < shapes.Count; index++) mask |= 1u << (int)ShapeKindFromName(shapes[index]);
            return mask;
        }

        internal static uint SelectionMode(string residentSelection)
        {
            if (residentSelection == "code-aware-proxy") return 1;
            throw new ArgumentException("Unknown resident selection mode.", "residentSelection");
        }

        internal static CandidateShapeKind ShapeKindFromCode(uint value)
        {
            if (value > 3) throw new ArgumentOutOfRangeException("value");
            return (CandidateShapeKind)value;
        }

        internal static FitCandidate FromResidentResult(uint shapeKind, CudaCandidate candidate, CudaScore score)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (score == null) throw new ArgumentNullException("score");
            if (candidate.CandidateId != score.CandidateId) throw new ArgumentException("Candidate and score IDs differ.");
            var kind = ShapeKindFromCode(shapeKind);
            var poolShapeFamily = PoolShapeFamily(kind);
            var shape = kind == CandidateShapeKind.LineRectangle ? "rotated-rect" : poolShapeFamily;
            return new FitCandidate(candidate.CandidateId, candidate.GroupId, kind, shape, poolShapeFamily, candidate.Cx, candidate.Cy, candidate.Rx, candidate.Ry, score.Red, score.Green, score.Blue, score.Alpha, candidate.AngleDegrees, score.Energy, score.OldErrorDelta, score.NewErrorDelta);
        }

        internal static FitCandidate FromCatalogResult(string identifier, uint group, CudaCatalogCandidate candidate, CudaScore score)
        {
            if (String.IsNullOrWhiteSpace(identifier)) throw new ArgumentException("Catalog identifier is required.", "identifier");
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (score == null || candidate.CandidateId != score.CandidateId) throw new ArgumentException("Catalog candidate and score IDs differ.");
            return new FitCandidate(candidate.CandidateId, group, CandidateShapeKind.OfficialCatalog, identifier, identifier,
                checked((int)Math.Round(candidate.Cx, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(candidate.Cy, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(candidate.Rx, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(candidate.Ry, MidpointRounding.AwayFromZero)),
                score.Red, score.Green, score.Blue, score.Alpha, candidate.AngleDegrees, score.Energy, score.OldErrorDelta, score.NewErrorDelta);
        }

        internal static ShapeState ToShapeState(FitCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            string shape;
            if (candidate.Kind == CandidateShapeKind.RotatedEllipse) shape = "ellipse";
            else if (candidate.Kind == CandidateShapeKind.RotatedTriangle) shape = "triangle";
            else if (candidate.Kind == CandidateShapeKind.RotatedRectangle || candidate.Kind == CandidateShapeKind.LineRectangle) shape = "rectangle";
            else if (candidate.Kind == CandidateShapeKind.OfficialCatalog) shape = candidate.Shape;
            else throw new ArgumentOutOfRangeException("candidate");
            return new ShapeState(shape, candidate.Cx, candidate.Cy, candidate.Rx, candidate.Ry, candidate.Red, candidate.Green, candidate.Blue, candidate.Alpha, candidate.AngleDegrees);
        }

        internal static FitCandidate FromShapeState(ShapeState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            var kind = ShapeKindFromName(state.Shape);
            return new FitCandidate(
                0,
                0,
                kind,
                state.Shape,
                kind == CandidateShapeKind.OfficialCatalog ? state.Shape : PoolShapeFamily(kind),
                checked((int)Math.Round(state.Cx, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(state.Cy, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(state.Rx, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(state.Ry, MidpointRounding.AwayFromZero)),
                checked((byte)state.Red),
                checked((byte)state.Green),
                checked((byte)state.Blue),
                checked((byte)state.Alpha),
                (float)state.AngleDegrees,
                0,
                0,
                0);
        }

        internal static CandidateShapeKind ShapeKindFromName(string shape)
        {
            if (shape == "line-rect") return CandidateShapeKind.LineRectangle;
            if (shape == "rotated-rect" || shape == "rectangle") return CandidateShapeKind.RotatedRectangle;
            if (shape == "rotated-triangle" || shape == "triangle") return CandidateShapeKind.RotatedTriangle;
            if (shape == "rotated" || shape == "ellipse" || shape == "circle" || shape == "round") return CandidateShapeKind.RotatedEllipse;
            ShapeDefinition definition;
            if (OfficialCatalog.TryGetDefinition(shape, out definition)) return CandidateShapeKind.OfficialCatalog;
            throw new ArgumentException("Unknown candidate shape.", "shape");
        }

        private static string PoolShapeFamily(CandidateShapeKind kind)
        {
            if (kind == CandidateShapeKind.LineRectangle) return "line-rect";
            if (kind == CandidateShapeKind.RotatedRectangle) return "rotated-rect";
            if (kind == CandidateShapeKind.RotatedTriangle) return "rotated-triangle";
            if (kind == CandidateShapeKind.RotatedEllipse) return "rotated";
            throw new ArgumentOutOfRangeException("kind");
        }
    }
}
