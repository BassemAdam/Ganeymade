// Vulkan compute shader Unity plugin — entry point

#include "PlatformBase.h"
#include "Unity/IUnityGraphics.h"

#if SUPPORT_VULKAN
extern void VulkanCompute_Initialize(IUnityInterfaces* interfaces);
extern void VulkanCompute_Shutdown();
extern void VulkanCompute_Dispatch();
#endif

// --------------------------------------------------------------------------
// Unity plugin lifecycle

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType);

static IUnityInterfaces* s_UnityInterfaces = NULL;
static IUnityGraphics* s_Graphics = NULL;
static UnityGfxRenderer s_DeviceType = kUnityGfxRendererNull;

extern "C" void	UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
	s_UnityInterfaces = unityInterfaces;
	s_Graphics = s_UnityInterfaces->Get<IUnityGraphics>();
	s_Graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);

	OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
	s_Graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
}

// --------------------------------------------------------------------------
// Graphics device events

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
	if (eventType == kUnityGfxDeviceEventInitialize)
	{
		s_DeviceType = s_Graphics->GetRenderer();
#if SUPPORT_VULKAN
		if (s_DeviceType == kUnityGfxRendererVulkan)
			VulkanCompute_Initialize(s_UnityInterfaces);
#endif
	}

	if (eventType == kUnityGfxDeviceEventShutdown)
	{
#if SUPPORT_VULKAN
		if (s_DeviceType == kUnityGfxRendererVulkan)
			VulkanCompute_Shutdown();
#endif
		s_DeviceType = kUnityGfxRendererNull;
	}
}

// --------------------------------------------------------------------------
// Render event callback — dispatches compute shader

static void UNITY_INTERFACE_API OnRenderEvent(int eventID)
{
	if (eventID == 3)
	{
#if SUPPORT_VULKAN
		VulkanCompute_Dispatch();
#endif
	}
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API GetRenderEventFunc()
{
	return OnRenderEvent;
}
