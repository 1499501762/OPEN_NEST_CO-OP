using System;
using System.IO;

namespace OpenNestCoop.GameSync;

/// <summary>
/// 外部模型适配配置（模型"接口"层）：不同模型的坐标系/单位/朝向千差万别，
/// 通过一个与模型同目录的 .cfg 文件（如 Models\soldier.cfg）声明如何把它
/// 摆正、缩放到目标尺寸。换模型只需换配置，无需改代码。
///
/// 配置格式（key=value 文本，# 注释）：
///   targetHeight=1.8    目标身高（单位/米，默认 1.8，按玩家比例）
///   rotateX=-90         欧拉角（度），把模型坐标系转到 Unity Y-up 站立
///   rotateY=0
///   rotateZ=0
///   scale=0             统一缩放（0=自动按 targetHeight 计算）
///   offsetX=0           横向偏移（0=自动按包围盒 X 居中）
///   offsetZ=0           纵向偏移（0=自动按包围盒 Z 居中）
///   offsetY=0           高度偏移（0=自动把脚底对齐到 y=0；>0 额外抬高）
///
/// 不提供配置文件时使用默认值（targetHeight=1.8, rotateX=-90 等）。
/// </summary>
public sealed class ModelFitConfig
{
    /// <summary>目标身高（单位）。加载后模型脚底在 y=0、顶在 y=targetHeight。</summary>
    public float TargetHeight = 1.8f;

    /// <summary>旋转欧拉角（度，Unity 顺序 ZYX）。默认绕 X +90°：把 Z-up 躺倒模型立起来。
    /// 注意：glTF 加载时已翻转 Z（左手系），+90 才是立正，-90 会倒立。</summary>
    public float RotateX = 90f;
    public float RotateY = 0f;
    public float RotateZ = 0f;

    /// <summary>统一缩放。0=自动（用 targetHeight / 模型实际身高）。</summary>
    public float Scale = 0f;

    /// <summary>位置偏移（缩放/脚底对齐之后追加）。0=自动居中。</summary>
    public float OffsetX = 0f;
    public float OffsetY = 0f;
    public float OffsetZ = 0f;

    /// <summary>T-pose 手臂校准（度，绕骨骼局部 Z）：把水平手臂转到自然下垂。左右相反。</summary>
    public float ArmCalibLZ = -90f;
    public float ArmCalibRZ = 90f;

    /// <summary>从与模型同目录的 .cfg 加载；找不到返回默认配置。解析失败时保留默认值。</summary>
    public static ModelFitConfig Load(string modelPath)
    {
        var cfg = new ModelFitConfig();
        try
        {
            if (string.IsNullOrEmpty(modelPath)) return cfg;
            var dir = Path.GetDirectoryName(modelPath) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(modelPath);
            var candidates = new[]
            {
                Path.Combine(dir, baseName + ".cfg"),
                Path.Combine(dir, "model.cfg"),
            };
            string path = null;
            foreach (var c in candidates)
                if (File.Exists(c)) { path = c; break; }
            if (path == null) return cfg;

            foreach (var raw in File.ReadLines(path))
            {
                if (raw == null) continue;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                if (!float.TryParse(line.Substring(eq + 1).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val))
                    continue;
                switch (key)
                {
                    case "targetheight": cfg.TargetHeight = val; break;
                    case "rotatex": cfg.RotateX = val; break;
                    case "rotatey": cfg.RotateY = val; break;
                    case "rotatez": cfg.RotateZ = val; break;
                    case "scale": cfg.Scale = val; break;
                    case "offsetx": cfg.OffsetX = val; break;
                    case "offsety": cfg.OffsetY = val; break;
                    case "offsetz": cfg.OffsetZ = val; break;
                    case "armcaliblz": cfg.ArmCalibLZ = val; break;
                    case "armcalibz": cfg.ArmCalibRZ = val; break;
                    case "armcalibrz": cfg.ArmCalibRZ = val; break;
                }
            }
        }
        catch { }
        return cfg;
    }
}
