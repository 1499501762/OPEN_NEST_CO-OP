#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 默认角色视觉提供者：头球（sphere）+ 游戏内防毒面罩头模（"gasmask" mesh，尽力而为）
/// + 3D 名字标签（billboard 正对观察者）。
/// 任何模组可注册自定义 IPlayerVisualProvider 替换此实现。
/// </summary>
public sealed class DefaultPlayerVisualProvider : IPlayerVisualProvider
{
    public static readonly DefaultPlayerVisualProvider Instance = new();

    private Mesh _maskMesh;
    private bool _maskTried;
    private Texture2D _maskTexture;
    private bool _textureTried;

    public GameObject Create(Transform root, string playerName, Color tint)
    {
        // 化身根（朝对应玩家 pose.Yaw；head/mask 挂这里）
        var body = new GameObject("Body");
        body.transform.SetParent(root, false);

        // 头球（防毒面罩头模的载体）
        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        var col = head.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.Destroy(col);
        head.transform.SetParent(body.transform, false);
        head.transform.localScale = Vector3.one * 0.28f;
        var mat = MakeUnlit(tint, null);
        if (mat != null)
        {
            var mr = head.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = mat;
        }

        // 防毒面罩头模（尽力而为：找不到就只显示头球）
        var maskMesh = FindGasmaskMesh();
        if (maskMesh != null)
        {
            var mask = new GameObject("Gasmask");
            mask.transform.SetParent(body.transform, false);
            mask.transform.localPosition = new Vector3(0f, -0.03f, 0.055f);
            // ⚠️ 化身朝对应玩家 pose.Yaw（玩家面朝方向，body +Z 朝 pose.Yaw）。
            // gasmask 模型默认脸朝 +Z——**不翻转**，让面罩正面朝 pose.Yaw（对应玩家朝向）。
            // 原实现 billboard 朝观察者才需 180 翻转；现在朝 pose.Yaw 若仍 180 → 脸朝 -Z 背对玩家朝向 → 后脑勺朝人。
            mask.transform.localRotation = Quaternion.identity;
            mask.transform.localScale = Vector3.one * 0.97f;
            var mf = mask.AddComponent<MeshFilter>();
            mf.mesh = maskMesh;
            var mmr = mask.AddComponent<MeshRenderer>();
            var mmat = MakeUnlit(tint, FindGasmaskTexture());
            if (mmat != null) mmr.sharedMaterial = mmat;
        }

        // 名字标签（3D TMP，独立 billboard 正对观察者——不随化身朝向转）
        AddNameTag(body.transform, playerName);

        return body;
    }

    public void Update(GameObject visual, float dt, ref AvatarPose pose)
    {
        if (visual == null) return;
        // 化身朝向 = 对应玩家朝向（pose.Yaw 由 PlayerSync 同步；不用本机相机）
        visual.transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f);
        // 名字标签单独 billboard：正对观察者相机（找 Name 子物体，不随化身朝向转）
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
        // 根由 PlayerSync 统一销毁，无需额外清理
    }

    // ---------------- 内部 ----------------

    private static Material MakeUnlit(Color tint, Texture map)
    {
        try
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            var mat = new Material(shader);
            if (map != null)
            {
                mat.mainTexture = map;
                mat.SetTexture("_BaseMap", map);
            }
            mat.color = tint;
            mat.SetColor("_BaseColor", tint);
            return mat;
        }
        catch { return null; }
    }

    private static void AddNameTag(Transform parent, string name)
    {
        try
        {
            var go = new GameObject("Name");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.34f, 0f);

            var tmp = go.AddComponent<TextMeshPro>();
            if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = name;
            tmp.fontSize = 0.4f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.enableWordWrapping = false;
        }
        catch { /* 标签失败不影响头模 */ }
    }

    private Mesh FindGasmaskMesh()
    {
        if (_maskTried) return _maskMesh;
        _maskTried = true;
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Mesh>();
            if (all != null)
                foreach (var m in all)
                    if (m != null && m.name == "gasmask") { _maskMesh = m; break; }
        }
        catch { }
        if (_maskMesh == null)
            CoopRuntime.LogSource?.LogWarning("DefaultPlayerVisual: 'gasmask' head mesh not found, showing head sphere only");
        return _maskMesh;
    }

    private Texture2D FindGasmaskTexture()
    {
        if (_textureTried) return _maskTexture;
        _textureTried = true;
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Texture2D>();
            if (all != null)
                foreach (var t in all)
                    if (t != null && t.name == "gas_mask_BaseColor") { _maskTexture = t; break; }
        }
        catch { }
        return _maskTexture;
    }
}
