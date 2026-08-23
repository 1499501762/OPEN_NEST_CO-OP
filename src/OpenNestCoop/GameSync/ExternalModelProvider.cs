#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 外部 3D 士兵模型提供者：从磁盘加载写实士兵模型（正常联机模式使用）。
/// - 优先加载 glTF 二进制 .glb（SharpGLTF 解析，支持 PBR 材质 + 内嵌贴图），
///   找不到时回退到 OBJ（自带轻量解析器 + MTL 贴图）。两者均为成熟格式，可用
///   Blender / Sketchfab 等现有工具导出，不必手写模型本身。
/// - 模型查找顺序：环境变量 ONC_MODEL → Mods/Models/soldier.glb|obj → 游戏目录/Models/。
/// - 没有骨骼层级（.glb 若含骨骼动画，本提供者暂不播放，只取网格/材质），故用整体动画：
///   跟随 root 位移/朝向 + 按移动速度整体前倾/上下起伏/横移侧倾，
///   模拟走路/奔跑/蹲下/跳跃的观感（真实骨骼动画需模型自带骨骼并做 SkinnedMesh 绑定）。
/// - 本地测试模式（LocalMode）不启用本提供者，改用骨架便于调动画。
/// </summary>
public sealed class ExternalModelProvider : IPlayerVisualProvider
{
    public static readonly ExternalModelProvider Instance = new();

    /// <summary>是否已找到并成功加载外部模型。</summary>
    public bool HasModel { get; private set; }

    private Mesh _mesh;
    private Material[] _materials; // 与 Mesh.subMeshCount 对应
    private GlbModelRuntime _skinnedRuntime; // 有骨骼时用（SkinnedMeshRenderer + 动画）
    private bool _tried;

    /// <summary>主动尝试加载模型（在 Create 之前调用以判定是否可用）。成功返回 true。</summary>
    public bool TryLoad()
    {
        // 先尝试加载（GetMesh 内部会设置 HasModel：静态 mesh 或 skinned runtime 成功都置 true）
        GetMesh();
        return HasModel;
    }

    /// <summary>读取配置文件 Models/oncmodel.txt 的内容（"1"/"0"，用于强制显示/隐藏外部模型）。
    /// 找不到返回 null。</summary>
    public string ConfigValue
    {
        get
        {
            try
            {
                foreach (var root in FindGameRootCandidates())
                {
                    if (root == null) continue;
                    var p = Path.Combine(root, "Models", "oncmodel.txt");
                    if (File.Exists(p))
                    {
                        var txt = File.ReadAllText(p);
                        return txt == null ? null : txt.Trim();
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // ---- 动画平滑 ----
    private float _animSpeed;
    private float _bobPhase;
    private float _updDiagAcc;

    public GameObject Create(Transform root, string playerName, Color tint)
    {
        var go = new GameObject("SoldierModel");
        go.transform.SetParent(root, false);
        go.transform.localRotation = Quaternion.identity;

        // 有骨骼：用 SkinnedMeshRenderer（内置动画 Idle/Walk/Run），无需整体动画
        if (_skinnedRuntime != null)
        {
            try
            {
                var visual = _skinnedRuntime.Visual;
                if (visual != null)
                {
                    _skinnedRuntime.AttachTo(go.transform);
                    AddNameTag(go.transform, playerName, 1.95f);
                    CoopRuntime.LogSource?.Info($"ExternalModelProvider.Create: SKINNED ok, visual={visual.name}");
                    return go;
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.LogSource?.LogWarning($"ExternalModelProvider.Create SKINNED failed: {ex}");
            }
        }

        var mesh = GetMesh();
        if (mesh == null)
        {
            CoopRuntime.LogSource?.LogWarning("ExternalModelProvider.Create: mesh null (fallback to skeleton)");
            UnityEngine.Object.Destroy(go);
            return null;
        }

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        if (_materials != null && _materials.Length > 0) mr.sharedMaterials = _materials;
        else mr.sharedMaterial = MakeUnlit(tint, null);

        AddNameTag(go.transform, playerName, 1.95f);
        return go;
    }

    private static void AddNameTag(Transform parent, string playerName, float y)
    {
        // 名字标签（复用骨架的 3D TMP 逻辑，尽力而为）
        try
        {
            var tag = new GameObject("Name");
            tag.transform.SetParent(parent, false);
            tag.transform.localPosition = new Vector3(0f, y, 0f);
            var tmp = tag.AddComponent<TMPro.TextMeshPro>();
            tmp.text = playerName;
            tmp.fontSize = 3f;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.color = Color.white;
        }
        catch { }
    }

    public void Update(GameObject visual, float dt, ref AvatarPose pose)
    {
        if (visual == null) return;
        // 有骨骼：SkinnedMeshRenderer 动画由 GlbModelRuntime 驱动
        if (_skinnedRuntime != null && _skinnedRuntime.Visual != null)
        {
            _skinnedRuntime.Update(dt, pose);
            // 朝向：模型用 glTF 原始坐标（不翻转），Soldier.glb 面向 +Z（与骨架/人形约定一致：
            // +Z 朝 pose.Yaw）。之前的 +180 是旧 flipZ 镜像补偿，已移除。
            visual.transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f);
            // 贴地/跳跃：先贴地（射线校正脚底），再在贴地基础上固定上浮模拟腾空。
            // ⚠️ 修复（2026-08-15）：之前 Airborne 分支直接 lp.y += 0.45f 累加不重置，
            // 每帧 +0.45m → 模型无限飞升到几百米（日志 visual pos y 持续 48→103→175→294）。
            // 正确：始终先 GroundModel 把 y 归到地面，Airborne 再加固定偏移（不累加）。
            GroundModel(visual);
            if (pose.Airborne)
            {
                var lp = visual.transform.localPosition;
                lp.y += 0.45f;
                visual.transform.localPosition = lp;
            }
            BillBoardName(visual);
            // 临时诊断：每 2 秒打印一次模型世界位置/旋转
            _updDiagAcc += dt;
            if (_updDiagAcc > 2f)
            {
                _updDiagAcc = 0f;
                try
                {
                    var t = visual.transform;
                    var pr = t.parent != null ? t.parent.name : "null";
                    var py = _skinnedRuntime.Visual.transform.position.y.ToString("F2");
                    CoopRuntime.LogSource?.LogWarning("EXT: visual pos=" + t.position.ToString("F2") + " rot=" + t.eulerAngles.ToString("F1") + " parent=" + pr + " pivotY=" + py);
                }
                catch { }
            }
            return;
        }
        // 位置/朝向已在 root 上；这里做整体动画
        _animSpeed = Mathf.Lerp(_animSpeed, Mathf.Max(0f, pose.Speed), 0.2f);
        float speed = _animSpeed;
        bool moving = speed > 0.05f;

        if (pose.Airborne)
        {
            // 跳跃：整体上抬一点 + 微后仰
            var lp = visual.transform.localPosition;
            lp.y = 0.1f;
            visual.transform.localPosition = lp;
            visual.transform.localRotation = Quaternion.Euler(pose.Pitch - 6f, 0f, 0f);
        }
        else if (pose.Crouched)
        {
            // 蹲下：整体压低
            var lp = visual.transform.localPosition;
            lp.y = -0.55f;
            visual.transform.localPosition = lp;
            visual.transform.localRotation = Quaternion.Euler(pose.Pitch - 20f, 0f, 0f);
        }
        else if (moving)
        {
            // 走路/奔跑：整体前倾 + 上下起伏（模拟步伐）
            _bobPhase += (speed * 2.2f) * dt;
            float bob = Mathf.Abs(Mathf.Sin(_bobPhase)) * 0.05f * (pose.Sprinting ? 1.4f : 1f);
            var lp = visual.transform.localPosition;
            lp.y = bob;
            visual.transform.localPosition = lp;
            float lean = (pose.Sprinting ? 16f : 8f);
            float strafeLean = -Mathf.Clamp(pose.MoveStrafe, -1f, 1f) * 6f;
            visual.transform.localRotation = Quaternion.Euler(pose.Pitch - lean, 0f, strafeLean);
        }
        else
        {
            // 待机：轻微呼吸
            var lp = visual.transform.localPosition;
            lp.y = 0f;
            visual.transform.localPosition = lp;
            visual.transform.localRotation = Quaternion.Euler(pose.Pitch, 0f, 0f);
        }

        // 名字标签 billboard
        BillBoardName(visual);
    }

    private static void BillBoardName(GameObject visual)
    {
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

    /// <summary>脚贴地：取脚骨（Foot/Toe）最低世界 Y，从根向下射线找地面，
    /// 校正 visual.localPosition.y 让脚骨刚好贴地（与骨架 GroundBody 思路一致）。</summary>
    private void GroundModel(GameObject visual)
    {
        try
        {
            float footY = _skinnedRuntime.GetFootWorldY();
            if (float.IsNaN(footY)) return;
            var root = visual.transform.parent;
            if (root == null) return;
            Vector3 origin = root.position + Vector3.up * 0.2f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 6f)) return;
            float groundY = hit.point.y;
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

    public void Destroy(GameObject visual) { /* 根由 PlayerSync 统一销毁 */ }

    // ---------------- OBJ 加载 ----------------

    private Mesh GetMesh()
    {
        if (_tried) return _mesh;
        _tried = true;
        try
        {
            string path = FindModelPath();
            if (path == null) return null;
            string ext = Path.GetExtension(path);
            if (ext != null && (ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
                                ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                // 优先尝试骨骼运行时（SkinnedMeshRenderer + 内置动画）
                var fit = ModelFitConfig.Load(path);
                _skinnedRuntime = GlbModelRuntime.Load(path, fit);
                if (_skinnedRuntime != null && _skinnedRuntime.Visual != null)
                {
                    HasModel = true;
                    CoopRuntime.LogSource?.Info($"ExternalModelProvider: loaded SKINNED model '{path}' (bones, {_skinnedRuntime.Visual.name})");
                    return _mesh; // _mesh 可能为 null，但 HasModel=true 表示可用
                }
                _mesh = LoadGlb(path);
            }
            else
            {
                _mesh = LoadObj(path);
            }
            HasModel = _mesh != null;
            if (HasModel)
                CoopRuntime.LogSource?.Info($"ExternalModelProvider: loaded model '{path}' ({_mesh.vertexCount} verts, {(_materials?.Length ?? 0)} mats)");
            else
                CoopRuntime.LogSource?.LogWarning($"ExternalModelProvider: failed to parse model '{path}'");
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"ExternalModelProvider: {ex.Message}");
        }
        return _mesh;
    }

    /// <summary>按优先级查找模型文件路径（从若干根目录候选里找 soldier.glb / soldier.obj 等）。</summary>
    private static string FindModelPath()
    {
        // 候选根目录：游戏根（向上探测）、Models 目录本身
        var roots = new List<string>();
        try
        {
            var env = Environment.GetEnvironmentVariable("ONC_MODEL");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        }
        catch { }

        foreach (var dir in FindGameRootCandidates())
        {
            if (dir == null) continue;
            roots.Add(dir);
            roots.Add(Path.Combine(dir, "Models"));
        }

        foreach (var root in roots)
        {
            // 用户指定：优先 german_unity.glb（Unity 空间导出的 German 模型，骨骼空间与 CSV 烘焙一致），
            // 再 fallback 到 GermanWW2Soldier（配合 Mixamo 动画 CSV 烘焙驱动）。
            foreach (var name in new[] { "german_unity.glb", "GermanUnity.glb", "german_ww2_soldier.glb", "GermanWW2Soldier.glb", "soldier.glb", "Soldier.glb",
                                         "player.glb", "Player.glb", "soldier.obj", "Soldier.obj", "player.obj", "Player.obj" })
            {
                try
                {
                    var p = Path.Combine(root, name);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// 用 SharpGLTF 加载 glTF/glb 二进制模型：把所有 mesh primitive 合并为一个 Unity Mesh
    /// （每个 primitive 一个 submesh），材质从 glTF 材质（PBR baseColor + 内嵌贴图）生成。
    /// glTF 为 Y-up 右手系，Unity 为 Y-up 左手系：翻转 Z 并反转三角形绕序。
    /// 加载后按 ModelFitConfig（Models\*.cfg）做旋转/缩放/居中适配，把模型摆正并缩放到玩家比例。
    /// </summary>
    private Mesh LoadGlb(string path)
    {
        // 宽松校验：部分免费模型（如 three.js Soldier.glb）含非严格字段（如动画采样 _byteStride），
        // 严格模式会直接拒绝加载。我们只需网格+材质，Skip 校验可加载这些模型。
        var settings = new SharpGLTF.Schema2.ReadSettings
        {
            Validation = SharpGLTF.Validation.ValidationMode.Skip
        };
        var model = SharpGLTF.Schema2.ModelRoot.Load(path, settings);
        var fit = ModelFitConfig.Load(path);

        var allVerts = new List<Vector3>();
        var allUvs = new List<Vector2>();
        var allNormals = new List<Vector3>();
        var allSubTris = new List<int[]>();
        var mats = new List<Material>();

        foreach (var gltfMesh in model.LogicalMeshes)
        {
            if (gltfMesh == null) continue;
            foreach (var prim in gltfMesh.Primitives)
            {
                if (prim == null) continue;
                int baseVertex = allVerts.Count;

                var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                int vcount = positions == null ? 0 : positions.Count;
                for (int i = 0; i < vcount; i++)
                {
                    var p = positions[i];
                    allVerts.Add(new Vector3(p.X, p.Y, -p.Z));
                    if (texcoords != null && i < texcoords.Count)
                    {
                        var uv = texcoords[i];
                        allUvs.Add(new Vector2(uv.X, 1f - uv.Y)); // glTF UV 原点在左上，Unity 在左下
                    }
                    else allUvs.Add(Vector2.zero);
                    if (normals != null && i < normals.Count)
                    {
                        var n = normals[i];
                        allNormals.Add(new Vector3(n.X, n.Y, -n.Z));
                    }
                    else allNormals.Add(Vector3.zero);
                }

                var triList = new List<int>();
                foreach (var tri in prim.GetTriangleIndices())
                {
                    triList.Add(baseVertex + tri.Item1);
                    triList.Add(baseVertex + tri.Item3); // 反转绕序适配左手系
                    triList.Add(baseVertex + tri.Item2);
                }
                if (triList.Count == 0) continue;
                allSubTris.Add(triList.ToArray());

                Material mat = null;
                try { mat = BuildGlbMaterial(prim.Material); } catch { }
                if (mat == null) mat = MakeUnlit(new Color(0.32f, 0.42f, 0.28f), null);
                mats.Add(mat);
            }
        }

        if (allVerts.Count == 0 || allSubTris.Count == 0) return null;

        // ---- 模型适配：旋转摆正 + 缩放到目标身高 + 居中/脚底对齐（配置驱动，接口化） ----
        ApplyModelFit(allVerts, allNormals, fit);

        var mesh = new Mesh();
        mesh.vertices = allVerts.ToArray();
        mesh.uv = allUvs.ToArray();
        mesh.normals = allNormals.ToArray();
        mesh.subMeshCount = allSubTris.Count;
        for (int i = 0; i < allSubTris.Count; i++) mesh.SetTriangles(allSubTris[i], i);
        mesh.RecalculateBounds();
        bool anyN = false;
        foreach (var n in allNormals) if (n != Vector3.zero) { anyN = true; break; }
        if (!anyN) mesh.RecalculateNormals();

        _materials = mats.ToArray();
        return mesh;
    }

    /// <summary>
    /// 应用模型适配：旋转（欧拉角）→ 统一缩放（自动按 targetHeight）→ 居中 + 脚底对齐 y=0。
    /// 直接改写顶点/法线数组（一次性，mesh 最终形态就是摆正后的）。
    /// </summary>
    private static void ApplyModelFit(List<Vector3> verts, List<Vector3> normals, ModelFitConfig fit)
    {
        if (verts == null || verts.Count == 0) return;
        var rot = Quaternion.Euler(fit.RotateX, fit.RotateY, fit.RotateZ);

        // 1) 旋转到站立（覆盖原顶点）
        for (int i = 0; i < verts.Count; i++) verts[i] = rot * verts[i];
        if (normals != null && normals.Count == verts.Count)
            for (int i = 0; i < normals.Count; i++) normals[i] = rot * normals[i];

        // 2) 计算包围盒，定缩放
        Vector3 min = verts[0], max = verts[0];
        for (int i = 1; i < verts.Count; i++)
        {
            var v = verts[i];
            if (v.x < min.x) min.x = v.x; if (v.x > max.x) max.x = v.x;
            if (v.y < min.y) min.y = v.y; if (v.y > max.y) max.y = v.y;
            if (v.z < min.z) min.z = v.z; if (v.z > max.z) max.z = v.z;
        }
        float height = Mathf.Max(0.001f, max.y - min.y);
        float scale = fit.Scale > 0f ? fit.Scale : (fit.TargetHeight / height);

        // 3) 缩放（绕原点）
        for (int i = 0; i < verts.Count; i++) verts[i] *= scale;

        // 4) 居中(X/Z) + 脚底对齐 y=0（缩放后重算包围盒）
        min = verts[0]; max = verts[0];
        for (int i = 1; i < verts.Count; i++)
        {
            var v = verts[i];
            if (v.x < min.x) min.x = v.x; if (v.x > max.x) max.x = v.x;
            if (v.y < min.y) min.y = v.y; if (v.y > max.y) max.y = v.y;
            if (v.z < min.z) min.z = v.z; if (v.z > max.z) max.z = v.z;
        }
        float cx = fit.OffsetX != 0f ? 0f : -(min.x + max.x) * 0.5f;
        float cz = fit.OffsetZ != 0f ? 0f : -(min.z + max.z) * 0.5f;
        float cy = fit.OffsetY != 0f ? fit.OffsetY : -min.y; // 脚底对齐 y=0
        var offset = new Vector3(fit.OffsetX != 0f ? fit.OffsetX : cx,
                                 cy,
                                 fit.OffsetZ != 0f ? fit.OffsetZ : cz);
        for (int i = 0; i < verts.Count; i++) verts[i] += offset;
    }

    /// <summary>从 glTF 材质生成 Unity 材质：baseColor 因子 + baseColor 贴图（内嵌 PNG/JPG 字节）。</summary>
    private static Material BuildGlbMaterial(SharpGLTF.Schema2.Material m)
    {
        if (m == null) return null;
        var bc = m.FindChannel("BaseColor"); // MaterialChannel?
        if (!bc.HasValue) return null;
        var ch = bc.Value;
        var factor = ch.Color; // System.Numerics.Vector4
        var tint = new Color(factor.X, factor.Y, factor.Z, factor.W);

        Texture2D tex = null;
        try
        {
            var texRef = ch.Texture;
            if (texRef != null)
            {
                var img = texRef.PrimaryImage;
                if (img != null && img.Content.IsEmpty == false)
                {
                    var bytes = img.Content.Content.ToArray();
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, bytes)) { UnityEngine.Object.Destroy(tex); tex = null; }
                }
            }
        }
        catch { if (tex != null) { UnityEngine.Object.Destroy(tex); tex = null; } }

        var mat = MakeUnlit(tint, tex);
        return mat ?? MakeUnlit(tint, null);
    }

    /// <summary>找游戏根目录候选：从 AppContext.BaseDirectory 逐级向上，命中含 Iron Nest 特征的目录即停。
    /// internal 供 AnimatorAvatarVisualProvider（AssetBundle 方案）复用。</summary>
    internal static IEnumerable<string> FindGameRootCandidates()
    {
        var results = new List<string>();
        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                try
                {
                    var lower = dir.ToLowerInvariant();
                    // 特征：目录下直接有 exe 或 BepInEx/MelonLoader 目录
                    if (Directory.Exists(Path.Combine(dir, "BepInEx")) ||
                        Directory.Exists(Path.Combine(dir, "MelonLoader")) ||
                        Directory.GetFiles(dir, "*.exe").Length > 0)
                    {
                        results.Add(dir);
                        break;
                    }
                }
                catch { }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        if (results.Count == 0)
        {
            try { results.Add(AppContext.BaseDirectory); } catch { }
        }
        return results;
    }

    /// <summary>
    /// 解析 OBJ 文件为 Unity Mesh。支持 v/vt/vn/f（三角化）；材质从同目录 MTL 的 map_Kd 加载贴图。
    /// </summary>
    private Mesh LoadObj(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var triV = new List<int>();
        var triUv = new List<int>();
        var triN = new List<int>();
        string mtlLib = null, curMtl = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            if (rawLine == null) continue;
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("mtllib ")) { mtlLib = line.Substring(7).Trim(); }
            else if (line.StartsWith("usemtl ")) { curMtl = line.Substring(7).Trim(); }
            else if (line.StartsWith("v ")) { ParseVec3(line, 1, verts); }
            else if (line.StartsWith("vt ")) { ParseVec2(line, 1, uvs); }
            else if (line.StartsWith("vn ")) { ParseVec3(line, 1, normals); }
            else if (line.StartsWith("f "))
            {
                var parts = line.Split(' ');
                int[] faceV = new int[parts.Length - 1];
                int[] faceUv = new int[parts.Length - 1];
                int[] faceN = new int[parts.Length - 1];
                bool ok = true;
                for (int i = 1; i < parts.Length; i++)
                {
                    if (!ParseFaceIndex(parts[i], out int vi, out int ti, out int ni)) { ok = false; break; }
                    faceV[i - 1] = vi; faceUv[i - 1] = ti; faceN[i - 1] = ni;
                }
                if (!ok) continue;
                for (int i = 1; i + 1 < faceV.Length; i++)
                {
                    triV.Add(faceV[0]); triUv.Add(faceUv[0]); triN.Add(faceN[0]);
                    triV.Add(faceV[i]); triUv.Add(faceUv[i]); triN.Add(faceN[i]);
                    triV.Add(faceV[i + 1]); triUv.Add(faceUv[i + 1]); triN.Add(faceN[i + 1]);
                }
            }
        }

        if (verts.Count == 0 || triV.Count == 0) return null;

        var mesh = new Mesh();
        var vArr = new Vector3[triV.Count];
        var uvArr = new Vector2[triV.Count];
        var nArr = new Vector3[triV.Count];
        bool anyN = false;
        for (int i = 0; i < triV.Count; i++)
        {
            int vi = ResolveIndex(triV[i], verts.Count);
            vArr[i] = verts[vi];
            vArr[i] = new Vector3(vArr[i].x, vArr[i].y, -vArr[i].z); // OBJ 右手系 → Unity 左手系（翻转 Z）
            if (triUv[i] != 0) uvArr[i] = uvs[ResolveIndex(triUv[i], uvs.Count)];
            if (triN[i] != 0) { nArr[i] = normals[ResolveIndex(triN[i], normals.Count)]; anyN = true; }
        }
        mesh.vertices = vArr;
        mesh.uv = uvArr;
        if (anyN) mesh.normals = nArr;
        mesh.triangles = BuildTriangles(triV.Count);
        mesh.RecalculateBounds();
        if (!anyN) mesh.RecalculateNormals();

        _materials = new[] { LoadMaterial(dir, mtlLib, curMtl) };
        return mesh;
    }

    /// <summary>从 MTL 引用加载材质（找 map_Kd 贴图从磁盘解码；找不到则用深色默认材质）。</summary>
    private static Material LoadMaterial(string dir, string mtlLib, string mtlName)
    {
        // 找 MTL 文件里的 map_Kd
        string mapPath = null;
        try
        {
            if (!string.IsNullOrEmpty(mtlLib))
            {
                var mtlPath = Path.Combine(dir, mtlLib);
                if (File.Exists(mtlPath))
                {
                    bool inMtl = string.IsNullOrEmpty(mtlName);
                    foreach (var rawLine in File.ReadLines(mtlPath))
                    {
                        var line = rawLine == null ? "" : rawLine.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        if (line.StartsWith("newmtl "))
                        {
                            inMtl = string.Equals(line.Substring(7).Trim(), mtlName, StringComparison.OrdinalIgnoreCase);
                            continue;
                        }
                        if (inMtl && line.StartsWith("map_Kd "))
                        {
                            mapPath = line.Substring(7).Trim();
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        Texture2D tex = null;
        if (!string.IsNullOrEmpty(mapPath))
        {
            try
            {
                var texPath = Path.Combine(dir, mapPath);
                if (File.Exists(texPath))
                    tex = LoadTextureFromFile(texPath);
            }
            catch { }
        }

        if (tex != null)
        {
            var m = MakeUnlit(Color.white, tex);
            if (m != null) return m;
        }
        // 默认深绿军装色
        return MakeUnlit(new Color(0.32f, 0.42f, 0.28f), null);
    }

    /// <summary>从磁盘文件解码贴图（PNG/JPG），IL2CPP 兼容。</summary>
    private static Texture2D LoadTextureFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (ImageConversion.LoadImage(tex, bytes)) return tex;
        UnityEngine.Object.Destroy(tex);
        return null;
    }

    private static void ParseVec3(string line, int startIdx, List<Vector3> list)
    {
        var parts = line.Split(' ');
        if (parts.Length < startIdx + 3) return;
        try
        {
            float x = float.Parse(parts[startIdx], System.Globalization.CultureInfo.InvariantCulture);
            float y = float.Parse(parts[startIdx + 1], System.Globalization.CultureInfo.InvariantCulture);
            float z = float.Parse(parts[startIdx + 2], System.Globalization.CultureInfo.InvariantCulture);
            list.Add(new Vector3(x, y, z));
        }
        catch { }
    }

    private static void ParseVec2(string line, int startIdx, List<Vector2> list)
    {
        var parts = line.Split(' ');
        if (parts.Length < startIdx + 2) return;
        try
        {
            float x = float.Parse(parts[startIdx], System.Globalization.CultureInfo.InvariantCulture);
            float y = float.Parse(parts[startIdx + 1], System.Globalization.CultureInfo.InvariantCulture);
            list.Add(new Vector2(x, y));
        }
        catch { }
    }

    /// <summary>解析面索引 "v/vt/vn" 或 "v//vn" 等，返回 1-based 索引（0 表示缺失）。</summary>
    private static bool ParseFaceIndex(string token, out int vi, out int ti, out int ni)
    {
        vi = 0; ti = 0; ni = 0;
        if (string.IsNullOrEmpty(token)) return false;
        var p = token.Split('/');
        if (p.Length >= 1 && int.TryParse(p[0], out int v)) vi = v; else return false;
        if (p.Length >= 2 && p[1].Length > 0 && int.TryParse(p[1], out int t)) ti = t;
        if (p.Length >= 3 && p[2].Length > 0 && int.TryParse(p[2], out int n)) ni = n;
        return true;
    }

    private static int ResolveIndex(int idx, int count)
    {
        if (idx > 0) return (idx - 1) % count;
        if (idx < 0) return (count + idx) % count;
        return 0;
    }

    private static int[] BuildTriangles(int count)
    {
        var t = new int[count];
        for (int i = 0; i < count; i++) t[i] = i;
        return t;
    }

    private static Material MakeUnlit(Color tint, Texture2D map)
    {
        try
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            var mat = new Material(shader);
            if (map != null) { mat.mainTexture = map; mat.SetTexture("_BaseMap", map); }
            mat.color = tint;
            mat.SetColor("_BaseColor", tint);
            return mat;
        }
        catch { return null; }
    }
}
