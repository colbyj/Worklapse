// Colour conversions shared by the video and image code.

#include <stddef.h>

#include "worklapse_native.h"

// --- BGRA ---------------------------------------------------------------------------------------

static void row_to_bgra(const uint8_t *yr, const uint8_t *ur, const uint8_t *vr, int width, uint8_t *out) {
  for (int x = 0; x < width; x++) {
    const int luma = yr[x] * 1024 + 512;
    const int cb = ur[x >> 1] - 128, cr = vr[x >> 1] - 128;
    out[x * 4] = wl_clamp_u8((luma + 1900 * cb) >> 10);
    out[x * 4 + 1] = wl_clamp_u8((luma - 192 * cb - 479 * cr) >> 10);
    out[x * 4 + 2] = wl_clamp_u8((luma + 1613 * cr) >> 10);
    out[x * 4 + 3] = 255;
  }
}

void wl_i420_planes_to_bgra(const uint8_t *py, int sy, const uint8_t *pu, int su, const uint8_t *pv, int sv, int width,
                            int height, uint8_t *bgra, int stride) {
  for (int y = 0; y < height; y++)
    row_to_bgra(py + (size_t)y * sy, pu + (size_t)(y >> 1) * su, pv + (size_t)(y >> 1) * sv, width,
                bgra + (size_t)y * stride);
}

void wl_i420_rows_to_bgra(const uint8_t *i420, int width, int height, int first_row, int count, uint8_t *bgra,
                          int stride) {
  const int cw = (width + 1) / 2, ch = (height + 1) / 2;
  const uint8_t *py = i420, *pu = i420 + (size_t)width * height, *pv = pu + (size_t)cw * ch;
  for (int i = 0; i < count; i++) {
    const int y = first_row + i;
    row_to_bgra(py + (size_t)y * width, pu + (size_t)(y >> 1) * cw, pv + (size_t)(y >> 1) * cw, width,
                bgra + (size_t)i * stride);
  }
}

// Converts a packed I420 frame to BGRA.
WL_API void wl_i420_to_bgra(const uint8_t *i420, int width, int height, uint8_t *bgra, int stride) {
  wl_i420_rows_to_bgra(i420, width, height, 0, height, bgra, stride);
}

void wl_bgra_pair_to_i420(const uint8_t *row0, const uint8_t *row1, int width, uint8_t *y0, uint8_t *y1, uint8_t *u,
                          uint8_t *v) {
  for (int x = 0; x < width; x++) {
    const uint8_t *p = row0 + x * 4;
    y0[x] = (uint8_t)((54 * p[2] + 183 * p[1] + 19 * p[0] + 128) >> 8);
    if (y1) {
      p = row1 + x * 4;
      y1[x] = (uint8_t)((54 * p[2] + 183 * p[1] + 19 * p[0] + 128) >> 8);
    }
  }
  // Chroma from the average of each 2x2 block; a frame's odd last column repeats.
  const int cw = (width + 1) / 2;
  for (int cx = 0; cx < cw; cx++) {
    const int x0 = cx * 2 * 4, x1 = (cx * 2 + 1 < width ? cx * 2 + 1 : cx * 2) * 4;
    const int b = row0[x0] + row0[x1] + row1[x0] + row1[x1];
    const int g = row0[x0 + 1] + row0[x1 + 1] + row1[x0 + 1] + row1[x1 + 1];
    const int r = row0[x0 + 2] + row0[x1 + 2] + row1[x0 + 2] + row1[x1 + 2];
    u[cx] = wl_clamp_u8(((-29 * r - 99 * g + 128 * b + 512) >> 10) + 128);
    v[cx] = wl_clamp_u8(((128 * r - 116 * g - 12 * b + 512) >> 10) + 128);
  }
}

// --- BT.709 full range <-> BT.601 limited range ---------------------------------------------------

// The same colour in BT.601 as in BT.709, both full range, with Cb and Cr centred on 0:
//   Y601 = Y + a*Cb + b*Cr,  Cb601 = c*Cb + d*Cr,  Cr601 = e*Cb + f*Cr.
// Worked out from the RGB that BT.709 YCbCr stands for: R = Y + 2(1-Kr)Cr, B = Y + 2(1-Kb)Cb, G = (Y - Kr R - Kb B)/Kg.
typedef struct {
  double a, b, c, d, e, f;
} matrix;

static matrix bt709_to_bt601(void) {
  const double kr7 = 0.2126, kb7 = 0.0722, kg7 = 1 - kr7 - kb7;
  const double kr6 = 0.299, kb6 = 0.114, kg6 = 1 - kr6 - kb6;
  // R, G and B as Y + (x)Cb + (y)Cr.
  const double r_cb = 0, r_cr = 2 * (1 - kr7);
  const double b_cb = 2 * (1 - kb7), b_cr = 0;
  const double g_cb = -kb7 * b_cb / kg7, g_cr = -kr7 * r_cr / kg7;
  matrix m;
  m.a = kr6 * r_cb + kg6 * g_cb + kb6 * b_cb;
  m.b = kr6 * r_cr + kg6 * g_cr + kb6 * b_cr;
  m.c = (b_cb - m.a) / (2 * (1 - kb6));
  m.d = (b_cr - m.b) / (2 * (1 - kb6));
  m.e = (r_cb - m.a) / (2 * (1 - kr6));
  m.f = (r_cr - m.b) / (2 * (1 - kr6));
  return m;
}

// Fixed point with 16 fractional bits.
static int fix(double x) { return (int)(x * 65536.0 + (x < 0 ? -0.5 : 0.5)); }
#define FIX_ROUND (1 << 15)

// Limited range: Y in 16..235, chroma 16..240 around 128.
#define LUMA_SCALE (219.0 / 255.0)
#define CHROMA_SCALE (224.0 / 255.0)

void wl_i420_709_full_to_601_limited(const uint8_t *i420, int width, int height, uint8_t *y, int y_stride, uint8_t *u,
                                     uint8_t *v, int uv_stride) {
  const matrix m = bt709_to_bt601();
  const int ky = fix(LUMA_SCALE), kya = fix(LUMA_SCALE * m.a), kyb = fix(LUMA_SCALE * m.b);
  const int kuc = fix(CHROMA_SCALE * m.c), kud = fix(CHROMA_SCALE * m.d);
  const int kve = fix(CHROMA_SCALE * m.e), kvf = fix(CHROMA_SCALE * m.f);

  const int cw = (width + 1) / 2, ch = (height + 1) / 2;
  const uint8_t *py = i420, *pu = i420 + (size_t)width * height, *pv = pu + (size_t)cw * ch;
  for (int row = 0; row < height; row++) {
    const uint8_t *yr = py + (size_t)row * width, *ur = pu + (size_t)(row >> 1) * cw, *vr = pv + (size_t)(row >> 1) * cw;
    uint8_t *out = y + (size_t)row * y_stride;
    for (int x = 0; x < width; x++) {
      const int cb = ur[x >> 1] - 128, cr = vr[x >> 1] - 128;
      out[x] = wl_clamp_u8((ky * yr[x] + kya * cb + kyb * cr + (16 << 16) + FIX_ROUND) >> 16);
    }
  }
  for (int row = 0; row < ch; row++) {
    const uint8_t *ur = pu + (size_t)row * cw, *vr = pv + (size_t)row * cw;
    uint8_t *uo = u + (size_t)row * uv_stride, *vo = v + (size_t)row * uv_stride;
    for (int x = 0; x < cw; x++) {
      const int cb = ur[x] - 128, cr = vr[x] - 128;
      uo[x] = wl_clamp_u8(((kuc * cb + kud * cr + FIX_ROUND) >> 16) + 128);
      vo[x] = wl_clamp_u8(((kve * cb + kvf * cr + FIX_ROUND) >> 16) + 128);
    }
  }
}

void wl_i420_601_limited_to_709_full(uint8_t *i420, int width, int height) {
  // Undo the range, then the chroma matrix (inverted), then the luma's chroma terms.
  const matrix m = bt709_to_bt601();
  const double det = m.c * m.f - m.d * m.e;
  const double s = 1 / CHROMA_SCALE;
  const double icb_u = s * m.f / det, icb_v = -s * m.d / det; // Cb709 from (U - 128, V - 128)
  const double icr_u = -s * m.e / det, icr_v = s * m.c / det; // Cr709 from (U - 128, V - 128)
  const int ky = fix(1 / LUMA_SCALE);
  const int kyu = fix(-m.a * icb_u - m.b * icr_u), kyv = fix(-m.a * icb_v - m.b * icr_v);
  const int kbu = fix(icb_u), kbv = fix(icb_v), kru = fix(icr_u), krv = fix(icr_v);

  const int cw = (width + 1) / 2, ch = (height + 1) / 2;
  uint8_t *py = i420, *pu = i420 + (size_t)width * height, *pv = pu + (size_t)cw * ch;
  // Luma first: it needs the chroma as it is now.
  for (int row = 0; row < height; row++) {
    uint8_t *yr = py + (size_t)row * width;
    const uint8_t *ur = pu + (size_t)(row >> 1) * cw, *vr = pv + (size_t)(row >> 1) * cw;
    for (int x = 0; x < width; x++) {
      const int cu = ur[x >> 1] - 128, cv = vr[x >> 1] - 128;
      yr[x] = wl_clamp_u8((ky * (yr[x] - 16) + kyu * cu + kyv * cv + FIX_ROUND) >> 16);
    }
  }
  for (int row = 0; row < ch; row++) {
    uint8_t *ur = pu + (size_t)row * cw, *vr = pv + (size_t)row * cw;
    for (int x = 0; x < cw; x++) {
      const int cu = ur[x] - 128, cv = vr[x] - 128;
      ur[x] = wl_clamp_u8(((kbu * cu + kbv * cv + FIX_ROUND) >> 16) + 128);
      vr[x] = wl_clamp_u8(((kru * cu + krv * cv + FIX_ROUND) >> 16) + 128);
    }
  }
}
