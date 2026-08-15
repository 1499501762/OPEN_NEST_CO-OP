// 平台 interop 命名空间适配（仅 ML 版生效）。
//
// BepInEx interop 把类型放全局命名空间（GunController / TMPro / Steamworks）；
// MelonLoader Il2CppAssemblies 则统一加前缀（Il2Cpp.GunController / Il2CppTMPro / Il2CppSteamworks）。
// 本文件用 global using 命名空间导入（不支持命名空间别名，故 Steamworks/TMPro 的
// 命名空间别名在用到它们的文件里用 #if MELONLOADER 显式声明）。
#if MELONLOADER
global using Il2Cpp;
global using Il2CppTMPro;
global using Il2CppSteamworks;
#endif
