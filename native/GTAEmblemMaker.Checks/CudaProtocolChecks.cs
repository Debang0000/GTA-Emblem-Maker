using System;
using System.IO;
using System.Threading;
using GTAEmblemMaker.Core;

namespace GTAEmblemMaker.Checks
{
    internal static class CudaProtocolChecks
    {
        public static void Run()
        {
            CheckInitRequest();
            CheckSetWeightMapRequest();
            CheckSetStrokeGuideRequest();
            CheckSetMultiScaleStrokeGuideRequest();
            CheckSetStructuralGuideRequest();
            CheckUpdateCurrentRequest();
            CheckShutdownRequest();
            CheckSetCatalogAtlasesRequest();
            CheckResidentCatalogBatchRequest();
            CheckResidentCatalogSelectionRequest();
            CheckRotatedResidentRequest();
            CheckMixedResidentRequest();
            CheckRotatedRequest();
            CheckMixedRequest();
            CheckGuidedMixedRequest();
            CheckStructuralGuidedMixedRequest();
            CheckRequestValidation();
            CheckResponses();
            CheckIntegration();
        }

        private static void CheckInitRequest()
        {
            Check.SequenceEqual(new byte[]
            {
                1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
                8, 7, 6, 5, 4, 3, 2, 1,
                1, 2, 3, 4, 5, 6, 7, 8
            }, CudaProtocol.CreateInitRequest(1, 1, 0x0102030405060708UL,
                new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }), "CUDA INIT layout");
        }

        private static void CheckSetWeightMapRequest()
        {
            Check.SequenceEqual(new byte[] { 7, 0, 0, 0, 0x34, 0x12, 0x78, 0x56 },
                CudaProtocol.CreateSetWeightMapRequest(new byte[] { 0x34, 0x12, 0x78, 0x56 }), "CUDA SET_WEIGHT_MAP layout");
        }

        private static void CheckSetStrokeGuideRequest()
        {
            Check.SequenceEqual(new byte[] { 18, 0, 0, 0, 0x34, 0x12, 0x78, 0x56 },
                CudaProtocol.CreateSetStrokeGuideRequest(new byte[] { 0x34, 0x12 }, new byte[] { 0x78, 0x56 }), "CUDA SET_STROKE_GUIDE layout");
        }

        private static void CheckSetMultiScaleStrokeGuideRequest()
        {
            Check.SequenceEqual(new byte[] { 20, 0, 0, 0, 0x34, 0x12, 0x78, 0x56, 0xbc, 0x9a },
                CudaProtocol.CreateSetMultiScaleStrokeGuideRequest(new byte[] { 0x34, 0x12 }, new byte[] { 0x78, 0x56 }, new byte[] { 0xbc, 0x9a }), "CUDA SET_MULTI_SCALE_STROKE_GUIDE layout");
        }

        private static void CheckSetStructuralGuideRequest()
        {
            Check.SequenceEqual(new byte[] { 21, 0, 0, 0, 0x34, 0x12, 0x78, 0x56 },
                CudaProtocol.CreateSetStructuralGuideRequest(new byte[] { 0x34, 0x12 }, new byte[] { 0x78, 0x56 }), "CUDA SET_STRUCTURAL_GUIDE layout");
        }

        private static void CheckUpdateCurrentRequest()
        {
            Check.SequenceEqual(new byte[]
            {
                3, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1, 9, 10, 11, 12
            }, CudaProtocol.CreateUpdateCurrentRequest(0x0102030405060708UL,
                new byte[] { 9, 10, 11, 12 }), "CUDA UPDATE_CURRENT layout");
        }

        private static void CheckShutdownRequest()
        {
            Check.SequenceEqual(new byte[] { 4, 0, 0, 0 }, CudaProtocol.CreateShutdownRequest(), "CUDA SHUTDOWN layout");
        }

        private static void CheckSetCatalogAtlasesRequest()
        {
            var atlas = CatalogMaskAtlas.Build(32)[0];
            var request = CudaProtocol.CreateSetCatalogAtlasesRequest(new[] { atlas });
            Check.Equal(25, BitConverter.ToInt32(request, 0), "CUDA resident catalog atlas command");
            Check.Equal(1, BitConverter.ToInt32(request, 4), "CUDA resident catalog atlas count");
            Check.Equal(8 + 28 + 32 * 32, request.Length, "CUDA resident catalog atlas payload length");
        }

        private static void CheckResidentCatalogBatchRequest()
        {
            var candidates = new[]
            {
                new CudaCatalogCandidate { CandidateId = 7, Cx = 1, Cy = 2, Rx = 3, Ry = 4, Alpha = 5, AngleDegrees = 6 },
                new CudaCatalogCandidate { CandidateId = 8, Cx = 9, Cy = 10, Rx = 11, Ry = 12, Alpha = 13, AngleDegrees = 14 }
            };
            var request = CudaProtocol.CreateResidentCatalogScoreRequest(new[] { new CudaCatalogBatch(3, candidates) }, true);
            Check.Equal(26, BitConverter.ToInt32(request, 0), "CUDA resident catalog score command");
            Check.Equal(1, BitConverter.ToInt32(request, 4), "CUDA resident catalog weighted flag");
            Check.Equal(1, BitConverter.ToInt32(request, 8), "CUDA resident catalog batch count");
            Check.Equal(2, BitConverter.ToInt32(request, 12), "CUDA resident catalog candidate count");
            Check.Equal(3, BitConverter.ToInt32(request, 16), "CUDA resident catalog atlas index");
            Check.Equal(2, BitConverter.ToInt32(request, 20), "CUDA resident catalog batch candidate count");
            Check.Equal(88, request.Length, "CUDA resident catalog score payload length");
        }

        private static void CheckResidentCatalogSelectionRequest()
        {
            Check.SequenceEqual(UInt32Fields(27, 1, 11, 512, 1174, 2, 0x12345678),
                CudaProtocol.CreateResidentCatalogSelectionRequest(11, 512, 1174, 2, 0x12345678, true),
                "CUDA resident catalog selection layout");
        }

        private static void CheckRotatedRequest()
        {
            var request = Request();
            request.EarlyStopRounds = 15;
            request.DeviceChunkRounds = 15;
            Check.SequenceEqual(UInt32Fields(16, 1, 2, 3, 4, 15, 6, 7, 8, 1, 1, 9, 10, 11, 12, 15),
                CudaProtocol.CreateSelectLayerRequest(request), "CUDA rotated device-chunk layout");
        }

        private static void CheckRotatedResidentRequest()
        {
            var request = Request();
            request.Mode = CudaSelectLayerMode.RotatedResident;
            request.DeviceChunkRounds = 0;
            Check.SequenceEqual(UInt32Fields(14, 1, 2, 3, 4, 5, 6, 7, 8, 1, 1, 9, 10, 11, 12),
                CudaProtocol.CreateSelectLayerRequest(request), "CUDA rotated compatibility layout");
        }

        private static void CheckMixedResidentRequest()
        {
            var request = Request();
            request.Mode = CudaSelectLayerMode.MixedResident;
            request.DeviceChunkRounds = 0;
            request.ShapeMask = 15;
            request.SelectionMode = 1;
            Check.SequenceEqual(UInt32Fields(15, 1, 2, 3, 4, 5, 6, 7, 8, 1, 1, 9, 10, 11, 12, 15, 1, 0),
                CudaProtocol.CreateSelectLayerRequest(request), "CUDA mixed compatibility layout");
        }

        private static void CheckMixedRequest()
        {
            var request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 15;
            request.SelectionMode = 1;
            request.GuideMode = 2;
            request.EarlyStopRounds = 16;
            request.DeviceChunkRounds = 16;
            Check.SequenceEqual(UInt32Fields(17, 1, 2, 3, 4, 16, 6, 7, 8, 1, 1, 9, 10, 11, 12, 15, 1, 2, 16),
                CudaProtocol.CreateSelectLayerRequest(request), "CUDA mixed device-chunk layout");
        }

        private static void CheckGuidedMixedRequest()
        {
            var request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 8;
            request.SelectionMode = 1;
            request.GuideMode = 2;
            request.MinAxis = 1;
            request.StrokeScale = 1;
            request.MinLongAxis = 2;
            request.MaxLongAxis = 20;
            request.EarlyStopRounds = 16;
            request.DeviceChunkRounds = 16;
            Check.SequenceEqual(UInt32Fields(19, 1, 2, 3, 4, 16, 6, 1, 8, 1, 1, 9, 10, 11, 12, 8, 1, 2, 1, 2, 20, 16),
                CudaProtocol.CreateSelectLayerRequest(request), "CUDA guided mixed device-chunk layout");
        }

        private static void CheckStructuralGuidedMixedRequest()
        {
            var request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 8;
            request.SelectionMode = 1;
            request.GuideMode = 2;
            request.MinAxis = 1;
            request.StrokeScale = 1;
            request.MinLongAxis = 2;
            request.MaxLongAxis = 20;
            request.EarlyStopRounds = 16;
            request.DeviceChunkRounds = 16;
            request.StructuralEdgeWeightQ16 = 16384;
            request.StructuralDistanceLimit = 8;
            request.StructuralRounds = 64;
            request.MaxPixelGainRegressionQ16 = 6554;
            Check.SequenceEqual(UInt32Fields(22, 1, 2, 3, 4, 16, 6, 1, 8, 1, 1, 9, 10, 11, 12, 8, 1, 2, 1, 2, 20, 16, 16384, 8, 64, 6554),
                CudaProtocol.CreateSelectLayerRequest(request), "CUDA structural mixed device-chunk layout");
        }

        private static void CheckRequestValidation()
        {
            var request = Request();
            request.Mode = CudaSelectLayerMode.Unspecified;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA explicit command mode");

            request = Request();
            request.ShapeMask = 1;
            Check.Throws<ArgumentException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA rotated fields");

            request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            Check.Throws<ArgumentException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA mixed shape mask");

            request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 8;
            request.GuideMode = 2;
            request.StrokeScale = 3;
            request.MinLongAxis = 2;
            request.MaxLongAxis = 20;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA guided stroke scale bound");

            request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 8;
            request.GuideMode = 2;
            request.StrokeScale = 1;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA guided stroke length bound");

            request = Request();
            request.Mode = CudaSelectLayerMode.MixedDeviceChunk;
            request.ShapeMask = 8;
            request.GuideMode = 2;
            request.StrokeScale = 2;
            request.MinLongAxis = 20;
            request.MaxLongAxis = 24;
            request.StructuralEdgeWeightQ16 = 16384;
            request.StructuralDistanceLimit = 8;
            request.StructuralRounds = 64;
            request.MaxPixelGainRegressionQ16 = 6554;
            Check.Throws<ArgumentException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA structural detail-stroke requirement");

            request.StrokeScale = 1;
            request.MinLongAxis = 2;
            request.MaxLongAxis = 20;
            request.GuideMode = 1;
            Check.Throws<ArgumentException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA structural fixed-width guide requirement");

            request = Request();
            request.CandidatesPerGroup = CudaProtocol.MaxCandidatesPerGroup + 1;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA candidate bound");

            request = Request();
            request.GroupCount = CudaProtocol.MaxGroupCount + 1;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA group bound");

            request = Request();
            request.DeviceChunkRounds = CudaProtocol.MaxDeviceChunkRounds + 1;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA chunk-round bound");

            request = Request();
            request.Layer = 1501;
            Check.Equal(64, CudaProtocol.CreateSelectLayerRequest(request).Length, "CUDA overfit attempt layer");

            request = Request();
            request.Layer = CudaProtocol.MaxLayer + 1;
            Check.Throws<ArgumentOutOfRangeException>(() => CudaProtocol.CreateSelectLayerRequest(request), "CUDA attempt layer bound");

            request = Request();
            request.CandidatesPerGroup = CudaProtocol.MaxCandidatesPerGroup;
            request.GroupCount = CudaProtocol.MaxGroupCount;
            Check.Equal(64, CudaProtocol.CreateSelectLayerRequest(request).Length, "CUDA checked production candidate product");

            Check.Throws<ArgumentException>(() => CudaScorerClient.Start("not-cuda-scorer.exe", 1, 1,
                new byte[4], new byte[4]), "CUDA packaged executable name");
        }

        private static void CheckResponses()
        {
            var rotated = new byte[CudaProtocol.RotatedResponseSize];
            WriteUInt32(rotated, 4, 23);
            WriteCandidate(rotated, 124, 41);
            WriteScore(rotated, 156, 41);
            var rotatedResult = CudaProtocol.ParseRotatedResponse(rotated);
            Check.Equal(23, (int)rotatedResult.InitialCandidateCount, "CUDA rotated response count");
            Check.Equal(41, (int)rotatedResult.SelectedCandidate.CandidateId, "CUDA rotated candidate");
            Check.Equal(41, (int)rotatedResult.SelectedScore.CandidateId, "CUDA rotated score");

            var mixedPrefix = new byte[CudaProtocol.MixedResponsePrefixSize];
            WriteUInt32(mixedPrefix, 36, 2);
            WriteCandidate(mixedPrefix, 128, 51);
            WriteScore(mixedPrefix, 160, 51);
            WriteUInt32(mixedPrefix, 192, 1);
            var mixedTail = new byte[CudaProtocol.MixedChainSize];
            WriteUInt32(mixedTail, 0, 3);
            WriteCandidate(mixedTail, 4, 61);
            WriteScore(mixedTail, 36, 61);
            var mixedResult = CudaProtocol.ParseMixedResponse(mixedPrefix, mixedTail);
            Check.Equal(2, (int)mixedResult.SelectedShapeKind, "CUDA mixed selected shape");
            Check.Equal(1, mixedResult.Chains.Count, "CUDA mixed chain count");
            Check.Equal(3, (int)mixedResult.Chains[0].ShapeKind, "CUDA mixed chain shape");
            Check.Equal(61, (int)mixedResult.Chains[0].Candidate.CandidateId, "CUDA mixed chain candidate");

            var failed = new byte[12];
            WriteUInt32(failed, 0, 7);
            Check.Throws<InvalidDataException>(() => CudaProtocol.ParseTimingResponse("INIT", failed), "CUDA status validation");

            var oversizedMixed = new byte[CudaProtocol.MixedResponsePrefixSize];
            WriteUInt32(oversizedMixed, 192, CudaProtocol.MaxMixedChainCount + 1);
            Check.Throws<InvalidDataException>(() => CudaProtocol.ParseMixedResponse(oversizedMixed, new byte[0]), "CUDA mixed response chain bound");
        }

        private static void CheckIntegration()
        {
            var executable = RepositoryFile("third_party", "cuda-scorer", "bin", "cuda-scorer.exe");
            if (!File.Exists(executable))
            {
                Console.WriteLine("SKIP CUDA integration: third_party\\cuda-scorer\\bin\\cuda-scorer.exe is absent.");
                return;
            }

            const int size = 32;
            var target = new byte[size * size * 4];
            var current = new byte[target.Length];
            for (var index = 0; index < target.Length; index += 4)
            {
                target[index] = (byte)(index % 251);
                target[index + 1] = (byte)((index * 3) % 251);
                target[index + 2] = (byte)((index * 7) % 251);
                target[index + 3] = 255;
                current[index] = 255;
                current[index + 1] = 255;
                current[index + 2] = 255;
                current[index + 3] = 255;
            }

            using (var client = CudaScorerClient.Start(executable, size, size, target, current))
            {
                var weights = new byte[size * size * 2];
                for (var index = 0; index < weights.Length; index += 2) weights[index + 1] = 1;
                client.SetWeightMapAsync(weights, CancellationToken.None).GetAwaiter().GetResult();
                var request = new CudaSelectLayerRequest
                {
                    Mode = CudaSelectLayerMode.MixedDeviceChunk,
                    CandidatesPerGroup = 1,
                    GroupCount = 1,
                    Age = 1,
                    Fanout = 1,
                    EarlyStopRounds = 1,
                    MaxHillSteps = 8,
                    MinAxis = 1,
                    Layer = 1,
                    Weighted = true,
                    MinAlpha = 1,
                    MaxAlpha = 255,
                    InitialAlpha = 128,
                    Seed = 1,
                    ShapeMask = 3,
                    SelectionMode = 1,
                    DeviceChunkRounds = 1
                };
                var result = client.SelectLayerAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(result.InitialCandidateCount > 0, "CUDA integration selected a layer");
                Check.Equal(2, result.Chains.Count, "CUDA integration mixed chain count");
                Check.Equal(0, (int)result.Chains[0].ShapeKind, "CUDA integration first mixed shape");
                Check.Equal(1, (int)result.Chains[1].ShapeKind, "CUDA integration second mixed shape");
                Check.Equal((int)result.Chains[0].Candidate.CandidateId, (int)result.Chains[0].Score.CandidateId, "CUDA integration first mixed record");
                Check.Equal((int)result.Chains[1].Candidate.CandidateId, (int)result.Chains[1].Score.CandidateId, "CUDA integration second mixed record");
                var guide = new byte[size * size * 2];
                for (var index = 0; index < guide.Length; index += 2)
                {
                    guide[index] = 255;
                    guide[index + 1] = 255;
                }
                client.SetStrokeGuideAsync(guide, new byte[guide.Length], CancellationToken.None).GetAwaiter().GetResult();
                request.ShapeMask = 8;
                request.GuideMode = 2;
                var guided = client.SelectLayerAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Check.Equal(1, guided.Chains.Count, "CUDA integration guided stroke chain count");
                Check.Equal(3, (int)guided.Chains[0].ShapeKind, "CUDA integration guided stroke shape");
                Check.Equal(1, guided.Chains[0].Candidate.Ry, "CUDA integration guided stroke half width");
                client.SetMultiScaleStrokeGuideAsync(guide, guide, new byte[guide.Length], CancellationToken.None).GetAwaiter().GetResult();
                request.StrokeScale = 1;
                request.MinLongAxis = 2;
                request.MaxLongAxis = 4;
                var detail = client.SelectLayerAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(detail.Chains[0].Candidate.Rx >= 2 && detail.Chains[0].Candidate.Rx <= 4, "CUDA integration detail stroke length: " + detail.Chains[0].Candidate.Rx);
                Check.Equal(1, detail.Chains[0].Candidate.Ry, "CUDA integration detail stroke half width");
                client.SetStructuralGuideAsync(new byte[guide.Length], new byte[guide.Length], CancellationToken.None).GetAwaiter().GetResult();
                request.StructuralEdgeWeightQ16 = 16384;
                request.StructuralDistanceLimit = 8;
                request.StructuralRounds = 64;
                request.MaxPixelGainRegressionQ16 = 6554;
                var structural = client.SelectLayerAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Check.Equal(1, structural.Chains.Count, "CUDA integration structural stroke chain count");
                Check.Equal(3, (int)structural.Chains[0].ShapeKind, "CUDA integration structural stroke shape");
                Check.True(structural.Rounds >= 65, "CUDA integration structural rounds");
                request.StructuralEdgeWeightQ16 = 0;
                request.StructuralDistanceLimit = 0;
                request.StructuralRounds = 0;
                request.MaxPixelGainRegressionQ16 = 0;
                request.StrokeScale = 2;
                request.MinLongAxis = 20;
                request.MaxLongAxis = 24;
                var contour = client.SelectLayerAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(contour.Chains[0].Candidate.Rx >= 20 && contour.Chains[0].Candidate.Rx <= 24, "CUDA integration contour stroke length");
                Check.Equal(1, contour.Chains[0].Candidate.Ry, "CUDA integration contour stroke half width");
                client.UpdateCurrentAsync(CudaScorerClient.CalculateTotalError(target, current), current, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private static string RepositoryFile(params string[] parts)
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (folder != null)
            {
                var pathParts = new string[parts.Length + 1];
                pathParts[0] = folder.FullName;
                Array.Copy(parts, 0, pathParts, 1, parts.Length);
                var candidate = Path.Combine(pathParts);
                if (File.Exists(candidate)) return candidate;
                folder = folder.Parent;
            }
            return Path.Combine(parts);
        }

        private static CudaSelectLayerRequest Request()
        {
            return new CudaSelectLayerRequest
            {
                Mode = CudaSelectLayerMode.RotatedDeviceChunk,
                CandidatesPerGroup = 1,
                GroupCount = 2,
                Age = 3,
                Fanout = 4,
                EarlyStopRounds = 5,
                MaxHillSteps = 6,
                MinAxis = 7,
                Layer = 8,
                Weighted = true,
                MutateAlpha = true,
                MinAlpha = 9,
                MaxAlpha = 10,
                InitialAlpha = 11,
                Seed = 12,
                DeviceChunkRounds = 1
            };
        }

        private static byte[] UInt32Fields(params uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (var index = 0; index < values.Length; index++) WriteUInt32(bytes, index * 4, values[index]);
            return bytes;
        }

        private static void WriteCandidate(byte[] bytes, int offset, uint candidateId)
        {
            WriteUInt32(bytes, offset, candidateId);
            WriteUInt32(bytes, offset + 4, 2);
            WriteUInt32(bytes, offset + 8, 3);
            WriteUInt32(bytes, offset + 12, 4);
            WriteUInt32(bytes, offset + 16, 5);
            WriteUInt32(bytes, offset + 20, 6);
            Buffer.BlockCopy(BitConverter.GetBytes(7.5f), 0, bytes, offset + 24, 4);
            WriteUInt32(bytes, offset + 28, 8);
        }

        private static void WriteScore(byte[] bytes, int offset, uint candidateId)
        {
            WriteUInt32(bytes, offset, candidateId);
            bytes[offset + 4] = 1;
            bytes[offset + 5] = 2;
            bytes[offset + 6] = 3;
            bytes[offset + 7] = 4;
            Buffer.BlockCopy(BitConverter.GetBytes(0.25), 0, bytes, offset + 8, 8);
            WriteUInt64(bytes, offset + 16, 9);
            WriteUInt64(bytes, offset + 24, 10);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            for (var index = 0; index < 4; index++) bytes[offset + index] = (byte)(value >> (index * 8));
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            for (var index = 0; index < 8; index++) bytes[offset + index] = (byte)(value >> (index * 8));
        }
    }
}
