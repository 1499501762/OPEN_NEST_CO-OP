using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using OpenNestCoop.Core;

namespace OpenNestCoop.GameSync;

/// <summary>
/// glTF/glb 模型的骨骼运行时：把模型节点树解析为 Unity Transform 层级，
/// 构建 SkinnedMeshRenderer（bones/bindposes/权重），并驱动内置骨骼动画
/// （Idle/Walk/Run，按移动速度切换）。
///
/// 坐标系策略（关键，保证数学正确）：
///   网格顶点、骨骼 localTransform、bindposes 全部保持在【同一模型内部空间】——
///   仅做 glTF(Y-up 右手) → Unity(Y-up 左手) 的 Z 翻转（顶点绕序 + 四元数 Z 分量取反）。
///   ModelFit 的旋转施加在根对象（绕 Y-up 站立朝向）；缩放/居中【烘焙进网格顶点 + 骨骼位置
///   + bindpose】——不缩放根对象（SkinnedMeshRenderer 依赖根/骨骼/bindpose 的比例一致，
///   靠 localScale 缩放会因 bindpose 固定而错乱）。动画采样骨骼局部旋转也只做 Z 翻转。
/// </summary>
public sealed class GlbModelRuntime
{
    private sealed class BoneNode
    {
        public Transform Transform;          // Unity 骨骼 Transform
        public SharpGLTF.Schema2.Node Gltf;  // 对应 glTF 节点（用于采样动画）
        public string Name;
    }

    private readonly List<BoneNode> _bones = new();
    private Transform _rootBone;
    private SkinnedMeshRenderer _skinned;
    private GameObject _visual;   // SkinnedMeshRenderer 所在（保持 identity，蒙皮安全）
    private GameObject _pivot;    // 容器：朝向/贴地/位置（ExternalModelProvider 控制这个）

    private ModelFitConfig _fit;
    private SharpGLTF.Schema2.ModelRoot _model;

    // 自采样动画（SharpGLTF.Core，IL2CPP 安全；替代 SharpGLTF.Runtime——Runtime 在 IL2CPP 下
    // NodeInstance.LocalMatrix 可能返回错误 → 骨骼位置被覆盖塌缩拉长）
    private SharpGLTF.Schema2.Animation _animIdle, _animWalk, _animRun;
    private readonly Dictionary<SharpGLTF.Schema2.Animation, Dictionary<string, List<SharpGLTF.Schema2.AnimationChannel>>> _animChannels = new();
    private float _animTime;
    private float _runtimeScale = 1f;

    // ---- CSV Mixamo 动画（Unity 烘焙到 German 骨骼，替代会扭曲的程序化驱动）----
    private Dictionary<string, Dictionary<string, List<CsvFrame>>> _csvAnims;
    private float _csvTime;
    private bool _csvLoaded;
    private struct CsvFrame { public float T; public Quaternion Q; }

    /// <summary>加载并构建骨骼运行时。失败返回 null。</summary>
    public static GlbModelRuntime Load(string path, ModelFitConfig fit)
    {
        try
        {
            var rt = new GlbModelRuntime();
            rt._fit = fit;
            rt._model = SharpGLTF.Schema2.ModelRoot.Load(path, new SharpGLTF.Schema2.ReadSettings
            {
                Validation = SharpGLTF.Validation.ValidationMode.Skip
            });
            rt.Build();
            rt.LoadCsvAnims(path);   // Unity 烘焙的 Mixamo 动画（German 骨骼），替代程序化驱动
            return rt;
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime.Load: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private void Build()
    {
        var model = _model;
        var scene = model.LogicalScenes.FirstOrDefault();
        if (scene == null) { CoopRuntime.LogSource?.LogWarning("GlbModelRuntime.Build: no scene"); return; }
        var skin = model.LogicalSkins.FirstOrDefault();
        if (skin == null) { CoopRuntime.LogSource?.LogWarning("GlbModelRuntime.Build: no skin"); return; }

        // 1) 根对象（承载 SkinnedMeshRenderer）+ 容器 _pivot。
        //    ⚠️ SkinnedMeshRenderer 的 _visual 必须保持 identity（位置/旋转/缩放都不动）：
        //    Unity 蒙皮用 bone.localToWorldMatrix 相对 mesh 局部计算，_visual 若带旋转/缩放会
        //    与骨骼/顶点空间不一致 → 蒙皮错乱。朝向/贴地全部放到 _pivot（_visual 的父）。
        _pivot = new GameObject("SoldierPivot");
        _visual = new GameObject("SoldierSkinned");
        _visual.transform.SetParent(_pivot.transform, false);
        _visual.transform.localPosition = Vector3.zero;
        _visual.transform.localRotation = Quaternion.identity;
        _visual.transform.localScale = Vector3.one;
        var rootTr = _visual.transform;

        // 2) 【不再烘焙缩放】Soldier.glb 等标准模型：根节点(Character)自带 R_x(-90°)+scale0.01，
        //    已把 Z-up 厘米顶点转到 Y-up 米制世界（骨骼 Hips world≈y=1.06）。顶点/骨骼全部用
        //    glTF 原始 localTransform + 标准 IBM bindPose → 蒙皮 = Σ w*W_j*IBM_j*POSITION = 正确世界位置。
        //    之前"烘焙 scale + 逐节点 flipZ"造成【双重缩放】(Character 0.01 × 烘焙 0.0098≈0.0001)
        //    + 破坏 Character 旋转语义 → 骨骼塌缩到原点、模型拉长。已修复。
        float scale = 1f;

        // 3) 【关键】完整复刻场景节点树（含所有中间节点，如 Character 的 R_x(-90)+scale0.01——
        //    它把 Z-up 网格转到 Y-up 世界）。只建 skin.Joints 会跳过这些变换 → 蒙皮错乱。翻转只
        //    在 _visual 的 scale=(1,1,-1)（整体镜像到左手系），不做逐节点 flipZ（镜像与根旋转不可交换）。
        var transformByGltf = new Dictionary<SharpGLTF.Schema2.Node, Transform>();
        var jointSet = new HashSet<SharpGLTF.Schema2.Node>(skin.Joints);

        // 递归创建节点（保留中间节点的局部变换；关节登记进 _bones 用于 SkinnedMeshRenderer.bones）
        void CreateNodeTree(SharpGLTF.Schema2.Node node, Transform parent)
        {
            if (node == null) return;
            var go = new GameObject(node.Name ?? "node");
            go.transform.SetParent(parent, false);

            // 应用节点局部变换（glTF 原始值，不翻转、不缩放——模型自带世界变换在根节点）
            var lt = node.LocalTransform.GetDecomposed();
            go.transform.localPosition = new Vector3(
                lt.Translation.X,
                lt.Translation.Y,
                lt.Translation.Z);
            go.transform.localRotation = new Quaternion(
                lt.Rotation.X, lt.Rotation.Y, lt.Rotation.Z, lt.Rotation.W);
            var sc = lt.Scale;
            go.transform.localScale = new Vector3(sc.X, sc.Y, sc.Z);

            transformByGltf[node] = go.transform;
            if (jointSet.Contains(node))
                _bones.Add(new BoneNode { Transform = go.transform, Gltf = node, Name = node.Name });

            foreach (var c in node.VisualChildren)
                CreateNodeTree(c, go.transform);
        }
        foreach (var n in scene.VisualChildren)
            CreateNodeTree(n, rootTr);

        // 4) SkinnedMeshRenderer.bones 必须按 skin.Joints 顺序（JOINTS_0 索引引用它），
        //    但上面是按树遍历顺序登记的 → 重排为 skin.Joints 顺序。
        _bones.Clear();
        foreach (var j in skin.Joints)
            if (transformByGltf.TryGetValue(j, out var t))
                _bones.Add(new BoneNode { Transform = t, Gltf = j, Name = j.Name });

        _rootBone = _bones.FirstOrDefault(b => b.Name != null && b.Name.IndexOf("Hips", StringComparison.OrdinalIgnoreCase) >= 0)?.Transform
                    ?? _bones.FirstOrDefault()?.Transform;

        // 诊断：骨骼层级（确认父子关系 + localPosition 正确，区分"构建错"vs"动画覆盖错"）
        try
        {
            foreach (var nm in new[] { "mixamorig:Hips", "mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftUpLeg", "mixamorig:LeftLeg" })
            {
                var bn = _bones.FirstOrDefault(b => b.Name == nm);
                if (bn != null && bn.Transform != null)
                    CoopRuntime.LogSource?.Info($"GlbModelRuntime bone '{nm}' parent={(bn.Transform.parent != null ? bn.Transform.parent.name : "null")} localPos={bn.Transform.localPosition.ToString("F4")} scale={scale:F5}");
                else if (_bones.Any(b => (b.Name ?? "").Contains(nm.Replace("mixamorig:", ""))))
                    CoopRuntime.LogSource?.Info($"GlbModelRuntime bone '{nm}' matched-by-suffix");
                else
                    CoopRuntime.LogSource?.Info($"GlbModelRuntime bone '{nm}' NOT-FOUND");
            }
        }
        catch { }

        // 5) SkinnedMeshRenderer（顶点乘 scale、bindpose 乘 1/scale，同空间）
        BuildSkinnedMesh(skin, scale);
        if (_skinned == null || _skinned.sharedMesh == null)
            CoopRuntime.LogSource?.LogWarning("GlbModelRuntime.Build: SkinnedMeshRenderer null");
        else
        {
            CoopRuntime.LogSource?.Info($"GlbModelRuntime.Build: OK bones={_bones.Count} verts={_skinned.sharedMesh.vertexCount} mats={_skinned.sharedMaterials.Length}");
            ValidateSkinning();
        }

        // 6) 动画初始化（自采样 SharpGLTF.Core）：记录 Idle/Walk/Run 动画 + 缓存每骨骼的 channel
        _runtimeScale = scale;
        _animChannels.Clear();
        foreach (var a in _model.LogicalAnimations)
        {
            if (a.Name == "Idle") _animIdle = a;
            else if (a.Name == "Walk") _animWalk = a;
            else if (a.Name == "Run") _animRun = a;
        }
        foreach (var a in new[] { _animIdle, _animWalk, _animRun })
        {
            if (a == null) continue;
            var byBone = new Dictionary<string, List<SharpGLTF.Schema2.AnimationChannel>>();
            foreach (var ch in a.Channels)
            {
                if (ch.TargetNode?.Name == null) continue;
                if (!byBone.TryGetValue(ch.TargetNode.Name, out var list))
                    byBone[ch.TargetNode.Name] = list = new List<SharpGLTF.Schema2.AnimationChannel>();
                list.Add(ch);
            }
            _animChannels[a] = byBone;
        }
        CoopRuntime.LogSource?.Info($"GlbModelRuntime: anim Idle={(_animIdle != null)} Walk={(_animWalk != null)} Run={(_animRun != null)} scale={_runtimeScale:F5}");
    }

    /// <summary>⚠️ 蒙皮验证（决定性问题）：在【绑定姿势】下用 boneWeights + bindposes + 骨骼矩阵
    /// 重算顶点，检查蒙皮输出的【世界空间】位置是否构成合理站姿（脚≈y=0、头≈y=targetHeight、
    /// 骨骼 Hips≈y=1）。因为顶点是原始 glTF POSITION（Z-up 厘米）、骨骼世界是 Y-up 米制，
    /// 蒙皮输出 = meshWorld*POSITION（Y-up 米制站立），验证它确实立起来了。</summary>
    private void ValidateSkinning()
    {
        try
        {
            var mesh = _skinned.sharedMesh;
            var verts = mesh.vertices;
            var bws = mesh.boneWeights;
            var bps = mesh.bindposes;
            var bones = _skinned.bones;
            int n = Mathf.Min(verts.Length, 4000);
            float minY = float.MaxValue, maxY = float.MinValue;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var v = verts[i];
                var bw = bws[i];
                Vector3 p = Vector3.zero;
                p += bw.weight0 * bones[bw.boneIndex0].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex0].MultiplyPoint3x4(v));
                p += bw.weight1 * bones[bw.boneIndex1].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex1].MultiplyPoint3x4(v));
                p += bw.weight2 * bones[bw.boneIndex2].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex2].MultiplyPoint3x4(v));
                p += bw.weight3 * bones[bw.boneIndex3].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex3].MultiplyPoint3x4(v));
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
            }
            var hip = _bones.FirstOrDefault(b => b.Name != null && b.Name.IndexOf("Hips", StringComparison.OrdinalIgnoreCase) >= 0)?.Transform;
            float hipY = hip != null ? hip.position.y : 0f;
            float height = maxY - minY;
            CoopRuntime.LogSource?.Info(
                $"GlbModelRuntime: skin-validate Y=[{minY:F3},{maxY:F3}] h={height:F3} hipY={hipY:F3} X=[{minX:F2},{maxX:F2}] Z=[{minZ:F2},{maxZ:F2}] (立起: 脚y≈0 头y≈1.8 hipY≈1.0)");
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime skin validate: {ex.Message}"); }
    }

    /// <summary>挂载到目标 Transform 下并贴地。⚠️ 新方案（2026-08）：
    /// 顶点/骨骼用 glTF 原始坐标 + 标准 IBM bindPose，骨骼 WorldMatrix 已是 Y-up 米制站立
    /// （根节点 Character 的 R_x(-90)+scale0.01 已把 Z-up 网格转到 Y-up 世界）。
    /// 因此【不需要】fit.RotateX90 立起、不需要镜像 scale（Unity 渲染不做手性转换，
    /// 几何位置正确；法线绕序问题用材质 Cull Off 解决）。只需按脚部骨骼最低 Y 贴地。
    /// ⚠️ 朝向/位置作用在 _pivot（_visual 的父，保持 _visual identity 蒙皮安全）。</summary>
    public void AttachTo(Transform parent)
    {
        if (_pivot == null) return;
        var fit = _fit ?? new ModelFitConfig();
        var root = _pivot.transform;

        // 先算贴地（此时根未 SetParent，骨骼 world=自身空间坐标，Y-up 米制：脚≈y=0.02）
        float footY = GetFootBoneWorldY();
        if (footY == float.MaxValue) footY = 0f;

        root.SetParent(parent, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;

        float offsetY = fit.OffsetY != 0f ? fit.OffsetY : -footY;
        root.localPosition = new Vector3(
            fit.OffsetX != 0f ? fit.OffsetX : 0f,
            offsetY,
            fit.OffsetZ != 0f ? fit.OffsetZ : 0f);

        CoopRuntime.LogSource?.Info($"GlbModelRuntime.AttachTo: footY={footY:F4} offsetY={offsetY:F4} (模型已 Y-up 米制站立，直接贴地)");
    }

    /// <summary>在绑定姿势（根为 identity）下，取脚部骨骼（名字含 Foot/Toe）的世界 Y 最小值作为脚底高度。
    /// 必须在 SetParent 之前调用（此时 position 是模型自身空间坐标）。</summary>
    private float GetFootBoneWorldY()
    {
        float best = float.MaxValue;
        foreach (var b in _bones)
        {
            if (b.Transform == null) continue;
            var n = b.Name ?? "";
            if (n.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Toe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                float y = b.Transform.position.y;
                if (y < best) best = y;
            }
        }
        return best < float.MaxValue ? best : float.MaxValue;
    }

    private void BuildSkinnedMesh(SharpGLTF.Schema2.Skin skin, float scale)
    {
        var model = _model;
        var mesh = new Mesh();
        var allVerts = new List<Vector3>();
        var allUvs = new List<Vector2>();
        var allNormals = new List<Vector3>();
        var allBoneWeights = new List<BoneWeight>();
        var allSubTris = new List<int[]>();
        var mats = new List<Material>();

        float vertMinY = 1e9f, vertMaxY = -1e9f;
        foreach (var gltfMesh in model.LogicalMeshes)
        {
            // mesh 节点 world：把 mesh 局部顶点转到 scene 空间，与骨骼 world（Y-up 米）一致。
            // Soldier.glb：mesh 挂 Character（R_x(-90)+scale0.01，Z-up 厘米→Y-up 米）→ 必须乘。
            // GermanWW2Soldier.glb：mesh 节点带残留平移（Object_5 T=(0,44,5)）会带偏顶点，
            //   但末尾"贴地校正"会用蒙皮最低点抬回脚 y=0，两种模型都能归一。
            SharpGLTF.Schema2.Node meshNode = null;
            foreach (var n in model.LogicalNodes)
                if (n.Mesh == gltfMesh) { meshNode = n; break; }
            var meshWorld = meshNode != null ? GltfMatToUnity(meshNode.WorldMatrix) : Matrix4x4.identity;

            foreach (var prim in gltfMesh.Primitives)
            {
                if (prim == null) continue;
                var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions == null || positions.Count == 0) continue;
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

                int baseV = allVerts.Count;
                for (int i = 0; i < positions.Count; i++)
                {
                    var p = positions[i];
                    // 顶点：原始 POSITION → 乘 mesh 节点 world 转 scene 空间（与骨骼 world 一致）
                    var v = meshWorld.MultiplyPoint3x4(new Vector3(p.X, p.Y, p.Z));
                    allVerts.Add(v);
                    if (v.y < vertMinY) vertMinY = v.y;
                    if (v.y > vertMaxY) vertMaxY = v.y;
                    if (texcoords != null && i < texcoords.Count)
                    {
                        var uv = texcoords[i];
                        allUvs.Add(new Vector2(uv.X, 1f - uv.Y));
                    }
                    else allUvs.Add(Vector2.zero);
                    if (normals != null && i < normals.Count)
                    {
                        var n = normals[i];
                        allNormals.Add(meshWorld.MultiplyVector(new Vector3(n.X, n.Y, n.Z)));
                    }
                    else allNormals.Add(Vector3.zero);

                    if (joints != null && weights != null && i < joints.Count && i < weights.Count)
                    {
                        var j = joints[i];
                        var w = weights[i];
                        allBoneWeights.Add(new BoneWeight
                        {
                            boneIndex0 = (int)j.X, weight0 = w.X,
                            boneIndex1 = (int)j.Y, weight1 = w.Y,
                            boneIndex2 = (int)j.Z, weight2 = w.Z,
                            boneIndex3 = (int)j.W, weight3 = w.W,
                        });
                    }
                    else allBoneWeights.Add(new BoneWeight());
                }

                var triList = new List<int>();
                foreach (var tri in prim.GetTriangleIndices())
                {
                    triList.Add(baseV + tri.Item1);
                    triList.Add(baseV + tri.Item3);
                    triList.Add(baseV + tri.Item2);
                }
                if (triList.Count == 0) continue;
                allSubTris.Add(triList.ToArray());

                Material mat = null;
                try { mat = BuildGlbMaterial(prim.Material); } catch { }
                if (mat == null) mat = MakeUnlit(new Color(0.32f, 0.42f, 0.28f), null);
                mats.Add(mat);
            }
        }

        if (allVerts.Count == 0 || allSubTris.Count == 0)
        {
            CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime: 无网格顶点/三角形 (verts={allVerts.Count}, subs={allSubTris.Count})");
            return;
        }

        // 高度归一化：把模型缩放到 ~1.8m（自动适配 cm/米单位）。
        // ⚠️ GermanWW2Soldier 是 Y-up 厘米立正（Hips y=101.5，模型高 ~177cm）→ sf≈0.01。
        //    米制人体模型（高 0.5~4m）不缩。顶点与骨骼树根同步缩放，bindPose 用骨骼绑定
        //    worldToLocal（自动含缩放）→ 三者一致，蒙皮输出米制 1.8m 立正。
        float modelH = vertMaxY - vertMinY;
        if (modelH > 4f || modelH < 0.5f)
        {
            float sf = 1.8f / modelH;
            for (int i = 0; i < allVerts.Count; i++) allVerts[i] = allVerts[i] * sf;
            Transform rootNode = null;
            try
            {
                for (int i = 0; i < _visual.transform.childCount; i++)
                {
                    var c = _visual.transform.GetChild(i);
                    if (c != null) { rootNode = c; break; }
                }
                if (rootNode != null)
                    rootNode.localScale = new Vector3(rootNode.localScale.x * sf, rootNode.localScale.y * sf, rootNode.localScale.z * sf);
            }
            catch { }
            // 贴地校正：mesh 局部 POSITION 的脚可能在负 Y（如 GermanWW2Soldier 脚≈-75cm），
            // 而骨骼脚在 0 → 顶点与骨骼有固定偏移。把归一化后的最低点抬到 y=0（顶点+骨骼同步，
            // 保证蒙皮脚贴地）。依赖"模型最低点=脚底"（T-pose 立正成立）。
            float dy = -(vertMinY * sf);
            if (Mathf.Abs(dy) > 0.001f)
            {
                for (int i = 0; i < allVerts.Count; i++) { var v = allVerts[i]; v.y += dy; allVerts[i] = v; }
                if (rootNode != null)
                    rootNode.localPosition = new Vector3(rootNode.localPosition.x, rootNode.localPosition.y + dy, rootNode.localPosition.z);
            }
            CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime: 高度归一化 h={modelH:F2} → sf={sf:F5} 贴地 dy={dy:F3}");
        }

        mesh.vertices = allVerts.ToArray();
        mesh.uv = allUvs.ToArray();
        mesh.normals = allNormals.ToArray();
        mesh.subMeshCount = allSubTris.Count;
        for (int i = 0; i < allSubTris.Count; i++) mesh.SetTriangles(allSubTris[i], i);
        mesh.boneWeights = allBoneWeights.ToArray();

        // 【绑定】bindpose = glTF 标准 IBM（inverse bind matrices）。已验证（ModelInfo）：
        //   Σ w * WorldMatrix_j * IBM_j * POSITION ≈ meshWorld * POSITION（avg=0.0001, bad=0）
        //   → 模型绑定完全标准，直接用 IBM 做 bindPose，任意动画姿势下蒙皮数学正确。
        //   （之前用 worldToLocalMatrix 绑定"当前层级姿势"绕开了 IBM，但那依赖骨骼 Transform
        //     与顶点同空间；配合逐节点 flipZ + 烘焙 scale 反而双重缩放塌缩。现在全部用 glTF
        //     原始坐标 + 标准 IBM，不需要再绕。）
        mesh.bindposes = ReadGltfBindposes(skin);

        mesh.RecalculateBounds();

        var smr = _visual.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.sharedMaterials = mats.ToArray();
        smr.rootBone = _rootBone;
        smr.bones = _bones.Select(b => b.Transform).ToArray();
        smr.updateWhenOffscreen = true;

        _skinned = smr;

        CoopRuntime.LogSource?.Info($"GlbModelRuntime: mesh verts={allVerts.Count} subs={allSubTris.Count} bones={_bones.Count} mats={mats.Count}");
    }

    /// <summary>生成 Unity bindPose：直接用骨骼树【绑定姿势】的 worldToLocalMatrix（Unity 标准做法）。
    /// ⚠️ 不用 glTF IBM：实测 Soldier.glb 的 inverseBindMatrices 经 SharpGLTF 读取 + GltfMatToUnity
    /// 转换后平移列丢失（Hips 应有 y=-1.06 平移却是 0）→ 蒙皮缺"减骨骼位置"项 → 顶点炸飞到 ±19000。
    /// 用骨骼树绑定姿势 worldToLocal 则与 bones/localToWorldMatrix 天然一致，蒙皮数学正确。</summary>
    private Matrix4x4[] ReadGltfBindposes(SharpGLTF.Schema2.Skin skin)
    {
        var list = new List<Matrix4x4>(_bones.Count);
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            list.Add(b.Transform != null ? b.Transform.worldToLocalMatrix : Matrix4x4.identity);
        }
        return list.ToArray();
    }

    /// <summary>System.Numerics.Matrix4x4（行主序）→ Unity Matrix4x4（列主序构造函数，4 列）。
    /// Unity 列 c = System.Numerics 的 c 列 [M1c, M2c, M3c, M4c]。</summary>
    private static Matrix4x4 GltfMatToUnity(System.Numerics.Matrix4x4 m)
    {
        return new Matrix4x4(
            new Vector4(m.M11, m.M21, m.M31, m.M41),
            new Vector4(m.M12, m.M22, m.M32, m.M42),
            new Vector4(m.M13, m.M23, m.M33, m.M43),
            new Vector4(m.M14, m.M24, m.M34, m.M44));
    }

    // ---------- 动画驱动（程序化骨架） ----------
    private float _animSpeed;
    private float _walkPhase;

    // 骨骼名匹配缓存（按名字匹配模型关节，绑上我们写的骨架动画）
    private Transform _hip, _spine, _spine1, _head;
    private Transform _thighL, _shinL, _thighR, _shinR;
    private Transform _shoulderL, _shoulderR, _armL, _foreL, _armR, _foreR;

    // 【方向B：T-pose 基准】绑定姿势 = 模型原始 T-pose（行业标准，模型零改动）。
    // 待机站姿 = 在 T-pose 上把手臂从水平（±X）转到自然下垂（-Y）；走路 = 站姿 + 摆臂摆腿。
    // 关键：手臂下垂/摆动必须【旋转肩关节 LeftShoulder】（它把手臂抬到水平），
    // 而不是在大臂 LeftArm 上做世界旋转（那样前臂/手不跟随、手臂被拉长扭曲）。
    private Quaternion _bindThighL = Quaternion.identity, _bindThighR = Quaternion.identity;
    private Quaternion _bindShinL = Quaternion.identity, _bindShinR = Quaternion.identity;
    private Quaternion _bindShoulderL = Quaternion.identity, _bindShoulderR = Quaternion.identity;
    private Quaternion _bindForeL = Quaternion.identity, _bindForeR = Quaternion.identity;
    private Quaternion _bindSpine = Quaternion.identity, _bindHead = Quaternion.identity;

    // 肩关节自然下垂校准：绑定姿势下肩关节的【世界旋转】+ 手臂【伸展方向】（大臂→小臂的世界差）。
    // 每帧用 FromToRotation(伸展方向, 目标方向) 旋转肩关节，整条手臂链（大臂/前臂/手）自然跟随。
    // T-pose 伸展方向≈±X，目标下垂=-Y → 自动得到 ~90° 旋转，左右自动相反，适配任何 T-pose 模型。
    private Vector3 _bindShoulderDirL = Vector3.right, _bindShoulderDirR = Vector3.left;
    private Quaternion _bindShoulderWorldL = Quaternion.identity, _bindShoulderWorldR = Quaternion.identity;

    // 腿：大腿绑定伸展方向 + 世界旋转（从绑定方向转到目标方向，T-pose 腿已朝下≈-Y）
    private Vector3 _bindThighDirL = Vector3.down, _bindThighDirR = Vector3.down;
    private Quaternion _bindThighWorldL = Quaternion.identity, _bindThighWorldR = Quaternion.identity;

    public void Update(float dt, AvatarPose pose)
    {
        if (_skinned == null || _bones.Count == 0) return;
        ResolveBones();
        CaptureBindPoses();
        Animate(dt, pose);
    }

    /// <summary>记录各骨骼的绑定姿势旋转 + 手臂/腿的自然下垂基准（首次调用一次）。</summary>
    private bool _bindCaptured;
    private void CaptureBindPoses()
    {
        if (_bindCaptured) return;
        _bindCaptured = true;
        if (_thighL != null) _bindThighL = _thighL.localRotation;
        if (_thighR != null) _bindThighR = _thighR.localRotation;
        if (_shinL != null) _bindShinL = _shinL.localRotation;
        if (_shinR != null) _bindShinR = _shinR.localRotation;
        if (_shoulderL != null) _bindShoulderL = _shoulderL.localRotation;
        if (_shoulderR != null) _bindShoulderR = _shoulderR.localRotation;
        if (_foreL != null) _bindForeL = _foreL.localRotation;
        if (_foreR != null) _bindForeR = _foreR.localRotation;
        if (_spine1 != null) _bindSpine = _spine1.localRotation;
        if (_head != null) _bindHead = _head.localRotation;

        // 肩关节：记录绑定【世界旋转】和手臂【伸展方向】（大臂→小臂的世界差）。
        // T-pose 下伸展方向≈±X（水平），目标下垂=-Y → FromToRotation 自动得到 ~90° 下垂。
        if (_shoulderL != null && _armL != null)
        {
            _bindShoulderWorldL = _shoulderL.rotation;
            Vector3 dir = (_foreL != null ? _foreL.position : _armL.position + Vector3.right) - _armL.position;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.right;
            _bindShoulderDirL = dir.normalized;
        }
        if (_shoulderR != null && _armR != null)
        {
            _bindShoulderWorldR = _shoulderR.rotation;
            Vector3 dir = (_foreR != null ? _foreR.position : _armR.position + Vector3.left) - _armR.position;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.left;
            _bindShoulderDirR = dir.normalized;
        }

        // 腿：记录大腿的绑定【世界旋转】和【伸展方向】（大腿→小腿的世界差，通常朝下）。
        if (_thighL != null)
        {
            _bindThighWorldL = _thighL.rotation;
            Vector3 dir = (_shinL != null ? _shinL.position : _thighL.position + Vector3.down) - _thighL.position;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.down;
            _bindThighDirL = dir.normalized;
        }
        if (_thighR != null)
        {
            _bindThighWorldR = _thighR.rotation;
            Vector3 dir = (_shinR != null ? _shinR.position : _thighR.position + Vector3.down) - _thighR.position;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.down;
            _bindThighDirR = dir.normalized;
        }

        // 诊断：打印骨骼方向基准（确认 ResolveBones 匹配正确 + Z 翻转/层级正确）。
        // 期望：_armL/_foreL 非空且 dir≈±X（水平 T-pose 手臂）；大腿 dir≈-Y（向下）。
        try
        {
            CoopRuntime.LogSource?.Info(
                $"GlbModelRuntime bindPose: " +
                $"armL={( _armL != null ? _armL.name : "NULL")} foreL={(_foreL != null ? _foreL.name : "NULL")} " +
                $"armLpos={(_armL != null ? _armL.position.ToString("F2") : "?")} foreLpos={(_foreL != null ? _foreL.position.ToString("F2") : "?")} " +
                $"dirL={_bindShoulderDirL.ToString("F2")} dirR={_bindShoulderDirR.ToString("F2")} " +
                $"thighDirL={_bindThighDirL.ToString("F2")} shinL={(_shinL != null ? _shinL.name : "NULL")} head={(_head != null ? _head.name : "NULL")}");
        }
        catch { }
    }

    /// <summary>按名字匹配模型骨骼（Hips_01 → Hips 等），绑上我们写的骨架动画。
    /// ⚠️ 修复（2026-08-13）：用 Contains 子串匹配有冲突——"LeftForeArm" 含 "LeftArm"、"HeadTop" 含 "Head"，
    /// 导致 _armL 错误指向前臂（_foreL=null）、_head 被头顶尖端覆盖 → 手臂动画基准错 → 拉长扭曲。
    /// 正确：ForeArm 先于 Arm 匹配（更具体），HeadTop 排除。</summary>
    private void ResolveBones()
    {
        if (_hip != null) return;
        foreach (var b in _bones)
        {
            if (b.Transform == null) continue;
            var n = b.Name ?? "";
            if (n.Contains("Hips")) _hip = b.Transform;
            else if (n.Contains("Spine1")) _spine1 = b.Transform;
            else if (n.Contains("Spine2")) { if (_spine == null) _spine = b.Transform; }
            else if (n.Contains("Spine") && _spine == null) _spine = b.Transform;
            else if (n.Contains("HeadTop")) { /* 排除头顶尖端 */ }
            else if (n.Contains("Head")) _head = b.Transform;
            else if (n.Contains("LeftUpLeg")) _thighL = b.Transform;
            else if (n.Contains("LeftLeg")) _shinL = b.Transform;
            else if (n.Contains("RightUpLeg")) _thighR = b.Transform;
            else if (n.Contains("RightLeg")) _shinR = b.Transform;
            else if (n.Contains("LeftShoulder")) _shoulderL = b.Transform;
            // ⚠️ ForeArm 必须先在 Arm 之前匹配（"LeftForeArm" 含 "LeftArm"）
            else if (n.Contains("LeftForeArm")) _foreL = b.Transform;
            else if (n.Contains("LeftArm")) _armL = b.Transform;
            else if (n.Contains("RightShoulder")) _shoulderR = b.Transform;
            else if (n.Contains("RightForeArm")) _foreR = b.Transform;
            else if (n.Contains("RightArm")) _armR = b.Transform;
        }
    }

    /// <summary>动画入口：CSV Mixamo 动画优先（German 骨骼，真动作）；否则模型自带动画；否则程序化。</summary>
    private void Animate(float dt, AvatarPose pose)
    {
        // 临时诊断开关：ONC_NO_CSV=1 禁用 CSV 动画（对比绑定姿势）
        if (Environment.GetEnvironmentVariable("ONC_NO_CSV") == "1")
        {
            if (_animChannels.Count == 0) { /* keep bind */ }
            return;
        }
        if (_csvAnims != null && _csvAnims.Count > 0) { CsvAnimate(dt, pose); return; }
        var anim = SelectAnimation(pose);
        if (anim != null && _animChannels.ContainsKey(anim)) { PlaySelfSampled(anim, dt, pose); return; }
        ProgrammaticAnimate(dt, pose);
    }

    /// <summary>按移动速度选动画（Idle/Walk/Run；蹲/跳用 Idle）。</summary>
    private SharpGLTF.Schema2.Animation SelectAnimation(AvatarPose pose)
    {
        float speed = Mathf.Max(0f, pose.Speed);
        if (pose.Crouched || pose.Airborne) return _animIdle;
        if (speed > 0.05f) return pose.Sprinting ? (_animRun ?? _animWalk ?? _animIdle) : (_animWalk ?? _animIdle);
        return _animIdle;
    }

    // ---------------- CSV Mixamo 动画（Unity 烘焙 German 骨骼，替代程序化驱动） ----------------

    /// <summary>加载模型同目录 german_anims/*.csv（Unity 烘焙的 Mixamo 动画，骨骼名 Hips/LeftUpLeg 等）。
    /// 运行时按归一化骨骼名（去 _NN 后缀）匹配 glb 骨骼（Hips_01→Hips）。</summary>
    private void LoadCsvAnims(string modelPath)
    {
        try
        {
            if (_csvLoaded) return;
            _csvLoaded = true;
            string dir = Path.Combine(Path.GetDirectoryName(modelPath), "german_anims");
            if (!Directory.Exists(dir)) return;
            var anims = new Dictionary<string, Dictionary<string, List<CsvFrame>>>();
            foreach (var f in Directory.GetFiles(dir, "*.csv"))
            {
                string clip = Path.GetFileNameWithoutExtension(f);
                var byBone = new Dictionary<string, List<CsvFrame>>();
                foreach (var line in File.ReadAllLines(f))
                {
                    var p = line.Split(',');
                    if (p.Length < 6) continue;
                    if (!float.TryParse(p[0], out var t)) continue;
                    string bone = NormalizeBoneName(p[1]);
                    if (!byBone.TryGetValue(bone, out var list)) byBone[bone] = list = new List<CsvFrame>();
                    list.Add(new CsvFrame
                    {
                        T = t,
                        Q = new Quaternion(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4]), float.Parse(p[5]))
                    });
                }
                anims[clip] = byBone;
            }
            if (anims.Count > 0)
            {
                _csvAnims = anims;
                CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime: 加载 CSV Mixamo 动画 {anims.Count} 个: {string.Join(",", anims.Keys)}");
                // ---- 临时诊断：骨骼匹配率 + 骨骼树 + 缺失骨骼 ----
                try
                {
                    int matched = 0; var missing = new List<string>();
                    foreach (var b in _bones)
                    {
                        if (b.Name == null) continue;
                        string bn = NormalizeBoneName(b.Name);
                        bool ok = anims.Values.Any(a => a.ContainsKey(bn));
                        if (ok) matched++; else missing.Add(b.Name);
                    }
                    CoopRuntime.LogSource?.LogWarning($"CSV 匹配: {matched}/{_bones.Count} 缺失({missing.Count}): {string.Join(",", missing.Take(25))}");
                    // glb 骨骼树（Hips_01 父链）
                    foreach (var b in _bones)
                    {
                        if (b.Name == null || b.Transform == null) continue;
                        if (NormalizeBoneName(b.Name) == "Hips")
                        {
                            var t = b.Transform; var chain = new List<string>();
                            while (t != null && chain.Count < 8) { chain.Add($"{t.name}(r={t.localRotation.eulerAngles})"); t = t.parent; }
                            CoopRuntime.LogSource?.LogWarning($"CSV Hips_01 父链: {string.Join(" ← ", chain)}");
                            break;
                        }
                    }
                }
                catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"CSV 诊断: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime CSV 加载: {ex.Message}");
        }
    }

    private void CsvAnimate(float dt, AvatarPose pose)
    {
        string clip = SelectCsvClip(pose);
        if (clip == null || !_csvAnims.TryGetValue(clip, out var bones)) return;
        float dur = 0f;
        foreach (var kv in bones)
            foreach (var fr in kv.Value)
                if (fr.T > dur) dur = fr.T;
        if (dur <= 0f) return;
        _csvTime += dt;
        if (_csvTime > dur) _csvTime -= dur;
        int applied = 0;
        foreach (var b in _bones)
        {
            if (b.Transform == null || b.Name == null) continue;
            string bn = NormalizeBoneName(b.Name);
            if (!bones.TryGetValue(bn, out var frames) || frames.Count == 0) continue;
            b.Transform.localRotation = SampleFrames(frames, _csvTime);
            applied++;
        }
        // 临时诊断：每 2 秒打印一次动画驱动状态
        _csvDiagAcc += dt;
        if (_csvDiagAcc > 2f)
        {
            _csvDiagAcc = 0f;
            var hip = _bones.FirstOrDefault(x => x.Name != null && NormalizeBoneName(x.Name) == "Hips");
            string hipInfo = "?";
            if (hip != null && hip.Transform != null)
            {
                string lr = hip.Transform.localRotation.eulerAngles.ToString("F1");
                string wp = hip.Transform.position.ToString("F2");
                string pn = hip.Transform.parent != null ? hip.Transform.parent.name : "null";
                hipInfo = "localRot=" + lr + " worldPos=" + wp + " parent=" + pn;
            }
            // 诊断：头/脚/手臂世界方向（判断横躺 vs 站立）
            string dirInfo = "";
            try
            {
                var head = _bones.FirstOrDefault(x => x.Name != null && NormalizeBoneName(x.Name) == "Head");
                var foot = _bones.FirstOrDefault(x => x.Name != null && (NormalizeBoneName(x.Name).Contains("Foot") || NormalizeBoneName(x.Name).Contains("Toe")));
                var armL = _bones.FirstOrDefault(x => x.Name != null && NormalizeBoneName(x.Name) == "LeftArm");
                if (hip != null && head != null && hip.Transform != null && head.Transform != null)
                {
                    var up = head.Transform.position - hip.Transform.position;
                    dirInfo += " headUp=" + up.ToString("F2");
                }
                if (hip != null && foot != null && hip.Transform != null && foot.Transform != null)
                {
                    var down = foot.Transform.position - hip.Transform.position;
                    dirInfo += " footDown=" + down.ToString("F2");
                }
                if (hip != null && armL != null && hip.Transform != null && armL.Transform != null)
                {
                    var a = armL.Transform.position - hip.Transform.position;
                    dirInfo += " armL=" + a.ToString("F2") + " armLRot=" + armL.Transform.localRotation.eulerAngles.ToString("F1");
                    // 手臂指向：LeftForeArm - LeftArm（判断水平 T-pose 还是下垂）
                    var fore = _bones.FirstOrDefault(x => x.Name != null && NormalizeBoneName(x.Name) == "LeftForeArm");
                    if (fore != null && fore.Transform != null)
                    {
                        var armDir = fore.Transform.position - armL.Transform.position;
                        dirInfo += " foreDir=" + armDir.ToString("F2");
                    }
                    // 手相对 Hips 方向（判断整条手臂方向）
                    var hand = _bones.FirstOrDefault(x => x.Name != null && NormalizeBoneName(x.Name) == "LeftHand");
                    if (hand != null && hand.Transform != null)
                    {
                        var hd = hand.Transform.position - hip.Transform.position;
                        dirInfo += " handRelHips=" + hd.ToString("F2");
                    }
                }
                var hipName = hip != null ? hip.Name : "null";
                dirInfo += " hipName=" + hipName;
                // 决定性诊断：SkinnedMeshRenderer 实际世界包围盒（渲染顶点真实范围）
                if (_skinned != null)
                {
                    var wb = _skinned.bounds;
                    var s = wb.size;
                    var c = wb.center;
                    dirInfo += " RENDER bounds size=(" + s.x.ToString("F2") + "," + s.y.ToString("F2") + "," + s.z.ToString("F2") + ") center=" + c.ToString("F2");
                    dirInfo += " skinnedRot=" + _skinned.transform.eulerAngles.ToString("F1");
                    dirInfo += " rootBone=" + (_skinned.rootBone != null ? _skinned.rootBone.name : "null");
                    // mesh 局部包围盒（相对 _visual）
                    var lb = _skinned.sharedMesh.bounds;
                    dirInfo += " MESH local size=(" + lb.size.x.ToString("F2") + "," + lb.size.y.ToString("F2") + "," + lb.size.z.ToString("F2") + ") min=(" + lb.min.x.ToString("F2") + "," + lb.min.y.ToString("F2") + "," + lb.min.z.ToString("F2") + ")";
                    // _visual 世界变换（看是否有额外旋转/缩放）
                    dirInfo += " visualPos=" + _skinned.transform.position.ToString("F2") + " visualScale=" + _skinned.transform.lossyScale.ToString("F2");
                    // Hips 骨骼世界矩阵旋转列（看骨骼整体是否被转平）
                    if (hip != null && hip.Transform != null)
                    {
                        var up = hip.Transform.up;
                        var fwd = hip.Transform.forward;
                        dirInfo += " hipUp=" + up.ToString("F2") + " hipFwd=" + fwd.ToString("F2");
                    }
                    // 用当前骨骼矩阵重算蒙皮顶点范围（对比引擎 RENDER bounds）
                    try
                    {
                        var mesh = _skinned.sharedMesh;
                        var verts = mesh.vertices;
                        var bws = mesh.boneWeights;
                        var bps = mesh.bindposes;
                        var skBones = _skinned.bones;
                        int nn = Mathf.Min(verts.Length, 4000);
                        float mmx = float.MaxValue, mxx = float.MinValue, mmy = float.MaxValue, mxy = float.MinValue, mmz = float.MaxValue, mxz = float.MinValue;
                        for (int i = 0; i < nn; i++)
                        {
                            var v = verts[i]; var bw = bws[i];
                            Vector3 p = Vector3.zero;
                            if (bw.weight0 > 0.001f && bw.boneIndex0 >= 0 && bw.boneIndex0 < skBones.Length) p += bw.weight0 * skBones[bw.boneIndex0].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex0].MultiplyPoint3x4(v));
                            if (bw.weight1 > 0.001f && bw.boneIndex1 >= 0 && bw.boneIndex1 < skBones.Length) p += bw.weight1 * skBones[bw.boneIndex1].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex1].MultiplyPoint3x4(v));
                            if (bw.weight2 > 0.001f && bw.boneIndex2 >= 0 && bw.boneIndex2 < skBones.Length) p += bw.weight2 * skBones[bw.boneIndex2].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex2].MultiplyPoint3x4(v));
                            if (bw.weight3 > 0.001f && bw.boneIndex3 >= 0 && bw.boneIndex3 < skBones.Length) p += bw.weight3 * skBones[bw.boneIndex3].localToWorldMatrix.MultiplyPoint3x4(bps[bw.boneIndex3].MultiplyPoint3x4(v));
                            if (p.x < mmx) mmx = p.x; if (p.x > mxx) mxx = p.x;
                            if (p.y < mmy) mmy = p.y; if (p.y > mxy) mxy = p.y;
                            if (p.z < mmz) mmz = p.z; if (p.z > mxz) mxz = p.z;
                        }
                        dirInfo += " RECOMPUTE range X=[" + mmx.ToString("F2") + "," + mxx.ToString("F2") + "] Y=[" + mmy.ToString("F2") + "," + mxy.ToString("F2") + "] Z=[" + mmz.ToString("F2") + "," + mxz.ToString("F2") + "]";
                    }
                    catch { }
                }
            }
            catch { }
            CoopRuntime.LogSource?.LogWarning("CSV驱动: clip=" + clip + " t=" + _csvTime.ToString("F2") + "/" + dur.ToString("F2") + " applied=" + applied + "/" + _bones.Count + " " + hipInfo + dirInfo);
        }
    }
    private float _csvDiagAcc;

    private static Quaternion SampleFrames(List<CsvFrame> f, float t)
    {
        if (f.Count == 0) return Quaternion.identity;
        if (f.Count == 1 || t <= f[0].T) return f[0].Q;
        if (t >= f[f.Count - 1].T) return f[f.Count - 1].Q;
        for (int i = 1; i < f.Count; i++)
        {
            if (t <= f[i].T)
            {
                var a = f[i - 1]; var b = f[i];
                float k = (b.T - a.T) > 0.0001f ? (t - a.T) / (b.T - a.T) : 0f;
                return Quaternion.Slerp(a.Q, b.Q, k);
            }
        }
        return f[f.Count - 1].Q;
    }

    /// <summary>按玩家状态选 CSV 动画片（Idle/Walking/Running/蹲/跳/横移）。</summary>
    private static string SelectCsvClip(AvatarPose pose)
    {
        float speed = Mathf.Max(0f, pose.Speed);
        if (pose.Crouched) return "Male_Crouch_Pose";
        if (pose.Airborne) return "Jumping_Up";
        if (speed > 0.05f)
        {
            if (Mathf.Abs(pose.MoveStrafe) > Mathf.Abs(pose.MoveFwd) * 1.2f)
                return pose.MoveStrafe > 0f ? "Right_Strafe_Walking" : "Left_Strafe_Walking";
            return pose.Sprinting ? "Running" : "Walking";
        }
        return "Idle";
    }

    /// <summary>归一化骨骼名：去掉末尾 _数字 后缀（Hips_01→Hips、LeftUpLeg_039→LeftUpLeg、
    /// HeadTop_End_07→HeadTop_End）。CSV（Unity 名 Hips）与 glb（Hips_01）匹配。</summary>
    private static string NormalizeBoneName(string n)
    {
        if (string.IsNullOrEmpty(n)) return n;
        for (int i = n.Length - 1; i >= 0; i--)
        {
            if (char.IsDigit(n[i])) continue;
            if (n[i] == '_' && i < n.Length - 1)
            {
                bool allDigit = true;
                for (int j = i + 1; j < n.Length; j++)
                    if (!char.IsDigit(n[j])) { allDigit = false; break; }
                if (allDigit) return n.Substring(0, i);
            }
            break;
        }
        return n;
    }

    /// <summary>自采样播放动画（SharpGLTF.Core）：每帧对每个骨骼采样其动画 channel 的
    /// translation/rotation/scale，直接应用到 Unity Transform。⚠️ 用 glTF 原始值
    /// （不翻转 Z、不缩放）——骨骼树已按 glTF 原始 localTransform 构建（含 Character 的
    /// R_x(-90)+scale0.01 世界变换），动画采样值也在同一 glTF 空间，直接对应即可。
    /// 动画是作者烘焙的 → 自然不扭曲（替代程序化骨骼驱动）。</summary>
    private void PlaySelfSampled(SharpGLTF.Schema2.Animation anim, float dt, AvatarPose pose)
    {
        try
        {
            float dur = anim.Duration;
            if (dur > 0.01f) { _animTime += dt; if (_animTime > dur) _animTime -= dur; }
            if (!_animChannels.TryGetValue(anim, out var byBone)) return;
            foreach (var b in _bones)
            {
                if (b.Transform == null || b.Name == null) continue;
                if (!byBone.TryGetValue(b.Name, out var chs)) continue;
                foreach (var ch in chs)
                {
                    string p = ch.TargetNodePath.ToString();
                    if (p == "translation")
                    {
                        var sv = ch.GetTranslationSampler().CreateCurveSampler().GetPoint(_animTime);
                        if (b.Transform == _rootBone)
                        {
                            // 根骨骼只保留 Y 起伏（呼吸/蹲起），XZ 保持绑定值——
                            // 防 Mixamo root motion 让身体相对玩家位置前后漂移滑步（位置由 PlayerSync 管）
                            var lp = b.Transform.localPosition;
                            b.Transform.localPosition = new Vector3(lp.x, sv.Y, lp.z);
                        }
                        else
                        {
                            b.Transform.localPosition = new Vector3(sv.X, sv.Y, sv.Z);
                        }
                    }
                    else if (p == "rotation")
                    {
                        var qv = ch.GetRotationSampler().CreateCurveSampler().GetPoint(_animTime);
                        b.Transform.localRotation = new Quaternion(qv.X, qv.Y, qv.Z, qv.W);
                    }
                    else if (p == "scale")
                    {
                        var sv2 = ch.GetScaleSampler().CreateCurveSampler().GetPoint(_animTime);
                        b.Transform.localScale = new Vector3(sv2.X, sv2.Y, sv2.Z);
                    }
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime anim: {ex.Message}"); }
    }

    /// <summary>程序化骨架动画（世界方向驱动，不受模型骨骼轴向影响）：
    /// 待机/走路/奔跑/蹲走/跳跃。用骨骼的绑定世界方向 + Quaternion.FromToRotation 驱动，
    /// 对任何骨骼轴向都正确（避免模型手臂沿 +Y、腿绕 Z 180° 导致的"手臂极长/关节错位"）。</summary>
    private void ProgrammaticAnimate(float dt, AvatarPose pose)
    {
        if (_hip == null) return;

        float targetSpeed = Mathf.Max(0f, pose.Speed);
        _animSpeed = Mathf.Lerp(_animSpeed, targetSpeed, 0.15f);
        float speed = _animSpeed;
        bool moving = speed > 0.05f;

        float amp = 0.32f + Mathf.Min(speed, 3f) * 0.12f;
        // 目标方向（世界）：大腿/小腿/大臂/小臂都朝 -Y 下垂为基准，再叠加摆幅
        float headPitch = Mathf.Clamp(pose.Pitch, -60f, 60f);

        // 腿：大腿绕 X 摆（cosA 前后）+ 绕 Z 侧移（sinA 横移）
        float fwdAmt = Mathf.Clamp(pose.MoveFwd, -1f, 1f);
        float strAmt = Mathf.Clamp(pose.MoveStrafe, -1f, 1f);
        float moveLen = Mathf.Sqrt(fwdAmt * fwdAmt + strAmt * strAmt);
        float cosA = moveLen > 0.001f ? fwdAmt / moveLen : 1f;
        float sinA = moveLen > 0.001f ? strAmt / moveLen : 0f;

        // 先恢复绑定姿势 + 手臂/腿自然下垂（每帧重置，避免累积/撕裂）
        RestoreBindPose();

        if (pose.Airborne)
        {
            // ⚠️ 跳跃（自然）：双腿微前屈 + 小腿微弯 + 手臂自然下垂微摆（不夸张上举/抬腿）。
            // 旧实现大腿 -35° 前抬 + 手臂 -55° 上举 → 像"跪着举手"，姿势怪异。
            PoseLimb(_thighL, _bindThighWorldL, _bindThighDirL, -22f, 0f);
            PoseLimb(_thighR, _bindThighWorldR, _bindThighDirR, -22f, 0f);
            BendBone(_shinL, _bindShinL, 28f);
            BendBone(_shinR, _bindShinR, 28f);
            PoseShoulder(_shoulderL, _bindShoulderWorldL, _bindShoulderDirL, -12f);
            PoseShoulder(_shoulderR, _bindShoulderWorldR, _bindShoulderDirR, -12f);
            BendBone(_foreL, _bindForeL, -12f);
            BendBone(_foreR, _bindForeR, -12f);
            if (_head != null) _head.localRotation = _bindHead * Quaternion.Euler(headPitch, 0f, 0f);
        }
        else if (pose.Crouched)
        {
            // ⚠️ 蹲（自然深蹲）：双腿对称前屈 + 小腿弯曲（像坐着），身体前倾，手臂自然下垂。
            // 旧实现"一腿跪一腿抬 + 小腿贴地 130°"→ 单膝跪地姿势怪异。
            float thighC = -48f, shinC = 55f;
            if (moving)
            {
                const float StrideLen = 0.9f;
                float freqC = Mathf.Clamp(speed / StrideLen, 0.5f, 1.1f);
                _walkPhase += freqC * Mathf.PI * 2f * dt;
                float sinL = Mathf.Sin(_walkPhase);
                float sinR = Mathf.Sin(_walkPhase + Mathf.PI);
                // 蹲走：双腿小幅交替前屈
                PoseLimb(_thighL, _bindThighWorldL, _bindThighDirL, thighC + sinL * 12f, 0f);
                PoseLimb(_thighR, _bindThighWorldR, _bindThighDirR, thighC + sinR * 12f, 0f);
                BendBone(_shinL, _bindShinL, shinC + Mathf.Max(0f, -sinL) * 12f);
                BendBone(_shinR, _bindShinR, shinC + Mathf.Max(0f, -sinR) * 12f);
            }
            else
            {
                PoseLimb(_thighL, _bindThighWorldL, _bindThighDirL, thighC, 0f);
                PoseLimb(_thighR, _bindThighWorldR, _bindThighDirR, thighC, 0f);
                BendBone(_shinL, _bindShinL, shinC);
                BendBone(_shinR, _bindShinR, shinC);
            }
            PoseShoulder(_shoulderL, _bindShoulderWorldL, _bindShoulderDirL, -10f);
            PoseShoulder(_shoulderR, _bindShoulderWorldR, _bindShoulderDirR, -10f);
            BendBone(_foreL, _bindForeL, -10f);
            BendBone(_foreR, _bindForeR, -10f);
            if (_spine1 != null) _spine1.localRotation = _bindSpine * Quaternion.Euler(18f, 0f, 0f);
            if (_head != null) _head.localRotation = _bindHead * Quaternion.Euler(-4f + headPitch, 0f, 0f);
        }
        else if (moving)
        {
            // 走路/奔跑：腿前后摆（绕世界 X 前倾）+ 手臂对侧摆
            bool sprint = pose.Sprinting;
            float StrideLen = sprint ? 1.20f : 1.40f;
            float freq = Mathf.Clamp(speed / StrideLen, 0.8f, sprint ? 2.4f : 1.8f);
            _walkPhase += freq * Mathf.PI * 2f * dt;
            float sinL = Mathf.Sin(_walkPhase);
            float sinR = Mathf.Sin(_walkPhase + Mathf.PI);
            float ampRun = sprint ? 1.25f : 1.0f;
            float shinKnee = sprint ? 42f : 60f;

            float xL = sinL * amp * 72f * cosA * ampRun;
            float xR = sinR * amp * 72f * cosA * ampRun;
            // 大腿绕世界 X 前后摆（正=前抬，负=后摆）——PoseLimb 的 fwd 角
            PoseLimb(_thighL, _bindThighWorldL, _bindThighDirL, xL, 0f);
            PoseLimb(_thighR, _bindThighWorldR, _bindThighDirR, xR, 0f);
            float shinAngL = Mathf.Max(0f, -xL) * (shinKnee / 72f);
            float shinAngR = Mathf.Max(0f, -xR) * (shinKnee / 72f);
            BendBone(_shinL, _bindShinL, shinAngL);
            BendBone(_shinR, _bindShinR, shinAngR);
            // 手臂对侧摆（绕肩关节）
            PoseShoulder(_shoulderL, _bindShoulderWorldL, _bindShoulderDirL, sinR * amp * 55f * cosA * ampRun);
            PoseShoulder(_shoulderR, _bindShoulderWorldR, _bindShoulderDirR, sinL * amp * 55f * cosA * ampRun);
            BendBone(_foreL, _bindForeL, -32f - sinR * amp * 22f * ampRun);
            BendBone(_foreR, _bindForeR, -32f - sinL * amp * 22f * ampRun);
            if (_spine1 != null) _spine1.localRotation = _bindSpine * Quaternion.Euler(sprint ? 18f : 8f, 0f, 0f);
            if (_head != null) _head.localRotation = _bindHead * Quaternion.Euler(headPitch - (sprint ? 4f : 0f), 0f, 0f);
        }
        else
        {
            // 待机：保持站姿（绑定姿势校准后）+ 轻微呼吸
            if (_spine1 != null) _spine1.localRotation = _bindSpine;
            if (_head != null) _head.localRotation = _bindHead * Quaternion.Euler(headPitch, 0f, 0f);
        }
    }

    /// <summary>世界方向驱动肢体：把骨骼从绑定伸展方向转到「下垂 + 绕世界 X 前后摆 + 绕世界 Z 侧摆」。
    /// 直接设绝对世界旋转，整条链一致、不撕裂。</summary>
    private static void PoseLimb(Transform bone, Quaternion bindWorld, Vector3 bindDir, float fwdAngle, float sideAngle)
    {
        if (bone == null) return;
        try
        {
            // 目标方向 = 绕世界 X 摆 fwdAngle（正=前），再绕世界 Z 摆 sideAngle
            var baseDir = Quaternion.Euler(fwdAngle, 0f, 0f) * Quaternion.Euler(0f, 0f, sideAngle) * Vector3.down;
            bone.rotation = Quaternion.FromToRotation(bindDir, baseDir) * bindWorld;
        }
        catch { }
    }

    /// <summary>世界方向驱动肩关节：把肩关节从绑定旋转转到「手臂下垂 + 绕世界 X 摆动」。
    /// 旋转肩关节 → 整条手臂链（大臂/前臂/手）作为子节点自动跟随，无撕裂/拉长。
    /// 前臂用 BendBone 局部弯曲补充屈肘。</summary>
    private static void PoseShoulder(Transform shoulder, Quaternion bindWorld, Vector3 bindDir, float fwdAngle)
    {
        if (shoulder == null) return;
        try
        {
            // 手臂目标方向 = 绕世界 X 摆 fwdAngle（正=前）的下垂方向（-Y）
            var baseDir = Quaternion.Euler(fwdAngle, 0f, 0f) * Vector3.down;
            shoulder.rotation = Quaternion.FromToRotation(bindDir, baseDir) * bindWorld;
        }
        catch { }
    }

    /// <summary>局部弯曲子骨骼（小腿/前臂）：在大腿/大臂已经被世界方向驱动后，
    /// 在绑定姿势的局部空间里绕局部 X 弯曲，作为子节点自然跟随父骨骼。</summary>
    private static void BendBone(Transform bone, Quaternion bindLocal, float angle)
    {
        if (bone == null) return;
        try
        {
            bone.localRotation = bindLocal * Quaternion.Euler(angle, 0f, 0f);
        }
        catch { }
    }

    /// <summary>恢复骨骼绑定姿势（T-pose 基准；肩关节用世界方向 FromToRotation 使手臂自然下垂）。</summary>
    private void RestoreBindPose()
    {
        if (_thighL != null) _thighL.localRotation = _bindThighL;
        if (_thighR != null) _thighR.localRotation = _bindThighR;
        if (_shinL != null) _shinL.localRotation = _bindShinL;
        if (_shinR != null) _shinR.localRotation = _bindShinR;
        if (_foreL != null) _foreL.localRotation = _bindForeL;
        if (_foreR != null) _foreR.localRotation = _bindForeR;
        if (_spine1 != null) _spine1.localRotation = _bindSpine;
        if (_head != null) _head.localRotation = _bindHead;

        // 手臂自然下垂：旋转【肩关节】，把手臂伸展方向从绑定（T-pose≈±X）转到世界 -Y。
        // 整条手臂链（大臂/前臂/手）作为子节点自动跟随，无撕裂、无拉长。
        if (_shoulderL != null && _armL != null)
            _shoulderL.rotation = Quaternion.FromToRotation(_bindShoulderDirL, Vector3.down) * _bindShoulderWorldL;
        if (_shoulderR != null && _armR != null)
            _shoulderR.rotation = Quaternion.FromToRotation(_bindShoulderDirR, Vector3.down) * _bindShoulderWorldR;
    }

    /// <summary>对外暴露的根对象：返回 _pivot（容器）。外部（ExternalModelProvider）对它的
    /// 位置/旋转/缩放操作，会正确作用于整个模型；_visual 保持 identity 蒙皮安全。</summary>
    public GameObject Visual => _pivot ?? _visual;

    /// <summary>当前动画姿态下，模型【可视最低点】的世界 Y（网格包围盒底，大衣/靴底延伸），
    /// 用于贴地校正（比脚骨更接近可见底部，避免"陷地"）。</summary>
    public float GetFootWorldY()
    {
        try
        {
            if (_skinned == null || _skinned.sharedMesh == null) return float.NaN;
            var b = _skinned.sharedMesh.bounds;
            // 网格包围盒最低点的世界 Y（考虑 SkinnedMeshRenderer 的当前姿势）
            var minY = _skinned.transform.TransformPoint(new Vector3(0f, b.min.y, 0f)).y;
            return minY;
        }
        catch { return float.NaN; }
    }

    // ---------- 坐标转换 ----------
    // （不再需要 FlipRot：Unity 的 Quaternion 与 glTF 四元数数学同构（标准轴角），直接拷贝即可。
    //   顶点/骨骼/bindPose 全部用 glTF 原始值，蒙皮 = Σ w * W_j * IBM_j * POSITION 输出正确世界位置。）

    // ---------- 材质 ----------
    private static Material BuildGlbMaterial(SharpGLTF.Schema2.Material m)
    {
        if (m == null) return null;
        // 兼容多种材质模型：标准 PBR 用 BaseColor；老式 SpecularGlossiness 扩展用 Diffuse。
        var bc = m.FindChannel("BaseColor");
        if (!bc.HasValue) bc = m.FindChannel("Diffuse"); // KHR_materials_pbrSpecularGlossiness
        if (!bc.HasValue) return null;
        var ch = bc.Value;
        var factor = ch.Color;
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
                    else CoopRuntime.LogSource?.Info($"GlbModelRuntime.BuildGlbMaterial: tex OK {bytes.Length}b mime={img.Content.MimeType}");
                }
            }
        }
        catch (Exception ex) { if (tex != null) { UnityEngine.Object.Destroy(tex); tex = null; } CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime.BuildGlbMaterial tex: {ex.Message}"); }

        var mat = MakeUnlit(tint, tex);
        if (mat == null) CoopRuntime.LogSource?.LogWarning($"GlbModelRuntime.BuildGlbMaterial: shader not found, tint=({tint.r:0.0},{tint.g:0.0},{tint.b:0.0}) tex={tex != null}");
        return mat ?? MakeUnlit(tint, null);
    }

    private static Material MakeUnlit(Color tint, Texture2D map)
    {
        try
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            var mat = new Material(shader);
            // Cull Off：模型用 glTF 原始绕序（右手系），Unity 背面剔除可能反 → 双面渲染避免"穿模/缺面"
            try { mat.SetInt("_Cull", 0); } catch { }
            if (map != null) { mat.mainTexture = map; mat.SetTexture("_BaseMap", map); }
            mat.color = tint;
            mat.SetColor("_BaseColor", tint);
            return mat;
        }
        catch { return null; }
    }
}
