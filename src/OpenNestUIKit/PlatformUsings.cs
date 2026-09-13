// 平台 interop 命名空间适配（仅 MelonLoader 版生效）。
//
// BepInEx interop 把游戏类型放全局命名空间（MissionManager / TMPro / ...）；
// MelonLoader Il2CppAssemblies 统一加前缀（Il2Cpp.MissionManager / Il2CppTMPro / ...）。
// 说明：经桥 BepInEx.MelonLoader.Loader 加载时，MLL 模组引用的 Il2Cpp* 类型由
//       宿主 interop 里的"别名类型"承担（见 docs/MOD_MENU.md §3.2），故此处适配两端通用。
#if MELONLOADER
global using Il2Cpp;
global using Il2CppTMPro;
#endif
