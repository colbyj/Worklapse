// Thin wrapper over libaom for Worklapse. It keeps libaom's large, version-specific structs on the C
// side, so the .NET code only sees a few plain functions.
//
// Frames go in as packed I420 (Y plane, then U, then V; chroma planes are ceil(w/2) x ceil(h/2), rows
// packed), BT.709 full range — the format Worklapse captures and stages in. Frames come out either as
// BGRA or not at all (decoding forward through a segment without converting).

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <aom/aom_decoder.h>
#include <aom/aom_encoder.h>
#include <aom/aomcx.h>
#include <aom/aomdx.h>

#ifdef _WIN32
#define WL_API __declspec(dllexport)
#else
#define WL_API __attribute__((visibility("default")))
#endif

#define WL_ERROR_LEN 512

static void set_error(char *buffer, const char *what, aom_codec_ctx_t *codec) {
  if (!buffer) return;
  if (codec) {
    const char *detail = aom_codec_error_detail(codec);
    snprintf(buffer, WL_ERROR_LEN, "%s: %s%s%s", what, aom_codec_error(codec), detail ? " - " : "", detail ? detail : "");
  } else {
    snprintf(buffer, WL_ERROR_LEN, "%s", what);
  }
}

static uint8_t clamp_u8(int v) { return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v); }

// --- colour conversion -------------------------------------------------------------------------

// I420 planes to BGRA, BT.709 full range. Chroma is upsampled by repeating each sample 2x2.
static void i420_planes_to_bgra(const uint8_t *py, int sy, const uint8_t *pu, int su, const uint8_t *pv, int sv,
                                int width, int height, uint8_t *bgra, int stride) {
  for (int y = 0; y < height; y++) {
    const uint8_t *yr = py + (size_t)y * sy, *ur = pu + (size_t)(y >> 1) * su, *vr = pv + (size_t)(y >> 1) * sv;
    uint8_t *out = bgra + (size_t)y * stride;
    for (int x = 0; x < width; x++) {
      const int luma = yr[x] * 1024 + 512;
      const int cb = ur[x >> 1] - 128, cr = vr[x >> 1] - 128;
      out[x * 4] = clamp_u8((luma + 1900 * cb) >> 10);
      out[x * 4 + 1] = clamp_u8((luma - 192 * cb - 479 * cr) >> 10);
      out[x * 4 + 2] = clamp_u8((luma + 1613 * cr) >> 10);
      out[x * 4 + 3] = 255;
    }
  }
}

WL_API int wl_abi_version(void) { return 2; }

WL_API const char *wl_aom_version(void) { return aom_codec_version_str(); }

// Converts a packed I420 frame to BGRA.
WL_API void wl_i420_to_bgra(const uint8_t *i420, int width, int height, uint8_t *bgra, int stride) {
  const int cw = (width + 1) / 2, ch = (height + 1) / 2;
  const uint8_t *py = i420, *pu = i420 + (size_t)width * height, *pv = pu + (size_t)cw * ch;
  i420_planes_to_bgra(py, width, pu, cw, pv, cw, width, height, bgra, stride);
}

// --- encoder -----------------------------------------------------------------------------------

typedef struct wl_encoder {
  aom_codec_ctx_t codec;
  aom_image_t image;
  int width, height, quality;
  uint8_t *out;
  size_t out_capacity;
  char error[WL_ERROR_LEN];
} wl_encoder;

WL_API void wl_enc_destroy(wl_encoder *e) {
  if (!e) return;
  aom_codec_destroy(&e->codec);
  aom_img_free(&e->image);
  free(e->out);
  free(e);
}

// Creates a low-delay encoder: every frame is returned by the call that submitted it. The first frame
// is a key frame and no others are inserted; a segment is one encoder's output.
WL_API wl_encoder *wl_enc_create(int width, int height, int quality, int cpu_used, int threads, char *error) {
  aom_codec_iface_t *iface = aom_codec_av1_cx();
  aom_codec_enc_cfg_t cfg;
  if (aom_codec_enc_config_default(iface, &cfg, AOM_USAGE_GOOD_QUALITY) != AOM_CODEC_OK) {
    set_error(error, "aom_codec_enc_config_default failed", NULL);
    return NULL;
  }
  cfg.g_w = (unsigned)width;
  cfg.g_h = (unsigned)height;
  cfg.g_timebase.num = 1;
  cfg.g_timebase.den = 1000;
  cfg.g_threads = (unsigned)threads;
  cfg.g_lag_in_frames = 0;
  cfg.g_pass = AOM_RC_ONE_PASS;
  cfg.rc_end_usage = AOM_Q;
  cfg.kf_mode = AOM_KF_DISABLED;

  wl_encoder *e = (wl_encoder *)calloc(1, sizeof(wl_encoder));
  if (!e) {
    set_error(error, "out of memory", NULL);
    return NULL;
  }
  e->width = width;
  e->height = height;
  e->quality = quality;

  if (aom_codec_enc_init(&e->codec, iface, &cfg, 0) != AOM_CODEC_OK) {
    set_error(error, "aom_codec_enc_init failed", &e->codec);
    free(e);
    return NULL;
  }

#define CONTROL(id, value)                                         \
  if (aom_codec_control(&e->codec, id, value) != AOM_CODEC_OK) {   \
    set_error(error, "aom_codec_control(" #id ") failed", &e->codec); \
    wl_enc_destroy(e);                                             \
    return NULL;                                                   \
  }
  CONTROL(AOME_SET_CPUUSED, cpu_used);
  CONTROL(AOME_SET_CQ_LEVEL, (unsigned)quality);
  CONTROL(AV1E_SET_ROW_MT, 1u);
  CONTROL(AV1E_SET_COLOR_PRIMARIES, AOM_CICP_CP_BT_709);
  CONTROL(AV1E_SET_TRANSFER_CHARACTERISTICS, AOM_CICP_TC_SRGB);
  CONTROL(AV1E_SET_MATRIX_COEFFICIENTS, AOM_CICP_MC_BT_709);
  CONTROL(AV1E_SET_COLOR_RANGE, AOM_CR_FULL_RANGE);
#undef CONTROL

  if (!aom_img_alloc(&e->image, AOM_IMG_FMT_I420, (unsigned)width, (unsigned)height, 32)) {
    set_error(error, "aom_img_alloc failed", NULL);
    wl_enc_destroy(e);
    return NULL;
  }
  e->image.range = AOM_CR_FULL_RANGE;
  e->image.cp = AOM_CICP_CP_BT_709;
  e->image.tc = AOM_CICP_TC_SRGB;
  e->image.mc = AOM_CICP_MC_BT_709;
  return e;
}

static void copy_plane(const uint8_t *src, int src_stride, uint8_t *dst, int dst_stride, int width, int height) {
  for (int y = 0; y < height; y++) memcpy(dst + (size_t)y * dst_stride, src + (size_t)y * src_stride, (size_t)width);
}

// Encodes one packed I420 frame. quality < 0 keeps the current quantizer. On success returns the size of
// the compressed frame and points *data at it (valid until the next call); returns -1 on failure.
WL_API int wl_enc_encode_i420(wl_encoder *e, const uint8_t *i420, int64_t pts_ms, int quality, const uint8_t **data,
                              int *is_key) {
  const int w = e->width, h = e->height, cw = (w + 1) / 2, ch = (h + 1) / 2;
  const uint8_t *py = i420, *pu = i420 + (size_t)w * h, *pv = pu + (size_t)cw * ch;
  copy_plane(py, w, e->image.planes[AOM_PLANE_Y], e->image.stride[AOM_PLANE_Y], w, h);
  copy_plane(pu, cw, e->image.planes[AOM_PLANE_U], e->image.stride[AOM_PLANE_U], cw, ch);
  copy_plane(pv, cw, e->image.planes[AOM_PLANE_V], e->image.stride[AOM_PLANE_V], cw, ch);

  if (quality >= 0 && quality != e->quality) {
    if (aom_codec_control(&e->codec, AOME_SET_CQ_LEVEL, (unsigned)quality) != AOM_CODEC_OK) {
      set_error(e->error, "aom_codec_control(AOME_SET_CQ_LEVEL) failed", &e->codec);
      return -1;
    }
    e->quality = quality;
  }

  if (aom_codec_encode(&e->codec, &e->image, pts_ms, 1, 0) != AOM_CODEC_OK) {
    set_error(e->error, "aom_codec_encode failed", &e->codec);
    return -1;
  }

  size_t size = 0;
  int key = 0;
  aom_codec_iter_t iter = NULL;
  const aom_codec_cx_pkt_t *packet;
  while ((packet = aom_codec_get_cx_data(&e->codec, &iter)) != NULL) {
    if (packet->kind != AOM_CODEC_CX_FRAME_PKT) continue;
    const size_t needed = size + packet->data.frame.sz;
    if (needed > e->out_capacity) {
      size_t capacity = e->out_capacity ? e->out_capacity : 65536;
      while (capacity < needed) capacity *= 2;
      uint8_t *grown = (uint8_t *)realloc(e->out, capacity);
      if (!grown) {
        set_error(e->error, "out of memory", NULL);
        return -1;
      }
      e->out = grown;
      e->out_capacity = capacity;
    }
    memcpy(e->out + size, packet->data.frame.buf, packet->data.frame.sz);
    size = needed;
    if (packet->data.frame.flags & AOM_FRAME_IS_KEY) key = 1;
  }

  if (size == 0) {
    set_error(e->error, "the encoder returned no data for a frame", NULL);
    return -1;
  }
  if (size > INT32_MAX) {
    set_error(e->error, "encoded frame too large", NULL);
    return -1;
  }
  *data = e->out;
  *is_key = key;
  return (int)size;
}

WL_API const char *wl_enc_error(wl_encoder *e) { return e->error; }

// Sets an encoder option by its aomenc name (for example "enable-restoration" = "0"). Must be called
// before the first frame for options that shape the stream. Returns 0 on success, -1 on failure.
WL_API int wl_enc_set_option(wl_encoder *e, const char *name, const char *value) {
  if (aom_codec_set_option(&e->codec, name, value) != AOM_CODEC_OK) {
    set_error(e->error, "aom_codec_set_option failed", &e->codec);
    return -1;
  }
  return 0;
}

// --- decoder -----------------------------------------------------------------------------------

typedef struct wl_decoder {
  aom_codec_ctx_t codec;
  aom_image_t *last;
  char error[WL_ERROR_LEN];
} wl_decoder;

WL_API wl_decoder *wl_dec_create(int threads, char *error) {
  wl_decoder *d = (wl_decoder *)calloc(1, sizeof(wl_decoder));
  if (!d) {
    set_error(error, "out of memory", NULL);
    return NULL;
  }
  aom_codec_dec_cfg_t cfg = {0};
  cfg.threads = (unsigned)threads;
  cfg.allow_lowbitdepth = 1;
  if (aom_codec_dec_init(&d->codec, aom_codec_av1_dx(), &cfg, 0) != AOM_CODEC_OK) {
    set_error(error, "aom_codec_dec_init failed", &d->codec);
    free(d);
    return NULL;
  }
  return d;
}

WL_API void wl_dec_destroy(wl_decoder *d) {
  if (!d) return;
  aom_codec_destroy(&d->codec);
  free(d);
}

// Decodes one frame; its size is returned through width/height. Returns 0 on success, -1 on failure.
WL_API int wl_dec_decode(wl_decoder *d, const uint8_t *data, int size, int *width, int *height) {
  d->last = NULL;
  if (aom_codec_decode(&d->codec, data, (size_t)size, NULL) != AOM_CODEC_OK) {
    set_error(d->error, "aom_codec_decode failed", &d->codec);
    return -1;
  }
  aom_codec_iter_t iter = NULL;
  aom_image_t *img;
  while ((img = aom_codec_get_frame(&d->codec, &iter)) != NULL) d->last = img;
  if (!d->last) {
    set_error(d->error, "the decoder returned no image", NULL);
    return -1;
  }
  if (d->last->fmt != AOM_IMG_FMT_I420) {
    set_error(d->error, "unexpected image format (expected 8-bit 4:2:0)", NULL);
    d->last = NULL;
    return -1;
  }
  *width = (int)d->last->d_w;
  *height = (int)d->last->d_h;
  return 0;
}

// Converts the most recently decoded frame to BGRA. Returns 0 on success, -1 if there is none.
WL_API int wl_dec_copy_bgra(wl_decoder *d, uint8_t *bgra, int stride) {
  if (!d->last) {
    set_error(d->error, "no decoded frame", NULL);
    return -1;
  }
  const aom_image_t *img = d->last;
  i420_planes_to_bgra(img->planes[AOM_PLANE_Y], img->stride[AOM_PLANE_Y], img->planes[AOM_PLANE_U],
                      img->stride[AOM_PLANE_U], img->planes[AOM_PLANE_V], img->stride[AOM_PLANE_V], (int)img->d_w,
                      (int)img->d_h, bgra, stride);
  return 0;
}

WL_API const char *wl_dec_error(wl_decoder *d) { return d->error; }
