using System;
using System.Security.Cryptography;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class RunArtifactsChecks
    {
        internal static void Run()
        {
            CheckSingleLayerParity(true);
            CheckSingleLayerParity(false);
        }

        private static void CheckSingleLayerParity(bool transparent)
        {
            var current = new byte[512 * 512 * 4];
            var backgroundRed = transparent ? 0 : 11;
            var backgroundGreen = transparent ? 0 : 23;
            var backgroundBlue = transparent ? 0 : 47;
            if (!transparent)
            {
                for (var index = 0; index < current.Length; index += 4)
                {
                    current[index] = (byte)backgroundRed;
                    current[index + 1] = (byte)backgroundGreen;
                    current[index + 2] = (byte)backgroundBlue;
                    current[index + 3] = 255;
                }
            }

            var state = new ShapeState("ellipse", 231, 287, 43, 29, 87, 143, 219, 255, 31);
            var builder = RockstarExporter.CreateBuilder(transparent, backgroundRed, backgroundGreen, backgroundBlue, 1700000000000);
            Check.True(builder.TryAdd(state, 1250000), "single exact replay payload");
            var payload = builder.Build();
            var expected = RunArtifacts.RenderPayloadPreview(new[] { state }, payload);
            var actual = RunArtifacts.RenderShapeOnto(current, state, payload.ExportMinAxis);
            Check.Equal(Hash(expected), Hash(actual), transparent ? "transparent single-layer exact replay" : "opaque single-layer exact replay");
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
