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

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Nuvc_nativeStop(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) u->stop();
}

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Nuvc_nativeClose(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) { u->close(); delete u; }
}

// [0]=frames [1]=badFrames [2]=lastBytes [3]=fps*100 [4]=w [5]=h [6]=mjpeg [7]=linkSpeed
JNIEXPORT jintArray JNICALL
Java_com_natis_nuvc_Nuvc_nativeStats(JNIEnv* env, jclass, jlong h) {
    jint v[8] = {0};
    if (auto* u = reinterpret_cast<nuvc::Uvc*>(h)) {
        v[0] = (jint)u->frames();
        v[1] = (jint)u->bad_frames();
        v[2] = (jint)u->last_bytes();
        v[3] = (jint)(u->fps() * 100.f);
        v[4] = u->mode().w;
        v[5] = u->mode().h;
        v[6] = u->mode().mjpeg ? 1 : 0;
        v[7] = u->link_speed();
    }
    jintArray a = env->NewIntArray(8);
    env->SetIntArrayRegion(a, 0, 8, v);
    return a;
}

} // extern "C"
