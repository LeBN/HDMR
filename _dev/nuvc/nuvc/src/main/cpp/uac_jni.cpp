#include <jni.h>
#include <android/log.h>
#include "uac.h"

#define TAG "nuvc-uac"

extern "C" {

JNIEXPORT jlong JNICALL
Java_com_natis_nuvc_Uac_nativeUacOpen(JNIEnv*, jclass, jint fd) {
    auto* u = new nuvc::Uac();
    if (!u->open(fd)) { delete u; return 0; }
    return reinterpret_cast<jlong>(u);
}

JNIEXPORT jboolean JNICALL
Java_com_natis_nuvc_Uac_nativeUacStart(JNIEnv*, jclass, jlong h) {
    auto* u = reinterpret_cast<nuvc::Uac*>(h);
    return (u && u->start()) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Uac_nativeUacStop(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uac*>(h)) u->stop();
}

JNIEXPORT void JNICALL
Java_com_natis_nuvc_Uac_nativeUacClose(JNIEnv*, jclass, jlong h) {
    if (auto* u = reinterpret_cast<nuvc::Uac*>(h)) { u->close(); delete u; }
}

JNIEXPORT jint JNICALL
Java_com_natis_nuvc_Uac_nativeUacRead(JNIEnv* env, jclass, jlong h, jbyteArray dst, jint len) {
    auto* u = reinterpret_cast<nuvc::Uac*>(h);
    if (!u) return 0;
    jbyte* buf = env->GetByteArrayElements(dst, nullptr);
    size_t n = u->read(reinterpret_cast<uint8_t*>(buf), (size_t)len);
    env->ReleaseByteArrayElements(dst, buf, 0);
    return (jint)n;
}

// [0]=rate [1]=channels [2]=bitDepth [3]=running(0/1)
JNIEXPORT jintArray JNICALL
Java_com_natis_nuvc_Uac_nativeUacInfo(JNIEnv* env, jclass, jlong h) {
    jint v[4] = {0};
    if (auto* u = reinterpret_cast<nuvc::Uac*>(h)) {
        v[0] = u->sampleRate(); v[1] = u->channels(); v[2] = u->bitDepth(); v[3] = u->running() ? 1 : 0;
    }
    jintArray a = env->NewIntArray(4);
    env->SetIntArrayRegion(a, 0, 4, v);
    return a;
}

} // extern "C"
