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

        if (addon == null ||
            addon->AtkValues == null ||
            addon->AtkValuesCount != 15)
        {
            error = "Unexpected World Visit native layout.";
            return false;
        }

        // Patch 7.58 observations:
        // Value[0] has been seen as 1 and 2.
        // Value[11] is 1 for Light and 0 for Chaos.
        // Value[12] tracks the current-world position.
        // Only the selected DC is used for this parser.
        if (addon->AtkValues[0].Type != AtkValueType.UInt ||
            (addon->AtkValues[0].UInt != 1 &&
             addon->AtkValues[0].UInt != 2) ||
            addon->AtkValues[11].Type != AtkValueType.UInt ||
            addon->AtkValues[11].UInt > 1 ||
            addon->AtkValues[12].Type != AtkValueType.Int)
        {
            error = "Unrecognized Patch 7.58 selector state.";
            return false;
        }

        var player = Objects.LocalPlayer;

        if (player == null)
        {
            error = "Player unavailable.";
            return false;
        }

        var sheet = Data.GetExcelSheet<World>();

        uint currentWorldId = player.CurrentWorld.RowId;
        var current = sheet.GetRow(currentWorldId);

        if (currentWorldId == 0 ||
            current.DataCenter.RowId == 0)
        {
            error = "Current data center unavailable.";
            return false;
        }

        // DEV4 is deliberately restricted to the tested
        // European Light/Chaos pair. Derive DC row IDs
        // from Lumina rather than hardcoding numeric IDs.
        var lightAnchors = sheet.Where(x =>
            x.IsPublic &&
            string.Equals(
                x.Name.ToString(),
                "Raiden",
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();

        var chaosAnchors = sheet.Where(x =>
            x.IsPublic &&
            string.Equals(
                x.Name.ToString(),
                "Cerberus",
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();

        if (lightAnchors.Length != 1 ||
            chaosAnchors.Length != 1)
        {
            error = "EU data center identities ambiguous.";
            return false;
        }

        uint lightDc = lightAnchors[0].DataCenter.RowId;
        uint chaosDc = chaosAnchors[0].DataCenter.RowId;

        if (lightDc == 0 ||
            chaosDc == 0 ||
            lightDc == chaosDc ||
            (current.DataCenter.RowId != lightDc &&
             current.DataCenter.RowId != chaosDc))
        {
            error = "Current region is not the verified EU pair.";
            return false;
        }

        uint dcFlag = addon->AtkValues[11].UInt;
        uint selectedDc = dcFlag == 1 ? lightDc : chaosDc;

        var expected = sheet
            .Where(x =>
                x.RowId != 0 &&
                x.IsPublic &&
                x.DataCenter.RowId == selectedDc &&
                !string.IsNullOrWhiteSpace(
                    x.Name.ToString()))
            .OrderBy(
                x => x.Name.ToString(),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (expected.Length != 8 ||
            expected.Select(x => x.Name.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != 8)
        {
            error = "Expected eight unique destination worlds.";
            return false;
        }

        // Unlike WorldTranslate, these are the labels
        // actually present in the native window.
        var labels = new List<string>();

        CollectWorldVisitNativeText(
            &addon->UldManager,
            labels);

        var worldLabels = labels
            .Where(label =>
                expected.Any(row =>
                    string.Equals(
                        row.Name.ToString(),
                        label,
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (worldLabels.Length != 8)
        {
            error =
                "Native world labels incomplete (" +
                worldLabels.Length + "/8).";
            return false;
        }

        // DEV3 confirmed zero-based list indices in the
        // displayed alphabetical order. Fail closed if
        // the UI's reported label order changes.
        for (int i = 0; i < expected.Length; i++)
        {
            var world = expected[i];
            string name = world.Name.ToString();

            if (!string.Equals(
                worldLabels[i],
                name,
                StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "Native row order mismatch at index " + i;
                destinations.Clear();
                return false;
            }

            destinations.Add(
                new WorldVisitDestination(
                    i,
                    world.RowId,
                    name,
                    world.RowId == currentWorldId));
        }

        if (destinations.Count(x => x.Current) !=
            (selectedDc == current.DataCenter.RowId ? 1 : 0))
        {
            error = "Current-world identity mismatch.";
            destinations.Clear();
            return false;
        }

        error = "";
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
            NavAccent, "WORLD VISIT - PATCH 7.58");

        ImGui.TextDisabled(
            "Open the native World Visit window and select Light or Chaos.");

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
                    // DEV4: display validated destinations
                    // without sending unverified callbacks.
                    bool enabled = false;

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
            "DEV4: Verified destination list only. Travel selection disabled.");
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