using Dalamud.Configuration;

namespace AethernetAssist;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool AutoOpen { get; set; } = true;
    public bool AutoWalkHousingGates { get; set; } = true;
    public bool ShowUnlearned { get; set; } = true;
    public bool ShowDebug { get; set; } = false;

    // Free map routes start automatically by default.
    // Paid map teleports require a persistent opt-in.
    public bool AutoPaidMapRouting { get; set; } = false;

    // Restore the route confirmation window for debugging.
    public bool ConfirmMapRoutes { get; set; } = false;
    public float OpenRange { get; set; } = 11.1f;
    public bool UseDalamudColors { get; set; } = false;
    public int HousingDistrictIndex { get; set; } = 0;
    public int HousingWard { get; set; } = 1;
    public int LastSelectedNavigationPage { get; set; } = -1;
    public uint[] MatchedStyleColors { get; set; } = System.Array.Empty<uint>();
}