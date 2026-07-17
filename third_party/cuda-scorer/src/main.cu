#include <cuda_runtime.h>
#include <cooperative_groups.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

#include "formats.h"

using cuda_scorer::CandidateRecord;
using cuda_scorer::RotatedCandidateRecord;
using cuda_scorer::ScoreRecord;
namespace cg = cooperative_groups;

struct Metadata {
    int width = 0;
    int height = 0;
    std::uint32_t candidateCount = 0;
    std::uint64_t baseTotalError = 0;
};

enum ServerCommand : std::uint32_t {
    CMD_INIT = 1,
    CMD_UPDATE_CURRENT = 3,
    CMD_SHUTDOWN = 4,
    CMD_SCORE_BATCH_GEOMETRY = 5,
    CMD_SCORE_BATCH_ROTATED_GEOMETRY = 6,
    CMD_SET_WEIGHT_MAP = 7,
    CMD_SCORE_BATCH_ROTATED_GEOMETRY_WEIGHTED = 8,
    CMD_SCORE_BATCH_ROTATED_RECT_GEOMETRY = 9,
    CMD_SCORE_BATCH_ROTATED_RECT_GEOMETRY_WEIGHTED = 10,
    CMD_SCORE_BATCH_ROTATED_TRIANGLE_GEOMETRY = 11,
    CMD_SCORE_BATCH_ROTATED_TRIANGLE_GEOMETRY_WEIGHTED = 12,
    CMD_RESIDENT_HILL_CLIMB_ROTATED = 13,
    CMD_RESIDENT_SELECT_LAYER_ROTATED = 14,
    CMD_RESIDENT_SELECT_LAYER_MIXED = 15,
    CMD_RESIDENT_SELECT_LAYER_ROTATED_DEVICE_CHUNK = 16,
    CMD_RESIDENT_SELECT_LAYER_MIXED_DEVICE_CHUNK = 17,
    CMD_SET_STROKE_GUIDE = 18,
    CMD_RESIDENT_SELECT_LAYER_GUIDED_DEVICE_CHUNK = 19,
    CMD_SET_MULTI_SCALE_STROKE_GUIDE = 20,
    CMD_SET_STRUCTURAL_GUIDE = 21,
    CMD_RESIDENT_SELECT_LAYER_STRUCTURAL_DEVICE_CHUNK = 22,
    CMD_SCORE_BATCH_CATALOG_GEOMETRY = 23,
    CMD_SCORE_BATCH_CATALOG_GEOMETRY_WEIGHTED = 24,
};

constexpr int GEOMETRY_ELLIPSE = 0;
constexpr int GEOMETRY_RECTANGLE = 1;
constexpr int GEOMETRY_TRIANGLE = 2;
constexpr int GEOMETRY_LINE_RECTANGLE = 3;
constexpr int GEOMETRY_RUNTIME = -1;
constexpr int GEOMETRY_KIND_COUNT = 4;
constexpr int RESIDENT_DEVICE_FANOUT_MAX = 8;
constexpr double CATALOG_GEOMETRY_SCALE = 10000.0;

struct CatalogMaskMetadata {
    float intrinsicWidth;
    float intrinsicHeight;
    float minX;
    float minY;
    float maxX;
    float maxY;
    std::uint32_t size;
};

static bool check(cudaError_t err, const char* step) {
    if (err == cudaSuccess) {
        return true;
    }
    std::fprintf(stderr, "%s failed: %s\n", step, cudaGetErrorString(err));
    return false;
}

static void require_cuda(cudaError_t err, const char* step) {
    if (err != cudaSuccess) {
        throw std::runtime_error(std::string(step) + " failed: " + cudaGetErrorString(err));
    }
}

static std::string arg_value(int argc, char** argv, const char* key) {
    for (int i = 1; i + 1 < argc; i++) {
        if (std::strcmp(argv[i], key) == 0) {
            return argv[i + 1];
        }
    }
    return "";
}

static double elapsed_ms(
    const std::chrono::steady_clock::time_point& start,
    const std::chrono::steady_clock::time_point& end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

__global__ void add_one_kernel(int* value) {
    *value += 1;
}

__device__ int clamp_channel(int value) {
    return value < 0 ? 0 : (value > 255 ? 255 : value);
}

__global__ void score_batch_geometry_kernel(
    const std::uint8_t* target,
    const std::uint8_t* current,
    const CandidateRecord* candidates,
    ScoreRecord* output,
    int width,
    int height,
    std::uint64_t baseTotalError) {
    const int tid = threadIdx.x;
    const CandidateRecord candidate = candidates[blockIdx.x];

    __shared__ long long rsum[256];
    __shared__ long long gsum[256];
    __shared__ long long bsum[256];
    __shared__ long long count[256];
    __shared__ unsigned long long old_error[256];
    __shared__ unsigned long long new_error[256];
    __shared__ int color_r;
    __shared__ int color_g;
    __shared__ int color_b;

    long long local_r = 0;
    long long local_g = 0;
    long long local_b = 0;
    long long local_count = 0;
    const int color_scale = 0x101 * 255 / candidate.alpha;
    const double aspect = static_cast<double>(candidate.rx) / static_cast<double>(candidate.ry);

    for (int dy = 0; dy < candidate.ry; dy++) {
        const int y1 = candidate.cy - dy;
        const int y2 = candidate.cy + dy;
        if ((y1 < 0 || y1 >= height) && (y2 < 0 || y2 >= height)) {
            continue;
        }
        const double radius2 = static_cast<double>(candidate.ry) * static_cast<double>(candidate.ry) - static_cast<double>(dy) * static_cast<double>(dy);
        const int s = static_cast<int>(sqrt(radius2) * aspect);
        int x1 = candidate.cx - s;
        int x2 = candidate.cx + s;
        if (x1 < 0) x1 = 0;
        if (x2 >= width) x2 = width - 1;

        for (int pass = 0; pass < 2; pass++) {
            if (pass == 1 && dy == 0) {
                continue;
            }
            const int y = pass == 0 ? y1 : y2;
            if (y < 0 || y >= height) {
                continue;
            }
            for (int x = x1 + tid; x <= x2; x += blockDim.x) {
                const int offset = (y * width + x) * 4;
                const int tr = target[offset + 0];
                const int tg = target[offset + 1];
                const int tb = target[offset + 2];
                const int cr = current[offset + 0];
                const int cg = current[offset + 1];
                const int cb = current[offset + 2];
                local_r += static_cast<long long>((tr - cr) * color_scale + cr * 0x101);
                local_g += static_cast<long long>((tg - cg) * color_scale + cg * 0x101);
                local_b += static_cast<long long>((tb - cb) * color_scale + cb * 0x101);
                local_count++;
            }
        }
    }

    rsum[tid] = local_r;
    gsum[tid] = local_g;
    bsum[tid] = local_b;
    count[tid] = local_count;
    __syncthreads();

    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            rsum[tid] += rsum[tid + stride];
            gsum[tid] += gsum[tid + stride];
            bsum[tid] += bsum[tid + stride];
            count[tid] += count[tid + stride];
        }
        __syncthreads();
    }

    if (tid == 0) {
        if (count[0] == 0) {
            color_r = 0;
            color_g = 0;
            color_b = 0;
        } else {
            color_r = clamp_channel(static_cast<int>(rsum[0] / count[0]) >> 8);
            color_g = clamp_channel(static_cast<int>(gsum[0] / count[0]) >> 8);
            color_b = clamp_channel(static_cast<int>(bsum[0] / count[0]) >> 8);
        }
    }
    __syncthreads();

    const unsigned int m = 0xffff;
    const unsigned int ma = 0xffff;
    const unsigned int alpha16 = static_cast<unsigned int>(candidate.alpha) * 0x101;
    const unsigned int blend = (m - alpha16 * ma / m) * 0x101;
    const unsigned int sr = static_cast<unsigned int>((static_cast<unsigned long long>(color_r) * 0x101 * candidate.alpha) / 255);
    const unsigned int sg = static_cast<unsigned int>((static_cast<unsigned long long>(color_g) * 0x101 * candidate.alpha) / 255);
    const unsigned int sb = static_cast<unsigned int>((static_cast<unsigned long long>(color_b) * 0x101 * candidate.alpha) / 255);

    unsigned long long local_old = 0;
    unsigned long long local_new = 0;
    for (int dy = 0; dy < candidate.ry; dy++) {
        const int y1 = candidate.cy - dy;
        const int y2 = candidate.cy + dy;
        if ((y1 < 0 || y1 >= height) && (y2 < 0 || y2 >= height)) {
            continue;
        }
        const double radius2 = static_cast<double>(candidate.ry) * static_cast<double>(candidate.ry) - static_cast<double>(dy) * static_cast<double>(dy);
        const int s = static_cast<int>(sqrt(radius2) * aspect);
        int x1 = candidate.cx - s;
        int x2 = candidate.cx + s;
        if (x1 < 0) x1 = 0;
        if (x2 >= width) x2 = width - 1;

        for (int pass = 0; pass < 2; pass++) {
            if (pass == 1 && dy == 0) {
                continue;
            }
            const int y = pass == 0 ? y1 : y2;
            if (y < 0 || y >= height) {
                continue;
            }
            for (int x = x1 + tid; x <= x2; x += blockDim.x) {
                const int offset = (y * width + x) * 4;
                const int tr = target[offset + 0];
                const int tg = target[offset + 1];
                const int tb = target[offset + 2];
                const int ta = target[offset + 3];
                const int br = current[offset + 0];
                const int bg = current[offset + 1];
                const int bb = current[offset + 2];
                const int ba = current[offset + 3];
                const int ar = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(br) * blend + static_cast<unsigned long long>(sr) * ma) / m) >> 8));
                const int ag = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bg) * blend + static_cast<unsigned long long>(sg) * ma) / m) >> 8));
                const int ab = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bb) * blend + static_cast<unsigned long long>(sb) * ma) / m) >> 8));
                const int aa = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(ba) * blend + static_cast<unsigned long long>(alpha16) * ma) / m) >> 8));
                const int dr1 = tr - br;
                const int dg1 = tg - bg;
                const int db1 = tb - bb;
                const int da1 = ta - ba;
                const int dr2 = tr - ar;
                const int dg2 = tg - ag;
                const int db2 = tb - ab;
                const int da2 = ta - aa;
                local_old += static_cast<unsigned long long>(dr1 * dr1 + dg1 * dg1 + db1 * db1 + da1 * da1);
                local_new += static_cast<unsigned long long>(dr2 * dr2 + dg2 * dg2 + db2 * db2 + da2 * da2);
            }
        }
    }

    old_error[tid] = local_old;
    new_error[tid] = local_new;
    __syncthreads();

    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            old_error[tid] += old_error[tid + stride];
            new_error[tid] += new_error[tid + stride];
        }
        __syncthreads();
    }

    if (tid == 0) {
        ScoreRecord record{};
        record.candidateId = candidate.candidateId;
        record.r = static_cast<std::uint8_t>(color_r);
        record.g = static_cast<std::uint8_t>(color_g);
        record.b = static_cast<std::uint8_t>(color_b);
        record.a = static_cast<std::uint8_t>(candidate.alpha);
        record.oldErrorDelta = old_error[0];
        record.newErrorDelta = new_error[0];
        const double total = static_cast<double>(baseTotalError - old_error[0] + new_error[0]);
        record.energy = sqrt(total / static_cast<double>(width * height * 4)) / 255.0;
        output[blockIdx.x] = record;
    }
}

constexpr int ROTATED_COORDINATE_SCALE = 4096;

__device__ int rotated_coordinate_scale(const RotatedCandidateRecord& candidate) {
    const int max_axis = candidate.rx > candidate.ry ? candidate.rx : candidate.ry;
    return max_axis <= 512 ? ROTATED_COORDINATE_SCALE : max_axis <= 1024 ? 1024 : 256;
}

__device__ long long resident_abs_i64(long long value) {
    return value < 0 ? -value : value;
}

__device__ void rotated_fixed_coordinates(
    const RotatedCandidateRecord& candidate,
    int x,
    int y,
    int cos_q,
    int sin_q,
    long long& local_x,
    long long& local_y) {
    const int dx2 = x * 2 + 1 - candidate.cx * 2;
    const int dy2 = y * 2 + 1 - candidate.cy * 2;
    local_x = static_cast<long long>(cos_q) * dx2 + static_cast<long long>(sin_q) * dy2;
    local_y = -static_cast<long long>(sin_q) * dx2 + static_cast<long long>(cos_q) * dy2;
}

__device__ bool rotated_ellipse_contains(const RotatedCandidateRecord& candidate, int x, int y, int cos_q, int sin_q) {
    long long local_x = 0;
    long long local_y = 0;
    rotated_fixed_coordinates(candidate, x, y, cos_q, sin_q, local_x, local_y);
    const long long rx = candidate.rx;
    const long long ry = candidate.ry;
    const long long rx2 = rx * rx;
    const long long ry2 = ry * ry;
    const long long left = local_x * local_x * ry2 + local_y * local_y * rx2;
    const long long scale = rotated_coordinate_scale(candidate);
    const long long scale2 = scale * scale;
    return left <= 4LL * rx2 * ry2 * scale2;
}

__device__ bool rotated_rect_contains(const RotatedCandidateRecord& candidate, int x, int y, int cos_q, int sin_q) {
    long long local_x = 0;
    long long local_y = 0;
    rotated_fixed_coordinates(candidate, x, y, cos_q, sin_q, local_x, local_y);
    const long long scale = rotated_coordinate_scale(candidate);
    return resident_abs_i64(local_x) <= static_cast<long long>(candidate.rx) * scale * 2 + 2 &&
        resident_abs_i64(local_y) <= static_cast<long long>(candidate.ry) * scale * 2 + 2;
}

__device__ bool rotated_triangle_contains(const RotatedCandidateRecord& candidate, int x, int y, int cos_q, int sin_q) {
    long long local_x = 0;
    long long local_y = 0;
    rotated_fixed_coordinates(candidate, x, y, cos_q, sin_q, local_x, local_y);
    const long long ry_q = static_cast<long long>(candidate.ry) * rotated_coordinate_scale(candidate) * 2;
    if (local_y < -ry_q - 2 || local_y > ry_q + 2) {
        return false;
    }
    return resident_abs_i64(local_x) * (2LL * candidate.ry) <=
        static_cast<long long>(candidate.rx) * (local_y + ry_q) + 2;
}

__device__ bool rotated_geometry_contains(const RotatedCandidateRecord& candidate, int x, int y, int cos_q, int sin_q, int shape_kind) {
    if (shape_kind == GEOMETRY_RECTANGLE || shape_kind == GEOMETRY_LINE_RECTANGLE) {
        return rotated_rect_contains(candidate, x, y, cos_q, sin_q);
    }
    if (shape_kind == GEOMETRY_TRIANGLE) {
        return rotated_triangle_contains(candidate, x, y, cos_q, sin_q);
    }
    return rotated_ellipse_contains(candidate, x, y, cos_q, sin_q);
}

template <int StaticShapeKind>
__device__ int candidate_shape_kind_for(const std::uint32_t* shape_kinds, int candidate_index, int shape_kind) {
    if constexpr (StaticShapeKind >= 0) {
        return StaticShapeKind;
    }
    return shape_kinds ? static_cast<int>(shape_kinds[candidate_index]) : shape_kind;
}

struct RotatedGeometryBlock {
    RotatedCandidateRecord candidate{};
    int shapeKind = GEOMETRY_ELLIPSE;
    float cosTheta = 1.0f;
    float sinTheta = 0.0f;
    int cosQ = ROTATED_COORDINATE_SCALE;
    int sinQ = 0;
    int minX = 0;
    int minY = 0;
    int boxWidth = 0;
    int boxHeight = 0;
    int pixelCount = 0;
};

template <int StaticShapeKind>
__device__ void initialize_rotated_geometry_block(
    const RotatedCandidateRecord* candidates,
    const std::uint32_t* shape_kinds,
    int candidate_index,
    int shape_kind,
    int width,
    int height,
    RotatedGeometryBlock& geometry) {
    if (threadIdx.x == 0) {
        geometry.candidate = candidates[candidate_index];
        geometry.shapeKind = candidate_shape_kind_for<StaticShapeKind>(shape_kinds, candidate_index, shape_kind);
        const float theta = geometry.candidate.angleDegrees * 0.01745329251994329577f;
        __sincosf(theta, &geometry.sinTheta, &geometry.cosTheta);
        const int coordinate_scale = rotated_coordinate_scale(geometry.candidate);
        geometry.cosQ = __float2int_rn(geometry.cosTheta * coordinate_scale);
        geometry.sinQ = __float2int_rn(geometry.sinTheta * coordinate_scale);
        const int extent_x = static_cast<int>(ceilf(
            fabsf(static_cast<float>(geometry.candidate.rx) * geometry.cosTheta) +
            fabsf(static_cast<float>(geometry.candidate.ry) * geometry.sinTheta))) + 1;
        const int extent_y = static_cast<int>(ceilf(
            fabsf(static_cast<float>(geometry.candidate.rx) * geometry.sinTheta) +
            fabsf(static_cast<float>(geometry.candidate.ry) * geometry.cosTheta))) + 1;
        geometry.minX = max(0, geometry.candidate.cx - extent_x);
        geometry.minY = max(0, geometry.candidate.cy - extent_y);
        const int max_x = min(width - 1, geometry.candidate.cx + extent_x);
        const int max_y = min(height - 1, geometry.candidate.cy + extent_y);
        geometry.boxWidth = max_x >= geometry.minX ? max_x - geometry.minX + 1 : 0;
        geometry.boxHeight = max_y >= geometry.minY ? max_y - geometry.minY + 1 : 0;
        geometry.pixelCount = geometry.boxWidth * geometry.boxHeight;
    }
    __syncthreads();
}

template <int StaticShapeKind>
__device__ bool rotated_geometry_contains_for(const RotatedCandidateRecord& candidate, int x, int y, int cos_q, int sin_q, int runtime_shape_kind) {
    if constexpr (StaticShapeKind == GEOMETRY_RECTANGLE || StaticShapeKind == GEOMETRY_LINE_RECTANGLE) {
        return rotated_rect_contains(candidate, x, y, cos_q, sin_q);
    } else if constexpr (StaticShapeKind == GEOMETRY_TRIANGLE) {
        return rotated_triangle_contains(candidate, x, y, cos_q, sin_q);
    } else if constexpr (StaticShapeKind == GEOMETRY_ELLIPSE) {
        return rotated_ellipse_contains(candidate, x, y, cos_q, sin_q);
    } else {
        return rotated_geometry_contains(candidate, x, y, cos_q, sin_q, runtime_shape_kind);
    }
}

constexpr int MAX_ROTATED_SCANLINES = 512;

template <int StaticShapeKind>
__device__ bool initialize_rotated_scanlines(
    const RotatedGeometryBlock& geometry,
    std::uint32_t* row_bounds) {
    const bool enabled = geometry.boxHeight <= MAX_ROTATED_SCANLINES;
    if (enabled) {
        for (int row = threadIdx.x; row < geometry.boxHeight; row += blockDim.x) {
            const int y = geometry.minY + row;
            int left = 0;
            while (left < geometry.boxWidth && !rotated_geometry_contains_for<StaticShapeKind>(
                geometry.candidate,
                geometry.minX + left,
                y,
                geometry.cosQ,
                geometry.sinQ,
                geometry.shapeKind)) {
                left++;
            }
            int right = geometry.boxWidth - 1;
            while (right >= left && !rotated_geometry_contains_for<StaticShapeKind>(
                geometry.candidate,
                geometry.minX + right,
                y,
                geometry.cosQ,
                geometry.sinQ,
                geometry.shapeKind)) {
                right--;
            }
            const std::uint32_t min_x = static_cast<std::uint32_t>(left);
            const std::uint32_t max_x_exclusive = right >= left ? static_cast<std::uint32_t>(right + 1) : 0u;
            row_bounds[row] = min_x | (max_x_exclusive << 16);
        }
    }
    __syncthreads();
    return enabled;
}

template <int StaticShapeKind>
__global__ void score_batch_rotated_geometry_kernel(
    const std::uint8_t* target,
    const std::uint8_t* current,
    const RotatedCandidateRecord* candidates,
    ScoreRecord* output,
    int width,
    int height,
    std::uint64_t baseTotalError,
    int shape_kind,
    const std::uint32_t* shape_kinds) {
    const int tid = threadIdx.x;

    __shared__ RotatedGeometryBlock geometry;
    __shared__ long long rsum[256];
    __shared__ long long gsum[256];
    __shared__ long long bsum[256];
    __shared__ long long count[256];
    __shared__ unsigned long long old_error[256];
    __shared__ unsigned long long new_error[256];
    __shared__ std::uint32_t row_bounds[MAX_ROTATED_SCANLINES];
    __shared__ int color_r;
    __shared__ int color_g;
    __shared__ int color_b;

    initialize_rotated_geometry_block<StaticShapeKind>(candidates, shape_kinds, blockIdx.x, shape_kind, width, height, geometry);
    const RotatedCandidateRecord candidate = geometry.candidate;
    const int candidate_shape_kind = geometry.shapeKind;
    const int cos_q = geometry.cosQ;
    const int sin_q = geometry.sinQ;
    const int min_x = geometry.minX;
    const int min_y = geometry.minY;
    const int box_width = geometry.boxWidth;
    const int pixel_count = geometry.pixelCount;
    const bool use_scanlines = initialize_rotated_scanlines<StaticShapeKind>(geometry, row_bounds);

    long long local_r = 0;
    long long local_g = 0;
    long long local_b = 0;
    long long local_count = 0;
    unsigned long long local_old = 0;
    const int color_scale = 0x101 * 255 / candidate.alpha;
    for (int index = tid; index < pixel_count; index += blockDim.x) {
        const int row = index / box_width;
        const int column = index - row * box_width;
        const int x = min_x + column;
        const int y = min_y + row;
        const std::uint32_t bounds = use_scanlines ? row_bounds[row] : 0u;
        const bool outside = use_scanlines
            ? column < static_cast<int>(bounds & 0xffffu) || column >= static_cast<int>(bounds >> 16)
            : !rotated_geometry_contains_for<StaticShapeKind>(candidate, x, y, cos_q, sin_q, candidate_shape_kind);
        if (outside) {
            continue;
        }
        const int offset = (y * width + x) * 4;
        const int tr = target[offset + 0];
        const int tg = target[offset + 1];
        const int tb = target[offset + 2];
        const int cr = current[offset + 0];
        const int cg = current[offset + 1];
        const int cb = current[offset + 2];
        const int ta = target[offset + 3];
        const int ca = current[offset + 3];
        const int dr = tr - cr;
        const int dg = tg - cg;
        const int db = tb - cb;
        const int da = ta - ca;
        local_r += static_cast<long long>((tr - cr) * color_scale + cr * 0x101);
        local_g += static_cast<long long>((tg - cg) * color_scale + cg * 0x101);
        local_b += static_cast<long long>((tb - cb) * color_scale + cb * 0x101);
        local_old += static_cast<unsigned long long>(dr * dr + dg * dg + db * db + da * da);
        local_count++;
    }

    rsum[tid] = local_r;
    gsum[tid] = local_g;
    bsum[tid] = local_b;
    count[tid] = local_count;
    old_error[tid] = local_old;
    __syncthreads();

    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            rsum[tid] += rsum[tid + stride];
            gsum[tid] += gsum[tid + stride];
            bsum[tid] += bsum[tid + stride];
            count[tid] += count[tid + stride];
        }
        __syncthreads();
    }

    if (tid == 0) {
        if (count[0] == 0) {
            color_r = 0;
            color_g = 0;
            color_b = 0;
        } else {
            color_r = clamp_channel(static_cast<int>(rsum[0] / count[0]) >> 8);
            color_g = clamp_channel(static_cast<int>(gsum[0] / count[0]) >> 8);
            color_b = clamp_channel(static_cast<int>(bsum[0] / count[0]) >> 8);
        }
    }
    __syncthreads();

    const unsigned int m = 0xffff;
    const unsigned int ma = 0xffff;
    const unsigned int alpha16 = static_cast<unsigned int>(candidate.alpha) * 0x101;
    const unsigned int blend = (m - alpha16 * ma / m) * 0x101;
    const unsigned int sr = static_cast<unsigned int>((static_cast<unsigned long long>(color_r) * 0x101 * candidate.alpha) / 255);
    const unsigned int sg = static_cast<unsigned int>((static_cast<unsigned long long>(color_g) * 0x101 * candidate.alpha) / 255);
    const unsigned int sb = static_cast<unsigned int>((static_cast<unsigned long long>(color_b) * 0x101 * candidate.alpha) / 255);

    unsigned long long local_new = 0;
    for (int index = tid; index < pixel_count; index += blockDim.x) {
        const int row = index / box_width;
        const int column = index - row * box_width;
        const int x = min_x + column;
        const int y = min_y + row;
        const std::uint32_t bounds = use_scanlines ? row_bounds[row] : 0u;
        const bool outside = use_scanlines
            ? column < static_cast<int>(bounds & 0xffffu) || column >= static_cast<int>(bounds >> 16)
            : !rotated_geometry_contains_for<StaticShapeKind>(candidate, x, y, cos_q, sin_q, candidate_shape_kind);
        if (outside) {
            continue;
        }
        const int offset = (y * width + x) * 4;
        const int tr = target[offset + 0];
        const int tg = target[offset + 1];
        const int tb = target[offset + 2];
        const int ta = target[offset + 3];
        const int br = current[offset + 0];
        const int bg = current[offset + 1];
        const int bb = current[offset + 2];
        const int ba = current[offset + 3];
        const int ar = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(br) * blend + static_cast<unsigned long long>(sr) * ma) / m) >> 8));
        const int ag = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bg) * blend + static_cast<unsigned long long>(sg) * ma) / m) >> 8));
        const int ab = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bb) * blend + static_cast<unsigned long long>(sb) * ma) / m) >> 8));
        const int aa = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(ba) * blend + static_cast<unsigned long long>(alpha16) * ma) / m) >> 8));
        const int dr2 = tr - ar;
        const int dg2 = tg - ag;
        const int db2 = tb - ab;
        const int da2 = ta - aa;
        local_new += static_cast<unsigned long long>(dr2 * dr2 + dg2 * dg2 + db2 * db2 + da2 * da2);
    }

    new_error[tid] = local_new;
    __syncthreads();

    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            old_error[tid] += old_error[tid + stride];
            new_error[tid] += new_error[tid + stride];
        }
        __syncthreads();
    }

    if (tid == 0) {
        ScoreRecord record{};
        record.candidateId = candidate.candidateId;
        record.r = static_cast<std::uint8_t>(color_r);
        record.g = static_cast<std::uint8_t>(color_g);
        record.b = static_cast<std::uint8_t>(color_b);
        record.a = static_cast<std::uint8_t>(candidate.alpha);
        record.oldErrorDelta = old_error[0];
        record.newErrorDelta = new_error[0];
        const double total = static_cast<double>(baseTotalError - old_error[0] + new_error[0]);
        record.energy = sqrt(total / static_cast<double>(width * height * 4)) / 255.0;
        output[blockIdx.x] = record;
    }
}

template <int StaticShapeKind>
__global__ void score_batch_rotated_geometry_weighted_kernel(
    const std::uint8_t* target,
    const std::uint8_t* current,
    const std::uint16_t* weights,
    const RotatedCandidateRecord* candidates,
    ScoreRecord* output,
    int width,
    int height,
    std::uint64_t baseTotalError,
    int shape_kind,
    const std::uint32_t* shape_kinds) {
    const int tid = threadIdx.x;

    __shared__ RotatedGeometryBlock geometry;
    __shared__ long long rsum[256];
    __shared__ long long gsum[256];
    __shared__ long long bsum[256];
    __shared__ long long weight_sum[256];
    __shared__ unsigned long long old_error[256];
    __shared__ unsigned long long new_error[256];
    __shared__ std::uint32_t row_bounds[MAX_ROTATED_SCANLINES];
    __shared__ int color_r;
    __shared__ int color_g;
    __shared__ int color_b;

    initialize_rotated_geometry_block<StaticShapeKind>(candidates, shape_kinds, blockIdx.x, shape_kind, width, height, geometry);
    const RotatedCandidateRecord candidate = geometry.candidate;
    const int candidate_shape_kind = geometry.shapeKind;
    const int cos_q = geometry.cosQ;
    const int sin_q = geometry.sinQ;
    const int min_x = geometry.minX;
    const int min_y = geometry.minY;
    const int box_width = geometry.boxWidth;
    const int pixel_count = geometry.pixelCount;
    const bool use_scanlines = initialize_rotated_scanlines<StaticShapeKind>(geometry, row_bounds);

    long long local_r = 0;
    long long local_g = 0;
    long long local_b = 0;
    long long local_weight = 0;
    unsigned long long local_old = 0;
    const int color_scale = 0x101 * 255 / candidate.alpha;
    for (int index = tid; index < pixel_count; index += blockDim.x) {
        const int row = index / box_width;
        const int column = index - row * box_width;
        const int x = min_x + column;
        const int y = min_y + row;
        const std::uint32_t bounds = use_scanlines ? row_bounds[row] : 0u;
        const bool outside = use_scanlines
            ? column < static_cast<int>(bounds & 0xffffu) || column >= static_cast<int>(bounds >> 16)
            : !rotated_geometry_contains_for<StaticShapeKind>(candidate, x, y, cos_q, sin_q, candidate_shape_kind);
        if (outside) {
            continue;
        }
        const int pixel = y * width + x;
        const int offset = pixel * 4;
        const int weight = weights[pixel];
        const int tr = target[offset + 0];
        const int tg = target[offset + 1];
        const int tb = target[offset + 2];
        const int cr = current[offset + 0];
        const int cg = current[offset + 1];
        const int cb = current[offset + 2];
        const int ta = target[offset + 3];
        const int ca = current[offset + 3];
        const int dr = tr - cr;
        const int dg = tg - cg;
        const int db = tb - cb;
        const int da = ta - ca;
        local_r += static_cast<long long>(((tr - cr) * color_scale + cr * 0x101) * weight);
        local_g += static_cast<long long>(((tg - cg) * color_scale + cg * 0x101) * weight);
        local_b += static_cast<long long>(((tb - cb) * color_scale + cb * 0x101) * weight);
        local_old += static_cast<unsigned long long>((static_cast<unsigned long long>(dr * dr + dg * dg + db * db + da * da) * static_cast<unsigned long long>(weight)) / 256);
        local_weight += weight;
    }

    rsum[tid] = local_r;
    gsum[tid] = local_g;
    bsum[tid] = local_b;
    weight_sum[tid] = local_weight;
    old_error[tid] = local_old;
    __syncthreads();

    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            rsum[tid] += rsum[tid + stride];
            gsum[tid] += gsum[tid + stride];
            bsum[tid] += bsum[tid + stride];
            weight_sum[tid] += weight_sum[tid + stride];
        }
        __syncthreads();
    }

    if (tid == 0) {
        if (weight_sum[0] == 0) {
            color_r = 0;
            color_g = 0;
            color_b = 0;
        } else {
            color_r = clamp_channel(static_cast<int>(rsum[0] / weight_sum[0]) >> 8);
            color_g = clamp_channel(static_cast<int>(gsum[0] / weight_sum[0]) >> 8);
            color_b = clamp_channel(static_cast<int>(bsum[0] / weight_sum[0]) >> 8);
        }
    }
    __syncthreads();

    const unsigned int m = 0xffff;
    const unsigned int ma = 0xffff;
    const unsigned int alpha16 = static_cast<unsigned int>(candidate.alpha) * 0x101;
    const unsigned int blend = (m - alpha16 * ma / m) * 0x101;
    const unsigned int sr = static_cast<unsigned int>((static_cast<unsigned long long>(color_r) * 0x101 * candidate.alpha) / 255);
    const unsigned int sg = static_cast<unsigned int>((static_cast<unsigned long long>(color_g) * 0x101 * candidate.alpha) / 255);
    const unsigned int sb = static_cast<unsigned int>((static_cast<unsigned long long>(color_b) * 0x101 * candidate.alpha) / 255);

    unsigned long long local_new = 0;
    for (int index = tid; index < pixel_count; index += blockDim.x) {
        const int row = index / box_width;
        const int column = index - row * box_width;
        const int x = min_x + column;
        const int y = min_y + row;
        const std::uint32_t bounds = use_scanlines ? row_bounds[row] : 0u;
        const bool outside = use_scanlines
            ? column < static_cast<int>(bounds & 0xffffu) || column >= static_cast<int>(bounds >> 16)
            : !rotated_geometry_contains_for<StaticShapeKind>(candidate, x, y, cos_q, sin_q, candidate_shape_kind);
        if (outside) {
            continue;
        }
        const int pixel = y * width + x;
        const int offset = pixel * 4;
        const unsigned long long weight = weights[pixel];
        const int tr = target[offset + 0];
        const int tg = target[offset + 1];
        const int tb = target[offset + 2];
        const int ta = target[offset + 3];
        const int br = current[offset + 0];
        const int bg = current[offset + 1];
        const int bb = current[offset + 2];
        const int ba = current[offset + 3];
        const int ar = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(br) * blend + static_cast<unsigned long long>(sr) * ma) / m) >> 8));
        const int ag = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bg) * blend + static_cast<unsigned long long>(sg) * ma) / m) >> 8));
        const int ab = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bb) * blend + static_cast<unsigned long long>(sb) * ma) / m) >> 8));
        const int aa = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(ba) * blend + static_cast<unsigned long long>(alpha16) * ma) / m) >> 8));
        const int dr2 = tr - ar;
        const int dg2 = tg - ag;
        const int db2 = tb - ab;
        const int da2 = ta - aa;
        local_new += static_cast<unsigned long long>((static_cast<unsigned long long>(dr2 * dr2 + dg2 * dg2 + db2 * db2 + da2 * da2) * weight) / 256);
    }

    new_error[tid] = local_new;
    __syncthreads();

    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            old_error[tid] += old_error[tid + stride];
            new_error[tid] += new_error[tid + stride];
        }
        __syncthreads();
    }

    if (tid == 0) {
        ScoreRecord record{};
        record.candidateId = candidate.candidateId;
        record.r = static_cast<std::uint8_t>(color_r);
        record.g = static_cast<std::uint8_t>(color_g);
        record.b = static_cast<std::uint8_t>(color_b);
        record.a = static_cast<std::uint8_t>(candidate.alpha);
        record.oldErrorDelta = old_error[0];
        record.newErrorDelta = new_error[0];
        const double total = static_cast<double>(baseTotalError - old_error[0] + new_error[0]);
        record.energy = sqrt(total / static_cast<double>(width * height * 4)) / 255.0;
        output[blockIdx.x] = record;
    }
}

__device__ bool catalog_mask_contains(
    const RotatedCandidateRecord& candidate,
    int x,
    int y,
    double cosine,
    double sine,
    const std::uint8_t* mask,
    CatalogMaskMetadata metadata) {
    const double rx = static_cast<double>(candidate.rx) / CATALOG_GEOMETRY_SCALE;
    const double ry = static_cast<double>(candidate.ry) / CATALOG_GEOMETRY_SCALE;
    const double dx = static_cast<double>(x) - static_cast<double>(candidate.cx) / CATALOG_GEOMETRY_SCALE;
    const double dy = static_cast<double>(y) - static_cast<double>(candidate.cy) / CATALOG_GEOMETRY_SCALE;
    const double local_x = cosine * dx + sine * dy;
    const double local_y = -sine * dx + cosine * dy;
    const double path_x = (local_x / (2.0 * rx) + 0.5) * metadata.intrinsicWidth;
    const double path_y = (local_y / (2.0 * ry) + 0.5) * metadata.intrinsicHeight;
    if (path_x < metadata.minX || path_x > metadata.maxX || path_y < metadata.minY || path_y > metadata.maxY) return false;
    const double mask_width = static_cast<double>(metadata.maxX - metadata.minX);
    const double mask_height = static_cast<double>(metadata.maxY - metadata.minY);
    const int atlas_x = min(static_cast<int>(metadata.size) - 1, max(0, static_cast<int>(floor((path_x - metadata.minX) / mask_width * metadata.size))));
    const int atlas_y = min(static_cast<int>(metadata.size) - 1, max(0, static_cast<int>(floor((path_y - metadata.minY) / mask_height * metadata.size))));
    return mask[atlas_y * metadata.size + atlas_x] >= 128;
}

__global__ void score_batch_catalog_geometry_kernel(
    const std::uint8_t* target,
    const std::uint8_t* current,
    const std::uint16_t* weights,
    const RotatedCandidateRecord* candidates,
    ScoreRecord* output,
    int width,
    int height,
    std::uint64_t base_total_error,
    const std::uint8_t* mask,
    CatalogMaskMetadata metadata) {
    const int tid = threadIdx.x;
    const RotatedCandidateRecord candidate = candidates[blockIdx.x];
    __shared__ long long rsum[256];
    __shared__ long long gsum[256];
    __shared__ long long bsum[256];
    __shared__ long long weight_sum[256];
    __shared__ unsigned long long old_error[256];
    __shared__ unsigned long long new_error[256];
    __shared__ int color_r;
    __shared__ int color_g;
    __shared__ int color_b;

    const double theta = static_cast<double>(candidate.angleDegrees) * 0.01745329251994329577;
    const double cosine = cos(theta);
    const double sine = sin(theta);
    const double center_x = static_cast<double>(candidate.cx) / CATALOG_GEOMETRY_SCALE;
    const double center_y = static_cast<double>(candidate.cy) / CATALOG_GEOMETRY_SCALE;
    const double rx = static_cast<double>(candidate.rx) / CATALOG_GEOMETRY_SCALE;
    const double ry = static_cast<double>(candidate.ry) / CATALOG_GEOMETRY_SCALE;
    const double extent_local_x = max(fabs((2.0 * metadata.minX / metadata.intrinsicWidth - 1.0) * rx), fabs((2.0 * metadata.maxX / metadata.intrinsicWidth - 1.0) * rx));
    const double extent_local_y = max(fabs((2.0 * metadata.minY / metadata.intrinsicHeight - 1.0) * ry), fabs((2.0 * metadata.maxY / metadata.intrinsicHeight - 1.0) * ry));
    const int extent_x = static_cast<int>(ceil(fabs(extent_local_x * cosine) + fabs(extent_local_y * sine))) + 1;
    const int extent_y = static_cast<int>(ceil(fabs(extent_local_x * sine) + fabs(extent_local_y * cosine))) + 1;
    const int min_x = max(0, static_cast<int>(floor(center_x - extent_x)));
    const int max_x = min(width - 1, static_cast<int>(ceil(center_x + extent_x)));
    const int min_y = max(0, static_cast<int>(floor(center_y - extent_y)));
    const int max_y = min(height - 1, static_cast<int>(ceil(center_y + extent_y)));
    const int box_width = max_x >= min_x ? max_x - min_x + 1 : 0;
    const int box_height = max_y >= min_y ? max_y - min_y + 1 : 0;
    const int pixel_count = box_width * box_height;

    long long local_r = 0;
    long long local_g = 0;
    long long local_b = 0;
    long long local_weight = 0;
    unsigned long long local_old = 0;
    const int color_scale = 0x101 * 255 / candidate.alpha;
    for (int index = tid; index < pixel_count; index += blockDim.x) {
        const int x = min_x + index % box_width;
        const int y = min_y + index / box_width;
        if (!catalog_mask_contains(candidate, x, y, cosine, sine, mask, metadata)) continue;
        const int pixel = y * width + x;
        const int offset = pixel * 4;
        const int weight = weights ? weights[pixel] : 256;
        const int tr = target[offset + 0];
        const int tg = target[offset + 1];
        const int tb = target[offset + 2];
        const int ta = target[offset + 3];
        const int cr = current[offset + 0];
        const int cg = current[offset + 1];
        const int cb = current[offset + 2];
        const int ca = current[offset + 3];
        const int dr = tr - cr;
        const int dg = tg - cg;
        const int db = tb - cb;
        const int da = ta - ca;
        local_r += static_cast<long long>(((tr - cr) * color_scale + cr * 0x101) * weight);
        local_g += static_cast<long long>(((tg - cg) * color_scale + cg * 0x101) * weight);
        local_b += static_cast<long long>(((tb - cb) * color_scale + cb * 0x101) * weight);
        local_old += static_cast<unsigned long long>(dr * dr + dg * dg + db * db + da * da) * weight / 256;
        local_weight += weight;
    }

    rsum[tid] = local_r;
    gsum[tid] = local_g;
    bsum[tid] = local_b;
    weight_sum[tid] = local_weight;
    old_error[tid] = local_old;
    __syncthreads();
    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            rsum[tid] += rsum[tid + stride];
            gsum[tid] += gsum[tid + stride];
            bsum[tid] += bsum[tid + stride];
            weight_sum[tid] += weight_sum[tid + stride];
        }
        __syncthreads();
    }
    if (tid == 0) {
        color_r = weight_sum[0] == 0 ? 0 : clamp_channel(static_cast<int>(rsum[0] / weight_sum[0]) >> 8);
        color_g = weight_sum[0] == 0 ? 0 : clamp_channel(static_cast<int>(gsum[0] / weight_sum[0]) >> 8);
        color_b = weight_sum[0] == 0 ? 0 : clamp_channel(static_cast<int>(bsum[0] / weight_sum[0]) >> 8);
    }
    __syncthreads();

    const unsigned int m = 0xffff;
    const unsigned int alpha16 = static_cast<unsigned int>(candidate.alpha) * 0x101;
    const unsigned int blend = (m - alpha16) * 0x101;
    const unsigned int sr = static_cast<unsigned int>((static_cast<unsigned long long>(color_r) * 0x101 * candidate.alpha) / 255);
    const unsigned int sg = static_cast<unsigned int>((static_cast<unsigned long long>(color_g) * 0x101 * candidate.alpha) / 255);
    const unsigned int sb = static_cast<unsigned int>((static_cast<unsigned long long>(color_b) * 0x101 * candidate.alpha) / 255);
    unsigned long long local_new = 0;
    for (int index = tid; index < pixel_count; index += blockDim.x) {
        const int x = min_x + index % box_width;
        const int y = min_y + index / box_width;
        if (!catalog_mask_contains(candidate, x, y, cosine, sine, mask, metadata)) continue;
        const int pixel = y * width + x;
        const int offset = pixel * 4;
        const int weight = weights ? weights[pixel] : 256;
        const int ar = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(current[offset + 0]) * blend + static_cast<unsigned long long>(sr) * m) / m) >> 8));
        const int ag = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(current[offset + 1]) * blend + static_cast<unsigned long long>(sg) * m) / m) >> 8));
        const int ab = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(current[offset + 2]) * blend + static_cast<unsigned long long>(sb) * m) / m) >> 8));
        const int aa = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(current[offset + 3]) * blend + static_cast<unsigned long long>(alpha16) * m) / m) >> 8));
        const int dr = target[offset + 0] - ar;
        const int dg = target[offset + 1] - ag;
        const int db = target[offset + 2] - ab;
        const int da = target[offset + 3] - aa;
        local_new += static_cast<unsigned long long>(dr * dr + dg * dg + db * db + da * da) * weight / 256;
    }
    new_error[tid] = local_new;
    __syncthreads();
    for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
        if (tid < stride) {
            old_error[tid] += old_error[tid + stride];
            new_error[tid] += new_error[tid + stride];
        }
        __syncthreads();
    }
    if (tid == 0) {
        ScoreRecord record{};
        record.candidateId = candidate.candidateId;
        record.r = static_cast<std::uint8_t>(color_r);
        record.g = static_cast<std::uint8_t>(color_g);
        record.b = static_cast<std::uint8_t>(color_b);
        record.a = static_cast<std::uint8_t>(candidate.alpha);
        record.oldErrorDelta = old_error[0];
        record.newErrorDelta = new_error[0];
        const double total = static_cast<double>(base_total_error - old_error[0] + new_error[0]);
        record.energy = sqrt(total / static_cast<double>(width * height * 4)) / 255.0;
        output[blockIdx.x] = record;
    }
}

struct ResidentDeviceRandom {
    std::uint32_t state = 0;
    int hasSpare = 0;
    double spare = 0.0;
};

struct ResidentDeviceChain {
    int group = 0;
    int shapeKind = GEOMETRY_ELLIPSE;
    RotatedCandidateRecord state{};
    ScoreRecord score{};
    ResidentDeviceRandom rng{};
    std::uint32_t remainingAge = 0;
    std::uint32_t steps = 0;
    std::uint32_t lockShortAxis = 0;
    int minLongAxis = 0;
    int maxLongAxis = 0;
    double structuralObjective = 0.0;
};

struct StructuralSearchState {
    double bestEnergy = 0.0;
    double baseEnergy = 0.0;
    double allowedEnergy = 0.0;
    double gain = 0.0;
};

struct ResidentDeviceChunkStats {
    std::uint32_t activeChains = 0;
    std::uint32_t rounds = 0;
    std::uint32_t proposalScores = 0;
    std::uint32_t acceptedMutations = 0;
    std::uint32_t lastAcceptRound = 0;
};

__device__ int resident_device_clamp_int(int value, int min_value, int max_value) {
    return value < min_value ? min_value : value > max_value ? max_value : value;
}

__device__ float resident_device_wrap_angle_180(double value) {
    double out = fmod(value, 180.0);
    if (out < 0) out += 180.0;
    return static_cast<float>(out);
}

__device__ double resident_device_next_float(ResidentDeviceRandom& random) {
    random.state = random.state * 1664525u + 1013904223u;
    return static_cast<double>(random.state) / 4294967296.0;
}

__device__ int resident_device_intn(ResidentDeviceRandom& random, int max) {
    return static_cast<int>(floor(resident_device_next_float(random) * static_cast<double>(max)));
}

__device__ double resident_device_normal(ResidentDeviceRandom& random) {
    if (random.hasSpare) {
        random.hasSpare = 0;
        return random.spare;
    }
    const double u = fmax(2.2204460492503131e-16, resident_device_next_float(random));
    const double v = resident_device_next_float(random);
    const double radius = sqrt(-2.0 * log(u));
    const double theta = 2.0 * 3.1415926535897932384626433832795 * v;
    random.spare = radius * sin(theta);
    random.hasSpare = 1;
    return radius * cos(theta);
}

__device__ double structural_sample_cost(
    const RotatedCandidateRecord& candidate,
    const std::uint16_t* distance_q8,
    const std::uint16_t* tangent_q8,
    int width,
    int height,
    int distance_limit,
    float cos_theta,
    float sin_theta,
    int local_x,
    int local_y,
    double candidate_tangent) {
    const int x = candidate.cx + __float2int_rn(cos_theta * local_x - sin_theta * local_y);
    const int y = candidate.cy + __float2int_rn(sin_theta * local_x + cos_theta * local_y);
    if (x < 0 || y < 0 || x >= width || y >= height) return 1.0;
    const int index = y * width + x;
    const double maximum_distance = static_cast<double>(distance_limit * 256);
    const double distance = fmin(static_cast<double>(distance_q8[index]), maximum_distance);
    if (distance >= maximum_distance) return 1.0;
    double tangent_delta = fabs(candidate_tangent - static_cast<double>(tangent_q8[index]) / 256.0);
    tangent_delta = fmin(tangent_delta, 180.0 - tangent_delta) / 90.0;
    return 0.5 * (distance / maximum_distance + tangent_delta);
}

__device__ double structural_edge_score(
    const RotatedCandidateRecord& candidate,
    const std::uint16_t* distance_q8,
    const std::uint16_t* tangent_q8,
    int width,
    int height,
    int distance_limit) {
    const float theta = candidate.angleDegrees * 0.01745329251994329577f;
    float sin_theta = 0.0f;
    float cos_theta = 1.0f;
    __sincosf(theta, &sin_theta, &cos_theta);
    const double long_tangent = static_cast<double>(candidate.angleDegrees);
    const double cap_tangent = fmod(long_tangent + 90.0, 180.0);
    double total = 0.0;
    int count = 0;
    for (int x = -candidate.rx; x <= candidate.rx; x++) {
        total += structural_sample_cost(candidate, distance_q8, tangent_q8, width, height, distance_limit, cos_theta, sin_theta, x, -candidate.ry, long_tangent);
        total += structural_sample_cost(candidate, distance_q8, tangent_q8, width, height, distance_limit, cos_theta, sin_theta, x, candidate.ry, long_tangent);
        count += 2;
    }
    for (int y = -candidate.ry + 1; y < candidate.ry; y++) {
        total += structural_sample_cost(candidate, distance_q8, tangent_q8, width, height, distance_limit, cos_theta, sin_theta, -candidate.rx, y, cap_tangent);
        total += structural_sample_cost(candidate, distance_q8, tangent_q8, width, height, distance_limit, cos_theta, sin_theta, candidate.rx, y, cap_tangent);
        count += 2;
    }
    return count == 0 ? 1.0 : total / static_cast<double>(count);
}

__device__ double structural_objective(
    const RotatedCandidateRecord& candidate,
    double energy,
    const StructuralSearchState& search,
    const std::uint16_t* distance_q8,
    const std::uint16_t* tangent_q8,
    int width,
    int height,
    int distance_limit,
    double edge_weight) {
    if (energy > search.allowedEnergy) return 1.7976931348623157e308;
    const double relative_pixel_penalty = (energy - search.bestEnergy) / search.gain;
    return relative_pixel_penalty + edge_weight * structural_edge_score(candidate, distance_q8, tangent_q8, width, height, distance_limit);
}

__global__ void prepare_structural_chains_kernel(
    ResidentDeviceChain* chains,
    std::uint32_t chain_count,
    StructuralSearchState* search,
    const std::uint16_t* distance_q8,
    const std::uint16_t* tangent_q8,
    int width,
    int height,
    double base_energy,
    int distance_limit,
    double edge_weight,
    double max_pixel_gain_regression,
    std::uint32_t rounds) {
    if (blockIdx.x != 0 || threadIdx.x != 0) return;
    double best_energy = chains[0].score.energy;
    for (std::uint32_t index = 1; index < chain_count; index++) best_energy = fmin(best_energy, chains[index].score.energy);
    search->bestEnergy = best_energy;
    search->baseEnergy = base_energy;
    search->gain = fmax(base_energy - best_energy, 1e-12);
    search->allowedEnergy = best_energy + max_pixel_gain_regression * search->gain;
    for (std::uint32_t index = 0; index < chain_count; index++) {
        chains[index].structuralObjective = structural_objective(chains[index].state, chains[index].score.energy, *search, distance_q8, tangent_q8, width, height, distance_limit, edge_weight);
        chains[index].remainingAge = rounds + 1u;
        chains[index].steps = 0;
    }
}

__global__ void prune_structural_chains_kernel(ResidentDeviceChain* chains, std::uint32_t chain_count) {
    if (blockIdx.x != 0 || threadIdx.x != 0) return;
    std::uint32_t best_index = 0;
    for (std::uint32_t index = 1; index < chain_count; index++) {
        if (chains[index].structuralObjective < chains[best_index].structuralObjective) best_index = index;
    }
    const ResidentDeviceChain best = chains[best_index];
    for (std::uint32_t index = 0; index < chain_count; index++) {
        if (chains[index].structuralObjective >= 1.7976931348623157e308) chains[index] = best;
    }
}

__device__ RotatedCandidateRecord resident_device_mutate_rotated_candidate(
    const ResidentDeviceChain& chain,
    ResidentDeviceRandom& random,
    int width,
    int height,
    int min_axis,
    bool mutate_alpha,
    int min_alpha,
    int max_alpha,
    double geometry_sigma,
    double angle_sigma,
    std::uint32_t candidate_id) {
    RotatedCandidateRecord proposal = chain.state;
    const int choice = resident_device_intn(random, chain.lockShortAxis ? 3 : 4);
    if (choice == 0) {
        proposal.cx = resident_device_clamp_int(proposal.cx + static_cast<int>(trunc(resident_device_normal(random) * geometry_sigma)), 0, width - 1);
        proposal.cy = resident_device_clamp_int(proposal.cy + static_cast<int>(trunc(resident_device_normal(random) * geometry_sigma)), 0, height - 1);
    } else if (choice == 1) {
        const int minimum = chain.minLongAxis > 0 ? chain.minLongAxis : min_axis;
        const int maximum = chain.maxLongAxis > 0 ? chain.maxLongAxis : width;
        proposal.rx = resident_device_clamp_int(proposal.rx + static_cast<int>(trunc(resident_device_normal(random) * geometry_sigma)), minimum, maximum);
    } else if (choice == 2 && !chain.lockShortAxis) {
        proposal.ry = resident_device_clamp_int(proposal.ry + static_cast<int>(trunc(resident_device_normal(random) * geometry_sigma)), min_axis, height);
    } else {
        proposal.angleDegrees = resident_device_wrap_angle_180(static_cast<double>(proposal.angleDegrees) + static_cast<int>(trunc(resident_device_normal(random) * angle_sigma)));
    }
    if (mutate_alpha) {
        proposal.alpha = resident_device_clamp_int(proposal.alpha + resident_device_intn(random, 21) - 10, min_alpha, max_alpha);
    } else {
        proposal.alpha = resident_device_clamp_int(proposal.alpha, min_alpha, max_alpha);
    }
    proposal.candidateId = candidate_id;
    proposal.reserved = static_cast<std::uint32_t>(chain.group);
    return proposal;
}

template <int StaticShapeKind>
__global__ void resident_rotated_geometry_coop_chunk_kernel(
    const std::uint8_t* target,
    const std::uint8_t* current,
    const std::uint16_t* weights,
    ResidentDeviceChain* chains,
    RotatedCandidateRecord* proposals,
    ScoreRecord* scores,
    double* structural_objectives,
    std::uint32_t* active_flags,
    ResidentDeviceChunkStats* stats,
    const std::uint16_t* structural_distance_q8,
    const std::uint16_t* structural_tangent_q8,
    const StructuralSearchState* structural_search,
    int width,
    int height,
    std::uint64_t baseTotalError,
    std::uint32_t chain_count,
    std::uint32_t age,
    std::uint32_t fanout,
    std::uint32_t chunk_rounds,
    std::uint32_t round_base,
    std::uint32_t max_hill_steps,
    int min_axis,
    bool weighted,
    bool mutate_alpha,
    int min_alpha,
    int max_alpha,
    bool structural_mode,
    int structural_distance_limit,
    double structural_edge_weight,
    std::uint32_t structural_total_rounds) {
    cg::grid_group grid = cg::this_grid();
    const std::uint32_t proposal_index = blockIdx.x;
    const std::uint32_t chain_index = proposal_index / fanout;
    const std::uint32_t fan = proposal_index - chain_index * fanout;
    const int tx = threadIdx.x;

    __shared__ RotatedGeometryBlock geometry;
    __shared__ long long rsum[256];
    __shared__ long long gsum[256];
    __shared__ long long bsum[256];
    __shared__ long long denom[256];
    __shared__ unsigned long long old_error[256];
    __shared__ unsigned long long new_error[256];
    __shared__ std::uint32_t row_bounds[MAX_ROTATED_SCANLINES];
    __shared__ int color_r;
    __shared__ int color_g;
    __shared__ int color_b;

    std::uint32_t local_rounds = 0;
    std::uint32_t local_proposals = 0;
    std::uint32_t local_accepts = 0;
    std::uint32_t local_last_accept_round = 0;

    for (std::uint32_t chunk_round = 0; chunk_round < chunk_rounds; chunk_round++) {
        if (tx == 0 && fan == 0) {
            ResidentDeviceChain chain = chains[chain_index];
            const bool active = chain.remainingAge > 0 && chain.steps < max_hill_steps;
            active_flags[chain_index] = active ? 1u : 0u;
            if (active) {
                const std::uint32_t round_number = round_base + chunk_round + 1u;
                double geometry_sigma = 16.0;
                if (structural_mode) {
                    if (chunk_round * 3u < structural_total_rounds) geometry_sigma = 8.0;
                    else if (chunk_round * 3u < structural_total_rounds * 2u) geometry_sigma = 4.0;
                    else geometry_sigma = 2.0;
                }
                const double angle_sigma = !structural_mode ? 15.0 : geometry_sigma;
                for (std::uint32_t i = 0; i < fanout; i++) {
                    const std::uint32_t candidate_id = round_number * 1000u + static_cast<std::uint32_t>(chain.group) * fanout + i + 1u;
                    proposals[chain_index * fanout + i] = resident_device_mutate_rotated_candidate(chain, chain.rng, width, height, min_axis, mutate_alpha, min_alpha, max_alpha, geometry_sigma, angle_sigma, candidate_id);
                }
                chains[chain_index].rng = chain.rng;
            }
        }
        grid.sync();

        if (active_flags[chain_index]) {
            initialize_rotated_geometry_block<StaticShapeKind>(
                proposals,
                nullptr,
                proposal_index,
                chains[chain_index].shapeKind,
                width,
                height,
                geometry);
            const RotatedCandidateRecord candidate = geometry.candidate;
            const int candidate_shape_kind = geometry.shapeKind;
            const int cos_q = geometry.cosQ;
            const int sin_q = geometry.sinQ;
            const int min_x = geometry.minX;
            const int min_y = geometry.minY;
            const int box_width = geometry.boxWidth;
            const int pixel_count = geometry.pixelCount;
            const bool use_scanlines = initialize_rotated_scanlines<StaticShapeKind>(geometry, row_bounds);

            long long local_r = 0;
            long long local_g = 0;
            long long local_b = 0;
            long long local_denom = 0;
            unsigned long long local_old = 0;
            const int color_scale = 0x101 * 255 / candidate.alpha;
            for (int index = tx; index < pixel_count; index += blockDim.x) {
                const int row = index / box_width;
                const int column = index - row * box_width;
                const int x = min_x + column;
                const int y = min_y + row;
                const std::uint32_t bounds = use_scanlines ? row_bounds[row] : 0u;
                const bool outside = use_scanlines
                    ? column < static_cast<int>(bounds & 0xffffu) || column >= static_cast<int>(bounds >> 16)
                    : !rotated_geometry_contains_for<StaticShapeKind>(candidate, x, y, cos_q, sin_q, candidate_shape_kind);
                if (outside) {
                    continue;
                }
                const int pixel = y * width + x;
                const int offset = pixel * 4;
                const int weight = weighted ? static_cast<int>(weights[pixel]) : 256;
                const int tr = target[offset + 0];
                const int tg = target[offset + 1];
                const int tb = target[offset + 2];
                const int cr = current[offset + 0];
                const int cg = current[offset + 1];
                const int cb = current[offset + 2];
                const int ta = target[offset + 3];
                const int ca = current[offset + 3];
                const int dr = tr - cr;
                const int dg = tg - cg;
                const int db = tb - cb;
                const int da = ta - ca;
                local_r += static_cast<long long>(((tr - cr) * color_scale + cr * 0x101) * weight);
                local_g += static_cast<long long>(((tg - cg) * color_scale + cg * 0x101) * weight);
                local_b += static_cast<long long>(((tb - cb) * color_scale + cb * 0x101) * weight);
                local_old += static_cast<unsigned long long>((static_cast<unsigned long long>(dr * dr + dg * dg + db * db + da * da) * static_cast<unsigned long long>(weight)) / 256);
                local_denom += weight;
            }

            rsum[tx] = local_r;
            gsum[tx] = local_g;
            bsum[tx] = local_b;
            denom[tx] = local_denom;
            old_error[tx] = local_old;
            __syncthreads();

            for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
                if (tx < stride) {
                    rsum[tx] += rsum[tx + stride];
                    gsum[tx] += gsum[tx + stride];
                    bsum[tx] += bsum[tx + stride];
                    denom[tx] += denom[tx + stride];
                    old_error[tx] += old_error[tx + stride];
                }
                __syncthreads();
            }

            if (tx == 0) {
                if (denom[0] == 0) {
                    color_r = 0;
                    color_g = 0;
                    color_b = 0;
                } else {
                    color_r = clamp_channel(static_cast<int>(rsum[0] / denom[0]) >> 8);
                    color_g = clamp_channel(static_cast<int>(gsum[0] / denom[0]) >> 8);
                    color_b = clamp_channel(static_cast<int>(bsum[0] / denom[0]) >> 8);
                }
            }
            __syncthreads();

            const unsigned int m = 0xffff;
            const unsigned int ma = 0xffff;
            const unsigned int alpha16 = static_cast<unsigned int>(candidate.alpha) * 0x101;
            const unsigned int blend = (m - alpha16 * ma / m) * 0x101;
            const unsigned int sr = static_cast<unsigned int>((static_cast<unsigned long long>(color_r) * 0x101 * candidate.alpha) / 255);
            const unsigned int sg = static_cast<unsigned int>((static_cast<unsigned long long>(color_g) * 0x101 * candidate.alpha) / 255);
            const unsigned int sb = static_cast<unsigned int>((static_cast<unsigned long long>(color_b) * 0x101 * candidate.alpha) / 255);

            unsigned long long local_new = 0;
            for (int index = tx; index < pixel_count; index += blockDim.x) {
                const int row = index / box_width;
                const int column = index - row * box_width;
                const int x = min_x + column;
                const int y = min_y + row;
                const std::uint32_t bounds = use_scanlines ? row_bounds[row] : 0u;
                const bool outside = use_scanlines
                    ? column < static_cast<int>(bounds & 0xffffu) || column >= static_cast<int>(bounds >> 16)
                    : !rotated_geometry_contains_for<StaticShapeKind>(candidate, x, y, cos_q, sin_q, candidate_shape_kind);
                if (outside) {
                    continue;
                }
                const int pixel = y * width + x;
                const int offset = pixel * 4;
                const unsigned long long weight = weighted ? static_cast<unsigned long long>(weights[pixel]) : 256ull;
                const int tr = target[offset + 0];
                const int tg = target[offset + 1];
                const int tb = target[offset + 2];
                const int ta = target[offset + 3];
                const int br = current[offset + 0];
                const int bg = current[offset + 1];
                const int bb = current[offset + 2];
                const int ba = current[offset + 3];
                const int ar = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(br) * blend + static_cast<unsigned long long>(sr) * ma) / m) >> 8));
                const int ag = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bg) * blend + static_cast<unsigned long long>(sg) * ma) / m) >> 8));
                const int ab = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(bb) * blend + static_cast<unsigned long long>(sb) * ma) / m) >> 8));
                const int aa = static_cast<int>(static_cast<std::uint8_t>(((static_cast<unsigned long long>(ba) * blend + static_cast<unsigned long long>(alpha16) * ma) / m) >> 8));
                const int dr2 = tr - ar;
                const int dg2 = tg - ag;
                const int db2 = tb - ab;
                const int da2 = ta - aa;
                local_new += static_cast<unsigned long long>((static_cast<unsigned long long>(dr2 * dr2 + dg2 * dg2 + db2 * db2 + da2 * da2) * weight) / 256);
            }

            new_error[tx] = local_new;
            __syncthreads();

            for (int stride = blockDim.x / 2; stride > 0; stride /= 2) {
                if (tx < stride) {
                    new_error[tx] += new_error[tx + stride];
                }
                __syncthreads();
            }

            if (tx == 0) {
                ScoreRecord record{};
                record.candidateId = candidate.candidateId;
                record.r = static_cast<std::uint8_t>(color_r);
                record.g = static_cast<std::uint8_t>(color_g);
                record.b = static_cast<std::uint8_t>(color_b);
                record.a = static_cast<std::uint8_t>(candidate.alpha);
                record.oldErrorDelta = old_error[0];
                record.newErrorDelta = new_error[0];
                const double total = static_cast<double>(baseTotalError - old_error[0] + new_error[0]);
                record.energy = sqrt(total / static_cast<double>(width * height * 4)) / 255.0;
                scores[proposal_index] = record;
                if (structural_mode) {
                    structural_objectives[proposal_index] = structural_objective(candidate, record.energy, *structural_search, structural_distance_q8, structural_tangent_q8, width, height, structural_distance_limit, structural_edge_weight);
                }
            }
        }
        grid.sync();

        if (tx == 0 && fan == 0 && active_flags[chain_index]) {
            ResidentDeviceChain chain = chains[chain_index];
            int best_index = -1;
            double best_value = structural_mode ? chain.structuralObjective : chain.score.energy;
            const std::uint32_t proposal_base = chain_index * fanout;
            for (std::uint32_t i = 0; i < fanout; i++) {
                const double value = structural_mode ? structural_objectives[proposal_base + i] : scores[proposal_base + i].energy;
                if (value < best_value) {
                    best_value = value;
                    best_index = static_cast<int>(proposal_base + i);
                }
            }
            if (best_index >= 0) {
                chain.state = proposals[best_index];
                chain.score = scores[best_index];
                if (structural_mode) chain.structuralObjective = structural_objectives[best_index];
                chain.state.alpha = static_cast<std::int32_t>(chain.score.a);
                chain.remainingAge = age;
                local_accepts++;
                local_last_accept_round = chunk_round + 1u;
            } else {
                chain.remainingAge--;
            }
            chain.steps++;
            chains[chain_index] = chain;
            local_rounds++;
            local_proposals += fanout;
        }
        grid.sync();
    }

    if (tx == 0 && fan == 0 && local_rounds > 0) {
        atomicAdd(&stats->activeChains, 1u);
        atomicMax(&stats->rounds, local_rounds);
        atomicAdd(&stats->proposalScores, local_proposals);
        atomicAdd(&stats->acceptedMutations, local_accepts);
        atomicMax(&stats->lastAcceptRound, local_last_accept_round);
    }
}

static int run_smoke() {
    int device_count = 0;
    cudaError_t err = cudaGetDeviceCount(&device_count);
    if (!check(err, "cudaGetDeviceCount") || device_count == 0) {
        std::fprintf(stderr, "cuda device unavailable: %s\n", cudaGetErrorString(err));
        return 1;
    }

    cudaDeviceProp prop{};
    if (!check(cudaGetDeviceProperties(&prop, 0), "cudaGetDeviceProperties")) {
        return 1;
    }
    std::printf("cuda-scorer-smoke 0.1\n");
    std::printf("device: %s\n", prop.name);
    std::printf("computeCapability: %d.%d\n", prop.major, prop.minor);

    int host_value = 41;
    int* device_value = nullptr;
    if (!check(cudaMalloc(&device_value, sizeof(int)), "cudaMalloc")) {
        return 1;
    }
    if (!check(cudaMemcpy(device_value, &host_value, sizeof(int), cudaMemcpyHostToDevice), "cudaMemcpy host-to-device")) {
        cudaFree(device_value);
        return 1;
    }
    add_one_kernel<<<1, 1>>>(device_value);
    if (!check(cudaGetLastError(), "add_one_kernel launch")) {
        cudaFree(device_value);
        return 1;
    }
    if (!check(cudaDeviceSynchronize(), "cudaDeviceSynchronize")) {
        cudaFree(device_value);
        return 1;
    }
    if (!check(cudaMemcpy(&host_value, device_value, sizeof(int), cudaMemcpyDeviceToHost), "cudaMemcpy device-to-host")) {
        cudaFree(device_value);
        return 1;
    }
    cudaFree(device_value);

    std::printf("kernelResult: %d\n", host_value);
    if (host_value != 42) {
        std::fprintf(stderr, "success: false\n");
        return 1;
    }

    std::printf("success: true\n");
    return 0;
}

static float run_score_geometry_kernel(
    const std::uint8_t* d_target,
    const std::uint8_t* d_current,
    const CandidateRecord* d_candidates,
    ScoreRecord* d_output,
    const Metadata& metadata,
    std::size_t candidate_count) {
    cudaEvent_t kernel_begin{};
    cudaEvent_t kernel_end{};
    require_cuda(cudaEventCreate(&kernel_begin), "cudaEventCreate geometry begin");
    require_cuda(cudaEventCreate(&kernel_end), "cudaEventCreate geometry end");
    require_cuda(cudaEventRecord(kernel_begin), "cudaEventRecord geometry begin");
    if (candidate_count > 0) {
        score_batch_geometry_kernel<<<static_cast<unsigned int>(candidate_count), 256>>>(
            d_target,
            d_current,
            d_candidates,
            d_output,
            metadata.width,
            metadata.height,
            metadata.baseTotalError);
    }
    require_cuda(cudaGetLastError(), "score_batch_geometry_kernel launch");
    require_cuda(cudaEventRecord(kernel_end), "cudaEventRecord geometry end");
    require_cuda(cudaEventSynchronize(kernel_end), "cudaEventSynchronize geometry end");
    float kernel_ms = 0;
    require_cuda(cudaEventElapsedTime(&kernel_ms, kernel_begin, kernel_end), "cudaEventElapsedTime geometry");
    cudaEventDestroy(kernel_begin);
    cudaEventDestroy(kernel_end);
    return kernel_ms;
}

static float run_score_rotated_geometry_kernel(
    const std::uint8_t* d_target,
    const std::uint8_t* d_current,
    const RotatedCandidateRecord* d_candidates,
    ScoreRecord* d_output,
    const Metadata& metadata,
    std::size_t candidate_count,
    int shape_kind = GEOMETRY_ELLIPSE,
    const std::uint32_t* d_shape_kinds = nullptr) {
    cudaEvent_t kernel_begin{};
    cudaEvent_t kernel_end{};
    require_cuda(cudaEventCreate(&kernel_begin), "cudaEventCreate rotated geometry begin");
    require_cuda(cudaEventCreate(&kernel_end), "cudaEventCreate rotated geometry end");
    require_cuda(cudaEventRecord(kernel_begin), "cudaEventRecord rotated geometry begin");
    if (candidate_count > 0) {
        if (d_shape_kinds) {
            score_batch_rotated_geometry_kernel<GEOMETRY_RUNTIME><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else if (shape_kind == GEOMETRY_RECTANGLE) {
            score_batch_rotated_geometry_kernel<GEOMETRY_RECTANGLE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else if (shape_kind == GEOMETRY_TRIANGLE) {
            score_batch_rotated_geometry_kernel<GEOMETRY_TRIANGLE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else if (shape_kind == GEOMETRY_LINE_RECTANGLE) {
            score_batch_rotated_geometry_kernel<GEOMETRY_LINE_RECTANGLE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else {
            score_batch_rotated_geometry_kernel<GEOMETRY_ELLIPSE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        }
    }
    require_cuda(cudaGetLastError(), "score_batch_rotated_geometry_kernel launch");
    require_cuda(cudaEventRecord(kernel_end), "cudaEventRecord rotated geometry end");
    require_cuda(cudaEventSynchronize(kernel_end), "cudaEventSynchronize rotated geometry end");
    float kernel_ms = 0;
    require_cuda(cudaEventElapsedTime(&kernel_ms, kernel_begin, kernel_end), "cudaEventElapsedTime rotated geometry");
    cudaEventDestroy(kernel_begin);
    cudaEventDestroy(kernel_end);
    return kernel_ms;
}

static float run_score_rotated_geometry_weighted_kernel(
    const std::uint8_t* d_target,
    const std::uint8_t* d_current,
    const std::uint16_t* d_weights,
    const RotatedCandidateRecord* d_candidates,
    ScoreRecord* d_output,
    const Metadata& metadata,
    std::size_t candidate_count,
    int shape_kind = GEOMETRY_ELLIPSE,
    const std::uint32_t* d_shape_kinds = nullptr) {
    cudaEvent_t kernel_begin{};
    cudaEvent_t kernel_end{};
    require_cuda(cudaEventCreate(&kernel_begin), "cudaEventCreate weighted rotated geometry begin");
    require_cuda(cudaEventCreate(&kernel_end), "cudaEventCreate weighted rotated geometry end");
    require_cuda(cudaEventRecord(kernel_begin), "cudaEventRecord weighted rotated geometry begin");
    if (candidate_count > 0) {
        if (d_shape_kinds) {
            score_batch_rotated_geometry_weighted_kernel<GEOMETRY_RUNTIME><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_weights,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else if (shape_kind == GEOMETRY_RECTANGLE) {
            score_batch_rotated_geometry_weighted_kernel<GEOMETRY_RECTANGLE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_weights,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else if (shape_kind == GEOMETRY_TRIANGLE) {
            score_batch_rotated_geometry_weighted_kernel<GEOMETRY_TRIANGLE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_weights,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else if (shape_kind == GEOMETRY_LINE_RECTANGLE) {
            score_batch_rotated_geometry_weighted_kernel<GEOMETRY_LINE_RECTANGLE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_weights,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        } else {
            score_batch_rotated_geometry_weighted_kernel<GEOMETRY_ELLIPSE><<<static_cast<unsigned int>(candidate_count), 256>>>(
                d_target,
                d_current,
                d_weights,
                d_candidates,
                d_output,
                metadata.width,
                metadata.height,
                metadata.baseTotalError,
                shape_kind,
                d_shape_kinds);
        }
    }
    require_cuda(cudaGetLastError(), "score_batch_rotated_geometry_weighted_kernel launch");
    require_cuda(cudaEventRecord(kernel_end), "cudaEventRecord weighted rotated geometry end");
    require_cuda(cudaEventSynchronize(kernel_end), "cudaEventSynchronize weighted rotated geometry end");
    float kernel_ms = 0;
    require_cuda(cudaEventElapsedTime(&kernel_ms, kernel_begin, kernel_end), "cudaEventElapsedTime weighted rotated geometry");
    cudaEventDestroy(kernel_begin);
    cudaEventDestroy(kernel_end);
    return kernel_ms;
}

static float run_score_catalog_geometry_kernel(
    const std::uint8_t* d_target,
    const std::uint8_t* d_current,
    const std::uint16_t* d_weights,
    const RotatedCandidateRecord* d_candidates,
    ScoreRecord* d_output,
    const Metadata& metadata,
    std::size_t candidate_count,
    const std::uint8_t* d_mask,
    CatalogMaskMetadata catalog_metadata) {
    cudaEvent_t kernel_begin{};
    cudaEvent_t kernel_end{};
    require_cuda(cudaEventCreate(&kernel_begin), "cudaEventCreate catalog geometry begin");
    require_cuda(cudaEventCreate(&kernel_end), "cudaEventCreate catalog geometry end");
    require_cuda(cudaEventRecord(kernel_begin), "cudaEventRecord catalog geometry begin");
    if (candidate_count > 0) {
        score_batch_catalog_geometry_kernel<<<static_cast<unsigned int>(candidate_count), 256>>>(
            d_target,
            d_current,
            d_weights,
            d_candidates,
            d_output,
            metadata.width,
            metadata.height,
            metadata.baseTotalError,
            d_mask,
            catalog_metadata);
    }
    require_cuda(cudaGetLastError(), "score_batch_catalog_geometry_kernel launch");
    require_cuda(cudaEventRecord(kernel_end), "cudaEventRecord catalog geometry end");
    require_cuda(cudaEventSynchronize(kernel_end), "cudaEventSynchronize catalog geometry end");
    float kernel_ms = 0;
    require_cuda(cudaEventElapsedTime(&kernel_ms, kernel_begin, kernel_end), "cudaEventElapsedTime catalog geometry");
    cudaEventDestroy(kernel_begin);
    cudaEventDestroy(kernel_end);
    return kernel_ms;
}

static float run_resident_rotated_geometry_device_chunk_kernel(
    const std::uint8_t* d_target,
    const std::uint8_t* d_current,
    const std::uint16_t* d_weights,
    ResidentDeviceChain* d_chains,
    RotatedCandidateRecord* d_proposals,
    ScoreRecord* d_scores,
    double* d_structural_objectives,
    std::uint32_t* d_active_flags,
    ResidentDeviceChunkStats* d_stats,
    const std::uint16_t* d_structural_distance,
    const std::uint16_t* d_structural_tangent,
    const StructuralSearchState* d_structural_search,
    const Metadata& metadata,
    std::size_t chain_count,
    std::uint32_t age,
    std::uint32_t fanout,
    std::uint32_t chunk_rounds,
    std::uint32_t round_base,
    std::uint32_t max_hill_steps,
    int min_axis,
    bool weighted,
    bool mutate_alpha,
    int min_alpha,
    int max_alpha,
    int shape_kind,
    bool structural_mode = false,
    int structural_distance_limit = 0,
    double structural_edge_weight = 0.0,
    std::uint32_t structural_total_rounds = 0) {
    cudaEvent_t kernel_begin{};
    cudaEvent_t kernel_end{};
    require_cuda(cudaEventCreate(&kernel_begin), "cudaEventCreate resident device chunk begin");
    require_cuda(cudaEventCreate(&kernel_end), "cudaEventCreate resident device chunk end");
    require_cuda(cudaEventRecord(kernel_begin), "cudaEventRecord resident device chunk begin");
    if (chain_count > 0) {
        const unsigned int block_count = static_cast<unsigned int>(chain_count * fanout);
        int width = metadata.width;
        int height = metadata.height;
        std::uint64_t base_total_error = metadata.baseTotalError;
        std::uint32_t chain_count_u32 = static_cast<std::uint32_t>(chain_count);
        void* kernel = reinterpret_cast<void*>(resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_RUNTIME>);
        if (shape_kind == GEOMETRY_RECTANGLE) {
            kernel = reinterpret_cast<void*>(resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_RECTANGLE>);
        } else if (shape_kind == GEOMETRY_TRIANGLE) {
            kernel = reinterpret_cast<void*>(resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_TRIANGLE>);
        } else if (shape_kind == GEOMETRY_LINE_RECTANGLE) {
            kernel = reinterpret_cast<void*>(resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_LINE_RECTANGLE>);
        } else if (shape_kind == GEOMETRY_ELLIPSE) {
            kernel = reinterpret_cast<void*>(resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_ELLIPSE>);
        }
        void* kernel_args[] = {
            &d_target,
            &d_current,
            &d_weights,
            &d_chains,
            &d_proposals,
            &d_scores,
            &d_structural_objectives,
            &d_active_flags,
            &d_stats,
            &d_structural_distance,
            &d_structural_tangent,
            &d_structural_search,
            &width,
            &height,
            &base_total_error,
            &chain_count_u32,
            &age,
            &fanout,
            &chunk_rounds,
            &round_base,
            &max_hill_steps,
            &min_axis,
            &weighted,
            &mutate_alpha,
            &min_alpha,
            &max_alpha,
            &structural_mode,
            &structural_distance_limit,
            &structural_edge_weight,
            &structural_total_rounds,
        };
        require_cuda(cudaLaunchCooperativeKernel(
            kernel,
            block_count,
            256,
            kernel_args),
            "resident_rotated_geometry_coop_chunk_kernel launch");
    }
    require_cuda(cudaGetLastError(), "resident device chunk kernel status");
    require_cuda(cudaEventRecord(kernel_end), "cudaEventRecord resident device chunk end");
    require_cuda(cudaEventSynchronize(kernel_end), "cudaEventSynchronize resident device chunk end");
    float kernel_ms = 0;
    require_cuda(cudaEventElapsedTime(&kernel_ms, kernel_begin, kernel_end), "cudaEventElapsedTime resident device chunk");
    cudaEventDestroy(kernel_begin);
    cudaEventDestroy(kernel_end);
    return kernel_ms;
}

static int resident_device_blocks_per_sm(int shape_kind) {
    int blocks_per_sm = 0;
    if (shape_kind == GEOMETRY_RECTANGLE) {
        require_cuda(cudaOccupancyMaxActiveBlocksPerMultiprocessor(&blocks_per_sm, resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_RECTANGLE>, 256, 0), "resident device occupancy rectangle");
    } else if (shape_kind == GEOMETRY_TRIANGLE) {
        require_cuda(cudaOccupancyMaxActiveBlocksPerMultiprocessor(&blocks_per_sm, resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_TRIANGLE>, 256, 0), "resident device occupancy triangle");
    } else if (shape_kind == GEOMETRY_LINE_RECTANGLE) {
        require_cuda(cudaOccupancyMaxActiveBlocksPerMultiprocessor(&blocks_per_sm, resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_LINE_RECTANGLE>, 256, 0), "resident device occupancy line rectangle");
    } else if (shape_kind == GEOMETRY_ELLIPSE) {
        require_cuda(cudaOccupancyMaxActiveBlocksPerMultiprocessor(&blocks_per_sm, resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_ELLIPSE>, 256, 0), "resident device occupancy ellipse");
    } else {
        require_cuda(cudaOccupancyMaxActiveBlocksPerMultiprocessor(&blocks_per_sm, resident_rotated_geometry_coop_chunk_kernel<GEOMETRY_RUNTIME>, 256, 0), "resident device occupancy runtime");
    }
    return blocks_per_sm;
}

template <class T>
static bool read_stdin_value(T& value) {
    std::cin.read(reinterpret_cast<char*>(&value), sizeof(T));
    return static_cast<bool>(std::cin);
}

static void read_stdin_bytes(void* data, std::size_t bytes, const char* label) {
    if (bytes == 0) return;
    std::cin.read(reinterpret_cast<char*>(data), static_cast<std::streamsize>(bytes));
    if (!std::cin) {
        throw std::runtime_error(std::string("failed to read ") + label);
    }
}

template <class T>
static void write_stdout_value(const T& value) {
    std::cout.write(reinterpret_cast<const char*>(&value), sizeof(T));
    if (!std::cout) {
        throw std::runtime_error("failed to write stdout value");
    }
}

static void write_stdout_bytes(const void* data, std::size_t bytes) {
    if (bytes == 0) return;
    std::cout.write(reinterpret_cast<const char*>(data), static_cast<std::streamsize>(bytes));
    if (!std::cout) {
        throw std::runtime_error("failed to write stdout bytes");
    }
}

struct ServerState {
    Metadata metadata{};
    std::uint8_t* d_target = nullptr;
    std::uint8_t* d_current = nullptr;
    std::uint16_t* d_weights = nullptr;
    std::uint16_t* d_structural_distance = nullptr;
    std::uint16_t* d_structural_tangent = nullptr;
    std::vector<std::uint16_t> h_weights;
    std::uint16_t max_weight = 0;
    std::vector<std::uint16_t> h_stroke_saliency;
    std::vector<std::uint16_t> h_stroke_tangent;
    std::uint16_t max_stroke_saliency = 0;
    std::vector<std::uint16_t> h_detail_saliency;
    std::vector<std::uint16_t> h_contour_saliency;
    std::vector<float> h_detail_alias_probability;
    std::vector<float> h_contour_alias_probability;
    std::vector<std::uint32_t> h_detail_alias_index;
    std::vector<std::uint32_t> h_contour_alias_index;
    CandidateRecord* d_candidates = nullptr;
    ScoreRecord* d_output = nullptr;
    std::uint8_t* d_catalog_mask = nullptr;
    std::size_t candidateCapacity = 0;
    std::size_t outputCapacity = 0;
    std::size_t catalogMaskCapacity = 0;

    ~ServerState() {
        cudaFree(d_target);
        cudaFree(d_current);
        cudaFree(d_weights);
        cudaFree(d_structural_distance);
        cudaFree(d_structural_tangent);
        cudaFree(d_candidates);
        cudaFree(d_output);
        cudaFree(d_catalog_mask);
    }

    void ensure_buffers(std::size_t candidate_count) {
        if (candidate_count > candidateCapacity) {
            cudaFree(d_candidates);
            require_cuda(cudaMalloc(&d_candidates, std::max<std::size_t>(candidate_count, 1) * sizeof(CandidateRecord)), "server cudaMalloc candidates");
            candidateCapacity = candidate_count;
        }
        if (candidate_count > outputCapacity) {
            cudaFree(d_output);
            require_cuda(cudaMalloc(&d_output, std::max<std::size_t>(candidate_count, 1) * sizeof(ScoreRecord)), "server cudaMalloc output");
            outputCapacity = candidate_count;
        }
    }

    void ensure_catalog_mask(std::size_t bytes) {
        if (bytes <= catalogMaskCapacity) return;
        cudaFree(d_catalog_mask);
        require_cuda(cudaMalloc(&d_catalog_mask, bytes), "server cudaMalloc catalog mask");
        catalogMaskCapacity = bytes;
    }
};

static void server_init(ServerState& state) {
    std::uint32_t width = 0;
    std::uint32_t height = 0;
    std::uint64_t base_total_error = 0;
    if (!read_stdin_value(width) || !read_stdin_value(height) || !read_stdin_value(base_total_error)) {
        throw std::runtime_error("invalid INIT payload");
    }
    const std::size_t image_bytes = static_cast<std::size_t>(width) * height * 4;
    std::vector<std::uint8_t> target(image_bytes);
    std::vector<std::uint8_t> current(image_bytes);
    read_stdin_bytes(target.data(), target.size(), "INIT target");
    read_stdin_bytes(current.data(), current.size(), "INIT current");

    cudaFree(state.d_target);
    cudaFree(state.d_current);
    cudaFree(state.d_weights);
    cudaFree(state.d_structural_distance);
    cudaFree(state.d_structural_tangent);
    state.h_weights.clear();
    state.max_weight = 0;
    state.h_stroke_saliency.clear();
    state.h_stroke_tangent.clear();
    state.max_stroke_saliency = 0;
    state.h_detail_saliency.clear();
    state.h_contour_saliency.clear();
    state.h_detail_alias_probability.clear();
    state.h_contour_alias_probability.clear();
    state.h_detail_alias_index.clear();
    state.h_contour_alias_index.clear();
    require_cuda(cudaMalloc(&state.d_target, image_bytes), "server cudaMalloc target");
    require_cuda(cudaMalloc(&state.d_current, image_bytes), "server cudaMalloc current");
    state.d_weights = nullptr;
    state.d_structural_distance = nullptr;
    state.d_structural_tangent = nullptr;

    const auto h2d_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(state.d_target, target.data(), target.size(), cudaMemcpyHostToDevice), "server cudaMemcpy target");
    require_cuda(cudaMemcpy(state.d_current, current.data(), current.size(), cudaMemcpyHostToDevice), "server cudaMemcpy current");
    const auto h2d_end = std::chrono::steady_clock::now();

    state.metadata.width = static_cast<int>(width);
    state.metadata.height = static_cast<int>(height);
    state.metadata.baseTotalError = base_total_error;

    const std::uint32_t status = 0;
    const double image_h2d_ms = elapsed_ms(h2d_start, h2d_end);
    write_stdout_value(status);
    write_stdout_value(image_h2d_ms);
    std::cout.flush();
}

static void server_score_batch_geometry(ServerState& state) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("SCORE_BATCH_GEOMETRY before INIT");
    }
    std::uint32_t candidate_count_u32 = 0;
    if (!read_stdin_value(candidate_count_u32)) {
        throw std::runtime_error("invalid SCORE_BATCH_GEOMETRY payload");
    }
    const std::size_t candidate_count = candidate_count_u32;
    std::vector<CandidateRecord> candidates(candidate_count);
    read_stdin_bytes(candidates.data(), candidates.size() * sizeof(CandidateRecord), "SCORE_BATCH_GEOMETRY candidates");

    const auto total_start = std::chrono::steady_clock::now();
    state.ensure_buffers(candidate_count);
    const auto h2d_start = std::chrono::steady_clock::now();
    if (!candidates.empty()) {
        require_cuda(cudaMemcpy(state.d_candidates, candidates.data(), candidates.size() * sizeof(CandidateRecord), cudaMemcpyHostToDevice), "server cudaMemcpy geometry candidates");
    }
    const auto h2d_end = std::chrono::steady_clock::now();

    Metadata batch_metadata = state.metadata;
    batch_metadata.candidateCount = candidate_count_u32;
    const float kernel_ms = run_score_geometry_kernel(
        state.d_target,
        state.d_current,
        state.d_candidates,
        state.d_output,
        batch_metadata,
        candidate_count);

    std::vector<ScoreRecord> output(candidate_count);
    const auto d2h_start = std::chrono::steady_clock::now();
    if (!output.empty()) {
        require_cuda(cudaMemcpy(output.data(), state.d_output, output.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "server cudaMemcpy geometry output");
    }
    const auto d2h_end = std::chrono::steady_clock::now();
    const auto total_end = std::chrono::steady_clock::now();

    const std::uint32_t status = 0;
    const double h2d_ms = elapsed_ms(h2d_start, h2d_end);
    const double d2h_ms = elapsed_ms(d2h_start, d2h_end);
    const double total_ms = elapsed_ms(total_start, total_end);
    write_stdout_value(status);
    write_stdout_value(candidate_count_u32);
    write_stdout_value(h2d_ms);
    write_stdout_value(static_cast<double>(kernel_ms));
    write_stdout_value(d2h_ms);
    write_stdout_value(total_ms);
    write_stdout_bytes(output.data(), output.size() * sizeof(ScoreRecord));
    std::cout.flush();
}

static const char* rotated_geometry_command_name(int shape_kind, bool weighted) {
    if (shape_kind == GEOMETRY_RECTANGLE) {
        return weighted ? "SCORE_BATCH_ROTATED_RECT_GEOMETRY_WEIGHTED" : "SCORE_BATCH_ROTATED_RECT_GEOMETRY";
    }
    if (shape_kind == GEOMETRY_TRIANGLE) {
        return weighted ? "SCORE_BATCH_ROTATED_TRIANGLE_GEOMETRY_WEIGHTED" : "SCORE_BATCH_ROTATED_TRIANGLE_GEOMETRY";
    }
    return weighted ? "SCORE_BATCH_ROTATED_GEOMETRY_WEIGHTED" : "SCORE_BATCH_ROTATED_GEOMETRY";
}

static void server_score_batch_rotated_geometry(ServerState& state, int shape_kind = GEOMETRY_ELLIPSE) {
    const char* command_name = rotated_geometry_command_name(shape_kind, false);
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error(std::string(command_name) + " before INIT");
    }
    std::uint32_t candidate_count_u32 = 0;
    if (!read_stdin_value(candidate_count_u32)) {
        throw std::runtime_error(std::string("invalid ") + command_name + " payload");
    }
    const std::size_t candidate_count = candidate_count_u32;
    std::vector<RotatedCandidateRecord> candidates(candidate_count);
    const std::string candidate_label = std::string(command_name) + " candidates";
    read_stdin_bytes(candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), candidate_label.c_str());

    const auto total_start = std::chrono::steady_clock::now();
    state.ensure_buffers(candidate_count);
    const auto h2d_start = std::chrono::steady_clock::now();
    if (!candidates.empty()) {
        require_cuda(cudaMemcpy(state.d_candidates, candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "server cudaMemcpy rotated geometry candidates");
    }
    const auto h2d_end = std::chrono::steady_clock::now();

    Metadata batch_metadata = state.metadata;
    batch_metadata.candidateCount = candidate_count_u32;
    const float kernel_ms = run_score_rotated_geometry_kernel(
        state.d_target,
        state.d_current,
        reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
        state.d_output,
        batch_metadata,
        candidate_count,
        shape_kind);

    std::vector<ScoreRecord> output(candidate_count);
    const auto d2h_start = std::chrono::steady_clock::now();
    if (!output.empty()) {
        require_cuda(cudaMemcpy(output.data(), state.d_output, output.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "server cudaMemcpy rotated geometry output");
    }
    const auto d2h_end = std::chrono::steady_clock::now();
    const auto total_end = std::chrono::steady_clock::now();

    const std::uint32_t status = 0;
    const double h2d_ms = elapsed_ms(h2d_start, h2d_end);
    const double d2h_ms = elapsed_ms(d2h_start, d2h_end);
    const double total_ms = elapsed_ms(total_start, total_end);
    write_stdout_value(status);
    write_stdout_value(candidate_count_u32);
    write_stdout_value(h2d_ms);
    write_stdout_value(static_cast<double>(kernel_ms));
    write_stdout_value(d2h_ms);
    write_stdout_value(total_ms);
    write_stdout_bytes(output.data(), output.size() * sizeof(ScoreRecord));
    std::cout.flush();
}

static void server_set_weight_map(ServerState& state) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("SET_WEIGHT_MAP before INIT");
    }
    const std::size_t pixels = static_cast<std::size_t>(state.metadata.width) * state.metadata.height;
    std::vector<std::uint16_t> weights(pixels);
    read_stdin_bytes(weights.data(), weights.size() * sizeof(std::uint16_t), "SET_WEIGHT_MAP weights");
    state.h_weights = weights;
    state.max_weight = weights.empty() ? 0 : *std::max_element(weights.begin(), weights.end());
    cudaFree(state.d_weights);
    require_cuda(cudaMalloc(&state.d_weights, weights.size() * sizeof(std::uint16_t)), "server cudaMalloc weights");
    const auto h2d_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(state.d_weights, weights.data(), weights.size() * sizeof(std::uint16_t), cudaMemcpyHostToDevice), "server cudaMemcpy weights");
    const auto h2d_end = std::chrono::steady_clock::now();
    const std::uint32_t status = 0;
    const double h2d_ms = elapsed_ms(h2d_start, h2d_end);
    write_stdout_value(status);
    write_stdout_value(h2d_ms);
    std::cout.flush();
}

static void server_set_stroke_guide(ServerState& state) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("SET_STROKE_GUIDE before INIT");
    }
    const auto start = std::chrono::steady_clock::now();
    const std::size_t pixels = static_cast<std::size_t>(state.metadata.width) * state.metadata.height;
    state.h_stroke_saliency.resize(pixels);
    state.h_stroke_tangent.resize(pixels);
    read_stdin_bytes(state.h_stroke_saliency.data(), pixels * sizeof(std::uint16_t), "SET_STROKE_GUIDE saliency");
    read_stdin_bytes(state.h_stroke_tangent.data(), pixels * sizeof(std::uint16_t), "SET_STROKE_GUIDE tangent");
    state.max_stroke_saliency = state.h_stroke_saliency.empty() ? 0 : *std::max_element(state.h_stroke_saliency.begin(), state.h_stroke_saliency.end());
    const std::uint32_t status = 0;
    const double elapsed = elapsed_ms(start, std::chrono::steady_clock::now());
    write_stdout_value(status);
    write_stdout_value(elapsed);
    std::cout.flush();
}

static void build_alias_table(
    const std::vector<std::uint16_t>& weights,
    std::vector<float>& probability,
    std::vector<std::uint32_t>& alias) {
    std::uint64_t total = 0;
    for (const std::uint16_t weight : weights) total += weight;
    if (weights.empty() || total == 0) {
        probability.clear();
        alias.clear();
        return;
    }

    const std::size_t count = weights.size();
    probability.resize(count);
    alias.resize(count);
    std::vector<double> scaled(count);
    std::vector<std::uint32_t> underfull;
    std::vector<std::uint32_t> overfull;
    underfull.reserve(count);
    overfull.reserve(count);
    for (std::size_t index = 0; index < count; index++) {
        scaled[index] = static_cast<double>(weights[index]) * count / static_cast<double>(total);
        (scaled[index] < 1.0 ? underfull : overfull).push_back(static_cast<std::uint32_t>(index));
    }
    while (!underfull.empty() && !overfull.empty()) {
        const std::uint32_t low = underfull.back();
        underfull.pop_back();
        const std::uint32_t high = overfull.back();
        overfull.pop_back();
        probability[low] = static_cast<float>(scaled[low]);
        alias[low] = high;
        scaled[high] = scaled[high] + scaled[low] - 1.0;
        (scaled[high] < 1.0 ? underfull : overfull).push_back(high);
    }
    for (const std::uint32_t index : overfull) {
        probability[index] = 1.0f;
        alias[index] = index;
    }
    for (const std::uint32_t index : underfull) {
        probability[index] = 1.0f;
        alias[index] = index;
    }
}

static void server_set_multi_scale_stroke_guide(ServerState& state) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("SET_MULTI_SCALE_STROKE_GUIDE before INIT");
    }
    const auto start = std::chrono::steady_clock::now();
    const std::size_t pixels = static_cast<std::size_t>(state.metadata.width) * state.metadata.height;
    state.h_detail_saliency.resize(pixels);
    state.h_contour_saliency.resize(pixels);
    state.h_stroke_tangent.resize(pixels);
    read_stdin_bytes(state.h_detail_saliency.data(), pixels * sizeof(std::uint16_t), "SET_MULTI_SCALE_STROKE_GUIDE detail");
    read_stdin_bytes(state.h_contour_saliency.data(), pixels * sizeof(std::uint16_t), "SET_MULTI_SCALE_STROKE_GUIDE contour");
    read_stdin_bytes(state.h_stroke_tangent.data(), pixels * sizeof(std::uint16_t), "SET_MULTI_SCALE_STROKE_GUIDE tangent");
    build_alias_table(state.h_detail_saliency, state.h_detail_alias_probability, state.h_detail_alias_index);
    build_alias_table(state.h_contour_saliency, state.h_contour_alias_probability, state.h_contour_alias_index);
    const std::uint32_t status = 0;
    const double elapsed = elapsed_ms(start, std::chrono::steady_clock::now());
    write_stdout_value(status);
    write_stdout_value(elapsed);
    std::cout.flush();
}

static void server_set_structural_guide(ServerState& state) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("SET_STRUCTURAL_GUIDE before INIT");
    }
    const auto start = std::chrono::steady_clock::now();
    const std::size_t pixels = static_cast<std::size_t>(state.metadata.width) * state.metadata.height;
    std::vector<std::uint16_t> distance(pixels);
    std::vector<std::uint16_t> tangent(pixels);
    read_stdin_bytes(distance.data(), pixels * sizeof(std::uint16_t), "SET_STRUCTURAL_GUIDE distance");
    read_stdin_bytes(tangent.data(), pixels * sizeof(std::uint16_t), "SET_STRUCTURAL_GUIDE tangent");
    cudaFree(state.d_structural_distance);
    cudaFree(state.d_structural_tangent);
    require_cuda(cudaMalloc(&state.d_structural_distance, pixels * sizeof(std::uint16_t)), "server cudaMalloc structural distance");
    require_cuda(cudaMalloc(&state.d_structural_tangent, pixels * sizeof(std::uint16_t)), "server cudaMalloc structural tangent");
    require_cuda(cudaMemcpy(state.d_structural_distance, distance.data(), pixels * sizeof(std::uint16_t), cudaMemcpyHostToDevice), "server cudaMemcpy structural distance");
    require_cuda(cudaMemcpy(state.d_structural_tangent, tangent.data(), pixels * sizeof(std::uint16_t), cudaMemcpyHostToDevice), "server cudaMemcpy structural tangent");
    const std::uint32_t status = 0;
    const double elapsed = elapsed_ms(start, std::chrono::steady_clock::now());
    write_stdout_value(status);
    write_stdout_value(elapsed);
    std::cout.flush();
}

static void server_score_batch_rotated_geometry_weighted(ServerState& state, int shape_kind = GEOMETRY_ELLIPSE) {
    const char* command_name = rotated_geometry_command_name(shape_kind, true);
    if (!state.d_target || !state.d_current || !state.d_weights) {
        throw std::runtime_error(std::string(command_name) + " before INIT/SET_WEIGHT_MAP");
    }
    std::uint32_t candidate_count_u32 = 0;
    if (!read_stdin_value(candidate_count_u32)) {
        throw std::runtime_error(std::string("invalid ") + command_name + " payload");
    }
    const std::size_t candidate_count = candidate_count_u32;
    std::vector<RotatedCandidateRecord> candidates(candidate_count);
    const std::string candidate_label = std::string(command_name) + " candidates";
    read_stdin_bytes(candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), candidate_label.c_str());

    const auto total_start = std::chrono::steady_clock::now();
    state.ensure_buffers(candidate_count);
    const auto h2d_start = std::chrono::steady_clock::now();
    if (!candidates.empty()) {
        require_cuda(cudaMemcpy(state.d_candidates, candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "server cudaMemcpy weighted rotated candidates");
    }
    const auto h2d_end = std::chrono::steady_clock::now();

    Metadata batch_metadata = state.metadata;
    batch_metadata.candidateCount = candidate_count_u32;
    const float kernel_ms = run_score_rotated_geometry_weighted_kernel(
        state.d_target,
        state.d_current,
        state.d_weights,
        reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
        state.d_output,
        batch_metadata,
        candidate_count,
        shape_kind);

    std::vector<ScoreRecord> output(candidate_count);
    const auto d2h_start = std::chrono::steady_clock::now();
    if (!output.empty()) {
        require_cuda(cudaMemcpy(output.data(), state.d_output, output.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "server cudaMemcpy weighted rotated output");
    }
    const auto d2h_end = std::chrono::steady_clock::now();
    const auto total_end = std::chrono::steady_clock::now();

    const std::uint32_t status = 0;
    const double h2d_ms = elapsed_ms(h2d_start, h2d_end);
    const double d2h_ms = elapsed_ms(d2h_start, d2h_end);
    const double total_ms = elapsed_ms(total_start, total_end);
    write_stdout_value(status);
    write_stdout_value(candidate_count_u32);
    write_stdout_value(h2d_ms);
    write_stdout_value(static_cast<double>(kernel_ms));
    write_stdout_value(d2h_ms);
    write_stdout_value(total_ms);
    write_stdout_bytes(output.data(), output.size() * sizeof(ScoreRecord));
    std::cout.flush();
}

static void server_score_batch_catalog_geometry(ServerState& state, bool weighted) {
    if (!state.d_target || !state.d_current || (weighted && !state.d_weights)) {
        throw std::runtime_error("SCORE_BATCH_CATALOG_GEOMETRY before INIT/SET_WEIGHT_MAP");
    }
    std::uint32_t candidate_count_u32 = 0;
    CatalogMaskMetadata metadata{};
    if (!read_stdin_value(candidate_count_u32) || !read_stdin_value(metadata)) {
        throw std::runtime_error("invalid SCORE_BATCH_CATALOG_GEOMETRY payload");
    }
    const std::size_t candidate_count = candidate_count_u32;
    const std::size_t mask_bytes = static_cast<std::size_t>(metadata.size) * metadata.size;
    if (metadata.size < 32 || metadata.size > 1024 || metadata.intrinsicWidth <= 0 || metadata.intrinsicHeight <= 0 ||
        metadata.maxX <= metadata.minX || metadata.maxY <= metadata.minY) {
        throw std::runtime_error("invalid catalog mask metadata");
    }
    std::vector<std::uint8_t> mask(mask_bytes);
    std::vector<RotatedCandidateRecord> candidates(candidate_count);
    read_stdin_bytes(mask.data(), mask.size(), "catalog mask");
    read_stdin_bytes(candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), "catalog candidates");

    const auto total_start = std::chrono::steady_clock::now();
    state.ensure_buffers(candidate_count);
    state.ensure_catalog_mask(mask_bytes);
    const auto h2d_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(state.d_catalog_mask, mask.data(), mask.size(), cudaMemcpyHostToDevice), "server cudaMemcpy catalog mask");
    if (!candidates.empty()) {
        require_cuda(cudaMemcpy(state.d_candidates, candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "server cudaMemcpy catalog candidates");
    }
    const auto h2d_end = std::chrono::steady_clock::now();
    Metadata batch_metadata = state.metadata;
    batch_metadata.candidateCount = candidate_count_u32;
    const float kernel_ms = run_score_catalog_geometry_kernel(
        state.d_target,
        state.d_current,
        weighted ? state.d_weights : nullptr,
        reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
        state.d_output,
        batch_metadata,
        candidate_count,
        state.d_catalog_mask,
        metadata);
    std::vector<ScoreRecord> scores(candidate_count);
    const auto d2h_start = std::chrono::steady_clock::now();
    if (!scores.empty()) {
        require_cuda(cudaMemcpy(scores.data(), state.d_output, scores.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "server cudaMemcpy catalog output");
    }
    const auto d2h_end = std::chrono::steady_clock::now();
    const std::uint32_t status = 0;
    write_stdout_value(status);
    write_stdout_value(candidate_count_u32);
    write_stdout_value(elapsed_ms(h2d_start, h2d_end));
    write_stdout_value(static_cast<double>(kernel_ms));
    write_stdout_value(elapsed_ms(d2h_start, d2h_end));
    write_stdout_value(elapsed_ms(total_start, std::chrono::steady_clock::now()));
    write_stdout_bytes(scores.data(), scores.size() * sizeof(ScoreRecord));
    std::cout.flush();
}

struct ResidentRandom {
    std::uint32_t state = 0;
    bool hasSpare = false;
    double spare = 0.0;

    explicit ResidentRandom(std::uint32_t seed) : state(seed) {}

    double nextFloat() {
        state = state * 1664525u + 1013904223u;
        return static_cast<double>(state) / 4294967296.0;
    }

    int intn(int max) {
        return static_cast<int>(std::floor(nextFloat() * static_cast<double>(max)));
    }

    double normal() {
        if (hasSpare) {
            hasSpare = false;
            return spare;
        }
        const double u = std::max(std::numeric_limits<double>::epsilon(), nextFloat());
        const double v = nextFloat();
        const double radius = std::sqrt(-2.0 * std::log(u));
        const double theta = 2.0 * 3.1415926535897932384626433832795 * v;
        spare = radius * std::sin(theta);
        hasSpare = true;
        return radius * std::cos(theta);
    }
};

struct ResidentChain {
    int group = 0;
    int shapeKind = GEOMETRY_ELLIPSE;
    RotatedCandidateRecord state{};
    ScoreRecord score{};
    ResidentRandom rng;
    std::uint32_t remainingAge = 0;
    std::uint32_t steps = 0;
    bool lockShortAxis = false;
    int minLongAxis = 0;
    int maxLongAxis = 0;
    double structuralObjective = 0.0;

    ResidentChain(int group_in, const RotatedCandidateRecord& state_in, const ScoreRecord& score_in, std::uint32_t seed, std::uint32_t age, int shape_kind = GEOMETRY_ELLIPSE, bool lock_short_axis = false, int min_long_axis = 0, int max_long_axis = 0)
        : group(group_in), shapeKind(shape_kind), state(state_in), score(score_in), rng(seed), remainingAge(age), lockShortAxis(lock_short_axis), minLongAxis(min_long_axis), maxLongAxis(max_long_axis) {
        state.alpha = static_cast<std::int32_t>(score.a);
        state.reserved = static_cast<std::uint32_t>(group);
    }
};

static int clamp_int(int value, int min_value, int max_value) {
    return value < min_value ? min_value : value > max_value ? max_value : value;
}

static float wrap_angle_180(double value) {
    double out = std::fmod(value, 180.0);
    if (out < 0) out += 180.0;
    return static_cast<float>(out);
}

static RotatedCandidateRecord mutate_resident_state(
    const ResidentChain& chain,
    ResidentRandom& random,
    int width,
    int height,
    int min_axis,
    bool mutate_alpha,
    int min_alpha,
    int max_alpha,
    std::uint32_t candidate_id) {
    RotatedCandidateRecord proposal = chain.state;
    const int choice = random.intn(chain.lockShortAxis ? 3 : 4);
    if (choice == 0) {
        proposal.cx = clamp_int(proposal.cx + static_cast<int>(std::trunc(random.normal() * 16.0)), 0, width - 1);
        proposal.cy = clamp_int(proposal.cy + static_cast<int>(std::trunc(random.normal() * 16.0)), 0, height - 1);
    } else if (choice == 1) {
        const int minimum = chain.minLongAxis > 0 ? chain.minLongAxis : min_axis;
        const int maximum = chain.maxLongAxis > 0 ? chain.maxLongAxis : width;
        proposal.rx = clamp_int(proposal.rx + static_cast<int>(std::trunc(random.normal() * 16.0)), minimum, maximum);
    } else if (choice == 2 && !chain.lockShortAxis) {
        proposal.ry = clamp_int(proposal.ry + static_cast<int>(std::trunc(random.normal() * 16.0)), min_axis, height);
    } else {
        proposal.angleDegrees = wrap_angle_180(static_cast<double>(proposal.angleDegrees) + static_cast<int>(std::trunc(random.normal() * 15.0)));
    }
    if (mutate_alpha) {
        proposal.alpha = clamp_int(proposal.alpha + random.intn(21) - 10, min_alpha, max_alpha);
    } else {
        proposal.alpha = clamp_int(proposal.alpha, min_alpha, max_alpha);
    }
    proposal.candidateId = candidate_id;
    proposal.reserved = static_cast<std::uint32_t>(chain.group);
    return proposal;
}

static void run_resident_rotated_device_chunks(
    ServerState& state,
    std::vector<ResidentChain>& chains,
    std::uint32_t age,
    std::uint32_t fanout,
    std::uint32_t early_stop_rounds,
    std::uint32_t max_hill_steps,
    std::uint32_t min_axis,
    bool weighted,
    bool mutate_alpha,
    std::uint32_t min_alpha,
    std::uint32_t max_alpha,
    std::uint32_t chunk_rounds,
    std::uint32_t& rounds,
    std::uint32_t& rounds_without_accept,
    std::uint32_t& proposal_scores,
    std::uint32_t& scorer_calls,
    std::uint32_t& accepted_mutations,
    bool& early_stop,
    double& h2d_ms,
    double& kernel_ms,
    double& d2h_ms) {
    if (chains.empty()) return;
    if (fanout == 0 || fanout > RESIDENT_DEVICE_FANOUT_MAX) {
        throw std::runtime_error("resident device chunk requires fanout in 1..8");
    }
    if (chunk_rounds == 0) {
        chunk_rounds = 1;
    }

    std::vector<ResidentDeviceChain> host_chains(chains.size());
    for (std::size_t i = 0; i < chains.size(); i++) {
        host_chains[i].group = chains[i].group;
        host_chains[i].shapeKind = chains[i].shapeKind;
        host_chains[i].state = chains[i].state;
        host_chains[i].score = chains[i].score;
        host_chains[i].rng.state = chains[i].rng.state;
        host_chains[i].rng.hasSpare = chains[i].rng.hasSpare ? 1 : 0;
        host_chains[i].rng.spare = chains[i].rng.spare;
        host_chains[i].remainingAge = chains[i].remainingAge;
        host_chains[i].steps = chains[i].steps;
        host_chains[i].lockShortAxis = chains[i].lockShortAxis ? 1u : 0u;
        host_chains[i].minLongAxis = chains[i].minLongAxis;
        host_chains[i].maxLongAxis = chains[i].maxLongAxis;
    }
    int device_shape_kind = host_chains[0].shapeKind;
    for (const ResidentDeviceChain& chain : host_chains) {
        if (chain.shapeKind != device_shape_kind) {
            device_shape_kind = GEOMETRY_RUNTIME;
            break;
        }
    }

    ResidentDeviceChain* d_chains = nullptr;
    RotatedCandidateRecord* d_proposals = nullptr;
    ScoreRecord* d_scores = nullptr;
    std::uint32_t* d_active_flags = nullptr;
    ResidentDeviceChunkStats* d_stats = nullptr;
    try {
        cudaDeviceProp prop{};
        require_cuda(cudaGetDeviceProperties(&prop, 0), "resident device cudaGetDeviceProperties");
        if (!prop.cooperativeLaunch) {
            throw std::runtime_error("resident device chunk requires cooperativeLaunch support");
        }
        const int blocks_per_sm = resident_device_blocks_per_sm(device_shape_kind);
        const std::size_t proposal_capacity = host_chains.size() * fanout;
        const std::size_t cooperative_block_limit = static_cast<std::size_t>(blocks_per_sm) * prop.multiProcessorCount;
        if (proposal_capacity > cooperative_block_limit) {
            throw std::runtime_error("resident device chunk cooperative grid is too large");
        }
        require_cuda(cudaMalloc(&d_chains, host_chains.size() * sizeof(ResidentDeviceChain)), "resident device cudaMalloc chains");
        require_cuda(cudaMalloc(&d_proposals, proposal_capacity * sizeof(RotatedCandidateRecord)), "resident device cudaMalloc proposals");
        require_cuda(cudaMalloc(&d_scores, proposal_capacity * sizeof(ScoreRecord)), "resident device cudaMalloc scores");
        require_cuda(cudaMalloc(&d_active_flags, host_chains.size() * sizeof(std::uint32_t)), "resident device cudaMalloc active flags");
        require_cuda(cudaMalloc(&d_stats, sizeof(ResidentDeviceChunkStats)), "resident device cudaMalloc stats");
        const auto h2d_start = std::chrono::steady_clock::now();
        require_cuda(cudaMemcpy(d_chains, host_chains.data(), host_chains.size() * sizeof(ResidentDeviceChain), cudaMemcpyHostToDevice), "resident device cudaMemcpy chains");
        const auto h2d_end = std::chrono::steady_clock::now();
        h2d_ms += elapsed_ms(h2d_start, h2d_end);

        while (true) {
            if (rounds_without_accept >= early_stop_rounds) {
                early_stop = true;
                break;
            }
            require_cuda(cudaMemset(d_stats, 0, sizeof(ResidentDeviceChunkStats)), "resident device cudaMemset stats");
            kernel_ms += run_resident_rotated_geometry_device_chunk_kernel(
                state.d_target,
                state.d_current,
                state.d_weights,
                d_chains,
                d_proposals,
                d_scores,
                nullptr,
                d_active_flags,
                d_stats,
                nullptr,
                nullptr,
                nullptr,
                state.metadata,
                host_chains.size(),
                age,
                fanout,
                chunk_rounds,
                rounds,
                max_hill_steps,
                static_cast<int>(min_axis),
                weighted,
                mutate_alpha,
                static_cast<int>(min_alpha),
                static_cast<int>(max_alpha),
                device_shape_kind);

            ResidentDeviceChunkStats stats{};
            const auto stats_d2h_start = std::chrono::steady_clock::now();
            require_cuda(cudaMemcpy(&stats, d_stats, sizeof(stats), cudaMemcpyDeviceToHost), "resident device cudaMemcpy stats");
            const auto stats_d2h_end = std::chrono::steady_clock::now();
            d2h_ms += elapsed_ms(stats_d2h_start, stats_d2h_end);

            if (stats.activeChains == 0 || stats.rounds == 0) {
                break;
            }
            proposal_scores += stats.proposalScores;
            accepted_mutations += stats.acceptedMutations;
            rounds += stats.rounds;
            if (stats.proposalScores > 0) {
                scorer_calls++;
            }
            if (stats.acceptedMutations > 0) {
                rounds_without_accept = stats.rounds > stats.lastAcceptRound ? stats.rounds - stats.lastAcceptRound : 0;
            } else {
                rounds_without_accept += stats.rounds;
            }
        }

        const auto chains_d2h_start = std::chrono::steady_clock::now();
        require_cuda(cudaMemcpy(host_chains.data(), d_chains, host_chains.size() * sizeof(ResidentDeviceChain), cudaMemcpyDeviceToHost), "resident device cudaMemcpy final chains");
        const auto chains_d2h_end = std::chrono::steady_clock::now();
        d2h_ms += elapsed_ms(chains_d2h_start, chains_d2h_end);
    } catch (...) {
        cudaFree(d_chains);
        cudaFree(d_proposals);
        cudaFree(d_scores);
        cudaFree(d_active_flags);
        cudaFree(d_stats);
        throw;
    }
    cudaFree(d_chains);
    cudaFree(d_proposals);
    cudaFree(d_scores);
    cudaFree(d_active_flags);
    cudaFree(d_stats);

    for (std::size_t i = 0; i < chains.size(); i++) {
        chains[i].shapeKind = host_chains[i].shapeKind;
        chains[i].state = host_chains[i].state;
        chains[i].score = host_chains[i].score;
        chains[i].rng.state = host_chains[i].rng.state;
        chains[i].rng.hasSpare = host_chains[i].rng.hasSpare != 0;
        chains[i].rng.spare = host_chains[i].rng.spare;
        chains[i].remainingAge = host_chains[i].remainingAge;
        chains[i].steps = host_chains[i].steps;
    }
}

struct ResidentDeviceShapeGroup {
    int shapeKind = GEOMETRY_ELLIPSE;
    std::vector<std::size_t> sourceIndices;
    std::vector<ResidentDeviceChain> hostChains;
    ResidentDeviceChain* dChains = nullptr;
    RotatedCandidateRecord* dProposals = nullptr;
    ScoreRecord* dScores = nullptr;
    double* dStructuralObjectives = nullptr;
    std::uint32_t* dActiveFlags = nullptr;
    ResidentDeviceChunkStats* dStats = nullptr;
    StructuralSearchState* dStructuralSearch = nullptr;
};

static void free_resident_device_shape_groups(std::vector<ResidentDeviceShapeGroup>& groups) {
    for (ResidentDeviceShapeGroup& group : groups) {
        cudaFree(group.dChains);
        cudaFree(group.dProposals);
        cudaFree(group.dScores);
        cudaFree(group.dStructuralObjectives);
        cudaFree(group.dActiveFlags);
        cudaFree(group.dStats);
        cudaFree(group.dStructuralSearch);
        group.dChains = nullptr;
        group.dProposals = nullptr;
        group.dScores = nullptr;
        group.dStructuralObjectives = nullptr;
        group.dActiveFlags = nullptr;
        group.dStats = nullptr;
        group.dStructuralSearch = nullptr;
    }
}

static void run_resident_mixed_device_chunks(
    ServerState& state,
    std::vector<ResidentChain>& chains,
    const std::vector<int>& shapes,
    std::uint32_t age,
    std::uint32_t fanout,
    std::uint32_t early_stop_rounds,
    std::uint32_t max_hill_steps,
    std::uint32_t min_axis,
    bool weighted,
    bool mutate_alpha,
    std::uint32_t min_alpha,
    std::uint32_t max_alpha,
    std::uint32_t chunk_rounds,
    std::uint32_t& rounds,
    std::uint32_t& rounds_without_accept,
    std::uint32_t& proposal_scores,
    std::uint32_t& scorer_calls,
    std::uint32_t& accepted_mutations,
    bool& early_stop,
    double& h2d_ms,
    double& kernel_ms,
    double& d2h_ms,
    bool structural_mode,
    std::uint32_t structural_edge_weight_q16,
    std::uint32_t structural_distance_limit,
    std::uint32_t structural_rounds,
    std::uint32_t max_pixel_gain_regression_q16) {
    if (chains.empty()) return;
    if (fanout == 0 || fanout > RESIDENT_DEVICE_FANOUT_MAX) {
        throw std::runtime_error("resident mixed device chunk requires fanout in 1..8");
    }
    if (chunk_rounds == 0) {
        chunk_rounds = 1;
    }

    cudaDeviceProp prop{};
    require_cuda(cudaGetDeviceProperties(&prop, 0), "resident mixed device cudaGetDeviceProperties");
    if (!prop.cooperativeLaunch) {
        throw std::runtime_error("resident mixed device chunk requires cooperativeLaunch support");
    }

    std::vector<ResidentDeviceShapeGroup> groups;
    groups.reserve(shapes.size());
    for (const int shape_kind : shapes) {
        ResidentDeviceShapeGroup group{};
        group.shapeKind = shape_kind;
        group.sourceIndices.reserve(chains.size());
        group.hostChains.reserve(chains.size());
        for (std::size_t i = 0; i < chains.size(); i++) {
            if (chains[i].shapeKind != shape_kind) continue;
            ResidentDeviceChain device_chain{};
            device_chain.group = chains[i].group;
            device_chain.shapeKind = chains[i].shapeKind;
            device_chain.state = chains[i].state;
            device_chain.score = chains[i].score;
            device_chain.rng.state = chains[i].rng.state;
            device_chain.rng.hasSpare = chains[i].rng.hasSpare ? 1 : 0;
            device_chain.rng.spare = chains[i].rng.spare;
            device_chain.remainingAge = chains[i].remainingAge;
            device_chain.steps = chains[i].steps;
            device_chain.lockShortAxis = chains[i].lockShortAxis ? 1u : 0u;
            device_chain.minLongAxis = chains[i].minLongAxis;
            device_chain.maxLongAxis = chains[i].maxLongAxis;
            group.sourceIndices.push_back(i);
            group.hostChains.push_back(device_chain);
        }
        if (!group.hostChains.empty()) {
            groups.push_back(std::move(group));
        }
    }

    try {
        for (ResidentDeviceShapeGroup& group : groups) {
            const int blocks_per_sm = resident_device_blocks_per_sm(group.shapeKind);
            const std::size_t proposal_capacity = group.hostChains.size() * fanout;
            const std::size_t cooperative_block_limit = static_cast<std::size_t>(blocks_per_sm) * prop.multiProcessorCount;
            if (proposal_capacity > cooperative_block_limit) {
                throw std::runtime_error("resident mixed device chunk cooperative grid is too large");
            }
            require_cuda(cudaMalloc(&group.dChains, group.hostChains.size() * sizeof(ResidentDeviceChain)), "resident mixed device cudaMalloc chains");
            require_cuda(cudaMalloc(&group.dProposals, proposal_capacity * sizeof(RotatedCandidateRecord)), "resident mixed device cudaMalloc proposals");
            require_cuda(cudaMalloc(&group.dScores, proposal_capacity * sizeof(ScoreRecord)), "resident mixed device cudaMalloc scores");
            if (structural_mode) {
                require_cuda(cudaMalloc(&group.dStructuralObjectives, proposal_capacity * sizeof(double)), "resident mixed device cudaMalloc structural objectives");
                require_cuda(cudaMalloc(&group.dStructuralSearch, sizeof(StructuralSearchState)), "resident mixed device cudaMalloc structural search");
            }
            require_cuda(cudaMalloc(&group.dActiveFlags, group.hostChains.size() * sizeof(std::uint32_t)), "resident mixed device cudaMalloc active flags");
            require_cuda(cudaMalloc(&group.dStats, sizeof(ResidentDeviceChunkStats)), "resident mixed device cudaMalloc stats");
            const auto group_h2d_start = std::chrono::steady_clock::now();
            require_cuda(cudaMemcpy(group.dChains, group.hostChains.data(), group.hostChains.size() * sizeof(ResidentDeviceChain), cudaMemcpyHostToDevice), "resident mixed device cudaMemcpy chains");
            const auto group_h2d_end = std::chrono::steady_clock::now();
            h2d_ms += elapsed_ms(group_h2d_start, group_h2d_end);
        }

        while (true) {
            if (rounds_without_accept >= early_stop_rounds) {
                early_stop = true;
                break;
            }

            const std::uint32_t remaining_before_stop = early_stop_rounds - rounds_without_accept;
            const std::uint32_t current_chunk_rounds = std::min(chunk_rounds, std::max(1u, remaining_before_stop));
            std::uint32_t chunk_rounds_done = 0;
            std::uint32_t chunk_accepts = 0;
            std::uint32_t chunk_last_accept_round = 0;
            bool chunk_active = false;

            for (ResidentDeviceShapeGroup& group : groups) {
                require_cuda(cudaMemset(group.dStats, 0, sizeof(ResidentDeviceChunkStats)), "resident mixed device cudaMemset stats");
                kernel_ms += run_resident_rotated_geometry_device_chunk_kernel(
                    state.d_target,
                    state.d_current,
                    state.d_weights,
                    group.dChains,
                    group.dProposals,
                    group.dScores,
                    nullptr,
                    group.dActiveFlags,
                    group.dStats,
                    nullptr,
                    nullptr,
                    nullptr,
                    state.metadata,
                    group.hostChains.size(),
                    age,
                    fanout,
                    current_chunk_rounds,
                    rounds,
                    max_hill_steps,
                    static_cast<int>(min_axis),
                    weighted,
                    mutate_alpha,
                    static_cast<int>(min_alpha),
                    static_cast<int>(max_alpha),
                    group.shapeKind);

                ResidentDeviceChunkStats stats{};
                const auto stats_d2h_start = std::chrono::steady_clock::now();
                require_cuda(cudaMemcpy(&stats, group.dStats, sizeof(stats), cudaMemcpyDeviceToHost), "resident mixed device cudaMemcpy stats");
                const auto stats_d2h_end = std::chrono::steady_clock::now();
                d2h_ms += elapsed_ms(stats_d2h_start, stats_d2h_end);

                if (stats.activeChains == 0 || stats.rounds == 0) {
                    continue;
                }
                chunk_active = true;
                chunk_rounds_done = std::max(chunk_rounds_done, stats.rounds);
                chunk_accepts += stats.acceptedMutations;
                chunk_last_accept_round = std::max(chunk_last_accept_round, stats.lastAcceptRound);
                proposal_scores += stats.proposalScores;
                accepted_mutations += stats.acceptedMutations;
                if (stats.proposalScores > 0) {
                    scorer_calls++;
                }
            }

            if (!chunk_active || chunk_rounds_done == 0) {
                break;
            }
            rounds += chunk_rounds_done;
            if (chunk_accepts > 0) {
                rounds_without_accept = chunk_rounds_done > chunk_last_accept_round ? chunk_rounds_done - chunk_last_accept_round : 0;
            } else {
            rounds_without_accept += chunk_rounds_done;
            }
        }

        if (structural_mode) {
            if (groups.size() != 1 || groups[0].shapeKind != GEOMETRY_LINE_RECTANGLE || !state.d_structural_distance || !state.d_structural_tangent) {
                throw std::runtime_error("structural refinement requires a resident line-rectangle group and structural guide");
            }
            ResidentDeviceShapeGroup& group = groups[0];
            const double base_energy = std::sqrt(static_cast<double>(state.metadata.baseTotalError) / static_cast<double>(state.metadata.width * state.metadata.height * 4)) / 255.0;
            const double edge_weight = static_cast<double>(structural_edge_weight_q16) / 65536.0;
            const double max_pixel_gain_regression = static_cast<double>(max_pixel_gain_regression_q16) / 65536.0;
            prepare_structural_chains_kernel<<<1, 1>>>(
                group.dChains,
                static_cast<std::uint32_t>(group.hostChains.size()),
                group.dStructuralSearch,
                state.d_structural_distance,
                state.d_structural_tangent,
                state.metadata.width,
                state.metadata.height,
                base_energy,
                static_cast<int>(structural_distance_limit),
                edge_weight,
                max_pixel_gain_regression,
                structural_rounds);
            require_cuda(cudaGetLastError(), "prepare_structural_chains_kernel launch");
            require_cuda(cudaMemset(group.dStats, 0, sizeof(ResidentDeviceChunkStats)), "resident structural cudaMemset stats");
            kernel_ms += run_resident_rotated_geometry_device_chunk_kernel(
                state.d_target,
                state.d_current,
                state.d_weights,
                group.dChains,
                group.dProposals,
                group.dScores,
                group.dStructuralObjectives,
                group.dActiveFlags,
                group.dStats,
                state.d_structural_distance,
                state.d_structural_tangent,
                group.dStructuralSearch,
                state.metadata,
                group.hostChains.size(),
                structural_rounds + 1u,
                fanout,
                structural_rounds,
                rounds,
                structural_rounds,
                static_cast<int>(min_axis),
                weighted,
                false,
                static_cast<int>(min_alpha),
                static_cast<int>(max_alpha),
                group.shapeKind,
                true,
                static_cast<int>(structural_distance_limit),
                edge_weight,
                structural_rounds);
            ResidentDeviceChunkStats structural_stats{};
            const auto structural_stats_start = std::chrono::steady_clock::now();
            require_cuda(cudaMemcpy(&structural_stats, group.dStats, sizeof(structural_stats), cudaMemcpyDeviceToHost), "resident structural cudaMemcpy stats");
            const auto structural_stats_end = std::chrono::steady_clock::now();
            d2h_ms += elapsed_ms(structural_stats_start, structural_stats_end);
            rounds += structural_stats.rounds;
            proposal_scores += structural_stats.proposalScores;
            accepted_mutations += structural_stats.acceptedMutations;
            if (structural_stats.proposalScores > 0) scorer_calls++;
            prune_structural_chains_kernel<<<1, 1>>>(group.dChains, static_cast<std::uint32_t>(group.hostChains.size()));
            require_cuda(cudaGetLastError(), "prune_structural_chains_kernel launch");
        }

        for (ResidentDeviceShapeGroup& group : groups) {
            const auto chains_d2h_start = std::chrono::steady_clock::now();
            require_cuda(cudaMemcpy(group.hostChains.data(), group.dChains, group.hostChains.size() * sizeof(ResidentDeviceChain), cudaMemcpyDeviceToHost), "resident mixed device cudaMemcpy final chains");
            const auto chains_d2h_end = std::chrono::steady_clock::now();
            d2h_ms += elapsed_ms(chains_d2h_start, chains_d2h_end);
            for (std::size_t i = 0; i < group.hostChains.size(); i++) {
                ResidentChain& chain = chains[group.sourceIndices[i]];
                const ResidentDeviceChain& device_chain = group.hostChains[i];
                chain.shapeKind = device_chain.shapeKind;
                chain.state = device_chain.state;
                chain.score = device_chain.score;
                chain.rng.state = device_chain.rng.state;
                chain.rng.hasSpare = device_chain.rng.hasSpare != 0;
                chain.rng.spare = device_chain.rng.spare;
                chain.remainingAge = device_chain.remainingAge;
                chain.steps = device_chain.steps;
                chain.structuralObjective = device_chain.structuralObjective;
            }
        }
    } catch (...) {
        free_resident_device_shape_groups(groups);
        throw;
    }
    free_resident_device_shape_groups(groups);
}

static RotatedCandidateRecord random_resident_rotated_ellipse(
    ResidentRandom& random,
    std::uint32_t candidate_id,
    std::uint32_t group,
    int width,
    int height,
    int min_axis,
    int initial_alpha) {
    RotatedCandidateRecord record{};
    record.candidateId = candidate_id;
    record.cx = random.intn(width);
    record.cy = random.intn(height);
    record.rx = random.intn(29) + min_axis;
    record.ry = random.intn(29) + min_axis;
    record.alpha = initial_alpha;
    record.angleDegrees = static_cast<float>(random.nextFloat() * 180.0);
    record.reserved = group;
    return record;
}

static std::uint16_t guide_saliency_at(const ServerState& state, int x, int y, std::uint32_t stroke_scale = 0) {
    const std::vector<std::uint16_t>& saliency = stroke_scale == 1 ? state.h_detail_saliency : stroke_scale == 2 ? state.h_contour_saliency : state.h_stroke_saliency;
    if (saliency.empty() || x < 0 || y < 0 || x >= state.metadata.width || y >= state.metadata.height) {
        return 0;
    }
    return saliency[static_cast<std::size_t>(y) * state.metadata.width + x];
}

static void guide_center_or_random(const ServerState& state, ResidentRandom& random, std::uint32_t stroke_scale, int& cx, int& cy) {
    const int width = state.metadata.width;
    const int height = state.metadata.height;
    if (stroke_scale != 0) {
        const std::vector<float>& probability = stroke_scale == 1 ? state.h_detail_alias_probability : state.h_contour_alias_probability;
        const std::vector<std::uint32_t>& alias = stroke_scale == 1 ? state.h_detail_alias_index : state.h_contour_alias_index;
        if (!probability.empty()) {
            const int column = random.intn(static_cast<int>(probability.size()));
            const std::uint32_t pixel = random.nextFloat() < probability[column] ? static_cast<std::uint32_t>(column) : alias[column];
            cx = static_cast<int>(pixel % static_cast<std::uint32_t>(width));
            cy = static_cast<int>(pixel / static_cast<std::uint32_t>(width));
            return;
        }
    }
    if (state.h_stroke_saliency.empty() || state.max_stroke_saliency == 0) {
        cx = random.intn(width);
        cy = random.intn(height);
        return;
    }
    for (int attempt = 0; attempt < 24; attempt++) {
        const int x = random.intn(width);
        const int y = random.intn(height);
        const int weight = guide_saliency_at(state, x, y);
        if (random.intn(static_cast<int>(state.max_stroke_saliency)) < weight) {
            cx = x;
            cy = y;
            return;
        }
    }
    cx = random.intn(width);
    cy = random.intn(height);
}

static float guide_tangent_angle_or_random(const ServerState& state, ResidentRandom& random, int cx, int cy, std::uint32_t stroke_scale = 0) {
    if (state.h_stroke_tangent.empty() || guide_saliency_at(state, cx, cy, stroke_scale) == 0) {
        return static_cast<float>(random.nextFloat() * 180.0);
    }
    double angle = state.h_stroke_tangent[static_cast<std::size_t>(cy) * state.metadata.width + cx] / 256.0;
    angle += std::trunc(random.normal() * 8.0);
    return wrap_angle_180(angle);
}

static RotatedCandidateRecord random_resident_rotated_candidate(
    ResidentRandom& random,
    std::uint32_t candidate_id,
    std::uint32_t group,
    int width,
    int height,
    int min_axis,
    int initial_alpha,
    int shape_kind,
    const ServerState& state,
    std::uint32_t guide_mode,
    std::uint32_t stroke_scale = 0,
    int min_long_axis = 0,
    int max_long_axis = 0) {
    RotatedCandidateRecord record = random_resident_rotated_ellipse(
        random,
        candidate_id,
        group,
        width,
        height,
        min_axis,
        initial_alpha);
    const bool edge_guided = guide_mode != 0;
    const bool quality_guided = guide_mode >= 2;
    if (edge_guided && (shape_kind == GEOMETRY_LINE_RECTANGLE || shape_kind == GEOMETRY_TRIANGLE)) {
        guide_center_or_random(state, random, stroke_scale, record.cx, record.cy);
        record.angleDegrees = guide_tangent_angle_or_random(state, random, record.cx, record.cy, stroke_scale);
    } else if (quality_guided && shape_kind == GEOMETRY_RECTANGLE) {
        guide_center_or_random(state, random, stroke_scale, record.cx, record.cy);
        record.angleDegrees = guide_tangent_angle_or_random(state, random, record.cx, record.cy, stroke_scale);
    }
    if (shape_kind == GEOMETRY_LINE_RECTANGLE) {
        const int thin = quality_guided ? min_axis : min_axis + random.intn(3);
        const int long_axis = stroke_scale != 0
            ? min_long_axis + random.intn(max_long_axis - min_long_axis + 1)
            : quality_guided ? min_axis + 18 + random.intn(77) : min_axis + 8 + random.intn(53);
        record.rx = clamp_int(long_axis, min_axis, width);
        record.ry = clamp_int(thin, min_axis, width);
    } else if (edge_guided && shape_kind == GEOMETRY_TRIANGLE) {
        const int long_axis = quality_guided ? min_axis + 14 + random.intn(67) : min_axis + 8 + random.intn(49);
        const int thin_axis = quality_guided ? min_axis + random.intn(6) : min_axis + random.intn(9);
        record.rx = clamp_int(long_axis, min_axis, width);
        record.ry = clamp_int(thin_axis, min_axis, height);
    } else if (quality_guided && shape_kind == GEOMETRY_RECTANGLE) {
        const int long_axis = min_axis + 6 + random.intn(39);
        const int short_axis = min_axis + 1 + random.intn(15);
        record.rx = clamp_int(long_axis, min_axis, width);
        record.ry = clamp_int(short_axis, min_axis, height);
    }
    return record;
}

static void server_resident_hill_climb_rotated(ServerState& state) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("RESIDENT_HILL_CLIMB_ROTATED before INIT");
    }
    std::uint32_t chain_count = 0;
    std::uint32_t age = 0;
    std::uint32_t fanout = 0;
    std::uint32_t early_stop_rounds = 0;
    std::uint32_t max_hill_steps = 0;
    std::uint32_t min_axis = 0;
    std::uint32_t layer = 0;
    std::uint32_t weighted = 0;
    std::uint32_t mutate_alpha = 0;
    std::uint32_t min_alpha = 1;
    std::uint32_t max_alpha = 255;
    if (!read_stdin_value(chain_count) ||
        !read_stdin_value(age) ||
        !read_stdin_value(fanout) ||
        !read_stdin_value(early_stop_rounds) ||
        !read_stdin_value(max_hill_steps) ||
        !read_stdin_value(min_axis) ||
        !read_stdin_value(layer) ||
        !read_stdin_value(weighted) ||
        !read_stdin_value(mutate_alpha) ||
        !read_stdin_value(min_alpha) ||
        !read_stdin_value(max_alpha)) {
        throw std::runtime_error("invalid RESIDENT_HILL_CLIMB_ROTATED header");
    }
    if (weighted && !state.d_weights) {
        throw std::runtime_error("RESIDENT_HILL_CLIMB_ROTATED weighted before SET_WEIGHT_MAP");
    }
    std::vector<RotatedCandidateRecord> initial_candidates(chain_count);
    std::vector<ScoreRecord> initial_scores(chain_count);
    read_stdin_bytes(initial_candidates.data(), initial_candidates.size() * sizeof(RotatedCandidateRecord), "resident initial candidates");
    read_stdin_bytes(initial_scores.data(), initial_scores.size() * sizeof(ScoreRecord), "resident initial scores");

    std::vector<ResidentChain> chains;
    chains.reserve(chain_count);
    for (std::uint32_t i = 0; i < chain_count; i++) {
        chains.emplace_back(
            static_cast<int>(i),
            initial_candidates[i],
            initial_scores[i],
            97531u + layer * 1009u + i * 9176u,
            age);
    }

    const auto total_start = std::chrono::steady_clock::now();
    std::uint32_t rounds = 0;
    std::uint32_t rounds_without_accept = 0;
    std::uint32_t proposal_scores = 0;
    std::uint32_t scorer_calls = 0;
    std::uint32_t accepted_mutations = 0;
    std::uint32_t max_step_cap_hits = 0;
    bool early_stop = false;
    double proposal_generation_ms = 0;
    double h2d_ms = 0;
    double kernel_ms = 0;
    double d2h_ms = 0;
    double accept_reject_ms = 0;

    while (true) {
        std::vector<std::size_t> active;
        for (std::size_t i = 0; i < chains.size(); i++) {
            if (chains[i].remainingAge > 0 && chains[i].steps < max_hill_steps) active.push_back(i);
        }
        if (active.empty()) break;
        if (rounds_without_accept >= early_stop_rounds) {
            early_stop = true;
            break;
        }
        rounds++;

        const auto proposal_start = std::chrono::steady_clock::now();
        std::vector<RotatedCandidateRecord> proposals;
        proposals.reserve(active.size() * fanout);
        for (const std::size_t chain_index : active) {
            ResidentChain& chain = chains[chain_index];
            for (std::uint32_t fan = 0; fan < fanout; fan++) {
                const std::uint32_t candidate_id = rounds * 1000u + static_cast<std::uint32_t>(chain.group) * fanout + fan + 1u;
                proposals.push_back(mutate_resident_state(chain, chain.rng, state.metadata.width, state.metadata.height, static_cast<int>(min_axis), mutate_alpha != 0, static_cast<int>(min_alpha), static_cast<int>(max_alpha), candidate_id));
            }
        }
        const auto proposal_end = std::chrono::steady_clock::now();
        proposal_generation_ms += elapsed_ms(proposal_start, proposal_end);

        state.ensure_buffers(proposals.size());
        const auto h2d_start = std::chrono::steady_clock::now();
        if (!proposals.empty()) {
            require_cuda(cudaMemcpy(state.d_candidates, proposals.data(), proposals.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "resident cudaMemcpy proposals");
        }
        const auto h2d_end = std::chrono::steady_clock::now();
        h2d_ms += elapsed_ms(h2d_start, h2d_end);

        Metadata batch_metadata = state.metadata;
        batch_metadata.candidateCount = static_cast<std::uint32_t>(proposals.size());
        const float this_kernel_ms = weighted
            ? run_score_rotated_geometry_weighted_kernel(
                state.d_target,
                state.d_current,
                state.d_weights,
                reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
                state.d_output,
                batch_metadata,
                proposals.size(),
                GEOMETRY_ELLIPSE)
            : run_score_rotated_geometry_kernel(
                state.d_target,
                state.d_current,
                reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
                state.d_output,
                batch_metadata,
                proposals.size(),
                GEOMETRY_ELLIPSE);
        kernel_ms += static_cast<double>(this_kernel_ms);

        std::vector<ScoreRecord> scores(proposals.size());
        const auto d2h_start = std::chrono::steady_clock::now();
        if (!scores.empty()) {
            require_cuda(cudaMemcpy(scores.data(), state.d_output, scores.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "resident cudaMemcpy scores");
        }
        const auto d2h_end = std::chrono::steady_clock::now();
        d2h_ms += elapsed_ms(d2h_start, d2h_end);
        proposal_scores += static_cast<std::uint32_t>(proposals.size());
        scorer_calls++;

        const auto accept_start = std::chrono::steady_clock::now();
        std::uint32_t round_accepted = 0;
        std::size_t proposal_offset = 0;
        for (const std::size_t chain_index : active) {
            ResidentChain& chain = chains[chain_index];
            int best_index = -1;
            double best_energy = chain.score.energy;
            for (std::uint32_t fan = 0; fan < fanout && proposal_offset + fan < proposals.size(); fan++) {
                const std::size_t i = proposal_offset + fan;
                if (scores[i].energy < best_energy) {
                    best_energy = scores[i].energy;
                    best_index = static_cast<int>(i);
                }
            }
            proposal_offset += fanout;
            if (best_index >= 0) {
                chain.state = proposals[best_index];
                chain.score = scores[best_index];
                chain.state.alpha = static_cast<std::int32_t>(chain.score.a);
                chain.remainingAge = age;
                accepted_mutations++;
                round_accepted++;
            } else {
                chain.remainingAge--;
            }
            chain.steps++;
        }
        const auto accept_end = std::chrono::steady_clock::now();
        accept_reject_ms += elapsed_ms(accept_start, accept_end);
        rounds_without_accept = round_accepted > 0 ? 0 : rounds_without_accept + 1;
    }

    for (const ResidentChain& chain : chains) {
        if (chain.steps >= max_hill_steps && chain.remainingAge > 0) max_step_cap_hits++;
    }

    std::size_t best_index = 0;
    for (std::size_t i = 1; i < chains.size(); i++) {
        if (chains[i].score.energy < chains[best_index].score.energy ||
            (chains[i].score.energy == chains[best_index].score.energy && chains[i].score.candidateId < chains[best_index].score.candidateId)) {
            best_index = i;
        }
    }
    const auto total_end = std::chrono::steady_clock::now();
    const double total_ms = elapsed_ms(total_start, total_end);

    const std::uint32_t status = 0;
    write_stdout_value(status);
    write_stdout_value(proposal_scores);
    write_stdout_value(accepted_mutations);
    write_stdout_value(rounds);
    write_stdout_value(static_cast<std::uint32_t>(early_stop ? 1 : 0));
    write_stdout_value(max_step_cap_hits);
    write_stdout_value(scorer_calls);
    write_stdout_value(proposal_generation_ms);
    write_stdout_value(h2d_ms);
    write_stdout_value(kernel_ms);
    write_stdout_value(d2h_ms);
    write_stdout_value(accept_reject_ms);
    write_stdout_value(total_ms);
    write_stdout_value(chains[best_index].state);
    write_stdout_value(chains[best_index].score);
    std::cout.flush();
}

static void server_resident_select_layer_rotated(ServerState& state, bool device_chunked = false) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_ROTATED before INIT");
    }
    std::uint32_t candidates_per_group = 0;
    std::uint32_t group_count = 0;
    std::uint32_t age = 0;
    std::uint32_t fanout = 0;
    std::uint32_t early_stop_rounds = 0;
    std::uint32_t max_hill_steps = 0;
    std::uint32_t min_axis = 0;
    std::uint32_t layer = 0;
    std::uint32_t weighted = 0;
    std::uint32_t mutate_alpha = 0;
    std::uint32_t min_alpha = 1;
    std::uint32_t max_alpha = 255;
    std::uint32_t initial_alpha = 128;
    std::uint32_t seed = 0;
    std::uint32_t device_chunk_rounds = 1;
    if (!read_stdin_value(candidates_per_group) ||
        !read_stdin_value(group_count) ||
        !read_stdin_value(age) ||
        !read_stdin_value(fanout) ||
        !read_stdin_value(early_stop_rounds) ||
        !read_stdin_value(max_hill_steps) ||
        !read_stdin_value(min_axis) ||
        !read_stdin_value(layer) ||
        !read_stdin_value(weighted) ||
        !read_stdin_value(mutate_alpha) ||
        !read_stdin_value(min_alpha) ||
        !read_stdin_value(max_alpha) ||
        !read_stdin_value(initial_alpha) ||
        !read_stdin_value(seed)) {
        throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_ROTATED header");
    }
    if (device_chunked && !read_stdin_value(device_chunk_rounds)) {
        throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_ROTATED_DEVICE_CHUNK header");
    }
    if (weighted && !state.d_weights) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_ROTATED weighted before SET_WEIGHT_MAP");
    }
    if (candidates_per_group == 0 || group_count == 0) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_ROTATED requires candidates and groups");
    }

    const auto total_start = std::chrono::steady_clock::now();
    const std::size_t candidate_count = static_cast<std::size_t>(candidates_per_group) * group_count;

    const auto random_start = std::chrono::steady_clock::now();
    ResidentRandom random(seed);
    std::vector<RotatedCandidateRecord> initial_candidates;
    initial_candidates.reserve(candidate_count);
    for (std::uint32_t group = 0; group < group_count; group++) {
        for (std::uint32_t i = 0; i < candidates_per_group; i++) {
            const std::uint32_t candidate_id = static_cast<std::uint32_t>(initial_candidates.size()) + 1u;
            initial_candidates.push_back(random_resident_rotated_ellipse(
                random,
                candidate_id,
                group,
                state.metadata.width,
                state.metadata.height,
                static_cast<int>(min_axis),
                static_cast<int>(initial_alpha)));
        }
    }
    const auto random_end = std::chrono::steady_clock::now();
    const double random_generation_ms = elapsed_ms(random_start, random_end);

    state.ensure_buffers(initial_candidates.size());
    const auto random_h2d_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(state.d_candidates, initial_candidates.data(), initial_candidates.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "resident select cudaMemcpy initial candidates");
    const auto random_h2d_end = std::chrono::steady_clock::now();
    const double random_h2d_ms = elapsed_ms(random_h2d_start, random_h2d_end);

    Metadata batch_metadata = state.metadata;
    batch_metadata.candidateCount = static_cast<std::uint32_t>(initial_candidates.size());
    const float random_kernel_ms = weighted
        ? run_score_rotated_geometry_weighted_kernel(
            state.d_target,
            state.d_current,
            state.d_weights,
            reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
            state.d_output,
            batch_metadata,
            initial_candidates.size(),
            GEOMETRY_ELLIPSE)
        : run_score_rotated_geometry_kernel(
            state.d_target,
            state.d_current,
            reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
            state.d_output,
            batch_metadata,
            initial_candidates.size(),
            GEOMETRY_ELLIPSE);

    std::vector<ScoreRecord> initial_scores(initial_candidates.size());
    const auto random_d2h_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(initial_scores.data(), state.d_output, initial_scores.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "resident select cudaMemcpy initial scores");
    const auto random_d2h_end = std::chrono::steady_clock::now();
    const double random_d2h_ms = elapsed_ms(random_d2h_start, random_d2h_end);

    const auto group_start = std::chrono::steady_clock::now();
    std::vector<int> best_index(group_count, -1);
    for (std::size_t i = 0; i < initial_scores.size(); i++) {
        const std::uint32_t group = initial_candidates[i].reserved;
        if (group >= group_count) continue;
        const int existing = best_index[group];
        if (existing < 0 ||
            initial_scores[i].energy < initial_scores[existing].energy ||
            (initial_scores[i].energy == initial_scores[existing].energy && initial_scores[i].candidateId < initial_scores[existing].candidateId)) {
            best_index[group] = static_cast<int>(i);
        }
    }

    std::vector<ResidentChain> chains;
    chains.reserve(group_count);
    for (std::uint32_t group = 0; group < group_count; group++) {
        const int index = best_index[group];
        if (index < 0) continue;
        chains.emplace_back(
            static_cast<int>(group),
            initial_candidates[index],
            initial_scores[index],
            97531u + layer * 1009u + group * 9176u,
            age);
    }
    const auto group_end = std::chrono::steady_clock::now();
    const double group_best_ms = elapsed_ms(group_start, group_end);

    std::uint32_t rounds = 0;
    std::uint32_t rounds_without_accept = 0;
    std::uint32_t proposal_scores = 0;
    std::uint32_t scorer_calls = 0;
    std::uint32_t accepted_mutations = 0;
    std::uint32_t max_step_cap_hits = 0;
    bool early_stop = false;
    double proposal_generation_ms = 0;
    double h2d_ms = 0;
    double kernel_ms = 0;
    double d2h_ms = 0;
    double accept_reject_ms = 0;

    if (device_chunked) {
        run_resident_rotated_device_chunks(
            state,
            chains,
            age,
            fanout,
            early_stop_rounds,
            max_hill_steps,
            min_axis,
            weighted != 0,
            mutate_alpha != 0,
            min_alpha,
            max_alpha,
            device_chunk_rounds,
            rounds,
            rounds_without_accept,
            proposal_scores,
            scorer_calls,
            accepted_mutations,
            early_stop,
            h2d_ms,
            kernel_ms,
            d2h_ms);
    } else {
    while (true) {
        std::vector<std::size_t> active;
        for (std::size_t i = 0; i < chains.size(); i++) {
            if (chains[i].remainingAge > 0 && chains[i].steps < max_hill_steps) active.push_back(i);
        }
        if (active.empty()) break;
        if (rounds_without_accept >= early_stop_rounds) {
            early_stop = true;
            break;
        }
        rounds++;

        const auto proposal_start = std::chrono::steady_clock::now();
        std::vector<RotatedCandidateRecord> proposals;
        proposals.reserve(active.size() * fanout);
        for (const std::size_t chain_index : active) {
            ResidentChain& chain = chains[chain_index];
            for (std::uint32_t fan = 0; fan < fanout; fan++) {
                const std::uint32_t candidate_id = rounds * 1000u + static_cast<std::uint32_t>(chain.group) * fanout + fan + 1u;
                proposals.push_back(mutate_resident_state(chain, chain.rng, state.metadata.width, state.metadata.height, static_cast<int>(min_axis), mutate_alpha != 0, static_cast<int>(min_alpha), static_cast<int>(max_alpha), candidate_id));
            }
        }
        const auto proposal_end = std::chrono::steady_clock::now();
        proposal_generation_ms += elapsed_ms(proposal_start, proposal_end);

        state.ensure_buffers(proposals.size());
        const auto h2d_start = std::chrono::steady_clock::now();
        if (!proposals.empty()) {
            require_cuda(cudaMemcpy(state.d_candidates, proposals.data(), proposals.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "resident select cudaMemcpy proposals");
        }
        const auto h2d_end = std::chrono::steady_clock::now();
        h2d_ms += elapsed_ms(h2d_start, h2d_end);

        Metadata proposal_metadata = state.metadata;
        proposal_metadata.candidateCount = static_cast<std::uint32_t>(proposals.size());
        const float this_kernel_ms = weighted
            ? run_score_rotated_geometry_weighted_kernel(
                state.d_target,
                state.d_current,
                state.d_weights,
                reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
                state.d_output,
                proposal_metadata,
                proposals.size(),
                GEOMETRY_ELLIPSE)
            : run_score_rotated_geometry_kernel(
                state.d_target,
                state.d_current,
                reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
                state.d_output,
                proposal_metadata,
                proposals.size(),
                GEOMETRY_ELLIPSE);
        kernel_ms += static_cast<double>(this_kernel_ms);

        std::vector<ScoreRecord> scores(proposals.size());
        const auto d2h_start = std::chrono::steady_clock::now();
        if (!scores.empty()) {
            require_cuda(cudaMemcpy(scores.data(), state.d_output, scores.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "resident select cudaMemcpy scores");
        }
        const auto d2h_end = std::chrono::steady_clock::now();
        d2h_ms += elapsed_ms(d2h_start, d2h_end);
        proposal_scores += static_cast<std::uint32_t>(proposals.size());
        scorer_calls++;

        const auto accept_start = std::chrono::steady_clock::now();
        std::uint32_t round_accepted = 0;
        std::size_t proposal_offset = 0;
        for (const std::size_t chain_index : active) {
            ResidentChain& chain = chains[chain_index];
            int best_index_for_chain = -1;
            double best_energy = chain.score.energy;
            for (std::uint32_t fan = 0; fan < fanout && proposal_offset + fan < proposals.size(); fan++) {
                const std::size_t i = proposal_offset + fan;
                if (scores[i].energy < best_energy) {
                    best_energy = scores[i].energy;
                    best_index_for_chain = static_cast<int>(i);
                }
            }
            proposal_offset += fanout;
            if (best_index_for_chain >= 0) {
                chain.state = proposals[best_index_for_chain];
                chain.score = scores[best_index_for_chain];
                chain.state.alpha = static_cast<std::int32_t>(chain.score.a);
                chain.remainingAge = age;
                accepted_mutations++;
                round_accepted++;
            } else {
                chain.remainingAge--;
            }
            chain.steps++;
        }
        const auto accept_end = std::chrono::steady_clock::now();
        accept_reject_ms += elapsed_ms(accept_start, accept_end);
        rounds_without_accept = round_accepted > 0 ? 0 : rounds_without_accept + 1;
    }
    }

    for (const ResidentChain& chain : chains) {
        if (chain.steps >= max_hill_steps && chain.remainingAge > 0) max_step_cap_hits++;
    }

    std::size_t selected_index = 0;
    for (std::size_t i = 1; i < chains.size(); i++) {
        if (chains[i].score.energy < chains[selected_index].score.energy ||
            (chains[i].score.energy == chains[selected_index].score.energy && chains[i].score.candidateId < chains[selected_index].score.candidateId)) {
            selected_index = i;
        }
    }
    const auto total_end = std::chrono::steady_clock::now();
    const double total_ms = elapsed_ms(total_start, total_end);

    const std::uint32_t status = 0;
    write_stdout_value(status);
    write_stdout_value(static_cast<std::uint32_t>(initial_candidates.size()));
    write_stdout_value(proposal_scores);
    write_stdout_value(accepted_mutations);
    write_stdout_value(rounds);
    write_stdout_value(static_cast<std::uint32_t>(early_stop ? 1 : 0));
    write_stdout_value(max_step_cap_hits);
    write_stdout_value(scorer_calls);
    write_stdout_value(static_cast<std::uint32_t>(1));
    write_stdout_value(random_generation_ms);
    write_stdout_value(random_h2d_ms);
    write_stdout_value(static_cast<double>(random_kernel_ms));
    write_stdout_value(random_d2h_ms);
    write_stdout_value(group_best_ms);
    write_stdout_value(proposal_generation_ms);
    write_stdout_value(h2d_ms);
    write_stdout_value(kernel_ms);
    write_stdout_value(d2h_ms);
    write_stdout_value(accept_reject_ms);
    write_stdout_value(total_ms);
    write_stdout_value(chains[selected_index].state);
    write_stdout_value(chains[selected_index].score);
    std::cout.flush();
}

static std::vector<int> resident_shape_order(std::uint32_t shape_mask) {
    std::vector<int> shapes;
    if (shape_mask & (1u << GEOMETRY_ELLIPSE)) shapes.push_back(GEOMETRY_ELLIPSE);
    if (shape_mask & (1u << GEOMETRY_TRIANGLE)) shapes.push_back(GEOMETRY_TRIANGLE);
    if (shape_mask & (1u << GEOMETRY_RECTANGLE)) shapes.push_back(GEOMETRY_RECTANGLE);
    if (shape_mask & (1u << GEOMETRY_LINE_RECTANGLE)) shapes.push_back(GEOMETRY_LINE_RECTANGLE);
    return shapes;
}

static void score_resident_rotated_candidates(
    ServerState& state,
    const std::vector<RotatedCandidateRecord>& candidates,
    int shape_kind,
    bool weighted,
    std::vector<ScoreRecord>& scores,
    double& h2d_ms,
    double& kernel_ms,
    double& d2h_ms) {
    scores.clear();
    if (candidates.empty()) {
        return;
    }
    state.ensure_buffers(candidates.size());
    const auto h2d_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(state.d_candidates, candidates.data(), candidates.size() * sizeof(RotatedCandidateRecord), cudaMemcpyHostToDevice), "resident shape cudaMemcpy candidates");
    const auto h2d_end = std::chrono::steady_clock::now();
    h2d_ms += elapsed_ms(h2d_start, h2d_end);

    Metadata batch_metadata = state.metadata;
    batch_metadata.candidateCount = static_cast<std::uint32_t>(candidates.size());
    const float this_kernel_ms = weighted
        ? run_score_rotated_geometry_weighted_kernel(
            state.d_target,
            state.d_current,
            state.d_weights,
            reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
            state.d_output,
            batch_metadata,
            candidates.size(),
            shape_kind)
        : run_score_rotated_geometry_kernel(
            state.d_target,
            state.d_current,
            reinterpret_cast<RotatedCandidateRecord*>(state.d_candidates),
            state.d_output,
            batch_metadata,
            candidates.size(),
            shape_kind);
    kernel_ms += static_cast<double>(this_kernel_ms);

    scores.resize(candidates.size());
    const auto d2h_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(scores.data(), state.d_output, scores.size() * sizeof(ScoreRecord), cudaMemcpyDeviceToHost), "resident shape cudaMemcpy scores");
    const auto d2h_end = std::chrono::steady_clock::now();
    d2h_ms += elapsed_ms(d2h_start, d2h_end);
}

static double resident_mixed_proxy_code_cost(int shape_kind) {
    if (shape_kind == GEOMETRY_TRIANGLE) return 910.0;
    if (shape_kind == GEOMETRY_RECTANGLE || shape_kind == GEOMETRY_LINE_RECTANGLE) return 856.0;
    return 924.0;
}

static double resident_base_energy(const Metadata& metadata) {
    return std::sqrt(static_cast<double>(metadata.baseTotalError) / static_cast<double>(metadata.width * metadata.height * 4)) / 255.0;
}

static int resident_shape_order_index(const std::vector<int>& shapes, int shape_kind) {
    const auto iter = std::find(shapes.begin(), shapes.end(), shape_kind);
    return iter == shapes.end() ? 999 : static_cast<int>(iter - shapes.begin());
}

static bool resident_mixed_chain_better(
    const ResidentChain& current,
    const ResidentChain& selected,
    const std::vector<int>& shapes,
    std::uint32_t selection_mode,
    double base_energy) {
    if (selection_mode == 1) {
        const double current_score = (base_energy - current.score.energy) / resident_mixed_proxy_code_cost(current.shapeKind);
        const double selected_score = (base_energy - selected.score.energy) / resident_mixed_proxy_code_cost(selected.shapeKind);
        if (current_score != selected_score) return current_score > selected_score;
    } else if (current.score.energy != selected.score.energy) {
        return current.score.energy < selected.score.energy;
    }
    const int current_order = resident_shape_order_index(shapes, current.shapeKind);
    const int selected_order = resident_shape_order_index(shapes, selected.shapeKind);
    if (current_order != selected_order) return current_order < selected_order;
    return current.score.candidateId < selected.score.candidateId;
}

static void server_resident_select_layer_mixed(ServerState& state, bool device_chunked = false, bool multi_scale = false, bool structural_mode = false) {
    if (!state.d_target || !state.d_current) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_MIXED before INIT");
    }
    std::uint32_t candidates_per_group = 0;
    std::uint32_t group_count = 0;
    std::uint32_t age = 0;
    std::uint32_t fanout = 0;
    std::uint32_t early_stop_rounds = 0;
    std::uint32_t max_hill_steps = 0;
    std::uint32_t min_axis = 0;
    std::uint32_t layer = 0;
    std::uint32_t weighted = 0;
    std::uint32_t mutate_alpha = 0;
    std::uint32_t min_alpha = 1;
    std::uint32_t max_alpha = 255;
    std::uint32_t initial_alpha = 128;
    std::uint32_t seed = 0;
    std::uint32_t shape_mask = 0;
    std::uint32_t selection_mode = 0;
    std::uint32_t guide_mode = 0;
    std::uint32_t stroke_scale = 0;
    std::uint32_t min_long_axis = 0;
    std::uint32_t max_long_axis = 0;
    std::uint32_t device_chunk_rounds = 1;
    std::uint32_t structural_edge_weight_q16 = 0;
    std::uint32_t structural_distance_limit = 0;
    std::uint32_t structural_rounds = 0;
    std::uint32_t max_pixel_gain_regression_q16 = 0;
    if (!read_stdin_value(candidates_per_group) ||
        !read_stdin_value(group_count) ||
        !read_stdin_value(age) ||
        !read_stdin_value(fanout) ||
        !read_stdin_value(early_stop_rounds) ||
        !read_stdin_value(max_hill_steps) ||
        !read_stdin_value(min_axis) ||
        !read_stdin_value(layer) ||
        !read_stdin_value(weighted) ||
        !read_stdin_value(mutate_alpha) ||
        !read_stdin_value(min_alpha) ||
        !read_stdin_value(max_alpha) ||
        !read_stdin_value(initial_alpha) ||
        !read_stdin_value(seed) ||
        !read_stdin_value(shape_mask) ||
        !read_stdin_value(selection_mode) ||
        !read_stdin_value(guide_mode)) {
        throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_MIXED header");
    }
    if (multi_scale && (!read_stdin_value(stroke_scale) || !read_stdin_value(min_long_axis) || !read_stdin_value(max_long_axis))) {
        throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_GUIDED_DEVICE_CHUNK bounds");
    }
    if (device_chunked && !read_stdin_value(device_chunk_rounds)) {
        throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_MIXED_DEVICE_CHUNK header");
    }
    if (structural_mode && (!read_stdin_value(structural_edge_weight_q16) || !read_stdin_value(structural_distance_limit) || !read_stdin_value(structural_rounds) || !read_stdin_value(max_pixel_gain_regression_q16))) {
        throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_STRUCTURAL_DEVICE_CHUNK header");
    }
    if (weighted && !state.d_weights) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_MIXED weighted before SET_WEIGHT_MAP");
    }
    if (!multi_scale && guide_mode != 0 && (state.h_stroke_saliency.empty() || state.h_stroke_tangent.empty())) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_MIXED guided before SET_STROKE_GUIDE");
    }
    if (multi_scale) {
        const bool missing_guide = stroke_scale == 1 ? state.h_detail_alias_probability.empty() : stroke_scale == 2 ? state.h_contour_alias_probability.empty() : true;
        if (missing_guide || state.h_stroke_tangent.empty()) throw std::runtime_error("RESIDENT_SELECT_LAYER_GUIDED before SET_MULTI_SCALE_STROKE_GUIDE");
        if (shape_mask != (1u << GEOMETRY_LINE_RECTANGLE) || guide_mode == 0 || min_long_axis < min_axis || max_long_axis < min_long_axis || max_long_axis > static_cast<std::uint32_t>(state.metadata.width)) {
            throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_GUIDED configuration");
        }
    }
    if (structural_mode) {
        if (!device_chunked || !multi_scale || stroke_scale != 1 || guide_mode != 2 || !state.d_structural_distance || !state.d_structural_tangent || structural_edge_weight_q16 == 0 || structural_edge_weight_q16 > 65536 || structural_distance_limit == 0 || structural_distance_limit > 32 || structural_rounds == 0 || structural_rounds > 256 || max_pixel_gain_regression_q16 > 65536) {
            throw std::runtime_error("invalid RESIDENT_SELECT_LAYER_STRUCTURAL configuration");
        }
    }
    const std::vector<int> shapes = resident_shape_order(shape_mask);
    if (candidates_per_group == 0 || group_count == 0 || shapes.empty()) {
        throw std::runtime_error("RESIDENT_SELECT_LAYER_MIXED requires candidates, groups, and shapes");
    }

    const auto total_start = std::chrono::steady_clock::now();
    const std::size_t per_shape_count = static_cast<std::size_t>(candidates_per_group) * group_count;
    std::uint32_t initial_candidate_count = 0;
    std::uint32_t random_scorer_calls = 0;
    double random_generation_ms = 0;
    double random_h2d_ms = 0;
    double random_kernel_ms = 0;
    double random_d2h_ms = 0;
    double group_best_ms = 0;

    std::vector<ResidentChain> chains;
    chains.reserve(group_count * shapes.size());

    std::vector<RotatedCandidateRecord> initial_candidates;
    std::vector<ScoreRecord> initial_scores;
    std::vector<RotatedCandidateRecord> shape_initial_candidates;
    std::vector<ScoreRecord> shape_initial_scores;
    initial_candidates.reserve(per_shape_count * shapes.size());
    initial_scores.reserve(per_shape_count * shapes.size());
    shape_initial_candidates.reserve(per_shape_count);
    shape_initial_scores.reserve(per_shape_count);
    for (std::size_t shape_index = 0; shape_index < shapes.size(); shape_index++) {
        const int shape_kind = shapes[shape_index];
        shape_initial_candidates.clear();
        shape_initial_scores.clear();
        const auto random_start = std::chrono::steady_clock::now();
        ResidentRandom random(seed + static_cast<std::uint32_t>(shape_index) * 1000003u);
        for (std::uint32_t group = 0; group < group_count; group++) {
            for (std::uint32_t i = 0; i < candidates_per_group; i++) {
                const std::uint32_t candidate_id = group * candidates_per_group + i + 1u;
                shape_initial_candidates.push_back(random_resident_rotated_candidate(
                    random,
                    candidate_id,
                    group,
                    state.metadata.width,
                    state.metadata.height,
                    static_cast<int>(min_axis),
                    static_cast<int>(initial_alpha),
                    shape_kind,
                    state,
                    guide_mode,
                    stroke_scale,
                    static_cast<int>(min_long_axis),
                    static_cast<int>(max_long_axis)));
            }
        }
        const auto random_end = std::chrono::steady_clock::now();
        random_generation_ms += elapsed_ms(random_start, random_end);
        score_resident_rotated_candidates(state, shape_initial_candidates, shape_kind, weighted != 0, shape_initial_scores, random_h2d_ms, random_kernel_ms, random_d2h_ms);
        random_scorer_calls++;
        initial_candidates.insert(initial_candidates.end(), shape_initial_candidates.begin(), shape_initial_candidates.end());
        initial_scores.insert(initial_scores.end(), shape_initial_scores.begin(), shape_initial_scores.end());
    }
    initial_candidate_count = static_cast<std::uint32_t>(initial_candidates.size());

    const auto group_start = std::chrono::steady_clock::now();
    std::vector<int> best_index(group_count * shapes.size(), -1);
    for (std::size_t i = 0; i < initial_scores.size(); i++) {
        const std::size_t shape_index = i / per_shape_count;
        const std::uint32_t group = initial_candidates[i].reserved;
        const std::size_t bucket = shape_index * group_count + group;
        const int existing = best_index[bucket];
        if (existing < 0 ||
            initial_scores[i].energy < initial_scores[existing].energy ||
            (initial_scores[i].energy == initial_scores[existing].energy && initial_scores[i].candidateId < initial_scores[existing].candidateId)) {
            best_index[bucket] = static_cast<int>(i);
        }
    }
    for (std::size_t shape_index = 0; shape_index < shapes.size(); shape_index++) {
        const int shape_kind = shapes[shape_index];
        for (std::uint32_t group = 0; group < group_count; group++) {
            const int index = best_index[shape_index * group_count + group];
            if (index < 0) continue;
            chains.emplace_back(
                static_cast<int>(group),
                initial_candidates[index],
                initial_scores[index],
                97531u + layer * 1009u + group * 9176u,
                age,
                shape_kind,
                guide_mode >= 2 && shape_kind == GEOMETRY_LINE_RECTANGLE,
                static_cast<int>(min_long_axis),
                static_cast<int>(max_long_axis));
        }
    }
    const auto group_end = std::chrono::steady_clock::now();
    group_best_ms = elapsed_ms(group_start, group_end);

    std::uint32_t rounds = 0;
    std::uint32_t rounds_without_accept = 0;
    std::uint32_t proposal_scores = 0;
    std::uint32_t scorer_calls = 0;
    std::uint32_t accepted_mutations = 0;
    std::uint32_t max_step_cap_hits = 0;
    bool early_stop = false;
    double proposal_generation_ms = 0;
    double h2d_ms = 0;
    double kernel_ms = 0;
    double d2h_ms = 0;
    double accept_reject_ms = 0;
    std::vector<RotatedCandidateRecord> proposals[GEOMETRY_KIND_COUNT];
    std::vector<ScoreRecord> proposal_scores_by_shape[GEOMETRY_KIND_COUNT];
    std::vector<std::size_t> proposal_offset_by_chain(chains.size(), 0);
    std::vector<std::uint32_t> proposal_count_by_chain(chains.size(), 0);
    for (const int shape_kind : shapes) {
        proposals[shape_kind].reserve(static_cast<std::size_t>(group_count) * fanout);
        proposal_scores_by_shape[shape_kind].reserve(static_cast<std::size_t>(group_count) * fanout);
    }

    if (device_chunked) {
        run_resident_mixed_device_chunks(
            state,
            chains,
            shapes,
            age,
            fanout,
            early_stop_rounds,
            max_hill_steps,
            min_axis,
            weighted != 0,
            mutate_alpha != 0,
            min_alpha,
            max_alpha,
            device_chunk_rounds,
            rounds,
            rounds_without_accept,
            proposal_scores,
            scorer_calls,
            accepted_mutations,
            early_stop,
            h2d_ms,
            kernel_ms,
            d2h_ms,
            structural_mode,
            structural_edge_weight_q16,
            structural_distance_limit,
            structural_rounds,
            max_pixel_gain_regression_q16);
    } else {
    while (true) {
        std::vector<std::size_t> active;
        for (std::size_t i = 0; i < chains.size(); i++) {
            if (chains[i].remainingAge > 0 && chains[i].steps < max_hill_steps) active.push_back(i);
        }
        if (active.empty()) break;
        if (rounds_without_accept >= early_stop_rounds) {
            early_stop = true;
            break;
        }
        rounds++;

        const auto proposal_start = std::chrono::steady_clock::now();
        for (const int shape_kind : shapes) {
            proposals[shape_kind].clear();
            proposal_scores_by_shape[shape_kind].clear();
        }
        for (const std::size_t chain_index : active) {
            ResidentChain& chain = chains[chain_index];
            proposal_offset_by_chain[chain_index] = proposals[chain.shapeKind].size();
            proposal_count_by_chain[chain_index] = fanout;
            for (std::uint32_t fan = 0; fan < fanout; fan++) {
                const std::uint32_t candidate_id = rounds * 1000u + static_cast<std::uint32_t>(chain.group) * fanout + fan + 1u;
                proposals[chain.shapeKind].push_back(mutate_resident_state(chain, chain.rng, state.metadata.width, state.metadata.height, static_cast<int>(min_axis), mutate_alpha != 0, static_cast<int>(min_alpha), static_cast<int>(max_alpha), candidate_id));
            }
        }
        const auto proposal_end = std::chrono::steady_clock::now();
        proposal_generation_ms += elapsed_ms(proposal_start, proposal_end);

        for (const int shape_kind : shapes) {
            if (proposals[shape_kind].empty()) continue;
            score_resident_rotated_candidates(state, proposals[shape_kind], shape_kind, weighted != 0, proposal_scores_by_shape[shape_kind], h2d_ms, kernel_ms, d2h_ms);
            proposal_scores += static_cast<std::uint32_t>(proposals[shape_kind].size());
            scorer_calls++;
        }

        const auto accept_start = std::chrono::steady_clock::now();
        std::uint32_t round_accepted = 0;
        for (const std::size_t chain_index : active) {
            ResidentChain& chain = chains[chain_index];
            int best_index_for_chain = -1;
            double best_energy = chain.score.energy;
            const int shape_kind = chain.shapeKind;
            const std::size_t proposal_offset = proposal_offset_by_chain[chain_index];
            const std::uint32_t proposal_count = proposal_count_by_chain[chain_index];
            for (std::uint32_t fan = 0; fan < proposal_count && proposal_offset + fan < proposals[shape_kind].size(); fan++) {
                const std::size_t i = proposal_offset + fan;
                if (proposal_scores_by_shape[shape_kind][i].energy < best_energy) {
                    best_energy = proposal_scores_by_shape[shape_kind][i].energy;
                    best_index_for_chain = static_cast<int>(i);
                }
            }
            if (best_index_for_chain >= 0) {
                chain.state = proposals[shape_kind][best_index_for_chain];
                chain.score = proposal_scores_by_shape[shape_kind][best_index_for_chain];
                chain.state.alpha = static_cast<std::int32_t>(chain.score.a);
                chain.remainingAge = age;
                accepted_mutations++;
                round_accepted++;
            } else {
                chain.remainingAge--;
            }
            chain.steps++;
        }
        const auto accept_end = std::chrono::steady_clock::now();
        accept_reject_ms += elapsed_ms(accept_start, accept_end);
        rounds_without_accept = round_accepted > 0 ? 0 : rounds_without_accept + 1;
    }
    }

    for (const ResidentChain& chain : chains) {
        if (chain.steps >= max_hill_steps && chain.remainingAge > 0) max_step_cap_hits++;
    }

    std::size_t selected_index = 0;
    const double base_energy = resident_base_energy(state.metadata);
    for (std::size_t i = 1; i < chains.size(); i++) {
        const bool structural_better = structural_mode && (chains[i].structuralObjective < chains[selected_index].structuralObjective || (chains[i].structuralObjective == chains[selected_index].structuralObjective && chains[i].score.energy < chains[selected_index].score.energy));
        if (structural_better || (!structural_mode && resident_mixed_chain_better(chains[i], chains[selected_index], shapes, selection_mode, base_energy))) {
            selected_index = i;
        }
    }
    const auto total_end = std::chrono::steady_clock::now();
    const double total_ms = elapsed_ms(total_start, total_end);

    const std::uint32_t status = 0;
    write_stdout_value(status);
    write_stdout_value(initial_candidate_count);
    write_stdout_value(proposal_scores);
    write_stdout_value(accepted_mutations);
    write_stdout_value(rounds);
    write_stdout_value(static_cast<std::uint32_t>(early_stop ? 1 : 0));
    write_stdout_value(max_step_cap_hits);
    write_stdout_value(scorer_calls);
    write_stdout_value(random_scorer_calls);
    write_stdout_value(static_cast<std::uint32_t>(chains[selected_index].shapeKind));
    write_stdout_value(random_generation_ms);
    write_stdout_value(random_h2d_ms);
    write_stdout_value(random_kernel_ms);
    write_stdout_value(random_d2h_ms);
    write_stdout_value(group_best_ms);
    write_stdout_value(proposal_generation_ms);
    write_stdout_value(h2d_ms);
    write_stdout_value(kernel_ms);
    write_stdout_value(d2h_ms);
    write_stdout_value(accept_reject_ms);
    write_stdout_value(total_ms);
    write_stdout_value(chains[selected_index].state);
    write_stdout_value(chains[selected_index].score);
    write_stdout_value(static_cast<std::uint32_t>(chains.size()));
    for (const ResidentChain& chain : chains) {
        write_stdout_value(static_cast<std::uint32_t>(chain.shapeKind));
        write_stdout_value(chain.state);
        write_stdout_value(chain.score);
    }
    std::cout.flush();
}

static void server_update_current(ServerState& state) {
    if (!state.d_current) {
        throw std::runtime_error("UPDATE_CURRENT before INIT");
    }
    std::uint64_t base_total_error = 0;
    if (!read_stdin_value(base_total_error)) {
        throw std::runtime_error("invalid UPDATE_CURRENT payload");
    }
    const std::size_t image_bytes = static_cast<std::size_t>(state.metadata.width) * state.metadata.height * 4;
    std::vector<std::uint8_t> current(image_bytes);
    read_stdin_bytes(current.data(), current.size(), "UPDATE_CURRENT current");
    const auto h2d_start = std::chrono::steady_clock::now();
    require_cuda(cudaMemcpy(state.d_current, current.data(), current.size(), cudaMemcpyHostToDevice), "server cudaMemcpy update current");
    const auto h2d_end = std::chrono::steady_clock::now();
    state.metadata.baseTotalError = base_total_error;

    const std::uint32_t status = 0;
    const double h2d_ms = elapsed_ms(h2d_start, h2d_end);
    write_stdout_value(status);
    write_stdout_value(h2d_ms);
    std::cout.flush();
}

static int run_server() {
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    ServerState state;
    bool first_command = true;
    while (true) {
        std::uint32_t command = 0;
        if (!read_stdin_value(command)) {
            return 0;
        }
        if (first_command && command == 0x01BFBBEF) {
            std::uint8_t command_tail[3]{};
            read_stdin_bytes(command_tail, sizeof(command_tail), "UTF-8 BOM command tail");
            if (command_tail[0] != 0 || command_tail[1] != 0 || command_tail[2] != 0) {
                throw std::runtime_error("invalid UTF-8 BOM command prefix");
            }
            command = CMD_INIT;
        }
        first_command = false;
        if (command == CMD_INIT) {
            server_init(state);
        } else if (command == CMD_SCORE_BATCH_GEOMETRY) {
            server_score_batch_geometry(state);
        } else if (command == CMD_SCORE_BATCH_ROTATED_GEOMETRY) {
            server_score_batch_rotated_geometry(state);
        } else if (command == CMD_SCORE_BATCH_ROTATED_RECT_GEOMETRY) {
            server_score_batch_rotated_geometry(state, GEOMETRY_RECTANGLE);
        } else if (command == CMD_SCORE_BATCH_ROTATED_TRIANGLE_GEOMETRY) {
            server_score_batch_rotated_geometry(state, GEOMETRY_TRIANGLE);
        } else if (command == CMD_SET_WEIGHT_MAP) {
            server_set_weight_map(state);
        } else if (command == CMD_SET_STROKE_GUIDE) {
            server_set_stroke_guide(state);
        } else if (command == CMD_SET_MULTI_SCALE_STROKE_GUIDE) {
            server_set_multi_scale_stroke_guide(state);
        } else if (command == CMD_SET_STRUCTURAL_GUIDE) {
            server_set_structural_guide(state);
        } else if (command == CMD_SCORE_BATCH_ROTATED_GEOMETRY_WEIGHTED) {
            server_score_batch_rotated_geometry_weighted(state);
        } else if (command == CMD_SCORE_BATCH_ROTATED_RECT_GEOMETRY_WEIGHTED) {
            server_score_batch_rotated_geometry_weighted(state, GEOMETRY_RECTANGLE);
        } else if (command == CMD_SCORE_BATCH_ROTATED_TRIANGLE_GEOMETRY_WEIGHTED) {
            server_score_batch_rotated_geometry_weighted(state, GEOMETRY_TRIANGLE);
        } else if (command == CMD_RESIDENT_HILL_CLIMB_ROTATED) {
            server_resident_hill_climb_rotated(state);
        } else if (command == CMD_RESIDENT_SELECT_LAYER_ROTATED) {
            server_resident_select_layer_rotated(state);
        } else if (command == CMD_RESIDENT_SELECT_LAYER_ROTATED_DEVICE_CHUNK) {
            server_resident_select_layer_rotated(state, true);
        } else if (command == CMD_RESIDENT_SELECT_LAYER_MIXED) {
            server_resident_select_layer_mixed(state);
        } else if (command == CMD_RESIDENT_SELECT_LAYER_MIXED_DEVICE_CHUNK) {
            server_resident_select_layer_mixed(state, true);
        } else if (command == CMD_RESIDENT_SELECT_LAYER_GUIDED_DEVICE_CHUNK) {
            server_resident_select_layer_mixed(state, true, true);
        } else if (command == CMD_RESIDENT_SELECT_LAYER_STRUCTURAL_DEVICE_CHUNK) {
            server_resident_select_layer_mixed(state, true, true, true);
        } else if (command == CMD_SCORE_BATCH_CATALOG_GEOMETRY) {
            server_score_batch_catalog_geometry(state, false);
        } else if (command == CMD_SCORE_BATCH_CATALOG_GEOMETRY_WEIGHTED) {
            server_score_batch_catalog_geometry(state, true);
        } else if (command == CMD_UPDATE_CURRENT) {
            server_update_current(state);
        } else if (command == CMD_SHUTDOWN) {
            const std::uint32_t status = 0;
            write_stdout_value(status);
            std::cout.flush();
            return 0;
        } else {
            throw std::runtime_error("unknown server command");
        }
    }
}

int main(int argc, char** argv) {
    try {
        const std::string mode = arg_value(argc, argv, "--mode");
        if (mode.empty() || mode == "smoke") {
            return run_smoke();
        }
        if (mode == "server") {
            return run_server();
        }
        std::fprintf(stderr, "unknown mode: %s\n", mode.c_str());
        return 2;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "error: %s\n", error.what());
        return 1;
    }
}
