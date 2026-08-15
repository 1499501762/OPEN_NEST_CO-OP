#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif
using System;
using UnityEngine;

using OpenNestCoop.Core;
namespace OpenNestCoop.GameSync;

/// <summary>
/// 程序化人形角色视觉提供者：用基础几何体（胶囊/球/方块）拼出普通成人形骨架
/// （头 + 躯干 + 双臂 + 双腿），程序化驱动行走/待机动画。零外部模型/动画资源，
/// 不增加包体；完全在 IPlayerVisualProvider 框架内实现，其他模组仍可注册自定义模型覆盖。
///
/// 动画：
/// - Idle：轻微呼吸（躯干/头上下微动）
/// - Moving：摆臂 + 抬腿走姿，速度驱动频率/幅度；躯干轻微前倾
/// 朝向由 pose.Yaw 驱动（body +Z 朝 pose.Yaw，玩家面朝方向）。
/// 头部附加游戏内防毒面罩（gasmask mesh，尽力而为）+ 3D 名字标签（billboard）。
/// </summary>
public sealed class HumanoidVisualProvider : IPlayerVisualProvider
{
    public static readonly HumanoidVisualProvider Instance = new();

    // ---- 骨骼命名（供子类/动画定位） ----
    internal const string K_Hips = "Hips";
    internal const string K_Spine = "Spine";
    internal const string K_Chest = "Chest";
    internal const string K_Head = "Head";
    internal const string K_ArmL = "ArmL";
    internal const string K_ForearmL = "ForearmL";
    internal const string K_ArmR = "ArmR";
    internal const string K_ForearmR = "ForearmR";
    internal const string K_ThighL = "ThighL";
    internal const string K_ShinL = "ShinL";
    internal const string K_ThighR = "ThighR";
    internal const string K_ShinR = "ShinR";

    // ---- 人体尺寸（米，成人 ~1.72m） ----
    private const float HipsY = 0.98f;        // 髋部高度
    private const float SpineY = 1.28f;       // 脊柱顶部（胸下）
    private const float ChestY = 1.44f;       // 胸腔中心
    private const float ShoulderY = 1.52f;    // 肩部高度
    private const float HeadY = 1.66f;        // 头中心
    private const float ArmLen = 0.62f;       // 单臂总长（肩→手）
    private const float LegLen = 0.98f;       // 单腿总长（髋→足）
    private const float UpperArm = 0.30f;
    private const float Forearm = 0.32f;
    private const float Thigh = 0.48f;
    private const float Shin = 0.50f;

    private Mesh _maskMesh;
    private bool _maskTried;
    private Texture2D _maskTexture;
    private bool _textureTried;

    // ---- 动画平滑状态（避免网络瞬时速度抖动导致步频抽搐） ----
    private float _animSpeed;   // 内部平滑速度
    private float _walkPhase;   // 累积步态相位（弧度，连续积分不跳变）

    // ---- 姿态混合（每根骨头平滑过渡，避免状态切换突跳） ----
    private bool _poseInit;
    private Quaternion _qThighL, _qThighR, _qShinL, _qShinR;
    private Quaternion _qArmL, _qArmR, _qForeL, _qForeR;
    private Quaternion _qSpine, _qChest, _qHead;
    private float _hipY;        // 平滑后的髋部高度（蹲下压低）
    private float _hipX;        // 平滑后的髋部横向偏移（蹲走重心摆动）

    public GameObject Create(Transform root, string playerName, Color tint)
    {
        var body = new GameObject("Body");
        body.transform.SetParent(root, false);
        body.transform.localRotation = Quaternion.identity;

        // ---- 材质 ----
        var mainMat = MakeUnlit(tint, null);            // 服装/身体主色
        var darkMat = MakeUnlit(TintDarken(tint), null); // 四肢/靴子深色

        // ---- 骨架层级 ----
        var hips = Bone(body.transform, K_Hips, new Vector3(0f, HipsY, 0f), Vector3.one, mainMat);
        var spine = Bone(hips, K_Spine, new Vector3(0f, SpineY - HipsY, 0f), Vector3.one, mainMat);
        var chest = Bone(spine, K_Chest, new Vector3(0f, ChestY - SpineY, 0f), Vector3.one, mainMat);

        // 头（球）
        var head = new GameObject(K_Head);
        head.transform.SetParent(chest, false);
        head.transform.localPosition = new Vector3(0f, HeadY - ChestY, 0f);
        head.transform.localRotation = Quaternion.identity;
        var skull = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        skull.name = "Skull";
        var skullCol = skull.GetComponent<Collider>();
        if (skullCol != null) UnityEngine.Object.Destroy(skullCol);
        skull.transform.SetParent(head.transform, false);
        skull.transform.localScale = Vector3.one * 0.24f;
        var skullMr = skull.GetComponent<MeshRenderer>();
        if (skullMr != null) skullMr.sharedMaterial = darkMat;

        // 躯干（胸腔：胶囊体，略粗）
        var chestMesh = LimbMesh(chest, "ChestMesh", new Vector3(0f, -0.05f, 0f),
            Vector3.one * 0.38f, 0.30f, mainMat);

        // ---- 双臂 ----
        // 左臂：肩→肘→手（+X 是玩家左侧，朝 pose.Yaw 时 +Z 是前方）
        var armL = Bone(chest, K_ArmL, new Vector3(-0.24f, -0.06f, 0f), Vector3.one, mainMat);
        var upperL = LimbMesh(armL, "UpperL", new Vector3(0f, -UpperArm * 0.5f, 0f),
            Vector3.one * 0.13f, UpperArm, darkMat);
        var forearmL = Bone(armL, K_ForearmL, new Vector3(0f, -UpperArm, 0f), Vector3.one, darkMat);
        LimbMesh(forearmL, "ForeL", new Vector3(0f, -Forearm * 0.5f, 0f),
            Vector3.one * 0.11f, Forearm, mainMat);
        Hand(forearmL, "HandL", new Vector3(0f, -Forearm, 0f));

        // 右臂
        var armR = Bone(chest, K_ArmR, new Vector3(0.24f, -0.06f, 0f), Vector3.one, mainMat);
        var upperR = LimbMesh(armR, "UpperR", new Vector3(0f, -UpperArm * 0.5f, 0f),
            Vector3.one * 0.13f, UpperArm, darkMat);
        var forearmR = Bone(armR, K_ForearmR, new Vector3(0f, -UpperArm, 0f), Vector3.one, darkMat);
        LimbMesh(forearmR, "ForeR", new Vector3(0f, -Forearm * 0.5f, 0f),
            Vector3.one * 0.11f, Forearm, mainMat);
        Hand(forearmR, "HandR", new Vector3(0f, -Forearm, 0f));

        // ---- 双腿 ----
        var thighL = Bone(hips, K_ThighL, new Vector3(-0.12f, -0.04f, 0f), Vector3.one, darkMat);
        LimbMesh(thighL, "ThighL", new Vector3(0f, -Thigh * 0.5f, 0f),
            Vector3.one * 0.15f, Thigh, darkMat);
        var shinL = Bone(thighL, K_ShinL, new Vector3(0f, -Thigh, 0f), Vector3.one, darkMat);
        LimbMesh(shinL, "ShinL", new Vector3(0f, -Shin * 0.5f, 0f),
            Vector3.one * 0.13f, Shin, darkMat);
        Foot(shinL, "FootL", new Vector3(0f, -Shin, 0.04f));

        var thighR = Bone(hips, K_ThighR, new Vector3(0.12f, -0.04f, 0f), Vector3.one, darkMat);
        LimbMesh(thighR, "ThighR", new Vector3(0f, -Thigh * 0.5f, 0f),
            Vector3.one * 0.15f, Thigh, darkMat);
        var shinR = Bone(thighR, K_ShinR, new Vector3(0f, -Thigh, 0f), Vector3.one, darkMat);
        LimbMesh(shinR, "ShinR", new Vector3(0f, -Shin * 0.5f, 0f),
            Vector3.one * 0.13f, Shin, darkMat);
        Foot(shinR, "FootR", new Vector3(0f, -Shin, 0.04f));

        // 名字标签（3D TMP，独立 billboard）
        AddNameTag(body.transform, playerName);

        return body;
    }

    public void Update(GameObject visual, float dt, ref AvatarPose pose)
    {
        if (visual == null) return;
        // 化身朝向 = 玩家朝向
        visual.transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f);

        // 程序化动画（先摆姿态——腿部姿态决定脚的位置）
        Animate(visual.transform, dt, ref pose);

        // 脚贴地：非空中时统一按"实际脚底位置"射线找地面，校正 body 到脚着地（脚不穿地）。
        // 蹲姿只要腿角度设计正确（跪腿膝盖着地、竖腿脚掌着地，两脚都贴近地面），
        // GroundBody 取较低脚贴地时不会把 body 抬太高，hip 自然保持低位。
        if (!pose.Airborne)
            GroundBody(visual);
        // 空中：body 保留当前 localPosition（随 root 升降，跳跃高度正确）

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

    /// <summary>脚贴地：取两只脚中较低的一只（着地脚）作为参考，从 root 向下射线找地面，
    /// 把 body 高度校正到让着地脚刚好贴地。脚陷多深都能找到地面并抬回。</summary>
    private static void GroundBody(GameObject visual)
    {
        try
        {
            // 地面高度：从 root（玩家位置）略上方向下射线，无论脚陷多深都能打到地面
            var root = visual.transform.parent;
            if (root == null) return;
            Vector3 origin = root.position + Vector3.up * 0.2f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 6f)) return;
            float groundY = hit.point.y;

            // 取较低的脚（着地的那只，y 最小）作为贴地参考
            var footL = Find(visual.transform, "FootL");
            var footR = Find(visual.transform, "FootR");
            float footY = float.PositiveInfinity;
            if (footL != null) footY = Mathf.Min(footY, footL.position.y);
            if (footR != null) footY = Mathf.Min(footY, footR.position.y);
            if (float.IsInfinity(footY)) return;

            // body 需要移动的量 = 地面 - 当前着地脚位置（脚在 body 子层级，body 移多少脚移多少）
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

    // ---------------- 程序化动画 ----------------

    private void Animate(Transform body, float dt, ref AvatarPose pose)
    {
        // 内部速度平滑：网络瞬时速度会抖动，直接驱动会让步频/幅度突变
        float targetSpeed = Mathf.Max(0f, pose.Speed);
        _animSpeed = Mathf.Lerp(_animSpeed, targetSpeed, 0.15f);
        float speed = _animSpeed;

        var hips = Find(body, K_Hips);
        var spine = Find(body, K_Spine);
        var chest = Find(body, K_Chest);
        var head = Find(body, K_Head);
        var armL = Find(body, K_ArmL); var foreL = Find(body, K_ForearmL);
        var armR = Find(body, K_ArmR); var foreR = Find(body, K_ForearmR);
        var thighL = Find(body, K_ThighL); var shinL = Find(body, K_ShinL);
        var thighR = Find(body, K_ThighR); var shinR = Find(body, K_ShinR);
        if (hips == null || chest == null) return;

        bool moving = speed > 0.05f;
        // 摆幅 = 基础值 + 速度加成（不再纯粹 ∝ speed，避免低速时幅度过小、步态发虚）
        float amp = 0.32f + Mathf.Min(speed, 3f) * 0.12f;   // 0.32 ~ 0.68

        // ---- 目标姿态（Quaternion 目标，之后统一平滑混合） ----
        Quaternion tThighL, tThighR, tShinL, tShinR;
        Quaternion tArmL, tArmR, tForeL, tForeR;
        Quaternion tSpine, tChest, tHead;
        float tHipY = HipsY;
        float tHipX = 0f;   // 蹲走重心摆动（默认 0）

        // 头部俯仰（抬头为正）：FPC.pitch 正=抬头，head 绕 X 正角=抬头 → 直接用 +pitch
        float headPitch = Mathf.Clamp(pose.Pitch, -60f, 60f);

        // 移动方向（本地空间）：用于把迈步分量分配到 前后(绕X)/横移(绕Z)
        float fwdAmt = Mathf.Clamp(pose.MoveFwd, -1f, 1f);
        float strAmt = Mathf.Clamp(pose.MoveStrafe, -1f, 1f);
        float moveLen = Mathf.Sqrt(fwdAmt * fwdAmt + strAmt * strAmt);
        float cosA = moveLen > 0.001f ? fwdAmt / moveLen : 1f;  // 前后权重
        float sinA = moveLen > 0.001f ? strAmt / moveLen : 0f;  // 横移权重

        if (pose.Airborne)
        {
            // ---- 跳跃/空中：收腿（膝盖向前上）+ 小腿向后折 + 举臂平衡 ----
            // 大腿绕 X 负角 = 膝盖向前上抬；小腿绕 X 正角 = 相对大腿向后折（屈膝收腿）
            tThighL = tThighR = Quaternion.Euler(-35f, 0f, 0f);
            tShinL = tShinR = Quaternion.Euler(45f, 0f, 0f);
            tArmL = Quaternion.Euler(-55f, 0f, -12f);
            tArmR = Quaternion.Euler(-55f, 0f, 12f);
            tForeL = tForeR = Quaternion.Euler(-35f, 0f, 0f);
            tSpine = Quaternion.Euler(-6f, 0f, 0f);
            tChest = Quaternion.identity;
            tHead = Quaternion.Euler(headPitch, 0f, 0f);
            tHipY = HipsY * 0.96f;
        }
        else if (pose.Crouched)
        {
            // ---- 单膝半跪蹲走（half-kneel walk）：一只膝盖跪地、另一只脚掌着地，交替切换 ----
            // 几何关键：两条腿的【大腿角度不同】形成一高一低的对比——
            //   - 跪腿：大腿向前压低（thigh -40°，膝盖贴地），小腿向后平放贴地（shin +130°）
            //   - 竖腿：大腿向前【抬高前伸】（thigh -80°，膝盖在身体前方较高位置），
            //           小腿相对折回垂直（shin +80°，脚掌在膝盖正下方着地）
            // 两脚都在地面附近 → GroundBody 贴地时不会把 body 抬高，hip 自然保持低位
            float thighKneel = -40f;   // 跪腿：大腿压低（膝盖贴地）
            float thighUp = -80f;      // 竖腿：大腿抬高前伸（膝盖在前方较高）
            float shinKneel = 130f;    // 跪腿：小腿向后平放贴地
            float shinUp = 80f;        // 竖腿：小腿垂直垂下（脚掌着地）
            float kneeIn = 6f;         // 膝盖轻微内收并拢
            if (moving)
            {
                const float StrideLen = 0.9f;    // 蹲走步幅（米）
                float freqC = Mathf.Clamp(speed / StrideLen, 0.5f, 1.1f);  // 蹲走步频慢
                _walkPhase += freqC * Mathf.PI * 2f * dt;
                float sinL = Mathf.Sin(_walkPhase);
                float sinR = Mathf.Sin(_walkPhase + Mathf.PI);
                // 交替切换：左腿竖=1（右腿跪），左腿跪=0（右腿竖）
                float fL = Mathf.Clamp01((sinL + 1f) * 0.5f);
                float fR = 1f - fL;
                float zL = -sinL * 4f * sinA;
                float zR = -sinR * 4f * sinA;
                tThighL = Quaternion.Euler(Mathf.Lerp(thighKneel, thighUp, fL), 0f, kneeIn + zL);
                tThighR = Quaternion.Euler(Mathf.Lerp(thighKneel, thighUp, fR), 0f, -kneeIn + zR);
                tShinL = Quaternion.Euler(Mathf.Lerp(shinKneel, shinUp, fL), 0f, 0f);
                tShinR = Quaternion.Euler(Mathf.Lerp(shinKneel, shinUp, fR), 0f, 0f);
                // 手臂：半跪蹲走手自然垂在身前，随脚步轻微摆动
                tArmL = Quaternion.Euler(-35f, 0f, 10f + sinR * 6f);
                tArmR = Quaternion.Euler(-35f, 0f, -10f - sinL * 6f);
                tForeL = tForeR = Quaternion.Euler(-15f, 0f, 0f);
                // hip 高度：半跪中间高度，随切换轻微起伏；重心随竖腿左右轻摆
                float hipX = (fL - 0.5f) * 0.10f;   // 左腿竖→重心偏左，反之偏右
                tHipX = hipX;
                tHipY = HipsY * (0.38f + Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.03f);
            }
            else
            {
                // 半跪蹲待机：左腿跪地（膝盖贴地、小腿平放）、右腿竖立（膝盖抬高前伸、脚掌着地）
                tHipY = HipsY * 0.38f;
                tThighL = Quaternion.Euler(thighKneel, 0f, kneeIn);   // 跪腿大腿压低（膝盖贴地）
                tThighR = Quaternion.Euler(thighUp, 0f, -kneeIn);     // 竖腿大腿抬高前伸（膝盖在前方较高）
                tShinL = Quaternion.Euler(shinKneel, 0f, 0f);         // 跪腿小腿贴地
                tShinR = Quaternion.Euler(shinUp, 0f, 0f);            // 竖腿小腿垂直垂下（脚掌着地）
                tArmL = Quaternion.Euler(-35f, 0f, 10f);
                tArmR = Quaternion.Euler(-35f, 0f, -10f);
                tForeL = tForeR = Quaternion.Euler(-15f, 0f, 0f);
            }
            tSpine = Quaternion.Euler(28f, 0f, 0f);   // 弓背前倾（半跪探身）
            tChest = Quaternion.identity;
            tHead = Quaternion.Euler(-6f + headPitch, 0f, 0f);   // 蹲时头微低，跟随俯仰
        }
        else if (moving)
        {
            // ---- 走路/奔跑：位移积分驱动步态，步频有上限 ----
            bool sprint = pose.Sprinting;
            // 步幅（米）：走路 1.40，奔跑 1.20（步幅更密、步频更高）
            float StrideLen = sprint ? 1.20f : 1.40f;
            // 步频 = speed/步幅，限制上限（走 1.8，跑 2.4 步/秒）
            float freq = Mathf.Clamp(speed / StrideLen, 0.8f, sprint ? 2.4f : 1.8f);
            _walkPhase += freq * Mathf.PI * 2f * dt;
            float sinL = Mathf.Sin(_walkPhase);
            float sinR = Mathf.Sin(_walkPhase + Mathf.PI);
            float ampRun = sprint ? 1.25f : 1.0f;   // 奔跑摆幅更大
            // 小腿屈膝：走路 60°（明显抬脚跟），奔跑 42°（冲刺姿态膝盖弯曲更小，像上一版）
            float shinKnee = sprint ? 42f : 60f;

            // 腿：
            // - 前后分量绕 X 抬腿（cosA 加权）
            // - 横移分量用"整流侧步"——两腿在各自正半周交替向移动方向伸出（sinA 符号定方向），
            //   避免两腿向两侧张开（劈叉/开合步态）。纯横移时 sinR=-sinL，max(0,sinL) 与 max(0,sinR) 反相 → 左右脚交替侧步。
            float xL = sinL * amp * 72f * cosA * ampRun;
            float xR = sinR * amp * 72f * cosA * ampRun;
            float zL = Mathf.Max(0f, sinL) * amp * 30f * sinA;
            float zR = Mathf.Max(0f, sinR) * amp * 30f * sinA;
            tThighL = Quaternion.Euler(xL, 0f, zL);
            tThighR = Quaternion.Euler(xR, 0f, zR);
            // 小腿：随大腿前摆屈膝（脚跟抬起）、后摆伸直。用 -xL（已含 cosA 符号）判断前摆，
            // 保证前进/后退膝盖都只向前弯（不会反向弯折）。
            float shinAngL = Mathf.Max(0f, -xL) * (shinKnee / 72f);
            float shinAngR = Mathf.Max(0f, -xR) * (shinKnee / 72f);
            tShinL = Quaternion.Euler(shinAngL, 0f, 0f);
            tShinR = Quaternion.Euler(shinAngR, 0f, 0f);
            // 手臂：对侧摆臂——左臂跟右腿（sinR）、右臂跟左腿（sinL），避免同手同脚；
            // 横移时跟随侧步小幅摆动
            tArmL = Quaternion.Euler(sinR * amp * 55f * cosA * ampRun, 0f, Mathf.Max(0f, sinR) * amp * 22f * sinA);
            tArmR = Quaternion.Euler(sinL * amp * 55f * cosA * ampRun, 0f, Mathf.Max(0f, sinL) * amp * 22f * sinA);
            tForeL = Quaternion.Euler(-32f - sinR * amp * 22f * ampRun, 0f, 0f);
            tForeR = Quaternion.Euler(-32f - sinL * amp * 22f * ampRun, 0f, 0f);
            // 躯干：走路轻微前倾，奔跑前倾更大（加速姿态）
            tSpine = Quaternion.Euler(sprint ? 18f : 8f, 0f, sinL * 2f);
            tChest = Quaternion.identity;
            tHead = Quaternion.Euler(headPitch - (sprint ? 4f : 0f), 0f, 0f);
            tHipY = HipsY + Mathf.Abs(Mathf.Sin(_walkPhase)) * amp * 0.07f * (sprint ? 1.2f : 1f);
        }
        else
        {
            // ---- 待机：呼吸 + 手臂自然下垂 + 头跟随俯仰 ----
            _walkPhase = Mathf.Lerp(_walkPhase, 0f, 0.1f);
            float time = Time.realtimeSinceStartup;
            float breathe = Mathf.Sin(time * 1.6f) * 0.02f;
            tHipY = HipsY + breathe;
            tThighL = tThighR = Quaternion.identity;
            tShinL = tShinR = Quaternion.identity;
            tArmL = Quaternion.Euler(0f, 0f, 6f);
            tArmR = Quaternion.Euler(0f, 0f, -6f);
            tForeL = Quaternion.Euler(0f, 0f, -8f);
            tForeR = Quaternion.Euler(0f, 0f, 8f);
            tSpine = Quaternion.identity;
            tChest = Quaternion.Euler(Mathf.Sin(time * 1.6f) * 0.6f, 0f, 0f);
            tHead = Quaternion.Euler(headPitch, 0f, 0f);
        }

        // ---- 姿态平滑混合（避免状态切换突跳） ----
        float blend = _poseInit ? Mathf.Clamp01(1f - Mathf.Exp(-10f * dt)) : 1f;
        _poseInit = true;
        _qThighL = Quaternion.Slerp(_qThighL, tThighL, blend);
        _qThighR = Quaternion.Slerp(_qThighR, tThighR, blend);
        _qShinL = Quaternion.Slerp(_qShinL, tShinL, blend);
        _qShinR = Quaternion.Slerp(_qShinR, tShinR, blend);
        _qArmL = Quaternion.Slerp(_qArmL, tArmL, blend);
        _qArmR = Quaternion.Slerp(_qArmR, tArmR, blend);
        _qForeL = Quaternion.Slerp(_qForeL, tForeL, blend);
        _qForeR = Quaternion.Slerp(_qForeR, tForeR, blend);
        _qSpine = Quaternion.Slerp(_qSpine, tSpine, blend);
        _qChest = Quaternion.Slerp(_qChest, tChest, blend);
        _qHead = Quaternion.Slerp(_qHead, tHead, blend);
        _hipY = Mathf.Lerp(_hipY, tHipY, blend);
        _hipX = Mathf.Lerp(_hipX, tHipX, blend);

        // ---- 应用到骨骼 ----
        if (thighL != null) thighL.localRotation = _qThighL;
        if (thighR != null) thighR.localRotation = _qThighR;
        if (shinL != null) shinL.localRotation = _qShinL;
        if (shinR != null) shinR.localRotation = _qShinR;
        if (armL != null) armL.localRotation = _qArmL;
        if (armR != null) armR.localRotation = _qArmR;
        if (foreL != null) foreL.localRotation = _qForeL;
        if (foreR != null) foreR.localRotation = _qForeR;
        if (spine != null) spine.localRotation = _qSpine;
        if (chest != null) chest.localRotation = _qChest;
        if (head != null) head.localRotation = _qHead;
        if (hips != null)
        {
            var hp = hips.localPosition;
            hp.y = _hipY;
            hp.x = _hipX;
            hips.localPosition = hp;
        }
    }

    private static Transform Find(Transform root, string name)
    {
        if (root == null) return null;
        try
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null) continue;
                if (c.name == name) return c;
                var r = Find(c, name);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    // ---------------- 构建辅助 ----------------

    /// <summary>创建骨骼节点（空 Transform，含可选碰撞/渲染），父级为 parent。</summary>
    private static Transform Bone(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        if (mat != null) go.transform.localScale = scale;
        return go.transform;
    }

    /// <summary>创建一段肢体（胶囊体），沿父级 +Y 向下延伸 length。</summary>
    private static Transform LimbMesh(Transform parent, string name, Vector3 localPos, Vector3 radiusScale, float length, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.Destroy(col);
        go.transform.SetParent(parent, false);
        // 胶囊体默认高 2（Y 向），缩放到 length
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(radiusScale.x, length / 2f, radiusScale.z);
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null && mat != null) mr.sharedMaterial = mat;
        return go.transform;
    }

    private static void Hand(Transform parent, string name, Vector3 localPos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.Destroy(col);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = Vector3.one * 0.09f;
    }

    private static void Foot(Transform parent, string name, Vector3 localPos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.Destroy(col);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = new Vector3(0.10f, 0.05f, 0.22f);
    }

    private void AttachGasmask(Transform head, Color tint)
    {
        var maskMesh = FindGasmaskMesh();
        if (maskMesh == null) return;
        try
        {
            var mask = new GameObject("Gasmask");
            mask.transform.SetParent(head, false);
            mask.transform.localPosition = new Vector3(0f, 0f, 0.13f);
            mask.transform.localRotation = Quaternion.identity;
            mask.transform.localScale = Vector3.one * 1.05f;
            var mf = mask.AddComponent<MeshFilter>();
            mf.mesh = maskMesh;
            var mmr = mask.AddComponent<MeshRenderer>();
            var mmat = MakeUnlit(tint, FindGasmaskTexture());
            if (mmat != null) mmr.sharedMaterial = mmat;
        }
        catch { }
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
            CoopRuntime.LogSource?.LogWarning("HumanoidVisual: 'gasmask' head mesh not found, head only");
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

    private static Color TintDarken(Color c)
    {
        Color.RGBToHSV(c, out var h, out var s, out var v);
        return Color.HSVToRGB(h, s, Mathf.Clamp01(v * 0.55f));
    }

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
            go.transform.localPosition = new Vector3(0f, 1.9f, 0f);
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
