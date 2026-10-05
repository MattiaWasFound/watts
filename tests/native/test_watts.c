/* Checks libwatts against include/watts.h: every return code, plausible numbers, string
 * truncation, the version, and a second start/stop cycle. Run by bin/test-native. */
#include <stdio.h>
#include <string.h>
#include "watts.h"
#ifdef _WIN32
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  define sleep_ms(ms) Sleep(ms)
#else
#  include <time.h>
static void sleep_ms(int ms) { struct timespec t = { ms / 1000, (ms % 1000) * 1000000L }; nanosleep(&t, NULL); }
#endif

static int failures = 0;
#define CHECK(cond) do { if (!(cond)) { printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); failures++; } } while (0)

static int valid_utf8(const unsigned char *s) {
    while (*s) {
        int n = *s < 0x80 ? 0 : (*s & 0xE0) == 0xC0 ? 1 : (*s & 0xF0) == 0xE0 ? 2 : (*s & 0xF8) == 0xF0 ? 3 : -1;
        if (n < 0) return 0;
        s++;
        while (n--) { if ((*s & 0xC0) != 0x80) return 0; s++; }
    }
    return 1;
}

int main(int argc, char **argv) {
    watts_reading r;

    /* Before start. */
    memset(&r, 0, sizeof r); r.struct_size = sizeof r;
    CHECK(watts_read(&r) == WATTS_ERR_NOT_STARTED);
    CHECK(watts_source(NULL, 0) == WATTS_ERR_NOT_STARTED);
    watts_set_gpu_load(0.5);  /* no-ops, must not crash */
    watts_reset_session();
    watts_stop();

    /* Version. */
    const char *v = watts_version();
    CHECK(v != NULL);
    if (argc > 1) CHECK(v && strcmp(v, argv[1]) == 0);
    printf("libwatts %s\n", v ? v : "(null)");

    /* Start, twice. */
    CHECK(watts_start(NULL) == WATTS_OK);
    CHECK(watts_start("ignored while running") == WATTS_OK);
    watts_set_gpu_load(0);

    /* Bad arguments. */
    CHECK(watts_read(NULL) == WATTS_ERR_BAD_ARGUMENT);
    memset(&r, 0, sizeof r); r.struct_size = sizeof r - 1;
    CHECK(watts_read(&r) == WATTS_ERR_BAD_ARGUMENT);

    /* A reading right away is the model's estimate; after samples it is the meter's. */
    memset(&r, 0, sizeof r); r.struct_size = sizeof r;
    CHECK(watts_read(&r) == WATTS_OK);
    CHECK(r.watts > 0 && r.watts < 5000);
    sleep_ms(2500);
    memset(&r, 0, sizeof r); r.struct_size = sizeof r;
    CHECK(watts_read(&r) == WATTS_OK);
    CHECK(r.watts > 0 && r.watts < 5000);
    CHECK(r.session_wh > 0 && r.session_wh < 10);
    CHECK(r.measured == 0 || r.measured == 1);
    CHECK(r.display_included == 0 || r.display_included == 1);
    CHECK(r.reserved == 0);

    /* Source: the size query, a full copy, and truncation that keeps UTF-8 whole. */
    int need = watts_source(NULL, 0);
    CHECK(need > 1);
    char full[2048];
    CHECK(watts_source(full, sizeof full) == need || need > (int)sizeof full);
    CHECK(strlen(full) > 0 && valid_utf8((const unsigned char *)full));
    for (int size = 1; size < 64; size++) {
        char part[64];
        memset(part, 'x', sizeof part);
        CHECK(watts_source(part, size) > 0);
        CHECK(strlen(part) < (size_t)size);
        CHECK(valid_utf8((const unsigned char *)part));
    }
    printf("%.1f W, %.5f Wh, %s: %s\n", r.watts, r.session_wh, r.measured ? "measured" : "estimated", full);

    /* Reset: the total restarts at zero. A tick may land in between, adding about a second's worth. */
    double watts_now = r.watts;
    watts_reset_session();
    memset(&r, 0, sizeof r); r.struct_size = sizeof r;
    CHECK(watts_read(&r) == WATTS_OK);
    CHECK(r.session_wh <= 2 * (watts_now > r.watts ? watts_now : r.watts) / 3600);

    /* Stop, twice, then everything reports not started again. */
    watts_stop();
    watts_stop();
    memset(&r, 0, sizeof r); r.struct_size = sizeof r;
    CHECK(watts_read(&r) == WATTS_ERR_NOT_STARTED);

    /* A second cycle. */
    CHECK(watts_start(NULL) == WATTS_OK);
    memset(&r, 0, sizeof r); r.struct_size = sizeof r;
    CHECK(watts_read(&r) == WATTS_OK);
    CHECK(r.session_wh == 0);
    watts_stop();

    printf(failures ? "%d check(s) failed\n" : "all checks passed\n", failures);
    return failures ? 1 : 0;
}
