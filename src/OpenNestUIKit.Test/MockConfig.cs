using System;
using System.IO;
using System.Text;

namespace OpenNestUIKit.Test;

/// <summary>
/// **模拟配置文件**（用户 2026-09-13：“给测试模组做个模拟配置文件，看看自动生成的配置控件够不够好用”）。
///
/// 为什么需要它：模组菜单的「设置」页会把一个模组的配置文件**逐项自动渲染成控件**
/// （开关 / 滑条 / 枚举 / 快捷键 / 文本 / 只读；类型来自 `## Setting type:` 注释，没有就按值推断）。
/// 机器上真实模组的配置文件五花八门，没法拿来做"每种类型都覆盖"的验收 ⇒
/// 这里给**测试模组自己**写一份覆盖全类型的 `open.nest.uikit.test.cfg`。
///
/// 定位方式：BepInEx 惯例 `<GUID>.cfg`（本模组 GUID = `open.nest.uikit.test`）⇒ ModMenu 靠加载器元数据
/// 直接命中，不需要任何映射；MLL 端则放到 `UserData\` 下（同惯例）。
///
/// 用法：
/// - 启动时**只补不覆盖**（文件在就什么都不做，用户改过的值不会被冲掉）；
/// - CLI：`mockcfg`（缺失才写）/ `mockcfg!`（强制重建，回归用）。
/// 然后在模组菜单里选「Open Nest UIKit Test」→「设置」页验收控件。
/// </summary>
public static class MockConfig
{
    /// <summary>文件名（= BepInEx 的 `&lt;GUID&gt;.cfg` 惯例）。</summary>
    public const string FileName = TestInfo.Guid + ".cfg";

    /// <summary>配置目录（优先 BepInEx 的 config；MLL 原生端退 `UserData`）。</summary>
    public static string Dir
    {
        get
        {
            try
            {
                var bep = Core.UiKitPaths.BepInExConfigDir;
                if (!string.IsNullOrEmpty(bep) && Directory.Exists(bep)) return bep;
                var user = Core.UiKitPaths.MelonUserDataDir;
                if (!string.IsNullOrEmpty(user) && Directory.Exists(user)) return user;
                if (!string.IsNullOrEmpty(bep)) return bep;
            }
            catch { }
            return "";
        }
    }

    /// <summary>完整路径。</summary>
    public static string Path => string.IsNullOrEmpty(Dir) ? "" : System.IO.Path.Combine(Dir, FileName);

    /// <summary>该路径上有没有模拟配置（诊断串用）。</summary>
    public static string Describe()
    {
        try
        {
            string p = Path;
            if (string.IsNullOrEmpty(p)) return "配置目录未知";
            if (!File.Exists(p)) return "缺失（跑 `mockcfg` 生成）：" + p;
            return $"{p}（{new FileInfo(p).Length} 字节，{File.ReadAllLines(p, Encoding.UTF8).Length} 行）";
        }
        catch (Exception ex) { return "读取失败：" + ex.Message; }
    }

    /// <summary>写文件（<paramref name="force"/> = false 时**只在缺失时**写，不覆盖用户改过的值）。</summary>
    public static bool Ensure(bool force)
    {
        try
        {
            string p = Path;
            if (string.IsNullOrEmpty(p)) { TestLog.Fail("mockcfg", "配置目录未知"); return false; }
            if (!force && File.Exists(p)) { TestLog.Pass("mockcfg", "已存在（未覆盖）：" + Describe()); return true; }

            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, Template, new UTF8Encoding(false));      // UTF-8 无 BOM（与本仓其它配置一致）
            TestLog.Pass("mockcfg", (force ? "已重建：" : "已生成：") + Describe());
            return true;
        }
        catch (Exception ex)
        {
            TestLog.Fail("mockcfg", "写入失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 模拟配置内容：**每种控件类型各至少一项**，并故意混入"没有类型声明"的项
    /// （验证按值推断这条路），以及带 `范围` 的数值项（验证滑条边界与步进推断）。
    /// </summary>
    public const string Template =
        "## Open Nest UIKit Test —— 模拟配置文件（验收自动生成的配置控件）\n" +
        "## 由测试模组生成；删除它 + 重启（或跑 `mockcfg!`）即可重建。\n" +
        "## 类型来自 `## Setting type:` 注释；没有声明的按值推断。\n" +
        "\n" +
        "[General]\n" +
        "\n" +
        "## 总开关：关掉之后下面那些功能都不生效（验证：开关控件）\n" +
        "## Setting type: System.Boolean\n" +
        "## Default value: true\n" +
        "Enabled = true\n" +
        "\n" +
        "## 采样次数：整数 + 范围（验证：滑条 ± 数值文本 + 边界钳制）\n" +
        "## Setting type: System.Int32\n" +
        "## Default value: 24\n" +
        "## Acceptable value range: 0 to 120\n" +
        "Samples = 24\n" +
        "\n" +
        "## 音量：小数（步进按小数位推断 = 0.05，验证：小数显示位数）\n" +
        "## Setting type: System.Single\n" +
        "## Default value: 0.65\n" +
        "## Acceptable value range: 0 to 1\n" +
        "Volume = 0.65\n" +
        "\n" +
        "## 画质档位（验证：枚举 → 循环选择；中文选项也要能显示）\n" +
        "## Setting type: System.String\n" +
        "## Default value: High\n" +
        "## Acceptable values: Low, Medium, High, 极高\n" +
        "Quality = High\n" +
        "\n" +
        "## 开火键（验证：快捷键捕捉行）\n" +
        "## Setting type: KeyboardShortcut\n" +
        "## Default value: F\n" +
        "KeyFire = F\n" +
        "\n" +
        "## 服务器地址（验证：文本输入行 + 长文本不折行）\n" +
        "## Setting type: System.String\n" +
        "## Default value: 127.0.0.1:27600\n" +
        "Server = 127.0.0.1:27600\n" +
        "\n" +
        "## 空值文本项（验证：空文本要画成**可输入的空框**而不是暗掉的一行）\n" +
        "## Setting type: System.String\n" +
        "## Default value: \n" +
        "Nickname = \n" +
        "\n" +
        "## 只读项（验证：声明 ReadOnly 才真只读，不画输入框）\n" +
        "## Setting type: ReadOnly\n" +
        "## Default value: 不可编辑\n" +
        "Notes = 这是只读文本项（声明了 ReadOnly）\n" +
        "\n" +
        "## 没有类型声明：按值推断成开关\n" +
        "## Default value: false\n" +
        "LegacyToggle = false\n" +
        "\n" +
        "## 没有类型声明：按值推断成数值（无范围 → 默认 0..1？验证一下边界处理）\n" +
        "LegacyNumber = 128\n" +
        "\n" +
        "[Advanced]\n" +
        "\n" +
        "## 分节标题应该显式出现（验证：多 [Section] 的分组渲染）\n" +
        "## Setting type: System.Boolean\n" +
        "## Default value: false\n" +
        "VerboseLog = false\n" +
        "\n" +
        "## 项目风格元数据（键**之后**写注释，也要能解析）\n" +
        "Retry = 3\n" +
        "## 类型: 数值 | 默认: 3 | 范围: 0..10\n";
}
