using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace SpotifyTrackHonorific.UI;

internal sealed class SettingsWindow : Window
{
    private readonly ConfigWindow owner;

    internal SettingsWindow(ConfigWindow owner)
        : base("SpotifyTrackHonorific - Settings###SpotifyTrackHonorificSettingsV2")
    {
        this.owner = owner;

        Size = new Vector2(660f, 500f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        owner.DrawSettingsContents();
    }
}