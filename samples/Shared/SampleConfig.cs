namespace OpenNestUIKit.Sample;

/// <summary>
/// The sample mod's settings, kept in memory.
///
/// **The host never persists anything for you** — a write-back callback tells you what the user
/// did, and where it lands is entirely your decision (your own config file, memory, a network
/// message). This sample keeps the values in fields so it stays dependency-free; a real mod would
/// replace <see cref="Save"/> with its own INI/JSON writer.
/// </summary>
public sealed class SampleConfig
{
    public bool Enabled = true;
    public double Range = 120;
    public int ModeIndex = 1;
    public string Callsign = "Gunner";
    public string Hotkey = "F8";
    public int Slots = 4;
    public bool Verbose = false;
    public double TickRate = 20;
    public int RegionIndex = 0;

    private static readonly string[] Regions = { "Europe", "North America", "Asia" };

    public string RegionName()
        => (RegionIndex >= 0 && RegionIndex < Regions.Length) ? Regions[RegionIndex] : "?";

    /// <summary>Called by the sample after every change (a real mod writes its config file here).</summary>
    public void Save()
    {
        // No file IO on purpose: the sample must not need Unity or a loader to compile.
    }

    public void Reset()
    {
        Enabled = true;
        Range = 120;
        ModeIndex = 1;
        Callsign = "Gunner";
        Hotkey = "F8";
        Slots = 4;
        Verbose = false;
        TickRate = 20;
        RegionIndex = 0;
        Save();
    }
}
