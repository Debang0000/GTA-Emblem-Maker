using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class CleanLogoSanitizerChecks
    {
        private const int Size = 512;

        internal static void Run()
        {
            CheckSupportMasks();
            CheckCleanError();
            CheckLocalNonRegression();
            CheckColorSnapAndAcceptance();
            CheckSupportRejection();
        }

        private static void CheckSupportMasks()
        {
            var alphaTarget = new byte[Size * Size * 4];
            alphaTarget[(10 * Size + 10) * 4 + 3] = 255;
            var alphaSupport = CleanLogoSanitizer.BuildAllowedAlphaSupport(alphaTarget);
            Check.True(alphaSupport[9 * Size + 9], "clean logo alpha support one-pixel dilation");
            Check.True(alphaSupport[11 * Size + 11], "clean logo alpha support diagonal dilation");
            Check.False(alphaSupport[10 * Size + 12], "clean logo alpha support dilation boundary");

            var edgeTarget = OpaqueBlack();
            edgeTarget[(20 * Size + 20) * 4] = 24;
            var edgeSupport = CleanLogoSanitizer.BuildAllowedEdgeSupport(edgeTarget);
            Check.True(edgeSupport[20 * Size + 20], "clean logo edge threshold");
            Check.True(edgeSupport[18 * Size + 20], "clean logo edge one-pixel dilation");
            Check.False(edgeSupport[16 * Size + 20], "clean logo edge dilation boundary");
        }

        private static void CheckCleanError()
        {
            var weights = UniformWeights();
            var transparentTarget = new byte[Size * Size * 4];
            var transparentCurrent = new byte[transparentTarget.Length];
            transparentCurrent[3] = 1;
            Check.Equal(4L, CleanLogoSanitizer.CleanError(transparentTarget, transparentCurrent, weights), "clean logo transparent alpha priority");

            var opaqueTarget = OpaqueBlack();
            var opaqueCurrent = (byte[])opaqueTarget.Clone();
            opaqueCurrent[0] = 1;
            opaqueCurrent[3] = 254;
            Check.Equal(2L, CleanLogoSanitizer.CleanError(opaqueTarget, opaqueCurrent, weights), "clean logo opaque RGBA parity");
        }

        private static void CheckLocalNonRegression()
        {
            var target = OpaqueBlack();
            var current = (byte[])target.Clone();
            for (var y = 0; y < 10; y++)
            {
                for (var x = 0; x < 10; x++) current[(y * Size + x) * 4] = 100;
            }
            var improving = (byte[])target.Clone();
            Check.True(CleanLogoSanitizer.AllowsLocalNonRegression(target, current, improving, UniformWeights()), "clean logo accepts local non-regression");
            improving[(32 * Size + 32) * 4] = 1;
            Check.False(CleanLogoSanitizer.AllowsLocalNonRegression(target, current, improving, UniformWeights()), "clean logo rejects one regressing tile");
        }

        private static void CheckColorSnapAndAcceptance()
        {
            var transparent = new byte[Size * Size * 4];
            var sourceState = new ShapeState("ellipse", 256, 256, 48, 32, 32, 144, 224, 255, 17);
            var target = RunArtifacts.RenderShapeOnto(transparent, sourceState, 4);
            var candidate = new FitCandidate(7, 0, CandidateShapeKind.RotatedEllipse, "rotated", "rotated", 256, 256, 48, 32, 31, 144, 224, 255, 17, 0.1, 100, 10);
            var sanitizer = new CleanLogoSanitizer(target, transparent, UniformWeights(), true, 4);
            CleanLogoProposal proposal;
            Check.True(sanitizer.TrySelect(new[] { candidate }, out proposal), "clean logo accepts exact source-supported proposal");
            Check.Equal(32, proposal.State.Red, "clean logo snapped red");
            Check.Equal(144, proposal.State.Green, "clean logo snapped green");
            Check.Equal(224, proposal.State.Blue, "clean logo snapped blue");
            Check.Equal(255, proposal.State.Alpha, "clean logo opaque state");
            sanitizer.Commit(proposal);
            Check.Equal(1, sanitizer.Metrics.ColorSnappedLayers, "clean logo snapped color metric");
            Check.Equal(CleanLogoSanitizer.CleanError(target, proposal.CurrentRgba, UniformWeights()), sanitizer.CurrentError, "clean logo committed exact error");
        }

        private static void CheckSupportRejection()
        {
            var transparent = new byte[Size * Size * 4];
            var sourceState = new ShapeState("ellipse", 256, 256, 16, 16, 40, 160, 220, 255, 0);
            var target = RunArtifacts.RenderShapeOnto(transparent, sourceState, 4);
            var oversized = new FitCandidate(8, 0, CandidateShapeKind.RotatedEllipse, "rotated", "rotated", 256, 256, 48, 48, 40, 160, 220, 255, 0, 0.1, 100, 10);
            var sanitizer = new CleanLogoSanitizer(target, transparent, UniformWeights(), true, 4);
            CleanLogoProposal proposal;
            Check.False(sanitizer.TrySelect(new[] { oversized }, out proposal), "clean logo rejects transparent spill");
            Check.Equal(1, sanitizer.Metrics.SupportRejectedLayers, "clean logo support rejection metric");
        }

        private static byte[] OpaqueBlack()
        {
            var rgba = new byte[Size * Size * 4];
            for (var offset = 3; offset < rgba.Length; offset += 4) rgba[offset] = 255;
            return rgba;
        }

        private static byte[] UniformWeights()
        {
            var weights = new byte[Size * Size * 2];
            for (var offset = 0; offset < weights.Length; offset += 2) weights[offset + 1] = 1;
            return weights;
        }
    }
}
