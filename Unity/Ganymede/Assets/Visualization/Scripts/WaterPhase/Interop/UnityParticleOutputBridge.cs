using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class UnityParticleOutputBridge
{
#if (UNITY_IOS || UNITY_TVOS || UNITY_SWITCH) && !UNITY_EDITOR
    private const string PluginName = "__Internal";
#else
    private const string PluginName = "RenderingPlugin";
#endif

    [DllImport(PluginName)]
    private static extern void SetUnityParticleOutputBuffer(IntPtr nativeBuffer);

    private IntPtr _registeredNativePtr = IntPtr.Zero;
    private bool _warnedAboutNonVulkan;

    public void RegisterIfNeeded(ComputeBuffer particleOutputBuffer)
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
        {
            if (!_warnedAboutNonVulkan)
            {
                Debug.LogWarning("[PhysicsWaterPhaseBridge] Graphics API is not Vulkan. Native plugin output copy will not run.");
                _warnedAboutNonVulkan = true;
            }
            return;
        }

        if (particleOutputBuffer == null)
            return;

        IntPtr nativePtr = particleOutputBuffer.GetNativeBufferPtr();
        if (nativePtr == IntPtr.Zero || nativePtr == _registeredNativePtr)
            return;

        SetUnityParticleOutputBuffer(nativePtr);
        _registeredNativePtr = nativePtr;
    }

    public void ClearRegistration()
    {
        try
        {
            SetUnityParticleOutputBuffer(IntPtr.Zero);
        }
        catch (Exception)
        {
            // Ignore shutdown order issues.
        }

        _registeredNativePtr = IntPtr.Zero;
    }
}
