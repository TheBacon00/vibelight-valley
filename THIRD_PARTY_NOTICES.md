# Third-party components

| Component | Used how | License |
|---|---|---|
| [Khronos OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK-Source) (`openxr_loader.dll`, 1.1.63) | Shipped in release packages next to the plugin | Apache-2.0 |
| [BepInEx](https://github.com/BepInEx/BepInEx) 6.0.0-be.788 (Unity IL2CPP) | Downloaded by the installer from builds.bepinex.dev; not redistributed here | LGPL-2.1 |
| [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) | Part of the BepInEx package (`winhttp.dll`) | LGPL-2.1 |
| [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop), [HarmonyX](https://github.com/BepInEx/HarmonyX) | Referenced at build time, provided by BepInEx | LGPL-3.0 / MIT |

Disney Dreamlight Valley is a trademark of its respective owners. Vibelight Valley is an unofficial fan
modification, not affiliated with or endorsed by Disney or Gameloft. It contains no game code or assets; the interop
assemblies it compiles against are generated locally from your own game installation and are never committed.

Design references (no code copied):
- [ConSimVR](https://github.com/nick10180/ConSimVR) and [UnityVRMod](https://github.com/NewUnityModder/UnityVRMod)
  showed that a C# OpenXR session with render-thread submission works in IL2CPP Unity games.
- [CircuitLordVRModInstaller](https://github.com/CircuitLord/CircuitLordVRModInstaller) (MIT) inspired the
  installer's design: a portable exe, a release manifest with pinned hashes, and self-update.
