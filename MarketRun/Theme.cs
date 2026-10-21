using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketRun;

// Matches AethernetAssist's 17-color palette/capture format.
internal static unsafe class Theme
{
    private static readonly Vector4[] Defaults =
    {
        new(0.065f, 0.080f, 0.105f, 0.97f),
        new(0.077f, 0.095f, 0.120f, 0.95f),
        new(0.86f, 0.89f, 0.93f, 1f),
        new(0.51f, 0.58f, 0.65f, 1f),
        new(0.51f, 0.76f, 0.92f, 1f),
        new(0.49f, 0.80f, 0.66f, 1f),
        new(0.14f, 0.20f, 0.26f, 0.95f),
        new(0.075f, 0.09f, 0.115f, 1f),
        new(0.26f, 0.34f, 0.42f, 0.9f),
        new(0.40f, 0.55f, 0.67f, 1f),
        new(0.10f, 0.14f, 0.18f, 1f),
        new(0.18f, 0.25f, 0.32f, 1f),
        new(0.11f, 0.16f, 0.20f, 1f),
        new(0.22f, 0.27f, 0.32f, 1f),
        new(0.18f, 0.26f, 0.32f, 1f),
        new(0.055f, 0.065f, 0.085f, 1f),
        new(0.085f, 0.12f, 0.15f, 1f)
    };

    private static readonly ImGuiCol[] Sources =
    {
        ImGuiCol.WindowBg,
        ImGuiCol.ChildBg,
        ImGuiCol.Text,
        ImGuiCol.TextDisabled,
        ImGuiCol.CheckMark,
        ImGuiCol.PlotHistogram,
        ImGuiCol.HeaderHovered,
        ImGuiCol.ScrollbarBg,
        ImGuiCol.ScrollbarGrab,
        ImGuiCol.ScrollbarGrabHovered,
        ImGuiCol.FrameBg,
        ImGuiCol.FrameBgHovered,
        ImGuiCol.Button,
        ImGuiCol.Border,
        ImGuiCol.ButtonActive,
        ImGuiCol.TitleBg,
        ImGuiCol.TitleBgActive
    };

    public static Vector4 Get(Configuration cfg, int index)
    {
        if (!cfg.UseDalamudColors ||
            cfg.MatchedStyleColors is not { Length: 17 })
            return Defaults[index];

        uint packed = cfg.MatchedStyleColors[index];
        return new Vector4(
            (packed & 255u) / 255f,
            ((packed >> 8) & 255u) / 255f,
            ((packed >> 16) & 255u) / 255f,
            ((packed >> 24) & 255u) / 255f);
    }

    public static uint[] Capture()
    {
        var result = new uint[Sources.Length];

        for (int i = 0; i < Sources.Length; i++)
            result[i] = ImGui.ColorConvertFloat4ToU32(
                *ImGui.GetStyleColorVec4(Sources[i]));

        return result;
    }

    public const int PushCount = 19;

    public static void Push(Configuration c)
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Get(c, 0));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Get(c, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, Get(c, 2));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Get(c, 3));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Get(c, 4));
        ImGui.PushStyleColor(ImGuiCol.Button, Get(c, 12));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Get(c, 6));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Get(c, 14));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Get(c, 6));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Get(c, 10));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Get(c, 11));
        ImGui.PushStyleColor(ImGuiCol.Border, Get(c, 13));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Get(c, 7));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Get(c, 8));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Get(c, 9));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Get(c, 4));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Get(c, 4));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Get(c, 15));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Get(c, 16));
    }
}
