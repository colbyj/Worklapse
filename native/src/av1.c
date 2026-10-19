// A thin wrapper over libaom for Worklapse. It keeps libaom's large, version-specific structs on the C side, so
// the .NET code only sees a few plain functions.
//
// Frames go in as packed I420, BT.709 full range (see worklapse_native.h). Frames come out as packed I420, as BGRA,
// or not at all (when decoding forward through a segment to reach a later frame).
//
// Every function that can fail returns -1 (or NULL) and leaves a message in an error buffer: the caller's buffer
// while creating an encoder or decoder, the object's own (wl_enc_error, wl_dec_error) after that.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <aom/aom_decoder.h>
#include <aom/aom_encoder.h>
#include <aom/aomcx.h>
#include <aom/aomdx.h>

#include "worklapse_native.h"

// Writes `what` into the error buffer, followed by libaom's explanation when there is a codec to ask.
static void set_error(char *buffer, const char *what, aom_codec_ctx_t *codec)
{
    if (!buffer) {
        return;
    }
    if (!codec) {
        snprintf(buffer, WL_ERROR_LEN, "%s", what);
        return;
    }
    const char *detail = aom_codec_error_detail(codec);
    if (detail) {
        snprintf(buffer, WL_ERROR_LEN, "%s: %s - %s", what, aom_codec_error(codec), detail);
    } else {
        snprintf(buffer, WL_ERROR_LEN, "%s: %s", what, aom_codec_error(codec));
    }
}

// Copies `height` rows of `width` bytes between planes whose rows are spaced differently.
static void copy_plane(const uint8_t *source, int source_stride, uint8_t *target, int target_stride, int width,
                       int height)
{
    for (int y = 0; y < height; y++) {
        memcpy(wl_row(target, target_stride, y), wl_const_row(source, source_stride, y), (size_t)width);
    }
}

WL_API int wl_abi_version(void)
{
    return WL_ABI_VERSION;
}

WL_API const char *wl_aom_version(void)
{
    return aom_codec_version_str();
}

// --- encoder --------------------------------------------------------------------------------------

typedef struct wl_encoder {
    aom_codec_ctx_t codec;
    aom_image_t image;   // libaom's input frame, refilled for every frame
    int width;
    int height;
    int quality;         // the current constant quantizer (AOME_SET_CQ_LEVEL)

    // The latest compressed frame. Grows as needed and is reused, so it stays valid until the next call.
    uint8_t *output;
    size_t output_capacity;

    char error[WL_ERROR_LEN];
} wl_encoder;

WL_API void wl_enc_destroy(wl_encoder *encoder)
{
    if (!encoder) {
        return;
    }
    aom_codec_destroy(&encoder->codec);
    aom_img_free(&encoder->image);
    free(encoder->output);
    free(encoder);
}

// Reports an aom_codec_control failure while creating an encoder, and frees the encoder.
static wl_encoder *control_failed(wl_encoder *encoder, const char *control, char *error)
{
    char what[128];
    snprintf(what, sizeof what, "aom_codec_control(%s) failed", control);
    set_error(error, what, &encoder->codec);
    wl_enc_destroy(encoder);
    return NULL;
}

// Creates a low-delay encoder: every frame is returned by the call that submitted it. The first frame is a key
// frame and no others are inserted; a segment is one encoder's output.
//   quality  the constant quantizer, 0 (best) to 63
//   cpu_used libaom's speed setting; higher is faster and a little larger
// Returns NULL on failure, with a message in `error`.
WL_API wl_encoder *wl_enc_create(int width, int height, int quality, int cpu_used, int threads, char *error)
{
    aom_codec_iface_t *av1 = aom_codec_av1_cx();
    aom_codec_enc_cfg_t config;
    if (aom_codec_enc_config_default(av1, &config, AOM_USAGE_GOOD_QUALITY) != AOM_CODEC_OK) {
        set_error(error, "aom_codec_enc_config_default failed", NULL);
        return NULL;
    }
    config.g_w = (unsigned)width;
    config.g_h = (unsigned)height;
    config.g_timebase.num = 1;               // timestamps in milliseconds
    config.g_timebase.den = 1000;
    config.g_threads = (unsigned)threads;
    config.g_lag_in_frames = 0;              // no look-ahead: each frame comes out straight away
    config.g_pass = AOM_RC_ONE_PASS;
    config.rc_end_usage = AOM_Q;             // constant quantizer
    config.kf_mode = AOM_KF_DISABLED;        // only the first frame is a key frame

    wl_encoder *encoder = (wl_encoder *)calloc(1, sizeof(wl_encoder));
    if (!encoder) {
        set_error(error, "out of memory", NULL);
        return NULL;
    }
    encoder->width = width;
    encoder->height = height;
    encoder->quality = quality;

    if (aom_codec_enc_init(&encoder->codec, av1, &config, 0) != AOM_CODEC_OK) {
        set_error(error, "aom_codec_enc_init failed", &encoder->codec);
        free(encoder);
        return NULL;
    }

    aom_codec_ctx_t *codec = &encoder->codec;
    if (aom_codec_control(codec, AOME_SET_CPUUSED, cpu_used) != AOM_CODEC_OK) {
        return control_failed(encoder, "AOME_SET_CPUUSED", error);
    }
    if (aom_codec_control(codec, AOME_SET_CQ_LEVEL, (unsigned)quality) != AOM_CODEC_OK) {
        return control_failed(encoder, "AOME_SET_CQ_LEVEL", error);
    }
    if (aom_codec_control(codec, AV1E_SET_ROW_MT, 1u) != AOM_CODEC_OK) {
        return control_failed(encoder, "AV1E_SET_ROW_MT", error);
    }

    // Tag the stream as BT.709 full range, so decoders (other programs too) show the right colours.
    if (aom_codec_control(codec, AV1E_SET_COLOR_PRIMARIES, AOM_CICP_CP_BT_709) != AOM_CODEC_OK) {
        return control_failed(encoder, "AV1E_SET_COLOR_PRIMARIES", error);
    }
    if (aom_codec_control(codec, AV1E_SET_TRANSFER_CHARACTERISTICS, AOM_CICP_TC_SRGB) != AOM_CODEC_OK) {
        return control_failed(encoder, "AV1E_SET_TRANSFER_CHARACTERISTICS", error);
    }
    if (aom_codec_control(codec, AV1E_SET_MATRIX_COEFFICIENTS, AOM_CICP_MC_BT_709) != AOM_CODEC_OK) {
        return control_failed(encoder, "AV1E_SET_MATRIX_COEFFICIENTS", error);
    }
    if (aom_codec_control(codec, AV1E_SET_COLOR_RANGE, AOM_CR_FULL_RANGE) != AOM_CODEC_OK) {
        return control_failed(encoder, "AV1E_SET_COLOR_RANGE", error);
    }

    if (!aom_img_alloc(&encoder->image, AOM_IMG_FMT_I420, (unsigned)width, (unsigned)height, 32)) {
        set_error(error, "aom_img_alloc failed", NULL);
        wl_enc_destroy(encoder);
        return NULL;
    }
    encoder->image.range = AOM_CR_FULL_RANGE;
    encoder->image.cp = AOM_CICP_CP_BT_709;
    encoder->image.tc = AOM_CICP_TC_SRGB;
    encoder->image.mc = AOM_CICP_MC_BT_709;
    return encoder;
}

// Appends a packet's data to the encoder's output buffer at `offset`, growing the buffer when needed.
static bool append_output(wl_encoder *encoder, size_t offset, const void *data, size_t size)
{
    const size_t needed = offset + size;
    if (needed > encoder->output_capacity) {
        size_t capacity = encoder->output_capacity ? encoder->output_capacity : 65536;
        while (capacity < needed) {
            capacity *= 2;
        }
        uint8_t *grown = (uint8_t *)realloc(encoder->output, capacity);
        if (!grown) {
            set_error(encoder->error, "out of memory", NULL);
            return false;
        }
        encoder->output = grown;
        encoder->output_capacity = capacity;
    }
    memcpy(encoder->output + offset, data, size);
    return true;
}

// Encodes one packed I420 frame at `pts_ms`. `quality` < 0 keeps the current quantizer.
// On success returns the size of the compressed frame and points *data at it (valid until the next call), and sets
// *is_key to 1 for a key frame. Returns -1 on failure.
WL_API int wl_enc_encode_i420(wl_encoder *encoder, const uint8_t *i420, int64_t pts_ms, int quality,
                              const uint8_t **data, int *is_key)
{
    const wl_i420 frame = wl_i420_planes(i420, encoder->width, encoder->height);
    aom_image_t *image = &encoder->image;
    copy_plane(frame.y, frame.width, image->planes[AOM_PLANE_Y], image->stride[AOM_PLANE_Y], frame.width,
               frame.height);
    copy_plane(frame.u, frame.chroma_width, image->planes[AOM_PLANE_U], image->stride[AOM_PLANE_U],
               frame.chroma_width, frame.chroma_height);
    copy_plane(frame.v, frame.chroma_width, image->planes[AOM_PLANE_V], image->stride[AOM_PLANE_V],
               frame.chroma_width, frame.chroma_height);

    if (quality >= 0 && quality != encoder->quality) {
        if (aom_codec_control(&encoder->codec, AOME_SET_CQ_LEVEL, (unsigned)quality) != AOM_CODEC_OK) {
            set_error(encoder->error, "aom_codec_control(AOME_SET_CQ_LEVEL) failed", &encoder->codec);
            return -1;
        }
        encoder->quality = quality;
    }

    const unsigned long duration = 1;
    const aom_enc_frame_flags_t flags = 0;
    if (aom_codec_encode(&encoder->codec, image, pts_ms, duration, flags) != AOM_CODEC_OK) {
        set_error(encoder->error, "aom_codec_encode failed", &encoder->codec);
        return -1;
    }

    // With no look-ahead the frame comes out now, possibly split over several packets.
    size_t size = 0;
    int key = 0;
    aom_codec_iter_t iterator = NULL;
    const aom_codec_cx_pkt_t *packet;
    while ((packet = aom_codec_get_cx_data(&encoder->codec, &iterator)) != NULL) {
        if (packet->kind != AOM_CODEC_CX_FRAME_PKT) {
            continue;
        }
        if (!append_output(encoder, size, packet->data.frame.buf, packet->data.frame.sz)) {
            return -1;
        }
        size += packet->data.frame.sz;
        if (packet->data.frame.flags & AOM_FRAME_IS_KEY) {
            key = 1;
        }
    }

    if (size == 0) {
        set_error(encoder->error, "the encoder returned no data for a frame", NULL);
        return -1;
    }
    if (size > INT32_MAX) {
        set_error(encoder->error, "encoded frame too large", NULL);
        return -1;
    }
    *data = encoder->output;
    *is_key = key;
    return (int)size;
}

WL_API const char *wl_enc_error(wl_encoder *encoder)
{
    return encoder->error;
}

// Sets an encoder option by its aomenc name (for example "enable-restoration" = "0"). Options that shape the
// stream must be set before the first frame. Returns 0 on success, -1 on failure.
WL_API int wl_enc_set_option(wl_encoder *encoder, const char *name, const char *value)
{
    if (aom_codec_set_option(&encoder->codec, name, value) != AOM_CODEC_OK) {
        set_error(encoder->error, "aom_codec_set_option failed", &encoder->codec);
        return -1;
    }
    return 0;
}

// --- decoder --------------------------------------------------------------------------------------

typedef struct wl_decoder {
    aom_codec_ctx_t codec;
    aom_image_t *last;   // the most recently decoded frame (owned by libaom), or NULL
    char error[WL_ERROR_LEN];
} wl_decoder;

WL_API wl_decoder *wl_dec_create(int threads, char *error)
{
    wl_decoder *decoder = (wl_decoder *)calloc(1, sizeof(wl_decoder));
    if (!decoder) {
        set_error(error, "out of memory", NULL);
        return NULL;
    }
    aom_codec_dec_cfg_t config = {0};
    config.threads = (unsigned)threads;
    config.allow_lowbitdepth = 1;   // 8-bit frames stay 8-bit (AOM_IMG_FMT_I420)
    if (aom_codec_dec_init(&decoder->codec, aom_codec_av1_dx(), &config, 0) != AOM_CODEC_OK) {
        set_error(error, "aom_codec_dec_init failed", &decoder->codec);
        free(decoder);
        return NULL;
    }
    return decoder;
}

WL_API void wl_dec_destroy(wl_decoder *decoder)
{
    if (!decoder) {
        return;
    }
    aom_codec_destroy(&decoder->codec);
    free(decoder);
}

// Decodes one compressed frame; its size comes back through `width` and `height`. The frame itself stays in the
// decoder for wl_dec_copy_bgra or wl_dec_copy_i420. Returns 0 on success, -1 on failure.
WL_API int wl_dec_decode(wl_decoder *decoder, const uint8_t *data, int size, int *width, int *height)
{
    decoder->last = NULL;
    if (aom_codec_decode(&decoder->codec, data, (size_t)size, NULL) != AOM_CODEC_OK) {
        set_error(decoder->error, "aom_codec_decode failed", &decoder->codec);
        return -1;
    }

    // A compressed frame normally holds one picture; keep the last if there are more.
    aom_codec_iter_t iterator = NULL;
    aom_image_t *image;
    while ((image = aom_codec_get_frame(&decoder->codec, &iterator)) != NULL) {
        decoder->last = image;
    }

    if (!decoder->last) {
        set_error(decoder->error, "the decoder returned no image", NULL);
        return -1;
    }
    if (decoder->last->fmt != AOM_IMG_FMT_I420) {
        set_error(decoder->error, "unexpected image format (expected 8-bit 4:2:0)", NULL);
        decoder->last = NULL;
        return -1;
    }
    *width = (int)decoder->last->d_w;
    *height = (int)decoder->last->d_h;
    return 0;
}

// Converts the most recently decoded frame to BGRA. Returns 0 on success, -1 if there is none.
WL_API int wl_dec_copy_bgra(wl_decoder *decoder, uint8_t *bgra, int stride)
{
    const aom_image_t *image = decoder->last;
    if (!image) {
        set_error(decoder->error, "no decoded frame", NULL);
        return -1;
    }
    wl_i420_planes_to_bgra(image->planes[AOM_PLANE_Y], image->stride[AOM_PLANE_Y],
                           image->planes[AOM_PLANE_U], image->stride[AOM_PLANE_U],
                           image->planes[AOM_PLANE_V], image->stride[AOM_PLANE_V],
                           (int)image->d_w, (int)image->d_h, bgra, stride);
    return 0;
}

// Copies the most recently decoded frame as packed I420. Returns 0 on success, -1 if there is none.
WL_API int wl_dec_copy_i420(wl_decoder *decoder, uint8_t *i420)
{
    const aom_image_t *image = decoder->last;
    if (!image) {
        set_error(decoder->error, "no decoded frame", NULL);
        return -1;
    }
    const wl_i420 frame = wl_i420_planes(i420, (int)image->d_w, (int)image->d_h);
    copy_plane(image->planes[AOM_PLANE_Y], image->stride[AOM_PLANE_Y], frame.y, frame.width, frame.width,
               frame.height);
    copy_plane(image->planes[AOM_PLANE_U], image->stride[AOM_PLANE_U], frame.u, frame.chroma_width,
               frame.chroma_width, frame.chroma_height);
    copy_plane(image->planes[AOM_PLANE_V], image->stride[AOM_PLANE_V], frame.v, frame.chroma_width,
               frame.chroma_width, frame.chroma_height);
    return 0;
}

WL_API const char *wl_dec_error(wl_decoder *decoder)
{
    return decoder->error;
}
