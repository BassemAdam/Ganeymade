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
