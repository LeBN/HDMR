#include <jni.h>
#include <android/log.h>
#include "uvc.h"

#define TAG "nuvc"
#define LI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)

extern "C" {

JNIEXPORT jlong JNICALL
Java_com_natis_nuvc_Nuvc_nativeOpen(JNIEnv*, jclass, jint fd) {
    auto* u = new nuvc::Uvc();
    if (!u->open(fd)) { delete u; return 0; }
    return reinterpret_cast<jlong>(u);
}

JNIEXPORT jboolean JNICALL
Java_com_natis_nuvc_Nuvc_nativeStart(JNIEnv*, jclass, jlong h) {
    auto* u = reinterpret_cast<nuvc::Uvc*>(h);
    if (!u) return JNI_FALSE;
    // P1: no consumer yet — the frame callback just exists so assembly runs.
    // uvc.cpp logs fps / frame size / bad-frame count itself.
    return u->start_preferred([](const uint8_t*, size_t, uint16_t, uint16_t, bool) {})
           ? JNI_TRUE : JNI_FALSE;
}

// Copy the newest frame into a direct ByteBuffer. Returns its sequence number
// (monotonic from 1) or 0 if nothing newer than `since`. NV12/YUY2 is raw
// bytes; MJPEG is the JPEG bitstream. w/h/fmt come from nativeStats.
JNIEXPORT jlong JNICALL
Java_com_natis_nuvc_Nuvc_nativeLatestInto(JNIEnv* env, jclass, jlong h,
                                          jobject buf, jlong since) {
    auto* u = reinterpret_cast<nuvc::Uvc*>(h);
    if (!u || !buf) return 0;
    auto* dst = static_cast<uint8_t*>(env->GetDirectBufferAddress(buf));
    jlong cap = env->GetDirectBufferCapacity(buf);
    if (!dst || cap <= 0) return 0;
    uint16_t w = 0, hh = 0; uint8_t fmt = 0;
    return (jlong)u->latest_into(dst, (size_t)cap, (uint64_t)since, &w, &hh, &fmt);
}

// Same as nativeLatestInto but the destination is a raw address (Unity passes
// a pinned NativeArray pointer) — no ByteBuffer round-trip on the hot path.
JNIEXPORT jlong JNICALL
Java_com_natis_nuvc_Nuvc_nativeLatestIntoAddr(JNIEnv*, jclass, jlong h,
                                              jlong addr, jlong cap, jlong since) {
    auto* u = reinterpret_cast<nuvc::Uvc*>(h);
    if (!u || !addr || cap <= 0) return 0;
    uint16_t w = 0, hh = 0; uint8_t fmt = 0;
    return (jlong)u->latest_into(reinterpret_cast<uint8_t*>(addr),
                                 (size_t)cap, (uint64_t)since, &w, &hh, &fmt);
}

// [0]=now_ns  [1]=newest frame EOF ns  [2]=last frame USB receive span (us)
JNIEXPORT jlongArray JNICALL
Java_com_natis_nuvc_Nuvc_nativeLat(JNIEnv* env, jclass, jlong h) {
    jlong v[3] = {0, 0, 0};
    v[0] = (jlong)nuvc::Uvc::clock_ns();
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) {
        v[1] = (jlong)u->latest_capture_ns();
        v[2] = (jlong)u->assembly_us();
    }
    jlongArray a = env->NewLongArray(3);
    env->SetLongArrayRegion(a, 0, 3, v);
    return a;
}

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Nuvc_nativeDumpAll(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) u->dump_all_interfaces();
}

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Nuvc_nativeStop(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) u->stop();
}

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Nuvc_nativeClose(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) { u->close(); delete u; }
}

// [0]=frames [1]=badFrames [2]=lastBytes [3]=fps*100 [4]=w [5]=h [6]=mjpeg
// [7]=linkSpeed [8]=fmt(0 NV12,1 YUY2,2 MJPEG)
JNIEXPORT jintArray JNICALL
Java_com_natis_nuvc_Nuvc_nativeStats(JNIEnv* env, jclass, jlong h) {
    jint v[9] = {0};
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) {
        v[0] = (jint)u->frames();
        v[1] = (jint)u->bad_frames();
        v[2] = (jint)u->last_bytes();
        v[3] = (jint)(u->fps() * 100.f);
        v[4] = u->mode().w;
        v[5] = u->mode().h;
        v[6] = u->mode().mjpeg ? 1 : 0;
        v[7] = u->link_speed();
        v[8] = (jint)u->fmt();
    }
    jintArray a = env->NewIntArray(9);
    env->SetIntArrayRegion(a, 0, 9, v);
    return a;
}

} // extern "C"
