/*
 * watts.h: the C API of libwatts.
 *
 * An estimate of how much power this computer draws right now at the wall socket, in watts, plus
 * the session's watt-hours and whether the figure was measured or estimated. macOS, Windows and
 * Linux; no admin rights, no network.
 *
 * One meter per process. watts_start() detects the hardware and begins sampling once a second on
 * a background thread; watts_stop() waits for that thread, up to about a second. Every other call
 * is a cheap read that never blocks. All functions are safe to call from any thread.
 *
 * Project: https://github.com/MattiaWasFound/watts
 *
 * Work in progress: until 1.0 this API may change between minor versions. struct_size lets
 * watts_reading grow without breaking callers built against an older header.
 */
#ifndef WATTS_H
#define WATTS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  define WATTS_API __declspec(dllimport)
#else
#  define WATTS_API
#endif

#define WATTS_OK               0
#define WATTS_ERR_NOT_STARTED -1  /* watts_start() has not been called, or watts_stop() has */
#define WATTS_ERR_BAD_ARGUMENT -2 /* NULL, or struct_size too small */
#define WATTS_ERR_FAILED      -3  /* unexpected. From watts_start: nothing is running. */

typedef struct watts_reading {
    uint32_t struct_size;      /* set to sizeof(watts_reading) before calling watts_read() */
    int32_t  measured;         /* 1: mostly from the machine's own sensors; 0: mostly the model */
    int32_t  display_included; /* 1: includes a laptop's built-in screen. Never an external monitor. */
    int32_t  reserved;
    double   watts;            /* wall power right now, supply losses included */
    double   session_wh;       /* energy since watts_start() or watts_reset_session(), in Wh */
} watts_reading;

/* Starts the meter. gpu_name (UTF-8, may be NULL) names the GPU the program renders on, for
 * machines with several. Calling it again while running does nothing. Returns WATTS_OK, or
 * WATTS_ERR_FAILED if the meter could not start (then nothing is running). */
WATTS_API int32_t watts_start(const char *gpu_name);

/* Copies the latest snapshot into *reading. Before the first sample (about a second after
 * watts_start) it holds the model's estimate. */
WATTS_API int32_t watts_read(watts_reading *reading);

/* What was measured and what was modelled, as UTF-8 text, for logs. Writes at most size bytes
 * including the terminating NUL. Returns the size needed (including the NUL), or an error. */
WATTS_API int32_t watts_source(char *buffer, int32_t size);

/* How busy the program keeps the GPU, 0..1, if it knows (for example from frame timings). Used
 * only where no system counter reports GPU load. Negative clears it. A program that renders
 * nothing should pass 0. Takes effect only while the meter runs: call it after watts_start(). */
WATTS_API void watts_set_gpu_load(double fraction);

/* Starts the watt-hour total again at zero. */
WATTS_API void watts_reset_session(void);

/* Stops sampling. Safe to call twice. */
WATTS_API void watts_stop(void);

/* The library version, for example "0.2.0". A static string; do not free it. */
WATTS_API const char *watts_version(void);

#ifdef __cplusplus
}
#endif

#endif /* WATTS_H */
