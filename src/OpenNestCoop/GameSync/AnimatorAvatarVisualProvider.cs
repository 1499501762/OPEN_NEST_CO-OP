#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// Animator 化身视觉提供者（方案 A：AssetBundle + Unity Animator）。
///
/// 背景：外部 3D 模型这条路之前"完全不行"——SharpGLTF 只是格式解析库，SkinnedMeshRenderer
/// 骨骼绑定 + 动画采样都是手搓的（且 SharpGLTF.Runtime 在 IL2CPP 下 NodeInstance.LocalMatrix
/// 返回错值），渲染/动画本该交给 Unity 自己的成熟引擎。
///
/// 本提供者改用 Unity 原生链路，运行时零手搓：
///   离线（tools/playerbundle/，Unity 6000.3.21f1 与游戏一致）：
///     导入 ref/model/*.glb（Rig = Humanoid）+ Mixamo 人形动画 → AnimatorController
///     （参数 Speed/Sprinting/Crouched/Airborne/Moving/Strafe/MoveFwd/HeadPitch）→ 打包。
///   运行时：
///     AssetBundle.LoadFromFile → LoadAsset&lt;GameObject&gt;（预置）→ Instantiate
///     → GetComponent&lt;Animator&gt;()，把 PlayerSync 的 AvatarPose 直接 SetFloat/SetBool。
///   动画混合/过渡/Humanoid 重定向全由 Unity 引擎完成，IL2CPP 安全（只调游戏已含的
///   UnityEngine.AnimationModule / AssetBundleModule）。
///
/// 加载顺序：env ONC_BUNDLE → 游戏根 Models/player.bundle → 游戏根 player.bundle
///   → 插件目录(OpenNestCoop) 下 player.bundle。找不到 → TryLoad 返回 false → PlayerSync 回退。
/// 根因注意：bundle 必须用与游戏相同的 Unity 版本（6000.3.21f1）打包，否则 LoadFromFile 失败。
/// </summary>
public sealed class AnimatorAvatarVisualProvider : IPlayerVisualProvider
{
    public static readonly AnimatorAvatarVisualProvider Instance = new();

    /// <summary>是否启用 AssetBundle 化身。
    /// ✅ 2026-08-15 最终方案：AssetBundle.LoadFromStream(Il2CppSystem.IO.FileStream) 打通。
    /// 该游戏 IL2CPP 裁剪极深：LoadFromFile(string) 缺 ReadOnlySpan.GetPinnableReference、
    /// LoadFromMemory(byte[]) 封送失败、原生 icall 直调崩游戏、UnityWebRequest 发送 icall 被裁。
    /// 只有 LoadFromStream(Il2CppSystem.IO.Stream)（对象指针，绕过 span/byte[]）可用。</summary>
    public static bool Enabled = true;

    /// <summary>是否尝试原生 il2cpp_runtime_invoke/icall 直调（已证实不可行，恒 false 保留说明）。</summary>
    private const bool NativeLoadEnabled = false;

    /// <summary>是否已成功加载 AssetBundle 与玩家预置（进程内缓存）。</summary>
    public bool IsLoaded => _prefab != null;

    // ---- AssetBundle 资源（受管句柄，进程内共享，会话期间保持加载；卸载由 AssetBundleIron 统一管理） ----
    private static OpenNestCore.Assets.AssetBundleIron _bundleHandle;
    private static GameObject _prefab;
    private static bool _tried;
    // ⚠️ 性能：Update 每帧避免 GetComponent/Find/Camera.main（IL2CPP 下都慢）→ 缓存。
    private static readonly Dictionary<GameObject, Animator> _animCache = new();
    private static readonly Dictionary<GameObject, Transform> _nameTagCache = new();
    private static float _lastGroundLog = -10f;   // 脚贴地降频（0.15s）
    private static Camera _camCache;              // Camera.main 缓存
    // ---- 动画参数名（与 tools/playerbundle 的 AnimatorController 约定一致；
    //      不存在的参数自动跳过 Set，不会报错刷屏） ----
    // ⚠️ 2026-08-15 参数名核对（Player.controller）：
    //   - 横移参数名是 "MoveStrafe"（2D BlendTree m_BlendParameterY: MoveStrafe），不是 "Strafe"！
    //   - 5 个参数：Speed(float)/MoveFwd(float)/MoveStrafe(float)/Crouched(bool)/Airborne(bool)
    //   - "Moving"/"Sprinting"/"HeadPitch" controller 里没有 → 自动跳过（无害）
    private const string P_Speed = "Speed";          // float：见下方 Speed 值映射说明
    private const string P_Sprinting = "Sprinting";  // bool：奔跑（controller 无此参数 → 跳过）
    private const string P_Crouched = "Crouched";    // bool：蹲下
    private const string P_Airborne = "Airborne";    // bool：空中（跳跃/下落）
    private const string P_Moving = "Moving";        // bool：是否在移动（controller 无 → 跳过）
    private const string P_Strafe = "MoveStrafe";    // float：横移（-1~1，正=右）⚠️ 必须用 MoveStrafe
    private const string P_MoveFwd = "MoveFwd";      // float：前进（-1~1，正=前）
    // 注：controller 无 HeadPitch 参数（头部未做额外旋转），模组不驱动。

    /// <summary>尝试加载 AssetBundle 与玩家预置。成功返回 true（之后 Create 才可用）。
    /// ✅ 2026-08-15 最终路线：AssetBundle.LoadFromStream(Il2CppSystem.IO.FileStream)。
    /// 这是唯一实证调通的加载入口：
    ///   - 托管 LoadFromFile(string)/LoadFromMemory(byte[]) → IL2CPP 裁剪/封送失败
    ///   - 原生 icall 直调 → 崩游戏
    ///   - UnityWebRequest → 发送 icall 被裁，请求不推进
    ///   - LoadFromStream(Il2CppSystem.IO.Stream) → ✅ 成功（参数是 Il2Cpp 对象指针，绕过 span 裁剪）
    /// 运行时实证：FileStream(456705B) → LoadFromStream → name='player.bundle' → prefab=Player</summary>
    public bool TryLoad()
    {
        if (!Enabled) return false;
        if (_tried) return _prefab != null;
        _tried = true;
        try
        {
            string path = FindBundlePath();
            if (path == null)
            {
                CoopLog.Debug("AnimatorAvatar.load", () => "AnimatorAvatar: no player.bundle found (fallback to other provider)");
                return false;
            }

            // ✅ 主路径：AssetBundle.LoadFromStream(Il2CppSystem.IO.FileStream)
            // 参数是 Il2CppSystem.IO.Stream 对象（非 span/byte[]），成功绕过该游戏 IL2CPP 的裁剪。
            // 失败时静默回退（日志标注），不抛异常。
            string full = Path.GetFullPath(path);
            try
            {
                // ✅ 主路径：AssetBundleIron.Load（LoadFromStream(Il2CppSystem.IO.FileStream)，
                // 参数是 Il2Cpp 对象指针绕过 span 裁剪；受管句柄 + 引用计数，保持 stream 打开）。
                _bundleHandle = OpenNestCore.Assets.AssetBundleIron.Load(full);
                if (_bundleHandle != null && _bundleHandle.IsValid)
                {
                    _prefab = _bundleHandle.LoadPrefab("Player", "PlayerPrefab", "Soldier", "Avatar", "player");
                    if (_prefab != null)
                    {
                        CoopLog.Info("AnimatorAvatar.loaded", () => $"AnimatorAvatar: player.bundle 加载成功（LoadFromStream）bundle='{_bundleHandle.Name}' prefab='{_prefab.name}'");
                        return true;
                    }
                    CoopLog.Warn("AnimatorAvatar.load", () => "player.bundle 加载成功但未找到 Player prefab，回退");
                }
                else
                {
                    CoopLog.Warn("AnimatorAvatar.load", () => "AssetBundle.LoadFromStream 返回 null，回退");
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warn("AnimatorAvatar.load", () => $"AssetBundle.LoadFromStream → {ex.GetType().Name}: {ex.Message}，回退");
            }
            return false;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("AnimatorAvatar.load", () => $"TryLoad: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public GameObject Create(Transform root, string playerName, Color tint)
    {
        if (_prefab == null) return null;
        var go = UnityEngine.Object.Instantiate(_prefab, root, false);
        go.name = "AnimatorAvatar";
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;

        var anim = go.GetComponent<Animator>();
        if (anim != null)
        {
            try
            {
                // 位置由 PlayerSync 插值驱动，禁用根运动；离屏也保持动画
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            catch { }
            _paramCache[go] = CollectParams(anim);
        }
        _animCache[go] = anim;                       // 缓存，Update 不再 GetComponent
        _nameTagCache[go] = go.transform.Find("Name"); // 缓存名字标签
        AddNameTag(go.transform, playerName, 1.95f);
        OpenNestCore.Assets.AssetBundleIron.RepairMaterials(go); // ⚠️ bundle 材质 shader 为空 → 用游戏内 shader 替换
        if (IsLocalDiag()) DumpAvatarState(go, anim, playerName);  // 诊断仅本地双端测试模式
        return go;
    }

    /// <summary>是否本地双端测试模式（诊断日志仅此模式开启）。</summary>
    private static bool IsLocalDiag()
    {
        try { return CoopRuntime.Net != null && CoopRuntime.Net.LocalMode; }
        catch { return false; }
    }

    // ⚠️ 材质修复已抽象到 OpenNestCore.Assets.AssetBundleIron.RepairMaterials（URP shader 替换 + _MainTex→_BaseMap 迁移）。

    // ⚠️ 诊断：打印实例化后模型/动画真实状态（仅本地双端测试模式调用，Debug 级）
    private static void DumpAvatarState(GameObject go, Animator anim, string playerName)
    {
        try
        {
            CoopLog.Debug("AnimatorAvatar.state", () => $"avatar='{playerName}' activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} pos={go.transform.position} localScale={go.transform.localScale}");
            int renderers = 0, skinned = 0, bones = 0;
            var mats = new System.Collections.Generic.List<string>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                if (r is SkinnedMeshRenderer smr) { skinned++; bones = Mathf.Max(bones, smr.bones != null ? smr.bones.Length : 0); }
                var m = r.sharedMaterial;
                mats.Add($"{r.name}:[{(m == null ? "NO_MAT" : m.name)}/{((m != null && m.shader != null && m.shader.name != "") ? m.shader.name : "BAD_SHADER")}]");
                try { CoopLog.Debug("AnimatorAvatar.state", () => $"  renderer '{r.name}' type={r.GetType().Name} enabled={r.enabled} bounds={r.bounds.center}/{r.bounds.size}"); } catch { }
            }
            // 直接找 SkinnedMeshRenderer（复数，绕过 is 判断可能的问题）
            try
            {
                var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs == null || smrs.Length == 0)
                {
                    CoopLog.Debug("AnimatorAvatar.state", () => "SkinnedMeshRenderer: NONE");
                }
                else
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var smr in smrs)
                        if (smr != null)
                            parts.Add($"'{smr.gameObject.name}' bones={(smr.bones != null ? smr.bones.Length : 0)} mesh={(smr.sharedMesh != null ? smr.sharedMesh.name : "no-mesh")} en={smr.enabled}");
                    CoopLog.Debug("AnimatorAvatar.state", () => $"SkinnedMeshRenderer x{smrs.Length}: {string.Join(" | ", parts)}");
                }
            }
            catch (Exception ex) { CoopLog.Debug("AnimatorAvatar.state", () => $"smr lookup err: {ex.Message}"); }
            CoopLog.Debug("AnimatorAvatar.state", () => $"renderers={renderers} skinned={skinned} skinnedBones={bones} mats={string.Join(" | ", mats)}");
            if (anim != null)
            {
                CoopLog.Debug("AnimatorAvatar.state", () => $"animator enabled={anim.enabled} isHuman={anim.isHuman} isInitialized={anim.isInitialized}");
                var acc = anim.runtimeAnimatorController;
                CoopLog.Debug("AnimatorAvatar.state", () => $"runtimeAnimatorController={(acc == null ? "NULL(动画控制器未加载!)" : "ok")} layers={anim.layerCount} params={(_paramCache.TryGetValue(go, out var s) ? s.Count : 0)}");
                try { var st = anim.GetCurrentAnimatorStateInfo(0); CoopLog.Debug("AnimatorAvatar.state", () => $"state0 nameHash={st.shortNameHash} length={st.length} speed={st.speed} normalized={st.normalizedTime}"); } catch (Exception ex) { CoopLog.Debug("AnimatorAvatar.state", () => $"state0 err: {ex.Message}"); }
                try { if (anim.avatar != null) CoopLog.Debug("AnimatorAvatar.state", () => $"avatar name={anim.avatar.name} isHuman={anim.avatar.isHuman}"); else CoopLog.Debug("AnimatorAvatar.state", () => "avatar=NULL"); } catch (Exception ex) { CoopLog.Debug("AnimatorAvatar.state", () => $"avatar err: {ex.Message}"); }
            }
            else
            {
                CoopLog.Debug("AnimatorAvatar.state", () => "animator=NULL (没有 Animator 组件)");
            }
        }
        catch (Exception ex) { CoopLog.Debug("AnimatorAvatar.state", () => $"DumpAvatarState err: {ex.GetType().Name}: {ex.Message}"); }
    }

    public void Update(GameObject visual, float dt, ref AvatarPose pose)
    {
        if (visual == null) return;

        // 朝向：模型 +Z 朝 pose.Yaw（打包工程里已确保 +Z 是模型正面，与骨架/人形约定一致）
        visual.transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f);

        // 用缓存 Animator（避免每帧 GetComponent）
        var anim = _animCache.TryGetValue(visual, out var a) ? a : null;
        if (anim != null)
        {
            var p = _paramCache.TryGetValue(visual, out var s) ? s : null;
            if (p != null)
            {
                // ⚠️ Speed 值映射（2026-08-15 核对 Player.controller）：
                //   - 站立 Locomotion2D 是 2D BlendTree（MoveFwd×MoveStrafe，-1~1），不用 Speed
                //   - Crouch 是 1D BlendTree，Speed 阈值 0~1.2（0=蹲idle，1.2=蹲走）
                //   - Jump 用 m_SpeedParameter:Speed / m_TimeParameter:Speed（播放速度≈1）
                //   → 不能直接发 m/s（跑动 5+ 会撑爆阈值/加速跳跃），按状态映射。
                float animSpeed;
                if (pose.Airborne)
                    animSpeed = 1f;                                  // 跳跃：正常播放速度
                else if (pose.Crouched)
                    animSpeed = Mathf.Clamp(pose.Speed * 0.6f, 0f, 1.2f); // 蹲走：0~1.2（蹲走 m/s≈0~2）
                else
                    animSpeed = Mathf.Clamp(pose.Speed * 0.15f, 0f, 1f);  // 站立：2D 不用，给个低值备用

                SetFloat(anim, p, P_Speed, animSpeed);
                SetFloat(anim, p, P_Strafe, Mathf.Clamp(pose.MoveStrafe, -1f, 1f));
                SetFloat(anim, p, P_MoveFwd, Mathf.Clamp(pose.MoveFwd, -1f, 1f));
                bool moving = pose.Moving || pose.Speed > 0.05f;
                SetBool(anim, p, P_Moving, moving);
                SetBool(anim, p, P_Sprinting, pose.Sprinting);
                SetBool(anim, p, P_Crouched, pose.Crouched);
                SetBool(anim, p, P_Airborne, pose.Airborne);
            }
        }

        // 脚贴地（非空中）：用脚骨世界 Y 向下射线找地面校正。降频（0.15s）避免每帧 Physics 射线。
        // 空中/跳跃：世界 Y 已由 PlayerSync 同步（含跳跃高度），不再额外抬升、也不贴地。
        if (!pose.Airborne)
        {
            float now = Time.time;
            if (now - _lastGroundLog > 0.15f)
            {
                _lastGroundLog = now;
                GroundModel(visual, anim);
            }
        }

        // 名字标签 billboard（用缓存的 Name transform + 缓存的 Camera）
        try
        {
            var nameTag = _nameTagCache.TryGetValue(visual, out var nt) ? nt : null;
            if (nameTag != null)
            {
                if (_camCache == null) _camCache = Camera.main;
                if (_camCache != null) nameTag.rotation = _camCache.transform.rotation;
            }
        }
        catch { }
    }

    public void Destroy(GameObject visual)
    {
        // 根由 PlayerSync 统一销毁；bundle 进程内共享不在此卸载
        try
        {
            if (visual != null)
            {
                _paramCache.Remove(visual);
                _animCache.Remove(visual);
                _nameTagCache.Remove(visual);
            }
        }
        catch { }
    }

    // ---------------- 加载 ----------------

    private static string FindBundlePath()
    {
        // 1) 环境变量显式指定
        try
        {
            var env = Environment.GetEnvironmentVariable("ONC_BUNDLE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        }
        catch { }

        // 2) 游戏根目录候选（向上探测 BepInEx/MelonLoader/exe 特征）
        var roots = new List<string>();
        foreach (var dir in ExternalModelProvider.FindGameRootCandidates())
        {
            if (dir == null) continue;
            roots.Add(Path.Combine(dir, "Models"));
            roots.Add(dir);
        }
        // 3) 插件部署目录（deploy.ps1 输出处）
        try
        {
            var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
            {
                var d = Path.GetDirectoryName(loc);
                if (d != null) roots.Add(d);
            }
        }
        catch { }

        foreach (var root in roots)
        {
            try
            {
                var p = Path.Combine(root, "player.bundle");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    // ---------------- 加载（路径查找保留在模组；加载/材质修复已抽象到 OpenNestCore.Assets.AssetBundleIron） ----------------

    // ---------------- Animator 参数（仅设存在的参数，避免 Unity 警告刷屏） ----------------
    // ⚠️ 2026-08-15：该游戏 IL2CPP 缺 ReadOnlySpan.GetPinnableReference → Animator.SetFloat(string)
    // /SetBool(string)/GetFloat(string) 全部抛 MethodNotFound！必须用 int 哈希重载
    // (SetFloat(int,float)/SetBool(int,bool))，它不经过 span 封装，可用。

    private static readonly Dictionary<GameObject, HashSet<string>> _paramCache = new();
    private static readonly Dictionary<string, int> _hashCache = new(StringComparer.Ordinal);

    private static int GetHash(string name)
    {
        if (_hashCache.TryGetValue(name, out var h)) return h;
        int hv = 0;
        try { hv = Animator.StringToHash(name); } catch { }
        _hashCache[name] = hv;
        return hv;
    }

    private static HashSet<string> CollectParams(Animator anim)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var ps = anim.parameters;
            if (ps != null)
                foreach (var prm in ps)
                {
                    if (prm == null) continue;
                    var nm = prm.name;
                    if (nm != null) set.Add(nm);
                }
        }
        catch { }
        return set;
    }

    private static void SetFloat(Animator anim, HashSet<string> p, string name, float v)
    {
        if (!p.Contains(name)) return;
        try { anim.SetFloat(GetHash(name), v); } catch { }
    }

    private static void SetBool(Animator anim, HashSet<string> p, string name, bool v)
    {
        if (!p.Contains(name)) return;
        try { anim.SetBool(GetHash(name), v); } catch { }
    }

    // ---------------- 脚贴地 ----------------

    private static void GroundModel(GameObject visual, Animator anim)
    {
        try
        {
            var root = visual.transform.parent;
            if (root == null) return;
            Vector3 origin = root.position + Vector3.up * 0.2f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 6f)) return;
            float groundY = hit.point.y;

            float footY = GetFootWorldY(visual, anim);
            if (float.IsNaN(footY)) return;
            float dy = groundY - footY;
            if (Mathf.Abs(dy) > 0.005f)
            {
                var lp = visual.transform.localPosition;
                lp.y += dy;
                visual.transform.localPosition = lp;
            }
        }
        catch { }
    }

    /// <summary>脚底参考世界 Y：优先 Humanoid 脚骨，回退 Hips-1m，再回退 renderer 包围盒最低点。</summary>
    private static float GetFootWorldY(GameObject visual, Animator anim)
    {
        try
        {
            if (anim != null)
            {
                var lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
                if (lf != null && rf != null)
                    return Mathf.Min(lf.position.y, rf.position.y);
                var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) return hips.position.y - 1.0f;
            }
        }
        catch { }
        try
        {
            var smr = visual.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) return smr.bounds.min.y;
        }
        catch { }
        try
        {
            var mf = visual.GetComponentInChildren<MeshRenderer>(true);
            if (mf != null) return mf.bounds.min.y;
        }
        catch { }
        return float.NaN;
    }

    // ---------------- 名字标签（与 HumanoidVisualProvider 一致） ----------------

    private static void AddNameTag(Transform parent, string name, float y)
    {
        try
        {
            var go = new GameObject("Name");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);
            var tmp = go.AddComponent<TextMeshPro>();
            if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = name;
            tmp.fontSize = 0.4f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.enableWordWrapping = false;
        }
        catch { }
    }
}
