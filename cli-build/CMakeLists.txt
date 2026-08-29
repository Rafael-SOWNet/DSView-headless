cmake_minimum_required(VERSION 3.10)
project(sigrok4dsl_shared C)

set(CMAKE_C_STANDARD 99)

include(FindPkgConfig)
find_package(PkgConfig REQUIRED)
pkg_search_module(GLIB REQUIRED glib-2.0)

set(FORK_ROOT ${CMAKE_SOURCE_DIR}/..)

include_directories(
    ${FORK_ROOT}/libsigrok4DSL
    ${FORK_ROOT}/common
    ${GLIB_INCLUDE_DIRS}
)
link_directories(${GLIB_LIBDIR})

# Same source list as the fork's own CMakeLists.txt (libsigrok4DSL_SOURCES),
# minus nothing - this builds the exact same protocol/driver code, just as a
# standalone DLL instead of statically linked into the DSView GUI executable.
set(SOURCES
    ${FORK_ROOT}/libsigrok4DSL/version.c
    ${FORK_ROOT}/libsigrok4DSL/strutil.c
    ${FORK_ROOT}/libsigrok4DSL/std.c
    ${FORK_ROOT}/libsigrok4DSL/session_driver.c
    ${FORK_ROOT}/libsigrok4DSL/session.c
    ${FORK_ROOT}/libsigrok4DSL/log.c
    ${FORK_ROOT}/libsigrok4DSL/hwdriver.c
    ${FORK_ROOT}/libsigrok4DSL/error.c
    ${FORK_ROOT}/libsigrok4DSL/backend.c
    ${FORK_ROOT}/libsigrok4DSL/output/output.c
    ${FORK_ROOT}/libsigrok4DSL/input/input.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/demo/demo.c
    ${FORK_ROOT}/libsigrok4DSL/input/in_binary.c
    ${FORK_ROOT}/libsigrok4DSL/input/in_vcd.c
    ${FORK_ROOT}/libsigrok4DSL/input/in_wav.c
    ${FORK_ROOT}/libsigrok4DSL/output/csv.c
    ${FORK_ROOT}/libsigrok4DSL/output/gnuplot.c
    ${FORK_ROOT}/libsigrok4DSL/output/srzip.c
    ${FORK_ROOT}/libsigrok4DSL/output/vcd.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/DSL/dslogic.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/common/usb.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/common/ezusb.c
    ${FORK_ROOT}/libsigrok4DSL/trigger.c
    ${FORK_ROOT}/libsigrok4DSL/dsdevice.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/DSL/dscope.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/DSL/command.c
    ${FORK_ROOT}/libsigrok4DSL/hardware/DSL/dsl.c
    ${FORK_ROOT}/libsigrok4DSL/lib_main.c
    ${FORK_ROOT}/common/log/xlog.c
    ${FORK_ROOT}/common/minizip/unzip.c
    ${FORK_ROOT}/common/minizip/zip.c
    ${FORK_ROOT}/common/minizip/ioapi.c
)

add_library(sigrok4dsl SHARED ${SOURCES})

target_compile_definitions(sigrok4dsl PRIVATE
    HAVE_CONFIG_H
    LIBSIGROK4DSL_MAJOR_VERSION=1
)

# The fork's SR_API uses GCC visibility attributes, which MinGW doesn't
# honor the way ELF does - nothing gets exported by default. This codebase
# was never set up for dllexport/dllimport, so export every global symbol
# instead of hand-annotating the header.
target_link_options(sigrok4dsl PRIVATE -Wl,--export-all-symbols)

target_link_libraries(sigrok4dsl
    ${GLIB_LIBRARIES}
    usb-1.0
    z
    ws2_32
    winmm
)
