//
// Created by Emmanuel Hansen on 6/19/2023.
//

#ifndef KENJINXNATIVE_KENJINX_H
#define KENJINXNATIVE_KENJINX_H

#include <cassert>
#include <dlfcn.h>
#include <exception>
#include <fcntl.h>
#include <jni.h>
#include <stdlib.h>
#include <string.h>
#include <string>

#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>

#include "vulkan_wrapper.h"
#include <vulkan/vulkan_android.h>
#include "adrenotools/driver.h"
#include "native_window.h"

// Android log function wrappers
static const char* TAG = "Kenjinx";
#define LOGI(...) \
  ((void)__android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__))
#define LOGW(...) \
  ((void)__android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__))
#define LOGE(...) \
  ((void)__android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__))

// A macro to pass call to Vulkan and check for return value for success
#define CALL_VK(func)                                                 \
  if (VK_SUCCESS != (func)) {                                         \
    __android_log_print(ANDROID_LOG_ERROR, "Tutorial ",               \
                        "Vulkan error. File[%s], line[%d]", __FILE__, \
                        __LINE__);                                    \
    assert(false);                                                    \
  }

// A macro to check value is VK_SUCCESS
// Used also for non-vulkan functions but return VK_SUCCESS
#define VK_CHECK(x)  CALL_VK(x)

#define LoadLib(a) dlopen(a, RTLD_NOW)

void *_kenjinxNative = nullptr;

// Kenjinx imported functions
bool (*initialize)(char *) = nullptr;

long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;

#endif //KENJINXNATIVE_KENJINX_H
