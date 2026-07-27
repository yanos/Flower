/* Compiles miniaudio's implementation into a shared library exposing the
   ma_* C API that Miniaudio-CS's generated P/Invoke bindings call into.
   Pinned to the exact miniaudio commit (0.11.22, see vendor/miniaudio.h)
   that Miniaudio-CS 1.0.4's own bindings were generated against - a
   mismatched miniaudio version here would silently desync struct layouts
   (ma_context/ma_device) from what the C# side expects. */
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"
