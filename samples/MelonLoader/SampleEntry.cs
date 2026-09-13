using MelonLoader;
using OpenNestUIKit.API;

// Assembly attributes must live outside the namespace, so the sample types are fully qualified.
[assembly: MelonInfo(typeof(OpenNestUIKit.Sample.MelonLoader.SampleEntry),
                     "UIKit Sample Mod", "1.0.0", "OpenNestUIKit")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace OpenNestUIKit.Sample.MelonLoader;

/// <summary>
/// MelonLoader entry point of the sample mod — the MelonLoader twin of
/// <c>samples/BepInEx/SamplePlugin.cs</c>.
///
/// Note that <see cref="SampleMenu"/> itself is **unchanged**: the provider is plain .NET, so one
/// source file serves both loaders. Only this shell differs.
/// </summary>
public sealed class SampleEntry : MelonMod
{
    private SampleMenu _menu;

    public override void OnInitializeMelon()
    {
        var cfg = new SampleConfig();
        _menu = new SampleMenu(cfg);

        UiKitHost.Register(_menu);
        MelonLogger.Msg($"registered '{_menu.Id}' (host available: {UiKitHost.IsHostAvailable})");
    }

    public override void OnApplicationQuit()
    {
        try { UiKitHost.Unregister(_menu); } catch { /* unload path must not throw */ }
    }
}
