#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;
using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 猫船员化身视觉提供者（方案 C：克隆游戏自己的猫船员 + 原 Animator）。
///
/// 背景：方案 A（AssetBundle）被 IL2CPP 剥离判死、原生 icall 是空壳；SharpGLTF 用户判"效果不行"。
/// 本提供者回到"游戏自己在用"的 API：场景里游戏自带的猫船员（CatController 挂 Animator，
/// 用 Unity 原版 AnimatorController + 动画 clip），模组运行时克隆一只猫的模型+Animator，
/// 挂到远端玩家身上，用 AvatarPose（Speed/Moving/Airborne/Crouched/Sprinting…）驱动它的
/// Animator 参数。动画/混合/重定向全是游戏引擎自己做的——这才是"真 Unity 动画"。
///
/// 步骤：
///   TryLoad()  场景找 CatController → 取 Animator（GetComponentInChildren）→ 缓存为模板 + 探测日志
///   Create()   克隆模板 GameObject（保留 Animator/Renderer/骨骼）→ 移除逻辑组件 → 挂 root → 名字标签
///   Update()   朝向 = pose.Yaw；用 AvatarPose 驱动 Animator 参数（按参数名语义尽力映射）；billboard
///
/// 注：猫 Animator 的具体参数名需运行时探测（ProbeCat 日志），映射表按常见命名覆盖，拿到真实参数名后可精调。
/// </summary>
public sealed class CatCrewVisualProvider : IPlayerVisualProvider
{
    public static readonly CatCrewVisualProvider Instance = new();

    private static Transform _template;      // 模板：Animator 所在 GameObject 的 transform
    private static bool _probed;             // 探测只跑一次（含 TryLoad 失败）

    /// <summary>场景里是否有可克隆的猫船员。失败返回 false（调用方回退其它 Provider）。只读，安全。</summary>
    public bool TryLoad()
    {
        if (_template != null) return true;
        if (_probed) return false;
        _probed = true;
        try
        {
            var cats = UnityEngine.Object.FindObjectsOfType<CatController>();
            if (cats == null || cats.Length == 0)
            {
                CoopRuntime.LogSource?.LogWarning("CatCrew: 场景无猫（CatController 未找到），不可用");
                return false;
            }
            var cat = cats[0];
            var anim = cat.GetComponentInChildren<Animator>(true);
            if (anim == null)
            {
                CoopRuntime.LogSource?.LogWarning("CatCrew: 猫无 Animator，不可用");
                return false;
            }
            _template = anim.transform;
            ProbeCat(cat, anim);
            CoopRuntime.LogSource?.Info($"CatCrew: 猫模板就绪 catRoot='{cat.gameObject.name}' animGo='{anim.gameObject.name}'");
            return true;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"CatCrew.TryLoad: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public GameObject Create(Transform root, string playerName, Color tint)
    {
        if (_template == null && !TryLoad()) return null;
        try
        {
            var clone = UnityEngine.Object.Instantiate(_template.gameObject, root, false);
            clone.name = "CatCrew";
            RemoveLogic(clone.transform);
            // 模型放在 root 原点（PlayerSync 管位置）；先保持原始 localScale（Instantiate 保留）
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            AddNameTag(clone.transform, playerName, 1.5f);
            CoopRuntime.LogSource?.Info($"CatCrew.Create: 克隆猫船员挂载 pid 玩家 '{playerName}'");
            return clone;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"CatCrew.Create: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Update(GameObject visual, float dt, ref AvatarPose pose)
    {
        if (visual == null) return;
        try { visual.transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f); } catch { }

        var anim = visual.GetComponent<Animator>();
        if (anim != null)
        {
            try { anim.applyRootMotion = false; } catch { }
            try { anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; } catch { }
            DriveAnimator(anim, pose);
        }

        // 名字标签 billboard
        try
        {
            var nameTag = visual.transform.Find("Name");
            if (nameTag != null)
            {
                var cam = Camera.main;
                if (cam != null) nameTag.rotation = cam.transform.rotation;
            }
        }
        catch { }
    }

    public void Destroy(GameObject visual)
    {
        // 根由 PlayerSync 统一销毁；这里清理实例缓存
        try
        {
            if (visual != null)
            {
                _paramCache.Remove(visual);
                _airborne.Remove(visual);
            }
        }
        catch { }
    }

    // ---------------- 组件清理：只留 渲染/骨骼/Animator，去掉猫 AI 逻辑 ----------------

    private static void RemoveLogic(Transform root)
    {
        try
        {
            // 只删 MonoBehaviour 逻辑组件（CatController、CatMovementManager、AgentMover、
            // AgentAnimation、Malbers 的 MAnimal 等）。Animator 是 Behaviour（非 MonoBehaviour）、
            // Renderer/MeshFilter 是 Component（非 MonoBehaviour）→ 天然不在遍历里，不会被误删。
            // （之前用 GetComponentsInChildren<Component> + `c is Animator` 判断 + 原生指针集合保护，
            //   实测 BepInEx 下指针比较失效导致 Renderer 被误删、猫透明不可见。）
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                try
                {
                    mb.enabled = false;   // 先停用，避免 Awake/Start/Update 干扰
                    UnityEngine.Object.Destroy(mb);
                }
                catch { }
            }
            // 物理组件（Collider/Rigidbody 不是 MonoBehaviour）：克隆猫不参与物理、不阻挡玩家
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                if (col != null) { try { UnityEngine.Object.Destroy(col); } catch { } }
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                if (rb != null) { try { UnityEngine.Object.Destroy(rb); } catch { } }
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"CatCrew.RemoveLogic: {ex.Message}");
        }
    }

    // ---------------- Animator 驱动（映射到猫实际参数：Malbers CatAnimator） ----------------
    // 探测实测参数：MovementSpeed(Float,0=待机)、Idle(Bool)、Carrying(Bool)、
    // JumpUp/JumpDown(Trigger)、Pause/Activity1-6/Shoo/Pet/loopEnd/PetIdle(Trigger)。
    // 只设存在的参数；Trigger 用沿触发（避免每帧重复触发）。

    private static readonly Dictionary<GameObject, HashSet<string>> _paramCache = new();
    private static readonly Dictionary<GameObject, bool> _airborne = new();

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

    private static void DriveAnimator(Animator anim, AvatarPose pose)
    {
        HashSet<string> p;
        try
        {
            if (!_paramCache.TryGetValue(anim.gameObject, out p))
            {
                p = CollectParams(anim);
                _paramCache[anim.gameObject] = p;
            }
        }
        catch { return; }

        bool moving = pose.Moving || pose.Speed > 0.05f;
        // MovementSpeed：Malbers 动物 locomotion，0=待机、走动/跑动给实际速度(m/s)
        SetF(anim, p, "MovementSpeed", moving ? Mathf.Max(0.01f, pose.Speed) : 0f);
        SetB(anim, p, "Idle", !moving);

        // 跳跃沿：进入空中 JumpUp、落地 JumpDown
        bool prevAir = _airborne.TryGetValue(anim.gameObject, out var pa) ? pa : false;
        if (pose.Airborne && !prevAir) SetT(anim, p, "JumpUp");
        else if (!pose.Airborne && prevAir) SetT(anim, p, "JumpDown");
        _airborne[anim.gameObject] = pose.Airborne;
    }

    private static void SetF(Animator anim, HashSet<string> p, string name, float v)
    {
        if (!p.Contains(name)) return;
        try { anim.SetFloat(name, v); } catch { }
    }

    private static void SetB(Animator anim, HashSet<string> p, string name, bool v)
    {
        if (!p.Contains(name)) return;
        try { anim.SetBool(name, v); } catch { }
    }

    private static void SetT(Animator anim, HashSet<string> p, string name)
    {
        if (!p.Contains(name)) return;
        try { anim.SetTrigger(name); } catch { }
    }

    // ---------------- 探测（只读日志：猫结构 + Animator 参数，供精调映射） ----------------

    private static void ProbeCat(CatController cat, Animator anim)
    {
        try
        {
            CoopRuntime.LogSource?.LogWarning($"[catprobe] catRoot='{cat.gameObject.name}' pos={cat.transform.position} rot={cat.transform.eulerAngles}");
            try
            {
                var ac = anim.runtimeAnimatorController;
                CoopRuntime.LogSource?.LogWarning($"[catprobe] animator on '{anim.gameObject.name}' layerCount={anim.layerCount} controller={(ac != null ? ac.name : "null")}");
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[catprobe] animator info: {ex.Message}"); }
            DumpTree(cat.transform, 0, 3);
            try
            {
                var ps = anim.parameters;
                if (ps == null) { CoopRuntime.LogSource?.LogWarning("[catprobe] anim.parameters = null"); }
                else
                {
                    CoopRuntime.LogSource?.LogWarning($"[catprobe] Animator 参数数={ps.Length}");
                    foreach (var prm in ps)
                    {
                        if (prm == null) continue;
                        string extra = "";
                        try { extra = $" type={prm.type} defF={prm.defaultFloat} defB={prm.defaultBool}"; } catch { }
                        CoopRuntime.LogSource?.LogWarning($"[catprobe]   param '{prm.name}'{extra}");
                    }
                }
            }
            catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[catprobe] parameters: {ex.GetType().Name}: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"[catprobe] ProbeCat: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DumpTree(Transform t, int depth, int maxDepth)
    {
        if (t == null || depth > maxDepth) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(' ', depth * 2);
            sb.Append($"'{t.gameObject.name}'");
            var anim = t.GetComponent<Animator>();
            if (anim != null) sb.Append(" [Animator]");
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) sb.Append(" [SkinnedMesh x" + (smr.bones != null ? smr.bones.Length.ToString() : "0") + "]");
            if (t.GetComponent<MeshRenderer>() != null) sb.Append(" [MeshRenderer]");
            if (t.GetComponent<Collider>() != null) sb.Append(" [Collider]");
            CoopRuntime.LogSource?.LogWarning($"[catprobe] {sb}");
            for (int i = 0; i < t.childCount && depth < maxDepth; i++)
                DumpTree(t.GetChild(i), depth + 1, maxDepth);
        }
        catch { }
    }

    // ---------------- 名字标签（3D TMP，billboard；独立实现避免依赖其它 Provider） ----------------

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
