"""Reads libwatts from Python with ctypes. Pass the library's path:

    python3 watts_example.py <dir>/lib/libwatts.dylib   (libwatts.so on Linux, libwatts.dll on Windows)
"""
import ctypes
import sys
import time


class Reading(ctypes.Structure):
    _fields_ = [("struct_size", ctypes.c_uint32), ("measured", ctypes.c_int32),
                ("display_included", ctypes.c_int32), ("reserved", ctypes.c_int32),
                ("watts", ctypes.c_double), ("session_wh", ctypes.c_double)]


lib = ctypes.CDLL(sys.argv[1])
lib.watts_start.argtypes = [ctypes.c_char_p]
lib.watts_read.argtypes = [ctypes.POINTER(Reading)]
lib.watts_source.argtypes = [ctypes.c_char_p, ctypes.c_int32]
lib.watts_set_gpu_load.argtypes = [ctypes.c_double]
lib.watts_version.restype = ctypes.c_char_p

lib.watts_start(None)
lib.watts_set_gpu_load(0.0)  # this script renders nothing
print("libwatts", lib.watts_version().decode())
for _ in range(5):
    time.sleep(1)
    r = Reading(struct_size=ctypes.sizeof(Reading))
    lib.watts_read(ctypes.byref(r))
    source = ctypes.create_string_buffer(512)
    lib.watts_source(source, len(source))
    print(f"{r.watts:6.1f} W  {r.session_wh:.5f} Wh  {'measured' if r.measured else 'estimated'}  {source.value.decode()}")
lib.watts_stop()
