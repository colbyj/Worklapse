// Shared declarations of Worklapse's native library:
//   av1.c    AV1 video (libaom)
//   image.c  JPEG and WebP images (libjpeg-turbo, libwebp)
//   color.c  the colour conversions both use
//
// Every frame Worklapse hands over is "packed I420" in BT.709 full range:
//   - the Y (luma) plane, width x height bytes,
//   - then the U (Cb) plane, then the V (Cr) plane, each ceil(width / 2) x ceil(height / 2) bytes,
//   - with no padding at the end of rows.
// That is the format Worklapse captures and stages frames in (Worklapse.Core's I420 class).

#ifndef WORKLAPSE_NATIVE_H
#define WORKLAPSE_NATIVE_H

#include <stdint.h>

// Marks the functions Worklapse.Video calls (WorklapseNative.cs).
#ifdef _WIN32
#define WL_API __declspec(dllexport)
#else
#define WL_API __attribute__((visibility("default")))
#endif

// Size of the error buffers the .NET side passes in, including the terminating zero.
#define WL_ERROR_LEN 512

// Bump when an exported function's signature or behaviour changes; Worklapse.Video checks it on first use.
#define WL_ABI_VERSION 4

// --- packed I420 frames ---------------------------------------------------------------------------

// Where the planes of a packed I420 frame are.
typedef struct {
    uint8_t *y;
    uint8_t *u;
    uint8_t *v;
    int width;          // of the Y plane; also its row length
    int height;
    int chroma_width;   // of the U and V planes; also their row length
    int chroma_height;
} wl_i420;

static inline int wl_chroma_size(int luma_size) { return (luma_size + 1) / 2; }

// Finds the planes of a packed I420 frame. The pointers aren't const, but a frame passed in as const must only
// be read through them.
static inline wl_i420 wl_i420_planes(const uint8_t *frame, int width, int height)
{
    wl_i420 planes;
    planes.width = width;
    planes.height = height;
    planes.chroma_width = wl_chroma_size(width);
    planes.chroma_height = wl_chroma_size(height);
    planes.y = (uint8_t *)frame;
    planes.u = planes.y + (size_t)width * height;
    planes.v = planes.u + (size_t)planes.chroma_width * planes.chroma_height;
    return planes;
}

// Row `row` of a plane whose rows are `stride` bytes apart.
static inline uint8_t *wl_row(uint8_t *plane, int stride, int row) { return plane + (size_t)row * stride; }
static inline const uint8_t *wl_const_row(const uint8_t *plane, int stride, int row)
{
    return plane + (size_t)row * stride;
}

static inline uint8_t wl_clamp_u8(int value)
{
    if (value < 0) {
        return 0;
    }
    if (value > 255) {
        return 255;
    }
    return (uint8_t)value;
}

// --- colour conversions (color.c) -----------------------------------------------------------------

// I420 planes (each with its own stride) to BGRA, BT.709 full range. Chroma is upsampled by repeating each sample
// for its 2x2 block of pixels.
void wl_i420_planes_to_bgra(const uint8_t *y_plane, int y_stride, const uint8_t *u_plane, int u_stride,
                            const uint8_t *v_plane, int v_stride, int width, int height, uint8_t *bgra, int stride);

// Rows first_row .. first_row + count - 1 of a packed I420 frame as BGRA (or BGRX: the fourth byte is always 255).
// The strip may start on any row of the frame.
void wl_i420_rows_to_bgra(const uint8_t *i420, int width, int height, int first_row, int count, uint8_t *bgra,
                          int stride);

// Two BGRA rows into luma rows `y0` and `y1` and the chroma rows `u` and `v` they share, all in packed I420. For a
// frame's odd last row, pass the same row as row0 and row1, and NULL for y1. The coefficients match
// Worklapse.Core's I420.FromBgraRows, so both give the same frame.
void wl_bgra_pair_to_i420(const uint8_t *row0, const uint8_t *row1, int width, uint8_t *y0, uint8_t *y1, uint8_t *u,
                          uint8_t *v);

// Packed I420 in BT.709 full range to separate planes in BT.601 limited range (what WebP files hold).
void wl_i420_709_full_to_601_limited(const uint8_t *i420, int width, int height, uint8_t *y_plane, int y_stride,
                                     uint8_t *u_plane, uint8_t *v_plane, int uv_stride);

// The reverse, in place: packed I420 in BT.601 limited range to BT.709 full range.
void wl_i420_601_limited_to_709_full(uint8_t *i420, int width, int height);

#endif
