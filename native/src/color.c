// Colour conversions shared by the video and image code.
//
// All arithmetic is integer ("fixed point"): a coefficient such as 1.8556 is stored multiplied by a power of two
// (1.8556 * 1024 = 1900), products are summed, and the sum is shifted back down. Adding half the divisor before the
// shift rounds to the nearest value instead of down.

#include <stddef.h>

#include "worklapse_native.h"

// --- I420 to BGRA ---------------------------------------------------------------------------------

// BT.709, full range, from YCbCr (Cb and Cr centred on 0) to RGB:
//   R = Y              + 1.5748 Cr
//   G = Y - 0.1873 Cb  - 0.4681 Cr
//   B = Y + 1.8556 Cb
// Coefficients scaled by 1024 (10 fractional bits).
#define TO_RGB_SHIFT 10
#define TO_RGB_HALF (1 << (TO_RGB_SHIFT - 1))
#define CR_TO_R 1613
#define CB_TO_G 192
#define CR_TO_G 479
#define CB_TO_B 1900

// One row of pixels. `u_row` and `v_row` are the chroma rows for this luma row; each chroma sample covers two
// pixels.
static void row_to_bgra(const uint8_t *y_row, const uint8_t *u_row, const uint8_t *v_row, int width, uint8_t *out)
{
    for (int x = 0; x < width; x++) {
        const int luma = (y_row[x] << TO_RGB_SHIFT) + TO_RGB_HALF;
        const int cb = u_row[x / 2] - 128;
        const int cr = v_row[x / 2] - 128;

        uint8_t *pixel = out + x * 4;
        pixel[0] = wl_clamp_u8((luma + CB_TO_B * cb) >> TO_RGB_SHIFT);
        pixel[1] = wl_clamp_u8((luma - CB_TO_G * cb - CR_TO_G * cr) >> TO_RGB_SHIFT);
        pixel[2] = wl_clamp_u8((luma + CR_TO_R * cr) >> TO_RGB_SHIFT);
        pixel[3] = 255;
    }
}

void wl_i420_planes_to_bgra(const uint8_t *y_plane, int y_stride, const uint8_t *u_plane, int u_stride,
                            const uint8_t *v_plane, int v_stride, int width, int height, uint8_t *bgra, int stride)
{
    for (int y = 0; y < height; y++) {
        row_to_bgra(wl_const_row(y_plane, y_stride, y),
                    wl_const_row(u_plane, u_stride, y / 2),
                    wl_const_row(v_plane, v_stride, y / 2),
                    width,
                    wl_row(bgra, stride, y));
    }
}

void wl_i420_rows_to_bgra(const uint8_t *i420, int width, int height, int first_row, int count, uint8_t *bgra,
                          int stride)
{
    const wl_i420 frame = wl_i420_planes(i420, width, height);
    for (int i = 0; i < count; i++) {
        const int y = first_row + i;
        row_to_bgra(wl_row(frame.y, frame.width, y),
                    wl_row(frame.u, frame.chroma_width, y / 2),
                    wl_row(frame.v, frame.chroma_width, y / 2),
                    width,
                    wl_row(bgra, stride, i));
    }
}

// Converts a packed I420 frame to BGRA.
WL_API void wl_i420_to_bgra(const uint8_t *i420, int width, int height, uint8_t *bgra, int stride)
{
    wl_i420_rows_to_bgra(i420, width, height, 0, height, bgra, stride);
}

// --- BGRA to I420 ---------------------------------------------------------------------------------

// BT.709, full range, from RGB to YCbCr:
//   Y  =  0.2126 R + 0.7152 G + 0.0722 B
//   Cb = -0.1146 R - 0.3854 G + 0.5    B
//   Cr =  0.5    R - 0.4542 G - 0.0458 B
// Coefficients scaled by 256 (8 fractional bits). Each row adds up exactly (Y to 256, so white stays 255; Cb and
// Cr to 0, so greys have no colour), which is why B's luma weight rounds up to 19.
#define TO_LUMA_SHIFT 8
#define TO_LUMA_HALF (1 << (TO_LUMA_SHIFT - 1))
#define R_TO_Y 54
#define G_TO_Y 183
#define B_TO_Y 19
#define R_TO_CB (-29)
#define G_TO_CB (-99)
#define B_TO_CB 128
#define R_TO_CR 128
#define G_TO_CR (-116)
#define B_TO_CR (-12)

// Chroma is computed from the sum of a 2x2 block of pixels: 4 times the average, so 2 more bits to shift away.
#define TO_CHROMA_SHIFT (TO_LUMA_SHIFT + 2)
#define TO_CHROMA_HALF (1 << (TO_CHROMA_SHIFT - 1))

static uint8_t luma_of(const uint8_t *bgra_pixel)
{
    const int b = bgra_pixel[0];
    const int g = bgra_pixel[1];
    const int r = bgra_pixel[2];
    return (uint8_t)((R_TO_Y * r + G_TO_Y * g + B_TO_Y * b + TO_LUMA_HALF) >> TO_LUMA_SHIFT);
}

void wl_bgra_pair_to_i420(const uint8_t *row0, const uint8_t *row1, int width, uint8_t *y0, uint8_t *y1, uint8_t *u,
                          uint8_t *v)
{
    for (int x = 0; x < width; x++) {
        y0[x] = luma_of(row0 + x * 4);
        if (y1) {
            y1[x] = luma_of(row1 + x * 4);
        }
    }

    // Each chroma sample comes from a 2x2 block of pixels. When the width is odd, the last block's right column
    // repeats its left one.
    const int chroma_width = wl_chroma_size(width);
    for (int cx = 0; cx < chroma_width; cx++) {
        const int left_x = cx * 2;
        const int right_x = left_x + 1 < width ? left_x + 1 : left_x;
        const uint8_t *top_left = row0 + left_x * 4;
        const uint8_t *top_right = row0 + right_x * 4;
        const uint8_t *bottom_left = row1 + left_x * 4;
        const uint8_t *bottom_right = row1 + right_x * 4;

        const int b = top_left[0] + top_right[0] + bottom_left[0] + bottom_right[0];
        const int g = top_left[1] + top_right[1] + bottom_left[1] + bottom_right[1];
        const int r = top_left[2] + top_right[2] + bottom_left[2] + bottom_right[2];

        const int cb = (R_TO_CB * r + G_TO_CB * g + B_TO_CB * b + TO_CHROMA_HALF) >> TO_CHROMA_SHIFT;
        const int cr = (R_TO_CR * r + G_TO_CR * g + B_TO_CR * b + TO_CHROMA_HALF) >> TO_CHROMA_SHIFT;
        u[cx] = wl_clamp_u8(cb + 128);
        v[cx] = wl_clamp_u8(cr + 128);
    }
}

// --- BT.709 full range <-> BT.601 limited range ---------------------------------------------------
//
// WebP files are BT.601 limited range, Worklapse's frames BT.709 full range. Both describe RGB colours with a
// luma and two chroma values, but with different weights for R, G and B (the matrix) and different value ranges.

// A colour converted from BT.709 YCbCr to BT.601 YCbCr, both full range, with Cb and Cr centred on 0:
//   Y601  = Y709 + y_cb  * Cb709 + y_cr  * Cr709    (the luma itself carries over unchanged)
//   Cb601 =        cb_cb * Cb709 + cb_cr * Cr709
//   Cr601 =        cr_cb * Cb709 + cr_cr * Cr709
typedef struct {
    double y_cb, y_cr;
    double cb_cb, cb_cr;
    double cr_cb, cr_cr;
} matrix_709_to_601;

// Worked out from the RGB that a BT.709 YCbCr colour stands for:
//   R = Y + 2(1 - Kr) Cr,   B = Y + 2(1 - Kb) Cb,   G = (Y - Kr R - Kb B) / Kg
// then converting that RGB with BT.601's Kr and Kb.
static matrix_709_to_601 bt709_to_bt601(void)
{
    const double kr_709 = 0.2126;
    const double kb_709 = 0.0722;
    const double kg_709 = 1 - kr_709 - kb_709;
    const double kr_601 = 0.299;
    const double kb_601 = 0.114;
    const double kg_601 = 1 - kr_601 - kb_601;

    // R, G and B as Y + (x) Cb + (y) Cr, in BT.709.
    const double r_from_cb = 0;
    const double r_from_cr = 2 * (1 - kr_709);
    const double b_from_cb = 2 * (1 - kb_709);
    const double b_from_cr = 0;
    const double g_from_cb = -kb_709 * b_from_cb / kg_709;
    const double g_from_cr = -kr_709 * r_from_cr / kg_709;

    matrix_709_to_601 m;
    m.y_cb = kr_601 * r_from_cb + kg_601 * g_from_cb + kb_601 * b_from_cb;
    m.y_cr = kr_601 * r_from_cr + kg_601 * g_from_cr + kb_601 * b_from_cr;
    m.cb_cb = (b_from_cb - m.y_cb) / (2 * (1 - kb_601));
    m.cb_cr = (b_from_cr - m.y_cr) / (2 * (1 - kb_601));
    m.cr_cb = (r_from_cb - m.y_cb) / (2 * (1 - kr_601));
    m.cr_cr = (r_from_cr - m.y_cr) / (2 * (1 - kr_601));
    return m;
}

// Fixed point with 16 fractional bits.
#define FIXED_SHIFT 16
#define FIXED_HALF (1 << (FIXED_SHIFT - 1))

static int to_fixed(double x)
{
    return (int)(x * 65536.0 + (x < 0 ? -0.5 : 0.5));
}

// Limited range squeezes luma into 16..235 and chroma into 16..240 (around 128).
#define LIMITED_LUMA_OFFSET 16
#define LIMITED_LUMA_SCALE (219.0 / 255.0)
#define LIMITED_CHROMA_SCALE (224.0 / 255.0)

void wl_i420_709_full_to_601_limited(const uint8_t *i420, int width, int height, uint8_t *y_plane, int y_stride,
                                     uint8_t *u_plane, uint8_t *v_plane, int uv_stride)
{
    // The matrix and the range change combined, in fixed point.
    const matrix_709_to_601 m = bt709_to_bt601();
    const int y_from_y = to_fixed(LIMITED_LUMA_SCALE);
    const int y_from_cb = to_fixed(LIMITED_LUMA_SCALE * m.y_cb);
    const int y_from_cr = to_fixed(LIMITED_LUMA_SCALE * m.y_cr);
    const int cb_from_cb = to_fixed(LIMITED_CHROMA_SCALE * m.cb_cb);
    const int cb_from_cr = to_fixed(LIMITED_CHROMA_SCALE * m.cb_cr);
    const int cr_from_cb = to_fixed(LIMITED_CHROMA_SCALE * m.cr_cb);
    const int cr_from_cr = to_fixed(LIMITED_CHROMA_SCALE * m.cr_cr);
    const int luma_offset = LIMITED_LUMA_OFFSET << FIXED_SHIFT;

    const wl_i420 in = wl_i420_planes(i420, width, height);

    for (int row = 0; row < in.height; row++) {
        const uint8_t *y_in = wl_row(in.y, in.width, row);
        const uint8_t *u_in = wl_row(in.u, in.chroma_width, row / 2);
        const uint8_t *v_in = wl_row(in.v, in.chroma_width, row / 2);
        uint8_t *y_out = wl_row(y_plane, y_stride, row);
        for (int x = 0; x < in.width; x++) {
            const int cb = u_in[x / 2] - 128;
            const int cr = v_in[x / 2] - 128;
            const int luma = y_from_y * y_in[x] + y_from_cb * cb + y_from_cr * cr + luma_offset;
            y_out[x] = wl_clamp_u8((luma + FIXED_HALF) >> FIXED_SHIFT);
        }
    }

    for (int row = 0; row < in.chroma_height; row++) {
        const uint8_t *u_in = wl_row(in.u, in.chroma_width, row);
        const uint8_t *v_in = wl_row(in.v, in.chroma_width, row);
        uint8_t *u_out = wl_row(u_plane, uv_stride, row);
        uint8_t *v_out = wl_row(v_plane, uv_stride, row);
        for (int x = 0; x < in.chroma_width; x++) {
            const int cb = u_in[x] - 128;
            const int cr = v_in[x] - 128;
            u_out[x] = wl_clamp_u8(((cb_from_cb * cb + cb_from_cr * cr + FIXED_HALF) >> FIXED_SHIFT) + 128);
            v_out[x] = wl_clamp_u8(((cr_from_cb * cb + cr_from_cr * cr + FIXED_HALF) >> FIXED_SHIFT) + 128);
        }
    }
}

void wl_i420_601_limited_to_709_full(uint8_t *i420, int width, int height)
{
    // Undo the range, then the chroma matrix (inverted), then the chroma's contribution to luma.
    const matrix_709_to_601 m = bt709_to_bt601();
    const double determinant = m.cb_cb * m.cr_cr - m.cb_cr * m.cr_cb;
    const double unscale = 1 / LIMITED_CHROMA_SCALE;

    // BT.709 Cb and Cr from the stored chroma (U - 128, V - 128).
    const double cb709_from_u = unscale * m.cr_cr / determinant;
    const double cb709_from_v = -unscale * m.cb_cr / determinant;
    const double cr709_from_u = -unscale * m.cr_cb / determinant;
    const double cr709_from_v = unscale * m.cb_cb / determinant;

    const int y_from_y = to_fixed(1 / LIMITED_LUMA_SCALE);
    const int y_from_u = to_fixed(-m.y_cb * cb709_from_u - m.y_cr * cr709_from_u);
    const int y_from_v = to_fixed(-m.y_cb * cb709_from_v - m.y_cr * cr709_from_v);
    const int cb_from_u = to_fixed(cb709_from_u);
    const int cb_from_v = to_fixed(cb709_from_v);
    const int cr_from_u = to_fixed(cr709_from_u);
    const int cr_from_v = to_fixed(cr709_from_v);

    const wl_i420 frame = wl_i420_planes(i420, width, height);

    // Luma first: it needs the chroma as it is now, before the chroma is converted in place.
    for (int row = 0; row < frame.height; row++) {
        uint8_t *y_row = wl_row(frame.y, frame.width, row);
        const uint8_t *u_row = wl_row(frame.u, frame.chroma_width, row / 2);
        const uint8_t *v_row = wl_row(frame.v, frame.chroma_width, row / 2);
        for (int x = 0; x < frame.width; x++) {
            const int u = u_row[x / 2] - 128;
            const int v = v_row[x / 2] - 128;
            const int luma = y_from_y * (y_row[x] - LIMITED_LUMA_OFFSET) + y_from_u * u + y_from_v * v;
            y_row[x] = wl_clamp_u8((luma + FIXED_HALF) >> FIXED_SHIFT);
        }
    }

    for (int row = 0; row < frame.chroma_height; row++) {
        uint8_t *u_row = wl_row(frame.u, frame.chroma_width, row);
        uint8_t *v_row = wl_row(frame.v, frame.chroma_width, row);
        for (int x = 0; x < frame.chroma_width; x++) {
            const int u = u_row[x] - 128;
            const int v = v_row[x] - 128;
            u_row[x] = wl_clamp_u8(((cb_from_u * u + cb_from_v * v + FIXED_HALF) >> FIXED_SHIFT) + 128);
            v_row[x] = wl_clamp_u8(((cr_from_u * u + cr_from_v * v + FIXED_HALF) >> FIXED_SHIFT) + 128);
        }
    }
}
