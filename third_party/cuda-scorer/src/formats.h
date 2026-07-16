#pragma once

#include <cstdint>

namespace cuda_scorer {

#pragma pack(push, 1)

struct CandidateRecord {
    std::uint32_t candidateId;
    std::int32_t cx;
    std::int32_t cy;
    std::int32_t rx;
    std::int32_t ry;
    std::int32_t alpha;
    std::uint32_t scanlineOffset;
    std::uint32_t scanlineCount;
};

struct RotatedCandidateRecord {
    std::uint32_t candidateId;
    std::int32_t cx;
    std::int32_t cy;
    std::int32_t rx;
    std::int32_t ry;
    std::int32_t alpha;
    float angleDegrees;
    std::uint32_t reserved;
};

struct ScanlineRecord {
    std::int32_t y;
    std::int32_t x1;
    std::int32_t x2;
    std::uint32_t alpha;
};

struct ScoreRecord {
    std::uint32_t candidateId;
    std::uint8_t r;
    std::uint8_t g;
    std::uint8_t b;
    std::uint8_t a;
    double energy;
    std::uint64_t oldErrorDelta;
    std::uint64_t newErrorDelta;
};

#pragma pack(pop)

static_assert(sizeof(CandidateRecord) == 32, "CandidateRecord binary size changed");
static_assert(sizeof(RotatedCandidateRecord) == 32, "RotatedCandidateRecord binary size changed");
static_assert(sizeof(ScanlineRecord) == 16, "ScanlineRecord binary size changed");
static_assert(sizeof(ScoreRecord) == 32, "ScoreRecord binary size changed");

}  // namespace cuda_scorer
