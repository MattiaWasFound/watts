/* Prints the meter five times, a second apart. With the release archive unpacked in <dir>:
 *   macOS:   cc watts_example.c -I<dir>/include <dir>/lib/libwatts.dylib -Wl,-rpath,<dir>/lib -o watts_example
 *   Linux:   cc watts_example.c -I<dir>/include <dir>/lib/libwatts.so -Wl,-rpath,<dir>/lib -o watts_example
 *   Windows: cl watts_example.c /I<dir>\include <dir>\lib\libwatts.lib  (libwatts.dll beside the .exe)
 */
#include <stdio.h>
#include "watts.h"
#ifdef _WIN32
#  include <windows.h>
#  define sleep_s(s) Sleep((s) * 1000)
#else
#  include <unistd.h>
#  define sleep_s(s) sleep(s)
#endif

int main(void) {
    if (watts_start(NULL) != WATTS_OK) return 1;
    watts_set_gpu_load(0); /* this program renders nothing */
    printf("libwatts %s\n", watts_version());
    for (int i = 0; i < 5; i++) {
        sleep_s(1);
        watts_reading r = { sizeof r };
        if (watts_read(&r) != WATTS_OK) return 1;
        char source[512];
        watts_source(source, sizeof source);
        printf("%6.1f W  %.5f Wh  %s  %s\n", r.watts, r.session_wh, r.measured ? "measured" : "estimated", source);
    }
    watts_stop();
    return 0;
}
