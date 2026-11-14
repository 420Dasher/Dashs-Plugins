using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AdvancedDyeRowInspector;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IDalamudPluginInterface Pi { get; set; } = null!;

    [PluginService]
    private static ICommandManager Commands { get; set; } = null!;

    [PluginService]
    private static IObjectTable Objects { get; set; } = null!;

    [PluginService]
    private static IPluginLog Log { get; set; } = null!;

    private readonly ICallGateSubscriber<
        ushort[],
        Dictionary<string, HashSet<string>>?[]
    > resourceIpc;

    private readonly ICallGateSubscriber<
        int, uint, (int, string?)
    > glamourerIpc;

    private bool windowOpen;
    private bool scanned;

    private string actorName = "Not scanned";
    private string actorIndex = "-";
    private string penumbraStatus = "Not checked";
    private string glamourerStatus = "Not checked";
    private string scanStatus = "Click Scan Current Appearance.";

    private DateTime scanTime;

    private readonly List<ResourceEntry> resources = new();

    private static readonly Dictionary<string, string> SlotNames = new()
    {
        ["met"] = "Head",
        ["top"] = "Body",
        ["glv"] = "Hands",
        ["dwn"] = "Legs",
        ["sho"] = "Feet",
        ["ear"] = "Earrings",
        ["nek"] = "Necklace",
        ["wrs"] = "Bracelets",
        ["rir"] = "Right Ring",
        ["ril"] = "Left Ring"
    };

    private static readonly string[] SlotOrder =
    {
        "Head",
        "Body",
        "Hands",
        "Legs",
        "Feet",
        "Earrings",
        "Necklace",
        "Bracelets",
        "Right Ring",
        "Left Ring",
        "Other / Unassigned"
    };

    private sealed class ResourceEntry
    {
        public string Slot { get; init; } = "";
        public string Kind { get; init; } = "";
        public string GamePath { get; init; } = "";
        public string ActualPath { get; init; } = "";
    }

    public Plugin()
    {
        // Penumbra V5 read-only resource-path IPC.
        resourceIpc = Pi.GetIpcSubscriber<
            ushort[],
            Dictionary<string, HashSet<string>>?[]
        >("Penumbra.GetGameObjectResourcePaths.V5");

        // Glamourer read-only appearance-state IPC.
        glamourerIpc = Pi.GetIpcSubscriber<
            int, uint, (int, string?)
        >("Glamourer.GetStateBase64");

        Commands.AddHandler("/adri", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Advanced Dye Row Inspector."
        });

        Pi.UiBuilder.Draw += Draw;
        Pi.UiBuilder.OpenMainUi += Open;
        Pi.UiBuilder.OpenConfigUi += Open;

        Log.Information("Advanced Dye Row Inspector DEV1 initialized.");
    }

    private void OnCommand(string command, string arguments)
    {
        windowOpen = !windowOpen;
    }

    private void Open()
    {
        windowOpen = true;
    }

    public void Dispose()
    {
        Commands.RemoveHandler("/adri");

        Pi.UiBuilder.Draw -= Draw;
        Pi.UiBuilder.OpenMainUi -= Open;
        Pi.UiBuilder.OpenConfigUi -= Open;

        resources.Clear();
    }

    private static string ClassifySlot(string gamePath)
    {
        // Use original game paths only.
        // Never guess a slot from a mod author's filename.

        var match = Regex.Match(
            gamePath,
            @"(?:^|[_/])(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)(?:[_\.]|$)",
            RegexOptions.IgnoreCase
        );

        if (!match.Success)
            return "Other / Unassigned";

        string key = match.Groups[1].Value.ToLowerInvariant();

        return SlotNames.TryGetValue(key, out var slot)
            ? slot
            : "Other / Unassigned";
    }

    private static string ClassifyKind(string gamePath)
    {
        if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "Model";

        if (gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            return "Material";

        if (gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(
                gamePath,
                @"(?:_id|_index)\.tex$",
                RegexOptions.IgnoreCase))
            {
                return "ID texture candidate";
            }

            return "Other texture";
        }

        return "Other resource";
    }

    private void Scan()
    {
        scanned = true;
        scanTime = DateTime.Now;

        resources.Clear();

        penumbraStatus = "Not checked";
        glamourerStatus = "Not checked";
        scanStatus = "Starting scan...";

        var actor = Objects.LocalPlayer;

        if (actor == null)
        {
            actorName = "Unavailable";
            actorIndex = "-";
            scanStatus = "Local player unavailable. Log into a character.";
            return;
        }

        actorName = actor.Name.TextValue;
        actorIndex = actor.ObjectIndex.ToString();

        // Optional Glamourer state availability check.
        // The returned state is never applied or modified.
        try
        {
            if (!glamourerIpc.HasFunction)
            {
                glamourerStatus = "Unavailable / IPC not registered";
            }
            else
            {
                var result = glamourerIpc.InvokeFunc(
                    (int)actor.ObjectIndex, 0u);

                glamourerStatus = result.Item2 != null
                    ? $"State available ({result.Item2.Length} encoded characters)"
                    : $"No state returned (API result {result.Item1})";
            }
        }
        catch (Exception ex)
        {
            glamourerStatus = "IPC error: " + ex.Message;
            Log.Warning($"ADRI Glamourer read failed: {ex}");
        }

        // Read loaded resource paths reported for the player.
        try
        {
            if (!resourceIpc.HasFunction)
            {
                penumbraStatus = "Unavailable / V5 IPC not registered";
                scanStatus = "Penumbra resource API unavailable.";
                return;
            }

            var response = resourceIpc.InvokeFunc(
                new ushort[] { (ushort)actor.ObjectIndex });

            if (response == null ||
                response.Length == 0 ||
                response[0] == null)
            {
                penumbraStatus = "No usable resource snapshot";
                scanStatus =
                    "Penumbra returned no resources. Try scanning after a character redraw.";
                return;
            }

            var map = response[0]!;

            penumbraStatus =
                $"Connected — {map.Count} resolved resource entries";

            var unique = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in map)
            {
                string actual = entry.Key ?? "";

                if (entry.Value == null)
                    continue;

                foreach (string gamePath in entry.Value)
                {
                    if (string.IsNullOrWhiteSpace(gamePath))
                        continue;

                    string kind = ClassifyKind(gamePath);

                    if (kind == "Other resource")
                        continue;

                    string key = actual + "\n" + gamePath;

                    if (!unique.Add(key))
                        continue;

                    resources.Add(new ResourceEntry
                    {
                        Slot = ClassifySlot(gamePath),
                        Kind = kind,
                        GamePath = gamePath,
                        ActualPath = actual
                    });
                }
            }

            resources.Sort((a, b) =>
            {
                int slotA = Array.IndexOf(SlotOrder, a.Slot);
                int slotB = Array.IndexOf(SlotOrder, b.Slot);

                int result = slotA.CompareTo(slotB);

                if (result != 0)
                    return result;

                result = string.Compare(
                    a.Kind,
                    b.Kind,
                    StringComparison.OrdinalIgnoreCase);

                return result != 0
                    ? result
                    : string.Compare(
                        a.GamePath,
                        b.GamePath,
                        StringComparison.OrdinalIgnoreCase);
            });

            int models = resources.Count(
                x => x.Kind == "Model");

            int materials = resources.Count(
                x => x.Kind == "Material");

            int ids = resources.Count(
                x => x.Kind == "ID texture candidate");

            scanStatus =
                $"Scan complete: {models} models, " +
                $"{materials} materials, " +
                $"{ids} ID texture candidates.";

            if (resources.Count == 0)
            {
                scanStatus =
                    "No inspectable models, materials or textures returned.";
            }
        }
        catch (Exception ex)
        {
            penumbraStatus = "IPC failure: " + ex.Message;
            scanStatus = "Resource scan failed.";

            resources.Clear();

            Log.Error($"ADRI Penumbra scan failed: {ex}");
        }
    }

    private static void DrawResource(ResourceEntry entry)
    {
        ImGui.BulletText(entry.Kind);

        ImGui.Indent();

        ImGui.TextWrapped("Game: " + entry.GamePath);
        ImGui.TextWrapped("Resolved: " + entry.ActualPath);

        if (entry.Kind == "Material")
        {
            ImGui.TextDisabled(
                "Colorset rows: unknown (ID sampler not resolved).");
        }

        if (entry.Kind == "ID texture candidate")
        {
            ImGui.TextDisabled(
                "Texture pixels: not decoded in DEV1.");
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private string MakeDiagnostics()
    {
        var sb = new StringBuilder();

        sb.AppendLine("Advanced Dye Row Inspector — DEV1");
        sb.AppendLine("Version: 0.0.1.0");
        sb.AppendLine("Scanned: " + scanTime.ToString("O"));
        sb.AppendLine("Actor: " + actorName);
        sb.AppendLine("Object index: " + actorIndex);
        sb.AppendLine("Penumbra: " + penumbraStatus);
        sb.AppendLine("Glamourer: " + glamourerStatus);
        sb.AppendLine("Status: " + scanStatus);
        sb.AppendLine();

        foreach (var entry in resources)
        {
            sb.AppendLine($"[{entry.Slot}] {entry.Kind}");
            sb.AppendLine("Game: " + entry.GamePath);
            sb.AppendLine("Resolved: " + entry.ActualPath);

            if (entry.Kind == "Material")
                sb.AppendLine("Rows: unverified");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(
            new Vector2(780, 600),
            ImGuiCond.FirstUseEver);

        bool open = windowOpen;

        bool visible = ImGui.Begin(
            "Advanced Dye Row Inspector###ADRIMain",
            ref open,
            ImGuiWindowFlags.NoCollapse);

        windowOpen = open;

        if (!visible)
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Advanced Dye Row Inspector");

        ImGui.SameLine();
        ImGui.TextDisabled("DEV1 — 0.0.1");

        ImGui.Separator();

        ImGui.TextUnformatted("Actor: Current Character");
        ImGui.TextWrapped("Name: " + actorName);
        ImGui.TextUnformatted("Object index: " + actorIndex);

        ImGui.Spacing();

        if (ImGui.Button("Scan Current Appearance"))
            Scan();

        ImGui.SameLine();

        if (ImGui.Button("Refresh"))
            Scan();

        ImGui.SameLine();

        if (ImGui.Button("Copy Diagnostics"))
            ImGui.SetClipboardText(MakeDiagnostics());

        ImGui.Spacing();

        ImGui.TextWrapped("Penumbra: " + penumbraStatus);
        ImGui.TextWrapped("Glamourer: " + glamourerStatus);

        ImGui.Separator();

        ImGui.TextWrapped(scanStatus);

        ImGui.TextDisabled(
            "Row detection: unconfirmed. DEV1 does not infer rows from filenames.");

        ImGui.Spacing();

        if (!scanned)
        {
            ImGui.TextWrapped(
                "Click Scan Current Appearance to inspect the loaded outfit.");
        }
        else if (resources.Count == 0)
        {
            ImGui.TextWrapped(
                "No inspectable resources. Review the status above.");
        }
        else
        {
            ImGui.TextDisabled(
                "Slot association uses original game resource paths.");

            ImGui.TextDisabled(
                "Resources in the same slot are not necessarily linked.");

            ImGui.Spacing();

            if (ImGui.BeginChild(
                "##ADRIResults",
                new Vector2(0, 0),
                true))
            {
                foreach (string slot in SlotOrder)
                {
                    var entries = resources
                        .Where(x => x.Slot == slot)
                        .ToList();

                    if (entries.Count == 0)
                        continue;

                    string header =
                        $"{slot} ({entries.Count})###ADRI_{slot}";

                    if (!ImGui.CollapsingHeader(header))
                        continue;

                    ImGui.Indent();

                    var models = entries
                        .Where(x => x.Kind == "Model")
                        .ToList();

                    var materials = entries
                        .Where(x => x.Kind == "Material")
                        .ToList();

                    var ids = entries
                        .Where(x => x.Kind == "ID texture candidate")
                        .ToList();

                    var others = entries
                        .Except(models)
                        .Except(materials)
                        .Except(ids)
                        .ToList();

                    if (models.Count > 0)
                    {
                        ImGui.TextUnformatted("Models");

                        foreach (var entry in models)
                            DrawResource(entry);
                    }

                    if (materials.Count > 0)
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted("Materials");

                        foreach (var entry in materials)
                            DrawResource(entry);
                    }

                    if (ids.Count > 0)
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted("ID Texture Candidates");

                        foreach (var entry in ids)
                            DrawResource(entry);
                    }

                    if (others.Count > 0)
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted("Additional Textures");

                        foreach (var entry in others)
                            DrawResource(entry);
                    }

                    ImGui.Unindent();
                    ImGui.Spacing();
                }
            }

            ImGui.EndChild();
        }

        ImGui.End();
    }
}