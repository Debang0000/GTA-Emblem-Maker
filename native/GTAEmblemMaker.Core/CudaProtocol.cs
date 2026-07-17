using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace GTAEmblemMaker.Core
{
    public enum CudaSelectLayerMode
    {
        Unspecified = 0,
        RotatedDeviceChunk = 1,
        MixedDeviceChunk = 2,
        RotatedResident = 3,
        MixedResident = 4
    }

    public sealed class CudaSelectLayerRequest
    {
        public CudaSelectLayerMode Mode { get; set; }
        public uint CandidatesPerGroup { get; set; }
        public uint GroupCount { get; set; }
        public uint Age { get; set; }
        public uint Fanout { get; set; }
        public uint EarlyStopRounds { get; set; }
        public uint MaxHillSteps { get; set; }
        public uint MinAxis { get; set; }
        public uint Layer { get; set; }
        public bool Weighted { get; set; }
        public bool MutateAlpha { get; set; }
        public uint MinAlpha { get; set; }
        public uint MaxAlpha { get; set; }
        public uint InitialAlpha { get; set; }
        public uint Seed { get; set; }
        public uint ShapeMask { get; set; }
        public uint SelectionMode { get; set; }
        public uint GuideMode { get; set; }
        public uint StrokeScale { get; set; }
        public uint MinLongAxis { get; set; }
        public uint MaxLongAxis { get; set; }
        public uint DeviceChunkRounds { get; set; }
        public uint StructuralEdgeWeightQ16 { get; set; }
        public uint StructuralDistanceLimit { get; set; }
        public uint StructuralRounds { get; set; }
        public uint MaxPixelGainRegressionQ16 { get; set; }

        public bool IsMixed { get { return Mode == CudaSelectLayerMode.MixedDeviceChunk || Mode == CudaSelectLayerMode.MixedResident; } }
        public bool IsDeviceChunk { get { return Mode == CudaSelectLayerMode.RotatedDeviceChunk || Mode == CudaSelectLayerMode.MixedDeviceChunk; } }
    }

    public sealed class CudaCandidate
    {
        public uint CandidateId { get; internal set; }
        public int Cx { get; internal set; }
        public int Cy { get; internal set; }
        public int Rx { get; internal set; }
        public int Ry { get; internal set; }
        public int Alpha { get; internal set; }
        public float AngleDegrees { get; internal set; }
        public uint GroupId { get; internal set; }
    }

    public sealed class CudaScore
    {
        public uint CandidateId { get; internal set; }
        public byte Red { get; internal set; }
        public byte Green { get; internal set; }
        public byte Blue { get; internal set; }
        public byte Alpha { get; internal set; }
        public double Energy { get; internal set; }
        public ulong OldErrorDelta { get; internal set; }
        public ulong NewErrorDelta { get; internal set; }
    }

    public sealed class CudaCatalogCandidate
    {
        public uint CandidateId { get; set; }
        public double Cx { get; set; }
        public double Cy { get; set; }
        public double Rx { get; set; }
        public double Ry { get; set; }
        public int Alpha { get; set; }
        public float AngleDegrees { get; set; }
    }

    public sealed class CudaCatalogScoreResult
    {
        public IReadOnlyList<CudaScore> Scores { get; internal set; }
    }

    public sealed class CudaChainResult
    {
        public uint ShapeKind { get; internal set; }
        public CudaCandidate Candidate { get; internal set; }
        public CudaScore Score { get; internal set; }
    }

    public sealed class CudaSelectLayerResult
    {
        public uint InitialCandidateCount { get; internal set; }
        public uint ProposalScores { get; internal set; }
        public uint AcceptedMutations { get; internal set; }
        public uint Rounds { get; internal set; }
        public bool EarlyStopTriggered { get; internal set; }
        public uint MaxStepCapHits { get; internal set; }
        public uint ProposalScorerCalls { get; internal set; }
        public uint RandomScorerCalls { get; internal set; }
        public uint SelectedShapeKind { get; internal set; }
        public double RandomGenerationMs { get; internal set; }
        public double RandomHostToDeviceMs { get; internal set; }
        public double RandomKernelMs { get; internal set; }
        public double RandomDeviceToHostMs { get; internal set; }
        public double GroupBestMs { get; internal set; }
        public double ProposalGenerationMs { get; internal set; }
        public double ProposalHostToDeviceMs { get; internal set; }
        public double ProposalKernelMs { get; internal set; }
        public double ProposalDeviceToHostMs { get; internal set; }
        public double AcceptRejectMs { get; internal set; }
        public double ServerTotalMs { get; internal set; }
        public CudaCandidate SelectedCandidate { get; internal set; }
        public CudaScore SelectedScore { get; internal set; }
        public IReadOnlyList<CudaChainResult> Chains { get; internal set; }
    }

    public static class CudaProtocol
    {
        public const int MaxImageDimension = 4096;
        public const int MaxImageBytes = 67108864;
        public const uint MaxCandidatesPerGroup = 13750;
        public const uint MaxGroupCount = 16;
        public const uint MaxAge = 100;
        public const uint MaxEarlyStopRounds = 256;
        public const uint MaxHillSteps = 5000;
        public const uint MaxMinAxis = 4096;
        public const uint MaxLayer = 10000;
        public const uint MaxDeviceChunkRounds = 256;
        public const uint MaxStructuralRounds = 256;
        public const uint MaxInitialCandidateCount = 220000;
        public const uint MaxMixedInitialCandidateCount = 880000;
        public const uint MaxMixedChainCount = 64;
        public const int RotatedResponseSize = 188;
        public const int MixedResponsePrefixSize = 196;
        public const int MixedChainSize = 68;
        public const int CatalogResponsePrefixSize = 40;

        public static byte[] CreateInitRequest(int width, int height, ulong baseTotalError, byte[] target, byte[] current)
        {
            ValidateInitImages(width, height, target, current);
            return WriteRequest(writer =>
            {
                writer.Write((uint)1);
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write(baseTotalError);
                writer.Write(target);
                writer.Write(current);
            });
        }

        public static byte[] CreateSetWeightMapRequest(byte[] weights)
        {
            if (weights == null) throw new ArgumentNullException("weights");
            if (weights.Length == 0 || weights.Length > MaxImageBytes / 2) throw new ArgumentOutOfRangeException("weights", "Weight map length is outside the production bounds.");
            return WriteRequest(writer => { writer.Write((uint)7); writer.Write(weights); });
        }

        public static byte[] CreateSetStrokeGuideRequest(byte[] saliencyQ8, byte[] tangentQ8)
        {
            if (saliencyQ8 == null) throw new ArgumentNullException("saliencyQ8");
            if (tangentQ8 == null) throw new ArgumentNullException("tangentQ8");
            if (saliencyQ8.Length == 0 || saliencyQ8.Length > MaxImageBytes / 2 || saliencyQ8.Length % 2 != 0) throw new ArgumentOutOfRangeException("saliencyQ8", "Stroke guide length is outside the production bounds.");
            if (tangentQ8.Length != saliencyQ8.Length) throw new ArgumentException("Stroke guide planes must have equal lengths.", "tangentQ8");
            return WriteRequest(writer => { writer.Write((uint)18); writer.Write(saliencyQ8); writer.Write(tangentQ8); });
        }

        public static byte[] CreateSetMultiScaleStrokeGuideRequest(byte[] detailSaliencyQ8, byte[] contourSaliencyQ8, byte[] tangentQ8)
        {
            if (detailSaliencyQ8 == null) throw new ArgumentNullException("detailSaliencyQ8");
            if (contourSaliencyQ8 == null) throw new ArgumentNullException("contourSaliencyQ8");
            if (tangentQ8 == null) throw new ArgumentNullException("tangentQ8");
            if (detailSaliencyQ8.Length == 0 || detailSaliencyQ8.Length > MaxImageBytes / 2 || detailSaliencyQ8.Length % 2 != 0) throw new ArgumentOutOfRangeException("detailSaliencyQ8", "Multi-scale stroke guide length is outside the production bounds.");
            if (contourSaliencyQ8.Length != detailSaliencyQ8.Length || tangentQ8.Length != detailSaliencyQ8.Length) throw new ArgumentException("Multi-scale stroke guide planes must have equal lengths.");
            return WriteRequest(writer => { writer.Write((uint)20); writer.Write(detailSaliencyQ8); writer.Write(contourSaliencyQ8); writer.Write(tangentQ8); });
        }

        public static byte[] CreateSetStructuralGuideRequest(byte[] distanceQ8, byte[] tangentQ8)
        {
            if (distanceQ8 == null) throw new ArgumentNullException("distanceQ8");
            if (tangentQ8 == null) throw new ArgumentNullException("tangentQ8");
            if (distanceQ8.Length == 0 || distanceQ8.Length > MaxImageBytes / 2 || distanceQ8.Length % 2 != 0) throw new ArgumentOutOfRangeException("distanceQ8", "Structural guide length is outside the production bounds.");
            if (tangentQ8.Length != distanceQ8.Length) throw new ArgumentException("Structural guide planes must have equal lengths.", "tangentQ8");
            return WriteRequest(writer => { writer.Write((uint)21); writer.Write(distanceQ8); writer.Write(tangentQ8); });
        }

        public static byte[] CreateUpdateCurrentRequest(ulong baseTotalError, byte[] current)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (current.Length == 0 || current.Length > MaxImageBytes || current.Length % 4 != 0) throw new ArgumentOutOfRangeException("current", "Current image length is outside the production bounds.");
            return WriteRequest(writer =>
            {
                writer.Write((uint)3);
                writer.Write(baseTotalError);
                writer.Write(current);
            });
        }

        public static byte[] CreateShutdownRequest()
        {
            return WriteRequest(writer => writer.Write((uint)4));
        }

        internal static byte[] CreateCatalogScoreRequest(CatalogMaskAtlasEntry atlas, IReadOnlyList<CudaCatalogCandidate> candidates, bool weighted)
        {
            if (atlas == null) throw new ArgumentNullException("atlas");
            if (candidates == null) throw new ArgumentNullException("candidates");
            if (candidates.Count == 0 || candidates.Count > MaxInitialCandidateCount) throw new ArgumentOutOfRangeException("candidates");
            return WriteRequest(writer =>
            {
                writer.Write(weighted ? 24u : 23u);
                writer.Write((uint)candidates.Count);
                writer.Write((float)atlas.IntrinsicWidth);
                writer.Write((float)atlas.IntrinsicHeight);
                writer.Write((float)atlas.MinX);
                writer.Write((float)atlas.MinY);
                writer.Write((float)atlas.MaxX);
                writer.Write((float)atlas.MaxY);
                writer.Write((uint)atlas.Size);
                writer.Write(atlas.Mask);
                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    if (candidate == null || candidate.Rx <= 0 || candidate.Ry <= 0 || candidate.Alpha < 1 || candidate.Alpha > 255) throw new ArgumentException("Catalog candidate is invalid.", "candidates");
                    writer.Write(candidate.CandidateId);
                    writer.Write(checked((int)Math.Round(candidate.Cx * 10000, MidpointRounding.AwayFromZero)));
                    writer.Write(checked((int)Math.Round(candidate.Cy * 10000, MidpointRounding.AwayFromZero)));
                    writer.Write(checked((int)Math.Round(candidate.Rx * 10000, MidpointRounding.AwayFromZero)));
                    writer.Write(checked((int)Math.Round(candidate.Ry * 10000, MidpointRounding.AwayFromZero)));
                    writer.Write(candidate.Alpha);
                    writer.Write(candidate.AngleDegrees);
                    writer.Write(0u);
                }
            });
        }

        public static byte[] CreateSelectLayerRequest(CudaSelectLayerRequest request)
        {
            Validate(request);
            return WriteRequest(writer =>
            {
                writer.Write(request.Mode == CudaSelectLayerMode.MixedResident
                    ? 15u
                    : request.Mode == CudaSelectLayerMode.RotatedResident
                        ? 14u
                        : request.Mode == CudaSelectLayerMode.MixedDeviceChunk
                            ? (request.StructuralRounds != 0 ? 22u : request.StrokeScale == 0 ? 17u : 19u)
                            : 16u);
                writer.Write(request.CandidatesPerGroup);
                writer.Write(request.GroupCount);
                writer.Write(request.Age);
                writer.Write(request.Fanout);
                writer.Write(request.EarlyStopRounds);
                writer.Write(request.MaxHillSteps);
                writer.Write(request.MinAxis);
                writer.Write(request.Layer);
                writer.Write(request.Weighted ? 1u : 0u);
                writer.Write(request.MutateAlpha ? 1u : 0u);
                writer.Write(request.MinAlpha);
                writer.Write(request.MaxAlpha);
                writer.Write(request.InitialAlpha);
                writer.Write(request.Seed);
                if (request.IsMixed)
                {
                    writer.Write(request.ShapeMask);
                    writer.Write(request.SelectionMode);
                    writer.Write(request.GuideMode);
                    if (request.StrokeScale != 0)
                    {
                        writer.Write(request.StrokeScale);
                        writer.Write(request.MinLongAxis);
                        writer.Write(request.MaxLongAxis);
                    }
                }
                if (request.IsDeviceChunk) writer.Write(request.DeviceChunkRounds);
                if (request.StructuralRounds != 0)
                {
                    writer.Write(request.StructuralEdgeWeightQ16);
                    writer.Write(request.StructuralDistanceLimit);
                    writer.Write(request.StructuralRounds);
                    writer.Write(request.MaxPixelGainRegressionQ16);
                }
            });
        }

        public static double ParseTimingResponse(string command, byte[] response)
        {
            RequireLength(response, 12, "response");
            using (var reader = Reader(response))
            {
                RequireSuccess(command, reader.ReadUInt32());
                return reader.ReadDouble();
            }
        }

        public static void ParseShutdownResponse(byte[] response)
        {
            RequireLength(response, 4, "response");
            using (var reader = Reader(response)) RequireSuccess("SHUTDOWN", reader.ReadUInt32());
        }

        public static CudaSelectLayerResult ParseRotatedResponse(byte[] response)
        {
            RequireLength(response, RotatedResponseSize, "response");
            using (var reader = Reader(response))
            {
                RequireSuccess("RESIDENT_SELECT_LAYER_ROTATED_DEVICE_CHUNK", reader.ReadUInt32());
                var result = ReadCommonPrefix(reader);
                result.RandomGenerationMs = reader.ReadDouble();
                ReadTimings(reader, result);
                result.SelectedCandidate = ReadCandidate(reader);
                result.SelectedScore = ReadScore(reader);
                result.Chains = new ReadOnlyCollection<CudaChainResult>(new List<CudaChainResult>());
                return result;
            }
        }

        internal static CudaCatalogScoreResult ParseCatalogScoreResponse(byte[] prefix, byte[] tail, int expectedCount)
        {
            RequireLength(prefix, CatalogResponsePrefixSize, "prefix");
            if (tail == null) throw new ArgumentNullException("tail");
            if (expectedCount < 0 || tail.Length != checked(expectedCount * 32)) throw new InvalidDataException("Catalog score response length does not match the request.");
            using (var reader = Reader(prefix))
            {
                RequireSuccess("SCORE_BATCH_CATALOG_GEOMETRY", reader.ReadUInt32());
                var count = reader.ReadUInt32();
                if (count != expectedCount) throw new InvalidDataException("Catalog score response count does not match the request.");
                reader.BaseStream.Position += 32;
            }
            var scores = new List<CudaScore>(expectedCount);
            using (var reader = Reader(tail)) for (var index = 0; index < expectedCount; index++) scores.Add(ReadScore(reader));
            return new CudaCatalogScoreResult { Scores = new ReadOnlyCollection<CudaScore>(scores) };
        }

        public static CudaSelectLayerResult ParseMixedResponse(byte[] prefix, byte[] tail)
        {
            RequireLength(prefix, MixedResponsePrefixSize, "prefix");
            if (tail == null) throw new ArgumentNullException("tail");
            using (var reader = Reader(prefix))
            {
                RequireSuccess("RESIDENT_SELECT_LAYER_MIXED_DEVICE_CHUNK", reader.ReadUInt32());
                var result = ReadCommonPrefix(reader);
                result.SelectedShapeKind = reader.ReadUInt32();
                result.RandomGenerationMs = reader.ReadDouble();
                ReadTimings(reader, result);
                result.SelectedCandidate = ReadCandidate(reader);
                result.SelectedScore = ReadScore(reader);
                var chainCount = reader.ReadUInt32();
                if (chainCount > MaxMixedChainCount) throw new InvalidDataException("Mixed response chain count exceeds the production bound.");
                if (tail.Length != checked((int)chainCount * MixedChainSize)) throw new InvalidDataException("Mixed response tail length does not match chain count.");
                var chains = new List<CudaChainResult>((int)chainCount);
                using (var tailReader = Reader(tail))
                {
                    for (var index = 0; index < chainCount; index++)
                    {
                        chains.Add(new CudaChainResult
                        {
                            ShapeKind = tailReader.ReadUInt32(),
                            Candidate = ReadCandidate(tailReader),
                            Score = ReadScore(tailReader)
                        });
                    }
                }
                result.Chains = new ReadOnlyCollection<CudaChainResult>(chains);
                return result;
            }
        }

        internal static uint ReadMixedChainCount(byte[] prefix)
        {
            RequireLength(prefix, MixedResponsePrefixSize, "prefix");
            using (var reader = Reader(prefix))
            {
                RequireSuccess("RESIDENT_SELECT_LAYER_MIXED_DEVICE_CHUNK", reader.ReadUInt32());
                reader.BaseStream.Position = 192;
                var chainCount = reader.ReadUInt32();
                if (chainCount > MaxMixedChainCount) throw new InvalidDataException("Mixed response chain count exceeds the production bound.");
                return chainCount;
            }
        }

        internal static uint ExpectedMixedChainCount(CudaSelectLayerRequest request)
        {
            Validate(request);
            return checked(request.GroupCount * CountShapeBits(request.ShapeMask));
        }

        internal static int ValidateInitImages(int width, int height, byte[] target, byte[] current)
        {
            if (width <= 0 || height <= 0 || width > MaxImageDimension || height > MaxImageDimension) throw new ArgumentOutOfRangeException("width", "Image dimensions are outside the production bounds.");
            var imageBytes = checked(checked(width * height) * 4);
            if (imageBytes > MaxImageBytes) throw new ArgumentOutOfRangeException("width", "Image byte count exceeds the production bound.");
            RequireLength(target, imageBytes, "target");
            RequireLength(current, imageBytes, "current");
            return imageBytes;
        }

        private static CudaSelectLayerResult ReadCommonPrefix(BinaryReader reader)
        {
            return new CudaSelectLayerResult
            {
                InitialCandidateCount = reader.ReadUInt32(),
                ProposalScores = reader.ReadUInt32(),
                AcceptedMutations = reader.ReadUInt32(),
                Rounds = reader.ReadUInt32(),
                EarlyStopTriggered = ReadBoolean(reader, "earlyStopTriggered"),
                MaxStepCapHits = reader.ReadUInt32(),
                ProposalScorerCalls = reader.ReadUInt32(),
                RandomScorerCalls = reader.ReadUInt32()
            };
        }

        private static void ReadTimings(BinaryReader reader, CudaSelectLayerResult result)
        {
            result.RandomHostToDeviceMs = reader.ReadDouble();
            result.RandomKernelMs = reader.ReadDouble();
            result.RandomDeviceToHostMs = reader.ReadDouble();
            result.GroupBestMs = reader.ReadDouble();
            result.ProposalGenerationMs = reader.ReadDouble();
            result.ProposalHostToDeviceMs = reader.ReadDouble();
            result.ProposalKernelMs = reader.ReadDouble();
            result.ProposalDeviceToHostMs = reader.ReadDouble();
            result.AcceptRejectMs = reader.ReadDouble();
            result.ServerTotalMs = reader.ReadDouble();
        }

        private static CudaCandidate ReadCandidate(BinaryReader reader)
        {
            return new CudaCandidate
            {
                CandidateId = reader.ReadUInt32(),
                Cx = reader.ReadInt32(),
                Cy = reader.ReadInt32(),
                Rx = reader.ReadInt32(),
                Ry = reader.ReadInt32(),
                Alpha = reader.ReadInt32(),
                AngleDegrees = reader.ReadSingle(),
                GroupId = reader.ReadUInt32()
            };
        }

        private static CudaScore ReadScore(BinaryReader reader)
        {
            return new CudaScore
            {
                CandidateId = reader.ReadUInt32(),
                Red = reader.ReadByte(),
                Green = reader.ReadByte(),
                Blue = reader.ReadByte(),
                Alpha = reader.ReadByte(),
                Energy = reader.ReadDouble(),
                OldErrorDelta = reader.ReadUInt64(),
                NewErrorDelta = reader.ReadUInt64()
            };
        }

        private static bool ReadBoolean(BinaryReader reader, string field)
        {
            var value = reader.ReadUInt32();
            if (value > 1) throw new InvalidDataException(field + " must be 0 or 1.");
            return value != 0;
        }

        private static void Validate(CudaSelectLayerRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Mode != CudaSelectLayerMode.RotatedDeviceChunk && request.Mode != CudaSelectLayerMode.MixedDeviceChunk && request.Mode != CudaSelectLayerMode.RotatedResident && request.Mode != CudaSelectLayerMode.MixedResident) throw new ArgumentOutOfRangeException("request", "CUDA command mode must be explicit.");
            if (request.CandidatesPerGroup == 0 || request.CandidatesPerGroup > MaxCandidatesPerGroup) throw new ArgumentOutOfRangeException("request", "Candidates per group are outside the production bounds.");
            if (request.GroupCount == 0 || request.GroupCount > MaxGroupCount) throw new ArgumentOutOfRangeException("request", "Group count is outside the production bounds.");
            if (request.Age == 0 || request.Age > MaxAge) throw new ArgumentOutOfRangeException("request", "Age is outside the production bounds.");
            if (request.Fanout < 1 || request.Fanout > 8) throw new ArgumentOutOfRangeException("request", "Fanout must be between 1 and 8.");
            if (request.EarlyStopRounds == 0 || request.EarlyStopRounds > MaxEarlyStopRounds) throw new ArgumentOutOfRangeException("request", "Early-stop rounds are outside the production bounds.");
            if (request.MaxHillSteps == 0 || request.MaxHillSteps > MaxHillSteps) throw new ArgumentOutOfRangeException("request", "Hill steps are outside the production bounds.");
            if (request.MinAxis == 0 || request.MinAxis > MaxMinAxis) throw new ArgumentOutOfRangeException("request", "Minimum axis is outside the production bounds.");
            if (request.Layer == 0 || request.Layer > MaxLayer) throw new ArgumentOutOfRangeException("request", "Layer is outside the production bounds.");
            if (request.IsDeviceChunk)
            {
                if (request.DeviceChunkRounds == 0 || request.DeviceChunkRounds > MaxDeviceChunkRounds || request.DeviceChunkRounds > request.EarlyStopRounds) throw new ArgumentOutOfRangeException("request", "Device chunk rounds are outside the production bounds.");
            }
            else if (request.DeviceChunkRounds != 0)
            {
                throw new ArgumentException("Resident compatibility mode does not accept device chunk rounds.", "request");
            }
            if (request.MinAlpha > request.MaxAlpha || request.MaxAlpha > 255 || request.InitialAlpha > 255) throw new ArgumentOutOfRangeException("request", "Alpha values must be valid bytes.");
            if (!request.IsMixed)
            {
                if (request.ShapeMask != 0 || request.SelectionMode != 0 || request.GuideMode != 0 || request.StrokeScale != 0 || request.MinLongAxis != 0 || request.MaxLongAxis != 0 || request.StructuralEdgeWeightQ16 != 0 || request.StructuralDistanceLimit != 0 || request.StructuralRounds != 0 || request.MaxPixelGainRegressionQ16 != 0) throw new ArgumentException("Mixed fields are not valid in rotated mode.", "request");
            }
            else
            {
                if (request.ShapeMask == 0 || (request.ShapeMask & ~15u) != 0) throw new ArgumentException("Mixed mode requires a valid shape mask.", "request");
                if (request.SelectionMode > 1 || request.GuideMode > 2) throw new ArgumentOutOfRangeException("request", "Mixed selection fields are invalid.");
                if (request.StrokeScale == 0)
                {
                    if (request.MinLongAxis != 0 || request.MaxLongAxis != 0) throw new ArgumentException("Stroke length bounds require a stroke scale.", "request");
                }
                else
                {
                    if (request.StrokeScale > 2) throw new ArgumentOutOfRangeException("request", "Stroke scale must be detail or contour.");
                    if (request.ShapeMask != 8 || request.GuideMode == 0) throw new ArgumentException("Multi-scale stroke selection requires guided line rectangles.", "request");
                    if (request.MinLongAxis < request.MinAxis || request.MaxLongAxis < request.MinLongAxis || request.MaxLongAxis > 512) throw new ArgumentOutOfRangeException("request", "Stroke length bounds are invalid.");
                }

                if (request.StructuralRounds == 0)
                {
                    if (request.StructuralEdgeWeightQ16 != 0 || request.StructuralDistanceLimit != 0 || request.MaxPixelGainRegressionQ16 != 0) throw new ArgumentException("Structural fields require structural rounds.", "request");
                }
                else
                {
                    if (request.StrokeScale != 1 || request.ShapeMask != 8 || request.GuideMode != 2) throw new ArgumentException("Structural refinement requires fixed-width guided detail line rectangles.", "request");
                    if (request.StructuralEdgeWeightQ16 == 0 || request.StructuralEdgeWeightQ16 > 65536 || request.StructuralDistanceLimit == 0 || request.StructuralDistanceLimit > 32 || request.StructuralRounds > MaxStructuralRounds || request.MaxPixelGainRegressionQ16 > 65536) throw new ArgumentOutOfRangeException("request", "Structural refinement fields are invalid.");
                }
            }

            var shapeCount = request.IsMixed ? CountShapeBits(request.ShapeMask) : 1u;
            var initialCandidates = checked(request.CandidatesPerGroup * request.GroupCount);
            if (initialCandidates > MaxInitialCandidateCount) throw new ArgumentOutOfRangeException("request", "Initial candidate count exceeds the production bound.");
            if (checked(initialCandidates * shapeCount) > MaxMixedInitialCandidateCount) throw new ArgumentOutOfRangeException("request", "Total initial candidate count exceeds the production bound.");
            var chainCount = checked(request.GroupCount * shapeCount);
            if (chainCount > MaxMixedChainCount || checked(chainCount * request.Fanout) > MaxMixedChainCount * 8) throw new ArgumentOutOfRangeException("request", "Resident chain workload exceeds the production bound.");
        }

        private static uint CountShapeBits(uint mask)
        {
            uint count = 0;
            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }
            return count;
        }

        private static void RequireSuccess(string command, uint status)
        {
            if (status != 0) throw new InvalidDataException(command + " returned status " + status + ".");
        }

        private static void RequireLength(byte[] bytes, int expected, string name)
        {
            if (bytes == null) throw new ArgumentNullException(name);
            if (bytes.Length != expected) throw new ArgumentException(name + " must contain exactly " + expected + " bytes.", name);
        }

        private static byte[] WriteRequest(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static BinaryReader Reader(byte[] bytes)
        {
            return new BinaryReader(new MemoryStream(bytes, false));
        }
    }
}
