# Ganymede — Fluid Simulation Project

Real-time SPH fluid simulation with thermal coupling, built as a Unity native plugin using Vulkan compute shaders.

---

## Prerequisites

Before opening the project, ensure the following minimum hardware is available:

- **GPU:** Discrete GPU with Vulkan 1.1 compute support (NVIDIA GTX 1060 / AMD RX 580 or newer recommended)
- **VRAM:** 4 GB minimum, 8 GB recommended for high particle counts
- **RAM:** 16 GB
- **OS:** Windows 10 64-bit or later (Vulkan compute is not supported on macOS or WebGL)

---

## 1. Install Unity Hub and the Editor

1. Download **Unity Hub** from https://unity.com/download and run the installer.
2. Open Unity Hub and sign in or create a free Unity account.
3. Go to **Installs -> Install Editor**.
4. Select **Unity 6 (6000.4.1f1)**. If it does not appear in the list, choose **Archive** and locate it at https://unity.com/releases/editor/archive.
5. During installation, enable the **Windows Build Support (IL2CPP)** module. No other platform modules are required.
6. Complete the installation and confirm the editor appears in the Installs list.

---

## 2. Open the Project

1. In Unity Hub, go to **Projects -> Add -> Add project from disk**.
2. Navigate to the `Ganymede/` folder inside the repository root and click **Select Folder**.
3. The project will appear labelled **Ganymede** with editor version **6000.4.1f1**. Click it to open.
4. Unity will import all assets on first launch — this takes several minutes.

---


## 3. Convert Materials to URP

After importing all assets, convert any remaining Built-in materials to URP in one pass:

1. Open **Window -> Rendering -> Render Pipeline Converter**.
2. Set the **Source Pipeline** dropdown to **Built-in**.
3. Set the **Target Pipeline** dropdown to **Universal Render Pipeline**.
4. Check the box next to **Material Upgrade**.
5. Click **Initialize Converters** (bottom-right) — Unity will scan the project and populate a list of affected materials.
6. Review the list, then click **Convert Assets**.

---

## 4. Enable Read/Write on All Meshes

Some imported assets ship with Read/Write disabled, which causes errors at runtime:

1. Open **Tools -> Voxel -> Enable Read/Write on All Meshes** from the menu bar.
2. Wait for the asset re-import to complete before entering Play mode.

---

## 5. Running the Demo Scenes

All demo scenes are located under `Assets/Scenes/Demo Scenes/`. Double-click a scene in the Project window to open it, then press **Play**.

| Scene | Description |
|---|---|
| `MainMenu.unity` | Start here — provides a menu to launch any other scene |
| `Free Play.unity` | Open sandbox with adjustable simulation parameters |

### 5.1 Simulation parameters

Select the **Physics** GameObject in the Hierarchy to expose the following settings in the Inspector:

| Parameter | Description |
|---|---|
| **Particle Count** | Reduce if frame rate is low |
| **Sub Step Count** | Physics sub-steps per frame — higher is more stable, lower is faster |
| **Stiffness / Smoothing Radius** | WCSPH pressure parameters — do not change unless re-baking the scene |
| **Use Fixed Time Step** | Enable if the simulation becomes unstable at variable frame rates |

### 6.2 Rendering — PhysicsWaterPhaseBridge

#### Render modes

Set **Rendering → Mode** to one of the three options:

| Mode | Description |
|---|---|
| `RaymarchVolume` | Ray-marched volumetric liquid + vapour. Best visual quality; requires a ray-marching material. |
| `MarchingCubesLiquidWithVapour` | Mesh-extracted liquid surface with a separate volumetric vapour pass. Lower GPU cost than full raymarching. |
| `ScreenSpaceFluid` | Screen-space depth/thickness blur. Fastest option; particle radius, blur, and refraction are controlled on the material itself. |

#### Density grid

Shared by all modes that use the density pipeline (raymarch and marching cubes).

| Parameter | Description |
|---|---|
| **Volume Dims** | Voxel resolution of the density grid (default 64³). Higher = sharper but more VRAM and compute. |
| **Vapour Smoothing Radius WS** | World-space splat radius for vapour particles. Wider = softer, more uniform vapour presence mask. |

#### Density blur (per render mode)

Two independent blur profiles exist: **Raymarch Density Blur** and **Marching Cubes Density Blur**. Each has identical controls for the liquid (R) and vapour (G) density channels.

| Parameter | Description |
|---|---|
| **Liquid Blur Enabled** | Gaussian-blur the liquid density channel before normal baking. Off by default. |
| **Liquid Blur Radius** | Kernel half-size in voxels (1–16). Cost is `3 × (2r+1)` taps (separable). |
| **Liquid Blur Sigma** | Gaussian sigma in voxels (0.1–8). Larger = wider spread. |
| **Liquid Blur Detail Preserve** | 0 = fully smooth; 1 = original high-frequency detail added back on top. |
| **Vapour Blur Enabled** | Same as above for the vapour channel. On by default. |
| **Vapour Blur Radius / Sigma / Detail Preserve** | Same controls as the liquid channel, applied to vapour. |

#### Adaptive liquid smoothing (per render mode)

Two independent profiles: **Raymarch Adaptive Liquid Smoothing** and **Marching Cubes Adaptive Liquid Smoothing**.

| Parameter | Description |
|---|---|
| **Liquid Smoothing Radius WS** | Splat radius for surface / isolated particles (low SPH density). Keep small for crisp splashes. But it does not yield that good looking but it was option we implemented|
| **Adaptive Radius Enabled** | Lerps per-particle radius between the surface and bulk radii based on local SPH density. |
| **Liquid Bulk Smoothing Radius WS** | Splat radius for fully-submerged bulk particles. Make larger than the surface radius for a smooth water body. |
| **Adaptive Density Surface** | SPH density threshold below which the surface radius applies (e.g. ~175). |
| **Adaptive Density Bulk** | SPH density threshold above which the bulk radius applies (e.g. ~300). |
| **Adaptive Density Curve** | Gamma on the density → radius mapping. `< 1` exaggerates variation among sparse particles; `> 1` among dense ones. |

#### Marching cubes only

| Parameter | Description |
|---|---|
| **Marching Cubes Iso Level** | Normalized density threshold for surface extraction (0–1). Higher = smaller / tighter surface. |
