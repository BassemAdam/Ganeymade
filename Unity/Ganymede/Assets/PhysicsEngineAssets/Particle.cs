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

    public float latentHeatAccum; // accumulated latent energy for phase transition
    public int fixedId;
    public float neighborCount;  // unweighted neighbor count within splashRadius (for screen-space rendering)
    public float _pad0;          // padding to match GPU array stride (80 bytes)
    public float _pad1;
    public float _pad2;

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
            latentHeatAccum = 0f,
            fixedId = 0,
            neighborCount = 0f,
        };
    }
}
