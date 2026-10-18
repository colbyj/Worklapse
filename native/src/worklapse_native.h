// Shared declarations of Worklapse's native library: AV1 video (av1.c), JPEG and WebP images (image.c) and the
// colour conversions they share (color.c).
//
// Frames are packed I420 throughout (Y plane, then U, then V; chroma planes are ceil(w/2) x ceil(h/2), rows packed),
// BT.709 full range — the format Worklapse captures and stages in.

#ifndef WORKLAPSE_NATIVE_H
#define WORKLAPSE_NATIVE_H

#include <stdint.h>

#ifdef _WIN32
#define WL_API __declspec(dllexport)
#else
#define WL_API __attribute__((visibility("default")))
#endif

#define WL_ERROR_LEN 512

// Bump when an exported function's signature or behaviour changes; Worklapse.Video checks it.
#define WL_ABI_VERSION 4

static inline uint8_t wl_clamp_u8(int v) { return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v); }

// I420 planes to BGRA, BT.709 full range. Chroma is upsampled by repeating each sample 2x2; row y of the output
// uses chroma row (first_row + y) / 2, so a strip may start on any row of the frame.
void wl_i420_planes_to_bgra(const uint8_t *py, int sy, const uint8_t *pu, int su, const uint8_t *pv, int sv, int width,
                            int height, uint8_t *bgra, int stride);

// Rows first_row .. first_row + count - 1 of a packed I420 frame as BGRA (or BGRX: the fourth byte is 255).
void wl_i420_rows_to_bgra(const uint8_t *i420, int width, int height, int first_row, int count, uint8_t *bgra,
                          int stride);

// Two BGRA rows (row1 may equal row0 for a frame's odd last row) into luma rows y0 and y0 + 1 (y1 may be NULL for that
// last row) and chroma row y0 / 2 of a packed I420 frame. The coefficients match Worklapse.Core's I420.FromBgraRows.
void wl_bgra_pair_to_i420(const uint8_t *row0, const uint8_t *row1, int width, uint8_t *y0, uint8_t *y1, uint8_t *u,
                          uint8_t *v);

// Packed I420 in BT.709 full range to planes in BT.601 limited range (what WebP stores), and back in place.
void wl_i420_709_full_to_601_limited(const uint8_t *i420, int width, int height, uint8_t *y, int y_stride, uint8_t *u,
                                     uint8_t *v, int uv_stride);
void wl_i420_601_limited_to_709_full(uint8_t *i420, int width, int height);

#endif
