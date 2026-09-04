cmake_minimum_required(VERSION 3.22.1)
project(nuvc C CXX)

set(LU ${CMAKE_CURRENT_SOURCE_DIR}/third_party/libusb)

# ---- libusb 1.0.27, static, using the shipped Android config.h -----------------
add_library(usb1.0 STATIC
    ${LU}/libusb/core.c
    ${LU}/libusb/descriptor.c
    ${LU}/libusb/hotplug.c
    ${LU}/libusb/io.c
    ${LU}/libusb/sync.c
    ${LU}/libusb/strerror.c
    ${LU}/libusb/os/linux_usbfs.c
    ${LU}/libusb/os/events_posix.c
    ${LU}/libusb/os/threads_posix.c
    ${LU}/libusb/os/linux_netlink.c
)
target_include_directories(usb1.0 PUBLIC
    ${LU}/android          # config.h
    ${LU}/libusb
    ${LU}/libusb/os
)
target_compile_definitions(usb1.0 PRIVATE _GNU_SOURCE)
target_compile_options(usb1.0 PRIVATE -fvisibility=hidden -Wno-unused-parameter)
find_library(log-lib log)
target_link_libraries(usb1.0 PRIVATE ${log-lib})

# ---- nuvc --------------------------------------------------------------------
add_library(nuvc SHARED
    nuvc.cpp
    uvc.cpp
    uac.cpp
    uac_jni.cpp
)
set_target_properties(nuvc PROPERTIES LINKER_LANGUAGE CXX)
target_include_directories(nuvc PRIVATE ${LU}/libusb)
target_link_libraries(nuvc PRIVATE usb1.0 ${log-lib} android c++_shared)
