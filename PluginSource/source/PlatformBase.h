#pragma once

// Platform detection and Vulkan compute support flags.
// This plugin only targets Vulkan compute — all other graphics APIs are disabled.

#include <stddef.h>


// ─── Platform detection ─────────────────────────────────────────────────────

#if _MSC_VER
	#define UNITY_WIN 1
#elif defined(__ANDROID__)
	#define UNITY_ANDROID 1
#elif defined(UNITY_LINUX)
	// defined externally by build system
#else
	#error "Unsupported platform — this plugin requires Windows, Linux, or Android with Vulkan."
#endif


// ─── Graphics API support (Vulkan only) ─────────────────────────────────────

#if UNITY_WIN || UNITY_LINUX
	#define SUPPORT_VULKAN 1
#elif UNITY_ANDROID
	#ifndef SUPPORT_VULKAN
		#define SUPPORT_VULKAN 1
	#endif
#else
	#define SUPPORT_VULKAN 0
#endif

