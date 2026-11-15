using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private sealed record WorldVisitDestination(
        int Index,
        uint WorldId,
        string Name,
        bool Current
    );

    private readonly List<string> worldVisitNativeInputTrace = new();

    private void InitializeWorldVisitNativeResearch()
    {
        AddonLifecycle.RegisterListener(
            AddonEvent.PreReceiveEvent,
            new[] { "WorldTravelSelect", "SelectString" },
            OnWorldVisitNativeInput
        );

        AddonLifecycle.RegisterListener(
            AddonEvent.PostReceiveEvent,
            new[] { "WorldTravelSelect", "SelectString" },
            OnWorldVisitNativeInput
        );
    }

    private void DisposeWorldVisitNativeResearch()
    {
        AddonLifecycle.UnregisterListener(
            OnWorldVisitNativeInput
        );
    }

    private void OnWorldVisitNativeInput(
        AddonEvent stage,
        AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs evt)
            return;

        try
        {
            var addon = GetAddon("WorldTravelSelect");

            if (addon == null)
                return;

            string eventType = evt.AtkEventType.ToString();

            // Do not fill the trace with mouse movements
            // and hover notifications.
            if (eventType.Contains(
                    "MouseMove",
                    StringComparison.OrdinalIgnoreCase) ||
                eventType.Contains(
                    "MouseOver",
                    StringComparison.OrdinalIgnoreCase) ||
                eventType.Contains(
                    "MouseOut",
                    StringComparison.OrdinalIgnoreCase))
                return;

            // Keep only meaningful interactions in the trace.
            // Hovering across multiple rows otherwise fills
            // the buffer before the user copies diagnostics.
            if (eventType == "ListItemRollOver" ||
                eventType == "ListItemRollOut")
                return;

            string selectedDc = "unavailable";
            string selectedWorld = "unavailable";

            if (addon->AtkValues != null &&
                addon->AtkValuesCount >= 13)
            {
                var dcValue = addon->AtkValues[11];
                var worldValue = addon->AtkValues[12];

                if (dcValue.Type == AtkValueType.UInt)
                    selectedDc = dcValue.UInt.ToString();

                if (worldValue.Type == AtkValueType.Int)
                    selectedWorld = worldValue.Int.ToString();
            }

            // The generic event parameter does not contain
            // the clicked row. ListItemData.SelectedIndex
            // is supplied separately by the native UI.
            string listDetails = "";

            if (stage == AddonEvent.PreReceiveEvent &&
                eventType == "ListItemClick" &&
                evt.AtkEventData != 0)
            {
                var data = (AtkEventData*)evt.AtkEventData;

                int selectedIndex =
                    data->ListItemData.SelectedIndex;

                short hoveredIndex =
                    data->ListItemData.HoveredItemIndex3;

                listDetails =
                    " | SelectedIndex=" + selectedIndex +
                    " | HoveredIndex=" + hoveredIndex +
                    " | MouseButton=" +
                    data->ListItemData.MouseButtonId;
            }

            string record =
                DateTime.Now.ToString("HH:mm:ss.fff") +
                " | " + stage +
                " | Addon=" + args.AddonName +
                " | Event=" + eventType +
                " | Param=" + evt.EventParam +
                " | Value11=" + selectedDc +
                " | Value12=" + selectedWorld +
                listDetails;

            worldVisitNativeInputTrace.Add(record);

            if (worldVisitNativeInputTrace.Count > 64)
                worldVisitNativeInputTrace.RemoveAt(0);
        }
        catch (Exception ex)
        {
            // Observational only. Never interrupt the
            // native menu or change its event processing.
            Log.Warning(
                "World Visit native event observation failed: " +
                ex.Message
            );
        }
    }

    private string BuildWorldVisitNativeInputReport()
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine();
        report.AppendLine(
            "=== PATCH 7.58 WORLD VISIT INPUT EVENTS ==="
        );

        report.AppendLine(
            "Captured events: " +
            worldVisitNativeInputTrace.Count
        );

        foreach (var record in worldVisitNativeInputTrace)
            report.AppendLine(record);

        report.AppendLine(
            "Observation only. No events intercepted or modified."
        );

        return report.ToString();
    }

    private string worldVisitSelectionStatus =
        "No native destination selected this session.";

    private string worldVisitSelectionDetails =
        "No native world selection submitted.";

    private bool worldVisitPending;

    private DateTime worldVisitLastRefresh =
        DateTime.MinValue;

    private nint worldVisitCachedAddon;
    private string worldVisitCachedError = "";

    private readonly List<WorldVisitDestination>
        worldVisitCachedDestinations = new();

    private static bool WorldVisitHasText(
        AtkUldManager* manager,
        string expected,
        int depth = 0)
    {
        if (manager == null ||
            manager->NodeList == null ||
            depth > 4)
            return false;

        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];

            if (node == null)
                continue;

            if (node->Type == NodeType.Text)
            {
                string label = ((AtkTextNode*)node)
                    ->NodeText.ToString().Trim();

                if (string.Equals(
                    label, expected,
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if ((int)node->Type > 1000)
            {
                var component =
                    ((AtkComponentNode*)node)->Component;

                if (component != null &&
                    WorldVisitHasText(
                        &component->UldManager,
                        expected,
                        depth + 1))
                    return true;
            }
        }

        return false;
    }

    private bool TryReadWorldVisitDestinations(
        out List<WorldVisitDestination> destinations,
        out string error)
    {
        destinations = new List<WorldVisitDestination>();
        error = "";

        if (!WorldVisitPageAvailable())
        {
            error = "Not near an eligible main Aetheryte.";
            return false;
        }

        var addon = GetAddon("WorldTravelSelect");

        if (addon == null || addon->AtkValues == null)
        {
            error = "WorldTravelSelect is not ready.";
            return false;
        }

        // The pre-7.58 selector used 15 values.
        // Do not run the old native selector against a
        // changed layout until its callbacks are verified.
        if (addon->AtkValuesCount != 15)
        {
            error =
                "Patch 7.58 native layout requires inspection (" +
                addon->AtkValuesCount +
                " values). Copy diagnostics with the menu open.";
            return false;
        }

        // Observed in live DEV4.3 diagnostics.
        if (addon->AtkValues[0].Type != AtkValueType.UInt ||
            addon->AtkValues[0].UInt != 1)
        {
            error = "Unexpected World Visit native layout.";
            return false;
        }

        var player = Objects.LocalPlayer;

        if (player == null)
        {
            error = "No local player.";
            return false;
        }

        uint currentWorldId = player.CurrentWorld.RowId;

        var sheet = Data.GetExcelSheet<World>();

        var currentRow = sheet.GetRow(currentWorldId);

        uint dataCenterId = currentRow.DataCenter.RowId;

        if (currentWorldId == 0 || dataCenterId == 0)
        {
            error = "Current data center could not be verified.";
            return false;
        }

        var module = RaptureAtkModule.Instance();

        if (module == null)
        {
            error = "Native UI data unavailable.";
            return false;
        }

        var array = module->AtkArrayDataHolder
            .StringArrays[(int)StringArrayType.WorldTranslate];

        if (array == null)
        {
            error = "Native World Visit destination array missing.";
            return false;
        }

        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        for (int i = 3; i <= 10; i++)
        {
            var entry = array->StringArray[i];

            if (!entry.HasValue)
                break;

            string name =
                MemoryHelper.ReadStringNullTerminated(
                    (nint)entry.Value).Trim();

            if (string.IsNullOrWhiteSpace(name))
                break;

            if (!seen.Add(name))
            {
                error = "Duplicate native world name: " + name;
                return false;
            }

            // The game's native destination list
            // must actually display this exact world.
            if (!WorldVisitHasText(
                &addon->UldManager, name))
            {
                error = "Native world label mismatch: " + name;
                return false;
            }

            var matches = sheet
                .Where(x =>
                    string.Equals(
                        x.Name.ToString(),
                        name,
                        StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();

            if (matches.Length != 1)
            {
                error = "World identity could not be verified: " +
                    name;
                return false;
            }

            var world = matches[0];

            // No cross-data-center selection.
            if (world.DataCenter.RowId != dataCenterId)
            {
                error = "Different data center detected: " + name;
                return false;
            }

            destinations.Add(new WorldVisitDestination(
                i - 3,
                world.RowId,
                name,
                world.RowId == currentWorldId));
        }

        if (destinations.Count < 2 ||
            destinations.Count > 8 ||
            destinations.Count(x => x.Current) != 1)
        {
            error =
                "Native World Visit destination list incomplete.";
            return false;
        }

        return true;
    }

    private void RefreshWorldVisitDestinations()
    {
        var addon = GetAddon("WorldTravelSelect");

        if (addon == null)
        {
            worldVisitCachedDestinations.Clear();
            worldVisitCachedAddon = nint.Zero;
            worldVisitPending = false;
            worldVisitCachedError =
                "Native World Visit menu is closed.";
            return;
        }

        var now = DateTime.UtcNow;

        if (worldVisitCachedAddon == (nint)addon &&
            (now - worldVisitLastRefresh)
                .TotalMilliseconds < 500)
            return;

        worldVisitCachedAddon = (nint)addon;
        worldVisitLastRefresh = now;

        worldVisitCachedDestinations.Clear();

        if (TryReadWorldVisitDestinations(
            out var results, out var error))
        {
            worldVisitCachedDestinations.AddRange(results);
            worldVisitCachedError = "";
        }
        else
        {
            worldVisitCachedError = error;
        }
    }

    private void SelectWorldVisitDestination(
        WorldVisitDestination selected)
    {
        // Patch 7.58 DEV1: the DC dropdown changes the
        // native selector. Never guess callback indices.
        // Resume automatic World Visit only after mapping
        // and validating the new native menu.
        worldVisitSelectionStatus =
            "Patch 7.58 native World Visit selection " +
            "is awaiting compatibility verification.";
        return;
    }

    private void SelectWorldVisitDestinationLegacy(
        WorldVisitDestination selected)
    {
        if (worldVisitPending || IsBusy())
            return;

        // Never trust previously cached indices at click time.
        if (!TryReadWorldVisitDestinations(
            out var fresh, out var error))
        {
            worldVisitSelectionStatus = error;
            return;
        }

        if (fresh.Count(x =>
            x.WorldId == selected.WorldId &&
            x.Name == selected.Name &&
            x.Index == selected.Index &&
            !x.Current) != 1)
        {
            worldVisitSelectionStatus =
                "Destination changed. Selection cancelled.";
            return;
        }

        var addon = GetAddon("WorldTravelSelect");

        if (addon == null ||
            GetAddon("SelectYesno") != null)
        {
            worldVisitSelectionStatus =
                "Close the existing confirmation first.";
            return;
        }

        int callbackIndex = selected.Index + 2;

        if (callbackIndex < 2 || callbackIndex > 9)
        {
            worldVisitSelectionStatus =
                "Native callback index out of range.";
            return;
        }

        AtkValue* values = stackalloc AtkValue[2];

        values[0] = new AtkValue
        {
            Type = AtkValueType.Int,
            Int = 0
        };

        values[1] = new AtkValue
        {
            Type = AtkValueType.Int,
            Int = callbackIndex
        };

        worldVisitSelectionDetails =
            "World=" + selected.Name +
            " | World ID=" + selected.WorldId +
            " | Native list index=" + selected.Index +
            " | Callback=0," + callbackIndex;

        // Native selection; the journey controller
        // handles the separately verified confirmation.
        addon->FireCallback(2, values, true);

        worldVisitPending = true;
        worldVisitSelectionStatus =
            "Selected " + selected.Name +
            ". Awaiting verified confirmation.";
    }

    private void DrawWorldVisitSelectionPage()
    {
        ImGui.Spacing();

        ImGui.TextColored(
            NavAccent, "SAME-DC WORLD VISIT");

        ImGui.TextDisabled(
            "Choose a world on your current data center.");

        ImGui.Spacing();

        RefreshWorldVisitChoices();

        if (worldVisitCachedDestinations.Count == 0)
        {
            ImGui.TextWrapped(
                "No World Visit destinations available.");

            ImGui.Spacing();

            ImGui.TextDisabled(worldVisitCachedError);
        }
        else
        {
            ImGui.TextColored(
                NavMuted, "AVAILABLE DESTINATIONS");

            ImGui.Spacing();

            float rowHeight =
                26f + ImGui.GetStyle().ItemSpacing.Y;

            float viewport = Math.Clamp(
                worldVisitCachedDestinations.Count *
                    rowHeight + 12f,
                85f, 300f);

            if (ImGui.BeginChild(
                "##WorldVisitDestinations",
                new Vector2(0f, viewport),
                false))
            {
                foreach (var world in worldVisitCachedDestinations)
                {
                    bool enabled =
                        !world.Current &&
                        !worldVisitPending &&
                        worldVisitPhase == WorldVisitPhase.Idle &&
                        !IsBusy();

                    Vector2 pos = ImGui.GetCursorScreenPos();

                    Vector2 size = new(
                        MathF.Max(100f,
                            ImGui.GetContentRegionAvail().X - 4f),
                        26f);

                    bool clicked = ImGui.InvisibleButton(
                        "##world-visit-" + world.WorldId,
                        size);

                    bool hovered =
                        ImGui.IsItemHovered() && enabled;

                    var draw = ImGui.GetWindowDrawList();

                    if (hovered)
                    {
                        draw.AddRectFilled(
                            pos, pos + size,
                            ImGui.ColorConvertFloat4ToU32(
                                ThemeColor(6)), 3f);
                    }

                    string label = world.Current
                        ? world.Name + "  (current)"
                        : world.Name;

                    draw.AddText(
                        pos + new Vector2(10f, 5f),
                        ImGui.ColorConvertFloat4ToU32(
                            enabled ? NavAccent : NavMuted),
                        "\u25C6");

                    draw.AddText(
                        pos + new Vector2(29f, 5f),
                        ImGui.ColorConvertFloat4ToU32(
                            enabled ? ThemeColor(2) :
                                      ThemeColor(3)),
                        label);

                    if (clicked && enabled)
                        StartWorldVisit(world);
                }
            }

            ImGui.EndChild();
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextWrapped(worldVisitJourneyStatus);

        if (worldVisitPhase != WorldVisitPhase.Idle)
        {
            ImGui.Spacing();

            if (WorldVisitCanStop())
            {
                if (ImGui.SmallButton("Stop automation"))
                    TryStopWorldVisitFromUser();

                ImGui.TextDisabled(
                    "This does not close native game menus.");
            }
            else
            {
                ImGui.TextWrapped(
                    "FFXIV controls the transfer after submission. " +
                    "Queued World Visits cannot be cancelled.");
            }
        }

        ImGui.Spacing();

        if (ImGui.SmallButton("Copy diagnostics"))
        {
            ImGui.SetClipboardText(

                BuildWorldVisitSelectionReport() +
                BuildWorldVisitJourneyReport() +
                BuildWorldVisitNativeLayoutReport() +
                BuildWorldVisitNativeInputReport());
        }

        ImGui.TextDisabled(
            "Same-DC only. Native confirmation is verified.");
    }

    private static void CollectWorldVisitNativeText(
        AtkUldManager* manager,
        List<string> results,
        int depth = 0)
    {
        if (manager == null ||
            manager->NodeList == null ||
            depth > 4 ||
            results.Count >= 80)
            return;

        for (int i = 0;
             i < manager->NodeListCount && results.Count < 80;
             i++)
        {
            var node = manager->NodeList[i];

            if (node == null)
                continue;

            if (node->Type == NodeType.Text)
            {
                string label = ((AtkTextNode*)node)
                    ->NodeText.ToString().Trim();

                if (!string.IsNullOrWhiteSpace(label) &&
                    !results.Contains(label))
                    results.Add(label);
            }
            else if ((int)node->Type > 1000)
            {
                var component =
                    ((AtkComponentNode*)node)->Component;

                if (component != null)
                {
                    CollectWorldVisitNativeText(
                        &component->UldManager,
                        results,
                        depth + 1);
                }
            }
        }
    }

    private string BuildWorldVisitNativeLayoutReport()
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine();
        report.AppendLine("=== PATCH 7.58 WORLD VISIT NATIVE LAYOUT ===");

        var addon = GetAddon("WorldTravelSelect");

        if (addon == null)
        {
            report.AppendLine("WorldTravelSelect: not open.");
            return report.ToString();
        }

        report.AppendLine("WorldTravelSelect: visible.");
        report.AppendLine(
            "AtkValuesCount: " + addon->AtkValuesCount);

        if (addon->AtkValues != null)
        {
            int count = Math.Min(
                (int)addon->AtkValuesCount, 32);

            for (int i = 0; i < count; i++)
            {
                var value = addon->AtkValues[i];
                string detail = "";

                if (value.Type == AtkValueType.Int)
                    detail = value.Int.ToString();
                else if (value.Type == AtkValueType.UInt)
                    detail = value.UInt.ToString();

                report.AppendLine(
                    "AtkValue[" + i + "] " +
                    value.Type + " " + detail);
            }
        }

        var labels = new List<string>();

        CollectWorldVisitNativeText(
            &addon->UldManager, labels);

        report.AppendLine("Visible native text labels:");

        foreach (string label in labels)
            report.AppendLine("  " + label);

        report.AppendLine(
            "Diagnostic only: no native selection callback sent.");

        return report.ToString();
    }
    private string BuildWorldVisitSelectionReport()
    {
        var output = new System.Text.StringBuilder();

        output.AppendLine();
        output.AppendLine(
            "=== WORLD VISIT DESTINATIONS ===");

        output.AppendLine(
            "Status: " + worldVisitSelectionStatus);

        output.AppendLine(
            "Pending native selection: " + worldVisitPending);

        output.AppendLine(
            "Cached world count: " +
            worldVisitCachedDestinations.Count);

        output.AppendLine(
            "Validation: " +
            (string.IsNullOrEmpty(worldVisitCachedError)
                ? "Passed"
                : worldVisitCachedError));

        foreach (var world in worldVisitCachedDestinations)
        {
            output.AppendLine(
                "Index " + world.Index +
                " | " + world.Name +
                " | WorldID=" + world.WorldId +
                " | Current=" + world.Current);
        }

        output.AppendLine(worldVisitSelectionDetails);

        output.AppendLine(
            "Confirmation handled by World Visit controller.");

        return output.ToString();
    }
}