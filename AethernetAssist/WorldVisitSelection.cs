using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

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

    // Listen only for player-controlled fallback
    // confirmations. Automatic selection uses the
    // separate, validated journey state machine.
    private void InitializeWorldVisitConfirmationObserver()
    {
        AddonLifecycle.RegisterListener(
            AddonEvent.PreReceiveEvent,
            "SelectYesno",
            OnWorldVisitConfirmationInput);
    }

    private void DisposeWorldVisitConfirmationObserver()
    {
        AddonLifecycle.UnregisterListener(
            OnWorldVisitConfirmationInput);
    }

    private void OnWorldVisitConfirmationInput(
        AddonEvent stage,
        AddonArgs args)
    {
        if (stage != AddonEvent.PreReceiveEvent ||
            args is not AddonReceiveEventArgs evt ||
            worldVisitPhase != WorldVisitPhase.ManualConfirmation ||
            evt.AtkEvent == nint.Zero ||
            evt.AtkEventType.ToString() != "ButtonClick")
            return;

        try
        {
            var yesno =
                (AddonSelectYesno*)GetAddon("SelectYesno");

            if (yesno == null ||
                yesno->PromptText == null ||
                worldVisitTarget == null ||
                !string.Equals(
                    Clean(
                        yesno->PromptText->NodeText.ToString()),
                    "Travel to " +
                        worldVisitTarget.Name + "?",
                    StringComparison.OrdinalIgnoreCase))
                return;

            var nativeEvent = (AtkEvent*)evt.AtkEvent;

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
        }
        catch (Exception ex)
        {
            Log.Warning(
                "World Visit confirmation observation: " +
                ex.Message);
        }
    }
    private string worldVisitSelectionStatus =
        "No native destination selected this session.";

    private string worldVisitSelectionDetails =
        "No native world selection submitted.";

    private bool worldVisitPending;


    private string worldVisitCachedError = "";

    private readonly List<WorldVisitDestination>
        worldVisitCachedDestinations = new();

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
    private void SelectWorldVisitDestination(
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