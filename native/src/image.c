// JPEG and WebP images for Worklapse: captures (packed I420, BT.709 full range) encoded to image files that any
// viewer shows in the right colours, and image files (other programs' JPEGs too) decoded back to packed I420.
//
// JPEG files are BT.601 full range (JFIF) and WebP files BT.601 limited range, which is what viewers assume.
//   - JPEGs are made from BGRX rows, a strip at a time; libjpeg-turbo converts those to its own YCbCr.
//   - WebP pictures are converted straight into libwebp's YUV planes.
// Neither needs a full-size BGRA copy of the frame.

#include <setjmp.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <jerror.h>
#include <jpeglib.h>
#include <webp/decode.h>
#include <webp/encode.h>

#include "worklapse_native.h"

// The same numbers as Worklapse's FrameFormat.
#define FORMAT_JPEG 1
#define FORMAT_WEBP 2

// Rows converted to BGRX at a time for the JPEG encoder: a multiple of its 16-row block (MCU) height.
#define STRIP_ROWS 16

// Bytes per BGRA / BGRX pixel.
#define BGRA_SIZE 4

static void set_error(char *buffer, const char *format, ...)
{
    if (!buffer) {
        return;
    }
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, WL_ERROR_LEN, format, args);
    va_end(args);
}

static bool is_jpeg(const uint8_t *data, int size)
{
    return size >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
}

// --- JPEG errors ----------------------------------------------------------------------------------
//
// libjpeg reports a fatal error by calling error_exit, which by default ends the process. Here it longjmps back to
// the setjmp in the function that started the work, which cleans up and returns -1. Anything that cleanup frees
// must already exist when setjmp is called: a longjmp leaves local variables changed after setjmp undefined. The
// pointers it frees are declared volatile so the compiler doesn't keep them only in registers (gcc's -Wclobbered).

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4324) // padded for jmp_buf's alignment, as it should be
#endif
typedef struct {
    struct jpeg_error_mgr base;
    jmp_buf escape;
    char *error;   // the caller's error buffer, or NULL
} jpeg_errors;
#ifdef _MSC_VER
#pragma warning(pop)
#endif

static void jpeg_fail(j_common_ptr cinfo)
{
    jpeg_errors *errors = (jpeg_errors *)cinfo->err;
    if (errors->error) {
        char message[JMSG_LENGTH_MAX];
        errors->base.format_message(cinfo, message);
        snprintf(errors->error, WL_ERROR_LEN, "JPEG: %s", message);
    }
    longjmp(errors->escape, 1);
}

// Warnings (damage the decoder could work around, such as a cut-off file) aren't fatal, and nothing reads stderr.
static void jpeg_ignore_message(j_common_ptr cinfo)
{
    (void)cinfo;
}

static void jpeg_errors_init(jpeg_errors *errors, char *error)
{
    jpeg_std_error(&errors->base);
    errors->base.error_exit = jpeg_fail;
    errors->base.output_message = jpeg_ignore_message;
    errors->error = error;
}

// --- JPEG encoding --------------------------------------------------------------------------------

// The compressed output: a buffer that doubles whenever libjpeg fills it. It lives on the heap so it survives a
// longjmp intact.
typedef struct {
    struct jpeg_destination_mgr base;
    uint8_t *data;
    size_t capacity;
} jpeg_buffer;

static void buffer_start(j_compress_ptr cinfo)
{
    jpeg_buffer *buffer = (jpeg_buffer *)cinfo->dest;
    buffer->base.next_output_byte = buffer->data;
    buffer->base.free_in_buffer = buffer->capacity;
}

// Called by libjpeg when the buffer is full.
static boolean buffer_grow(j_compress_ptr cinfo)
{
    jpeg_buffer *buffer = (jpeg_buffer *)cinfo->dest;
    const size_t used = buffer->capacity;
    uint8_t *grown = (uint8_t *)realloc(buffer->data, buffer->capacity * 2);
    if (!grown) {
        ERREXIT(cinfo, JERR_OUT_OF_MEMORY);
    }
    buffer->data = grown;
    buffer->capacity *= 2;
    buffer->base.next_output_byte = grown + used;
    buffer->base.free_in_buffer = buffer->capacity - used;
    return TRUE;
}

static void buffer_finish(j_compress_ptr cinfo)
{
    (void)cinfo;
}

// A buffer for a frame of width x height, starting at a sixteenth of the raw size (at least 64 KB): most captures
// compress well below that.
static jpeg_buffer *buffer_create(int width, int height)
{
    jpeg_buffer *buffer = (jpeg_buffer *)calloc(1, sizeof(jpeg_buffer));
    if (!buffer) {
        return NULL;
    }
    const size_t sixteenth = (size_t)width * height / 16;
    buffer->capacity = sixteenth > 65536 ? sixteenth : 65536;
    buffer->data = (uint8_t *)malloc(buffer->capacity);
    if (!buffer->data) {
        free(buffer);
        return NULL;
    }
    buffer->base.init_destination = buffer_start;
    buffer->base.empty_output_buffer = buffer_grow;
    buffer->base.term_destination = buffer_finish;
    return buffer;
}

static void buffer_free(jpeg_buffer *buffer)
{
    if (buffer) {
        free(buffer->data);
        free(buffer);
    }
}

// Feeds the whole frame to the compressor, converting it to BGRX a strip of rows at a time into `strip`.
static void write_frame(struct jpeg_compress_struct *cinfo, const uint8_t *i420, int width, int height,
                        uint8_t *strip)
{
    const int strip_stride = width * BGRA_SIZE;
    JSAMPROW rows[STRIP_ROWS];
    for (int i = 0; i < STRIP_ROWS; i++) {
        rows[i] = wl_row(strip, strip_stride, i);
    }

    while (cinfo->next_scanline < cinfo->image_height) {
        const int top = (int)cinfo->next_scanline;
        const int left = height - top;
        const int count = left < STRIP_ROWS ? left : STRIP_ROWS;
        wl_i420_rows_to_bgra(i420, width, height, top, count, strip, strip_stride);
        jpeg_write_scanlines(cinfo, rows, (JDIMENSION)count);
    }
}

// Encodes a packed I420 frame as a baseline JPEG (4:2:0) at quality 1-100. On success returns its size and points
// *data at it, to be freed with wl_image_free; returns -1 on failure.
WL_API int wl_jpeg_encode_i420(const uint8_t *i420, int width, int height, int quality, uint8_t **data, char *error)
{
    // Everything the failure path frees exists before setjmp.
    jpeg_buffer *volatile buffer = buffer_create(width, height);
    uint8_t *volatile strip = (uint8_t *)malloc((size_t)STRIP_ROWS * width * BGRA_SIZE);
    if (!buffer || !strip) {
        buffer_free(buffer);
        free(strip);
        set_error(error, "out of memory");
        return -1;
    }

    struct jpeg_compress_struct cinfo;
    jpeg_errors errors;
    jpeg_errors_init(&errors, error);
    cinfo.err = &errors.base;
    if (setjmp(errors.escape)) {
        // libjpeg failed (jpeg_fail has written the message).
        jpeg_destroy_compress(&cinfo);
        buffer_free(buffer);
        free(strip);
        return -1;
    }

    jpeg_create_compress(&cinfo);
    cinfo.dest = &buffer->base;
    cinfo.image_width = (JDIMENSION)width;
    cinfo.image_height = (JDIMENSION)height;
    cinfo.input_components = BGRA_SIZE;
    cinfo.in_color_space = JCS_EXT_BGRX;
    jpeg_set_defaults(&cinfo);
    jpeg_set_quality(&cinfo, quality, TRUE);
    cinfo.optimize_coding = TRUE; // a few percent smaller for a little more time

    jpeg_start_compress(&cinfo, TRUE);
    write_frame(&cinfo, i420, width, height, strip);
    jpeg_finish_compress(&cinfo);

    const size_t size = buffer->capacity - buffer->base.free_in_buffer;
    jpeg_destroy_compress(&cinfo);
    free(strip);

    if (size > INT32_MAX) {
        buffer_free(buffer);
        set_error(error, "JPEG: the image is too large");
        return -1;
    }
    // The caller takes over the data; only the bookkeeping around it goes.
    *data = buffer->data;
    free(buffer);
    return (int)size;
}

// --- JPEG decoding --------------------------------------------------------------------------------

// Reads one scanline into `row`.
static void read_row(struct jpeg_decompress_struct *cinfo, uint8_t *row)
{
    JSAMPROW rows[1] = {row};
    if (jpeg_read_scanlines(cinfo, rows, 1) == 0) {
        ERREXIT(cinfo, JERR_INPUT_EMPTY);
    }
}

// Reads the image as BGRA, two rows at a time into `pair`, and converts each pair to packed I420 (two luma rows and
// the chroma row they share). A frame's odd last row pairs with itself.
static void read_into_i420(struct jpeg_decompress_struct *cinfo, int width, int height, uint8_t *i420,
                           uint8_t *pair)
{
    const wl_i420 frame = wl_i420_planes(i420, width, height);
    const int pair_stride = width * BGRA_SIZE;
    uint8_t *first = wl_row(pair, pair_stride, 0);
    uint8_t *second = wl_row(pair, pair_stride, 1);

    while (cinfo->output_scanline < cinfo->output_height) {
        const int top = (int)cinfo->output_scanline;
        const bool has_second = height - top >= 2;

        read_row(cinfo, first);
        if (has_second) {
            read_row(cinfo, second);
        }
        wl_bgra_pair_to_i420(first,
                             has_second ? second : first,
                             width,
                             wl_row(frame.y, frame.width, top),
                             has_second ? wl_row(frame.y, frame.width, top + 1) : NULL,
                             wl_row(frame.u, frame.chroma_width, top / 2),
                             wl_row(frame.v, frame.chroma_width, top / 2));
    }
}

// Reads the image as BGRA rows straight into `bgra`.
static void read_into_bgra(struct jpeg_decompress_struct *cinfo, uint8_t *bgra, int stride)
{
    while (cinfo->output_scanline < cinfo->output_height) {
        read_row(cinfo, wl_row(bgra, stride, (int)cinfo->output_scanline));
    }
}

// Decodes a JPEG of width x height into one of two outputs:
//   - packed I420 (i420 set, bgra NULL), for Worklapse itself;
//   - BGRA rows as libjpeg-turbo converts them (bgra set, i420 NULL), which is how other programs show the file.
// Returns 0 on success, -1 on failure.
static int jpeg_decode(const uint8_t *data, int size, int width, int height, uint8_t *i420, uint8_t *bgra,
                       int stride, char *error)
{
    // Converting to I420 goes through two BGRA rows at a time.
    uint8_t *volatile pair = NULL;
    if (i420) {
        pair = (uint8_t *)malloc((size_t)2 * width * BGRA_SIZE);
        if (!pair) {
            set_error(error, "out of memory");
            return -1;
        }
    }

    struct jpeg_decompress_struct cinfo;
    jpeg_errors errors;
    jpeg_errors_init(&errors, error);
    cinfo.err = &errors.base;
    if (setjmp(errors.escape)) {
        // libjpeg failed (jpeg_fail has written the message).
        jpeg_destroy_decompress(&cinfo);
        free(pair);
        return -1;
    }

    jpeg_create_decompress(&cinfo);
    jpeg_mem_src(&cinfo, data, (unsigned long)size);
    jpeg_read_header(&cinfo, TRUE);
    cinfo.out_color_space = JCS_EXT_BGRA;
    jpeg_start_decompress(&cinfo);

    if ((int)cinfo.output_width != width || (int)cinfo.output_height != height) {
        set_error(error, "JPEG: the image is %ux%u, not %dx%d", cinfo.output_width, cinfo.output_height, width,
                  height);
        jpeg_destroy_decompress(&cinfo);
        free(pair);
        return -1;
    }

    if (i420) {
        read_into_i420(&cinfo, width, height, i420, pair);
    } else {
        read_into_bgra(&cinfo, bgra, stride);
    }

    jpeg_finish_decompress(&cinfo);
    jpeg_destroy_decompress(&cinfo);
    free(pair);
    return 0;
}

// --- WebP encoding --------------------------------------------------------------------------------

static const char *webp_error(WebPEncodingError code)
{
    switch (code) {
    case VP8_ENC_ERROR_OUT_OF_MEMORY:
    case VP8_ENC_ERROR_BITSTREAM_OUT_OF_MEMORY:
        return "out of memory";
    case VP8_ENC_ERROR_INVALID_CONFIGURATION:
        return "invalid settings";
    case VP8_ENC_ERROR_BAD_DIMENSION:
        return "the picture is too large (WebP allows up to 16383 x 16383)";
    case VP8_ENC_ERROR_PARTITION0_OVERFLOW:
    case VP8_ENC_ERROR_PARTITION_OVERFLOW:
        return "the picture is too complex for WebP at this quality";
    case VP8_ENC_ERROR_FILE_TOO_BIG:
        return "the file would be too large";
    default:
        return "error";
    }
}

// Encodes a packed I420 frame as a lossy WebP at quality 0-100. On success returns its size and points *data at it,
// to be freed with wl_image_free; returns -1 on failure.
WL_API int wl_webp_encode_i420(const uint8_t *i420, int width, int height, int quality, uint8_t **data, char *error)
{
    WebPConfig config;
    WebPPicture picture;
    if (!WebPConfigInit(&config) || !WebPPictureInit(&picture)) {
        set_error(error, "WebP: libwebp version mismatch");
        return -1;
    }
    config.quality = (float)quality;
    if (!WebPValidateConfig(&config)) {
        set_error(error, "WebP: invalid quality %d", quality);
        return -1;
    }

    // A YUV picture, filled with the frame converted to BT.601 limited range.
    picture.use_argb = 0;
    picture.colorspace = WEBP_YUV420;
    picture.width = width;
    picture.height = height;
    if (!WebPPictureAlloc(&picture)) {
        set_error(error, "WebP: could not make a %dx%d picture (%s)", width, height,
                  webp_error(picture.error_code));
        return -1;
    }
    wl_i420_709_full_to_601_limited(i420, width, height, picture.y, picture.y_stride, picture.u, picture.v,
                                    picture.uv_stride);

    WebPMemoryWriter writer;
    WebPMemoryWriterInit(&writer);
    picture.writer = WebPMemoryWrite;
    picture.custom_ptr = &writer;
    const int ok = WebPEncode(&config, &picture);
    const WebPEncodingError code = picture.error_code;
    WebPPictureFree(&picture);

    if (!ok || writer.size > INT32_MAX) {
        WebPMemoryWriterClear(&writer);
        set_error(error, "WebP encoding failed: %s", ok ? "the file would be too large" : webp_error(code));
        return -1;
    }
    *data = writer.mem;
    return (int)writer.size;
}

// --- reading images -------------------------------------------------------------------------------

// Frees what an encoder returned. `format` is the encoder's: libwebp allocates its own way.
WL_API void wl_image_free(uint8_t *data, int format)
{
    if (format == FORMAT_WEBP) {
        WebPFree(data);
    } else {
        free(data);
    }
}

// Returns the format of an image (1 = JPEG, 2 = WebP) and its size through `width` and `height`; -1 if it is
// neither, or its header can't be read.
WL_API int wl_image_info(const uint8_t *data, int size, int *width, int *height, char *error)
{
    if (is_jpeg(data, size)) {
        struct jpeg_decompress_struct cinfo;
        jpeg_errors errors;
        jpeg_errors_init(&errors, error);
        cinfo.err = &errors.base;
        if (setjmp(errors.escape)) {
            jpeg_destroy_decompress(&cinfo);
            return -1;
        }
        jpeg_create_decompress(&cinfo);
        jpeg_mem_src(&cinfo, data, (unsigned long)size);
        jpeg_read_header(&cinfo, TRUE);
        *width = (int)cinfo.image_width;
        *height = (int)cinfo.image_height;
        jpeg_destroy_decompress(&cinfo);
        return FORMAT_JPEG;
    }

    if (WebPGetInfo(data, (size_t)size, width, height)) {
        return FORMAT_WEBP;
    }

    set_error(error, "not a JPEG or WebP image");
    return -1;
}

// Checks that `data` is a WebP image of width x height. Returns 0 if it is, -1 with a message if not.
static int webp_check_size(const uint8_t *data, int size, int width, int height, char *error)
{
    int actual_width;
    int actual_height;
    if (!WebPGetInfo(data, (size_t)size, &actual_width, &actual_height)) {
        set_error(error, "not a JPEG or WebP image");
        return -1;
    }
    if (actual_width != width || actual_height != height) {
        set_error(error, "WebP: the image is %dx%d, not %dx%d", actual_width, actual_height, width, height);
        return -1;
    }
    return 0;
}

// Decodes a JPEG or WebP image of width x height (see wl_image_info) into packed I420, BT.709 full range.
// Returns 0 on success, -1 on failure.
WL_API int wl_image_decode_i420(const uint8_t *data, int size, int width, int height, uint8_t *i420, char *error)
{
    if (is_jpeg(data, size)) {
        return jpeg_decode(data, size, width, height, i420, NULL, 0, error);
    }
    if (webp_check_size(data, size, width, height, error) != 0) {
        return -1;
    }

    // libwebp writes its BT.601 limited range planes straight into the frame, which is then converted in place.
    const wl_i420 frame = wl_i420_planes(i420, width, height);
    const size_t luma_size = (size_t)frame.width * frame.height;
    const size_t chroma_size = (size_t)frame.chroma_width * frame.chroma_height;
    if (!WebPDecodeYUVInto(data, (size_t)size,
                           frame.y, luma_size, frame.width,
                           frame.u, chroma_size, frame.chroma_width,
                           frame.v, chroma_size, frame.chroma_width)) {
        set_error(error, "WebP: the image could not be decoded");
        return -1;
    }
    wl_i420_601_limited_to_709_full(i420, width, height);
    return 0;
}

// Decodes a JPEG or WebP image of width x height into BGRA with the libraries' own colour conversion, the way other
// programs show it (Worklapse itself decodes to I420). Returns 0 on success, -1 on failure.
WL_API int wl_image_decode_bgra(const uint8_t *data, int size, int width, int height, uint8_t *bgra, int stride,
                                char *error)
{
    if (is_jpeg(data, size)) {
        return jpeg_decode(data, size, width, height, NULL, bgra, stride, error);
    }
    if (webp_check_size(data, size, width, height, error) != 0) {
        return -1;
    }
    if (!WebPDecodeBGRAInto(data, (size_t)size, bgra, (size_t)stride * height, stride)) {
        set_error(error, "WebP: the image could not be decoded");
        return -1;
    }
    return 0;
}
