#!/usr/bin/env bash
set -e
export PATH=/mingw64/bin:$PATH
cd "$(dirname "$0")"
/mingw64/bin/cmake.exe -G Ninja -DCMAKE_PREFIX_PATH=/mingw64 -DCMAKE_C_COMPILER=/mingw64/bin/gcc.exe -DCMAKE_MAKE_PROGRAM=/mingw64/bin/ninja.exe .
/mingw64/bin/ninja.exe
