using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeAgentMap = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap;
using DalamudAgentId = Dalamud.Game.Agent.AgentId;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private readonly List<string> mapResearchEvents = new();

    private bool mapResearchArmed;
    private DateTime mapResearchUntil = DateTime.MinValue;
    private string mapResearchLastSignature = "";
    private string mapResearchStatus = "Not recording.";

    private const int MapResearchLimit = 60;

    private void InitializeMapResearch()
    {
        AgentLifecycle.RegisterListener(
            AgentEvent.PreReceiveEvent,
            DalamudAgentId.Map,
            OnMapResearchEvent
        );

        AgentLifecycle.RegisterListener(
            AgentEvent.PreReceiveEventWithResult,
            DalamudAgentId.Map,
            OnMapResearchEvent
        );

        Log.Information(
            "DEV8 TEST1: read-only Map agent listeners registered."
        );
    }

    private void DisposeMapResearch()
    {
        AgentLifecycle.UnregisterListener(OnMapResearchEvent);
        mapResearchArmed = false;
    }

    private void ArmMapResearch()
    {
        mapResearchEvents.Clear();
        mapResearchLastSignature = "";
        mapResearchUntil = DateTime.UtcNow.AddSeconds(45);
        mapResearchArmed = true;

        mapResearchStatus =
            "Recording Map agent events for 45 seconds.";

        Log.Information(
            "DEV8 TEST1: map research armed for 45 seconds."
        );
    }

    private void OnMapResearchEvent(
        AgentEvent type,
        AgentArgs args)
    {
        if (!mapResearchArmed)
            return;

        if (DateTime.UtcNow > mapResearchUntil)
        {
            mapResearchArmed = false;
            mapResearchStatus = "Capture finished (45-second limit).";
            return;
        }

        if (args is not AgentReceiveEventArgs received)
            return;

        try
        {
            // Never modify the event, result or AtkValues.
            // Only copy bounded diagnostic information.

            string nativeValues = "";

            if (received.AtkValues != 0)
            {
                int count = (int)Math.Min(
                    received.ValueCount,
                    8u
                );

                var values = (AtkValue*)received.AtkValues;
                var entries = new List<string>();

                for (int i = 0; i < count; i++)
                {
                    var value = values[i];

                    string text = value.Type switch
                    {
                        AtkValueType.Int
                            => value.Int.ToString(),

                        AtkValueType.UInt
                            => value.UInt.ToString(),

                        _ => "(type " + value.Type + ")"
                    };

                    entries.Add(i + "=" + text);
                }

                nativeValues = string.Join(", ", entries);
            }

            uint selectedTerritory = 0;
            uint selectedMap = 0;
            uint currentTerritory = 0;
            uint currentMap = 0;
            string tooltip = "";
            string markerInfo = "";

            var map = NativeAgentMap.Instance();

            if (map != null)
            {
                selectedTerritory = map->SelectedTerritoryId;
                selectedMap = map->SelectedMapId;
                currentTerritory = map->CurrentTerritoryId;
                currentMap = map->CurrentMapId;

                tooltip = Clean(map->TooltipString.ToString());

                if (tooltip.Length > 160)
                    tooltip = tooltip.Substring(0, 160);

                // Native MapMarkerInfo.DataType == 3 is
                // associated with Aetheryte map markers.
                // DataKey mapping is only a candidate
                // until validated against in-game clicks.
                var markerNames = new List<string>();

                int markerCount = Math.Min(
                    (int)map->MapMarkerCount,
                    map->MapMarkers.Length
                );

                for (int i = 0; i < markerCount; i++)
                {
                    var marker = map->MapMarkers[i];

                    if (marker.DataType != 3)
                        continue;

                    string name = "?";

                    if (overworldStops.TryGetValue(
                        selectedTerritory,
                        out var stops))
                    {
                        var possible = stops.FirstOrDefault(x =>
                            x.Id == marker.DataKey
                        );

                        if (possible != null)
                            name = possible.Name;
                    }

                    markerNames.Add(
                        $"index={i}, key={marker.DataKey}, " +
                        $"sub={marker.MapMarkerSubKey}, " +
                        $"icon={marker.MapMarker.IconId}, " +
                        $"possible={name}"
                    );

                    if (markerNames.Count >= 16)
                        break;
                }

                markerInfo = string.Join(" | ", markerNames);
            }

            string signature =
                type + "/" +
                received.EventKind + "/" +
                received.ValueCount + "/" +
                nativeValues + "/" +
                selectedTerritory + "/" +
                selectedMap + "/" +
                tooltip;

            // Suppress consecutive duplicate event messages
            // without hiding later, distinct interactions.
            if (signature == mapResearchLastSignature)
                return;

            mapResearchLastSignature = signature;

            string entry =
                $"[{DateTime.Now:HH:mm:ss.fff}] {type}\n" +
                $"EventKind={received.EventKind}; " +
                $"ValueCount={received.ValueCount}\n" +
                $"Values=[{nativeValues}]\n" +
                $"ClientTerritory={Client.TerritoryType}; " +
                $"MapCurrentTerritory={currentTerritory}; " +
                $"MapCurrentId={currentMap}\n" +
                $"MapSelectedTerritory={selectedTerritory}; " +
                $"MapSelectedId={selectedMap}\n" +
                $"Tooltip={tooltip}\n" +
                $"NativeAetheryteMarkers=[{markerInfo}]";

            mapResearchEvents.Add(entry);

            if (mapResearchEvents.Count >= MapResearchLimit)
            {
                mapResearchArmed = false;
                mapResearchStatus =
                    "Capture reached 60 events and stopped.";
            }
            else
            {
                mapResearchStatus =
                    "Captured " + mapResearchEvents.Count +
                    " distinct Map agent events.";
            }
        }
        catch (Exception ex)
        {
            mapResearchArmed = false;
            mapResearchStatus =
                "Capture stopped after diagnostic read failure.";

            Log.Warning(
                "DEV8 map research diagnostic read failed: " +
                ex.Message
            );
        }
    }

    private string BuildMapResearchReport()
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "=== AETHERNETASSIST DEV8 TEST1 MAP RESEARCH ==="
        );

        builder.AppendLine(
            "Version: " + UiVersion
        );

        builder.AppendLine(
            "Mode: READ ONLY - NO AUTOMATIC TRAVEL"
        );

        builder.AppendLine(
            "Status: " + mapResearchStatus
        );

        builder.AppendLine(
            "Events: " + mapResearchEvents.Count
        );

        for (int i = 0; i < mapResearchEvents.Count; i++)
        {
            builder.AppendLine("--- Event " + (i + 1) + " ---");
            builder.AppendLine(mapResearchEvents[i]);
        }

        return builder.ToString();
    }

    private void DrawMapResearchControls()
    {
        if (mapResearchArmed &&
            DateTime.UtcNow > mapResearchUntil)
        {
            mapResearchArmed = false;
            mapResearchStatus = "Capture finished (45-second limit).";
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (!ImGui.CollapsingHeader(
            "MAP RESEARCH - DEV8 TEST1"))
            return;

        ImGui.TextColored(
            NavMuted,
            "Read-only native map interaction research."
        );

        ImGui.TextWrapped(
            "Arm capture, click a map Aetheryte normally, " +
            "then copy the results. No plugin travel is triggered."
        );

        ImGui.Spacing();

        if (mapResearchArmed)
        {
            int seconds = Math.Max(
                0,
                (int)Math.Ceiling(
                    (mapResearchUntil - DateTime.UtcNow)
                    .TotalSeconds
                )
            );

            ImGui.TextUnformatted(
                $"Recording ({seconds}s remaining)"
            );

            if (ImGui.Button("Stop capture##map"))
            {
                mapResearchArmed = false;
                mapResearchStatus = "Capture stopped manually.";
            }
        }
        else
        {
            if (ImGui.Button("Arm map capture (45s)"))
                ArmMapResearch();
        }

        ImGui.TextWrapped(mapResearchStatus);

        ImGui.TextUnformatted(
            $"Recorded events: {mapResearchEvents.Count}"
        );

        if (ImGui.Button("Copy map diagnostics"))
        {
            ImGui.SetClipboardText(
                BuildMapResearchReport()
            );
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Clear##map-research"))
        {
            mapResearchEvents.Clear();
            mapResearchLastSignature = "";
            mapResearchStatus = "Capture cleared.";
        }

        if (mapResearchEvents.Count > 0)
        {
            ImGui.Spacing();

            // Display a preview without expanding the settings
            // window into an enormous diagnostics view.
            if (ImGui.BeginChild(
                "##MapResearchPreview",
                new System.Numerics.Vector2(0f, 130f),
                true))
            {
                ImGui.TextWrapped(
                    mapResearchEvents[mapResearchEvents.Count - 1]
                );
            }

            ImGui.EndChild();
        }
    }
}