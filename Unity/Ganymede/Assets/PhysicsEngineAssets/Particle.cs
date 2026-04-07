using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// CPU-side mirror of the native/Vulkan particle layout.
/// IMPORTANT: Field order/sizes must match the struct in the native plugin and HLSL.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Particle
{
    public Vector3 position;   // float3
    public float density;      // float

    public Vector3 velocity;   // float3
    public float pressure;     // float

    public Vector3 acceleration; // float3
    public float mass;           // float

    public float temperature;  // float
    public int phase;          // int

    public float _pad0;        // padding (kept for 64-byte stride)
    public float _pad1;

    public static Particle Create(Vector3 position, Vector3 velocity, float mass, int phase = 0, float temperature = 0f)
    {
        return new Particle
        {
            position = position,
            density = 0f,
            velocity = velocity,
            pressure = 0f,
            acceleration = Vector3.zero,
            mass = mass,
            temperature = temperature,
            phase = phase,
            _pad0 = 0f,
            _pad1 = 0f,
        };
    }
}
