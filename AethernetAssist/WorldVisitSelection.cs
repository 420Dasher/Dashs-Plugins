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
            new[] { "WorldTravelSelect", "SelectString", "SelectYesno" },
            OnWorldVisitNativeInput
        );

        AddonLifecycle.RegisterListener(
            AddonEvent.PostReceiveEvent,
            new[] { "WorldTravelSelect", "SelectString", "SelectYesno" },
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
            // Observe the real native Yes/No button.
            // Do not intercept or modify the event.
            if (args.AddonName == "SelectYesno")
            {
                if (stage == AddonEvent.PreReceiveEvent &&
                    worldVisitPhase ==
                        WorldVisitPhase.ManualConfirmation &&
                    evt.AtkEvent != nint.Zero &&
                    evt.AtkEventType.ToString() == "ButtonClick")
                {
                    var yesno =
                        (AddonSelectYesno*)GetAddon("SelectYesno");

                    if (yesno != null &&
                        yesno->PromptText != null &&
                        worldVisitTarget != null &&
                        string.Equals(
                            Clean(
                                yesno->PromptText->NodeText.ToString()),
                            "Travel to " +
                                worldVisitTarget.Name + "?",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var nativeEvent =
                            (AtkEvent*)evt.AtkEvent;

                        if (yesno->NoButton != null &&
                            (nint)nativeEvent->Target ==
                            (nint)yesno->NoButton->OwnerNode)
                        {
                            worldVisitManualDecision =
                                WorldVisitManualDecision.Declined;
                        }
                        else if (yesno->YesButton != null &&
                            (nint)nativeEvent->Target ==
                            (nint)yesno->YesButton->OwnerNode)
                        {
                            worldVisitManualDecision =
                                WorldVisitManualDecision.Accepted;
                        }

                        worldVisitNativeInputTrace.Add(
                            DateTime.Now.ToString("HH:mm:ss.fff") +
                            " | SelectYesno ButtonClick" +
                            " | Param=" + evt.EventParam +
                            " | Decision=" +
                                worldVisitManualDecision);

                        if (worldVisitNativeInputTrace.Count > 64)
                            worldVisitNativeInputTrace.RemoveAt(0);
                    }
                }

                return;
            }

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

        var addon = GetAddon("WorldTravelSelect");

        if (!WorldVisitPageAvailable() ||
            addon == null ||
            addon->AtkValues == null ||
            addon->AtkValuesCount != 15)
        {
            error = "Native World Visit menu unavailable.";
            return false;
        }

        if (addon->AtkValues[0].Type != AtkValueType.UInt ||
            (addon->AtkValues[0].UInt != 1 &&
             addon->AtkValues[0].UInt != 2) ||
            addon->AtkValues[11].Type != AtkValueType.UInt ||
            addon->AtkValues[11].UInt > 1 ||
            addon->AtkValues[12].Type != AtkValueType.Int)
        {
            error = "Unexpected Patch 7.58 selector state.";
            return false;
        }

        if (!TryBuildEuropeanWorldChoices(
            out var allWorlds, out error))
            return false;

        bool light = addon->AtkValues[11].UInt == 1;

        var expectedNames = light
            ? WorldVisitLightNames
            : WorldVisitChaosNames;

        var labels = new List<string>();

        CollectWorldVisitNativeText(
            &addon->UldManager, labels);

        var visibleWorlds = labels
            .Where(label => expectedNames.Contains(
                label, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (visibleWorlds.Length != 8)
        {
            error = "Native world labels incomplete (" +
                    visibleWorlds.Length + "/8).";
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            if (!string.Equals(
                visibleWorlds[i],
                expectedNames[i],
                StringComparison.OrdinalIgnoreCase))
            {
                error = "Native row order mismatch at " + i;
                destinations.Clear();
                return false;
            }

            var world = allWorlds.Single(x =>
                string.Equals(
                    x.Name, expectedNames[i],
                    StringComparison.OrdinalIgnoreCase));

            destinations.Add(new WorldVisitDestination(
                i,
                world.WorldId,
                world.Name,
                world.Current));
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
        // Reuses the previously tested callback,
        // after validating the Patch 7.58 native list.
        SelectWorldVisitDestinationLegacy(selected);
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

    private uint worldVisitUiSelectedWorldId;

    private void DrawWorldVisitWorldColumn(
        string dcName,
        string[] names,
        float columnWidth)
    {
        if (ImGui.BeginChild(
            "##dc-worlds-" + dcName,
            new Vector2(columnWidth, 271f),
            true))
        {
            ImGui.TextColored(NavAccent, dcName);
            ImGui.Separator();

            foreach (string name in names)
            {
                var world = worldVisitCachedDestinations
                    .FirstOrDefault(x =>
                        string.Equals(
                            x.Name, name,
                            StringComparison.OrdinalIgnoreCase));

                if (world == null)
                {
                    ImGui.TextDisabled(name + " (unavailable)");
                    continue;
                }

                bool enabled =
                    !world.Current &&
                    worldVisitPhase == WorldVisitPhase.Idle &&
                    !IsBusy();

                bool selected =
                    worldVisitUiSelectedWorldId == world.WorldId;

                if (selected)
                    ImGui.PushStyleColor(
                        ImGuiCol.Button, ThemeColor(6));

                ImGui.BeginDisabled(!enabled);

                string label = world.Name +
                    (world.Current ? " (current)" : "") +
                    "##world-" + world.WorldId;

                bool clicked = ImGui.Button(
                    label, new Vector2(-1f, 25f));

                ImGui.EndDisabled();

                if (selected)
                    ImGui.PopStyleColor();

                if (clicked && enabled)
                {
                    worldVisitUiSelectedWorldId = world.WorldId;
                    StartWorldVisit(world);
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawWorldVisitSelectionPage()
    {
        ImGui.Spacing();

        ImGui.TextColored(
            NavAccent, "WORLD VISIT");

        ImGui.TextDisabled(
            "Select a world on Light or Chaos.");

        ImGui.Spacing();

        RefreshWorldVisitChoices();

        if (worldVisitCachedDestinations.Count != 16)
        {
            ImGui.TextWrapped(
                "World Visit destinations unavailable.");
            ImGui.TextDisabled(worldVisitCachedError);
        }
        else
        {
            float gap = 8f;
            float width =
                (ImGui.GetContentRegionAvail().X - gap) / 2f;

            DrawWorldVisitWorldColumn(
                "Light", WorldVisitLightNames, width);

            ImGui.SameLine(0f, gap);

            DrawWorldVisitWorldColumn(
                "Chaos", WorldVisitChaosNames, width);
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextWrapped(worldVisitJourneyStatus);

        if (worldVisitPhase != WorldVisitPhase.Idle)
        {
            if (WorldVisitCanStop())
            {
                string stopLabel =
                    worldVisitPhase ==
                        WorldVisitPhase.WaitForArrival
                        ? "Stop monitoring"
                        : "Stop automation";

                if (ImGui.SmallButton(stopLabel))
                    TryStopWorldVisitFromUser();
            }
            else
            {
                ImGui.TextDisabled(
                    "FFXIV controls queued transfers.");
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
            "DEV6 FIX2: Native selection; manual confirmation with decline tracking.");
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

        if (GetAddon("WorldTravelSelect") != null)
        {
            bool validated = TryReadWorldVisitDestinations(
                out var nativeWorlds, out var nativeError);

            output.AppendLine(
                "Native selected DC validation: " +
                (validated ? "Passed (8 worlds)" : nativeError));

            foreach (var world in nativeWorlds)
                output.AppendLine(
                    "Native index " + world.Index +
                    " | " + world.Name);
        }

        return output.ToString();
    }
}