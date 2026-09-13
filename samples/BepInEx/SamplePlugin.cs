using BepInEx;
using BepInEx.Unity.IL2CPP;
using OpenNestUIKit.API;
using OpenNestUIKit.Sample;

namespace OpenNestUIKit.Sample.BepInEx;

/// <summary>
/// BepInEx entry point of the sample mod.
///
/// The whole job is: build the provider, register it, unregister it on unload. There is no
/// reference to Open Nest UIKit here — only to the contract assembly — so the mod loads (and does
/// nothing) when the UI library is not installed.
/// </summary>
[BepInPlugin("open.nest.uikit.sample", "UIKit Sample Mod", "1.0.0")]
public sealed class SamplePlugin : BasePlugin
{
    private SampleMenu _menu;

    public override void Load()
    {
        var cfg = new SampleConfig();
        _menu = new SampleMenu(cfg);

        // Safe in any load order: if UIKit is not loaded (yet), this only fills its registry.
        UiKitHost.Register(_menu);
        Log.LogInfo($"registered '{_menu.Id}' (host available: {UiKitHost.IsHostAvailable})");

        // Optional: open our page ourselves instead of waiting for the player.
        //   UiKitHost.OpenMenu("provider:" + SampleMenu.ModId);
        // (provider pages are namespaced by the host as "provider:<Id>[:<pageId>]")
    }

    public override bool Unload()
    {
        try { UiKitHost.Unregister(_menu); } catch { /* unload path must not throw */ }
        return true;
    }
}
