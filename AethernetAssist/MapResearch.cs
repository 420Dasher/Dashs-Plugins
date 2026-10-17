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
    private string mapResearchStatus = "Not recording.";

    private const int MapResearchLimit = 120;

    private void InitializeMapResearch()
    {
        foreach (var eventType in new[]
        {
            AgentEvent.PreReceiveEvent,
            AgentEvent.PostReceiveEvent,
            AgentEvent.PreReceiveEventWithResult,
            AgentEvent.PostReceiveEventWithResult
        })
        {
            AgentLifecycle.RegisterListener(
                eventType,
                DalamudAgentId.Map,
                OnMapResearchEvent
            );
        }

        Log.Information(
            "DEV8 TEST2: read-only pre/post Map listeners registered."
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

        mapResearchArmed = true;
        mapResearchUntil = DateTime.UtcNow.AddSeconds(45);

        mapResearchStatus =
            "Recording map interactions for 45 seconds.";

        Log.Information(
            "DEV8 TEST2: exact marker capture armed."
        );
    }

    private static string DescribeMapMarker(
        NativeAgentMap* map,
        uint context)
    {
        uint family = context >> 24;
        uint slot = context & 0x00FFFFFFu;

        int count = Math.Min(
            (int)map->MapMarkerCount,
            map->MapMarkers.Length
        );

        var result = new StringBuilder();

        result.AppendLine(
            $"PackedContext=0x{context:X8}; " +
            $"Family={family}; Slot={slot}"
        );

        result.AppendLine(
            $"MapMarkerCount={count}; " +
            $"MapMarkerCapacity={map->MapMarkers.Length}"
        );

        // The relationship between packed context and
        // MapMarkers[] is being investigated, not assumed.
        if (slot >= (uint)count)
        {
            result.AppendLine(
                "Candidate MapMarkers slot is outside populated range."
            );

            return result.ToString();
        }

        var marker = map->MapMarkers[(int)slot];

        result.AppendLine(
            $"CandidateSlot={slot}; " +
            $"DataType={marker.DataType}; " +
            $"DataKey={marker.DataKey}; " +
            $"SubKey={marker.MapMarkerSubKey}"
        );

        result.AppendLine(
            $"IconId={marker.MapMarker.IconId}; " +
            $"SecondaryIconId={marker.MapMarker.SecondaryIconId}; " +
            $"Flags={marker.MapMarker.IconFlags}; " +
            $"Scale={marker.MapMarker.Scale}"
        );

        result.AppendLine(
            $"MarkerX={marker.MapMarker.X}; " +
            $"MarkerY={marker.MapMarker.Y}"
        );

        return result.ToString();
    }

    private string DescribeCandidateAetherytes(
        ushort dataKey,
        uint territory)
    {
        var results = new List<string>();

        // Compare the observed DataKey with the existing
        // Aethernet sheet catalogue. This is a candidate
        // match, not a validated map-click destination.
        foreach (var shard in networks.Values
            .SelectMany(x => x)
            .Where(x => x.Id == dataKey)
            .Take(8))
        {
            results.Add(
                $"Aethernet: {shard.Name} " +
                $"(ID={shard.Id}, Territory={shard.Territory}, " +
                $"Group={shard.Group})"
            );
        }

        foreach (var stop in overworldStops.Values
            .SelectMany(x => x)
            .Where(x => x.Id == dataKey)
            .Take(8))
        {
            results.Add(
                $"Overworld: {stop.Name} " +
                $"(ID={stop.Id}, Territory={stop.Territory})"
            );
        }

        return results.Count == 0
            ? "No direct Aetheryte-row match for DataKey."
            : string.Join(" | ", results.Distinct());
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
            mapResearchStatus = "Capture finished after 45 seconds.";
            return;
        }

        if (args is not AgentReceiveEventArgs received)
            return;

        try
        {
            // Diagnostic reading only. Never modify arguments.
            var entries = new List<string>();

            bool hasContext = false;
            uint packedContext = 0;

            if (received.AtkValues != 0)
            {
                int count = (int)Math.Min(
                    received.ValueCount,
                    8u
                );

                var values = (AtkValue*)received.AtkValues;

                for (int i = 0; i < count; i++)
                {
                    var value = values[i];

                    string text = value.Type switch
                    {
                        AtkValueType.Int => value.Int.ToString(),
                        AtkValueType.UInt => value.UInt.ToString(),
                        _ => "(type " + value.Type + ")"
                    };

                    entries.Add($"{i}={text}");

                    if (i == 1)
                    {
                        if (value.Type == AtkValueType.UInt)
                        {
                            packedContext = value.UInt;
                            hasContext = true;
                        }
                        else if (value.Type == AtkValueType.Int)
                        {
                            packedContext = unchecked((uint)value.Int);
                            hasContext = true;
                        }
                    }
                }
            }

            string valuesText = string.Join(", ", entries);

            uint currentTerritory = 0;
            uint currentMap = 0;
            uint selectedTerritory = 0;
            uint selectedMap = 0;

            string tooltip = "";
            string slotInfo = "No numeric marker context.";
            string candidateInfo = "No candidate marker.";
            string allAetheryteMarkers = "";

            var map = NativeAgentMap.Instance();

            if (map != null)
            {
                currentTerritory = map->CurrentTerritoryId;
                currentMap = map->CurrentMapId;
                selectedTerritory = map->SelectedTerritoryId;
                selectedMap = map->SelectedMapId;

                tooltip = Clean(map->TooltipString.ToString());

                if (tooltip.Length > 160)
                    tooltip = tooltip.Substring(0, 160);

                int markerCount = Math.Min(
                    (int)map->MapMarkerCount,
                    map->MapMarkers.Length
                );

                if (hasContext)
                {
                    slotInfo = DescribeMapMarker(
                        map,
                        packedContext
                    );

                    uint slot = packedContext & 0x00FFFFFFu;

                    if (slot < (uint)markerCount)
                    {
                        var marker = map->MapMarkers[(int)slot];

                        candidateInfo = DescribeCandidateAetherytes(
                            marker.DataKey,
                            selectedTerritory
                        );
                    }
                }

                var markerSummaries = new List<string>();

                for (int i = 0;
                     i < markerCount && markerSummaries.Count < 16;
                     i++)
                {
                    var marker = map->MapMarkers[i];

                    if (marker.DataType != 3)
                        continue;

                    markerSummaries.Add(
                        $"slot={i}, key={marker.DataKey}, " +
                        $"sub={marker.MapMarkerSubKey}, " +
                        $"icon={marker.MapMarker.IconId}"
                    );
                }

                allAetheryteMarkers =
                    string.Join(" | ", markerSummaries);
            }

            string entry =
                $"[{DateTime.Now:HH:mm:ss.fff}] {type}\n" +
                $"EventKind={received.EventKind}; " +
                $"ValueCount={received.ValueCount}\n" +
                $"Values=[{valuesText}]\n" +
                $"ClientTerritory={Client.TerritoryType}; " +
                $"MapCurrentTerritory={currentTerritory}; " +
                $"MapCurrentId={currentMap}\n" +
                $"MapSelectedTerritory={selectedTerritory}; " +
                $"MapSelectedId={selectedMap}\n" +
                $"TooltipAtEvent={tooltip}\n" +
                $"CandidateMarker:\n{slotInfo}" +
                $"CandidateAetheryteRows={candidateInfo}\n" +
                $"Type3Markers=[{allAetheryteMarkers}]";

            mapResearchEvents.Add(entry);

            if (mapResearchEvents.Count >= MapResearchLimit)
            {
                mapResearchArmed = false;
                mapResearchStatus =
                    "Capture limit reached (120 events).";
            }
            else
            {
                mapResearchStatus =
                    $"Captured {mapResearchEvents.Count} map events.";
            }
        }
        catch (Exception ex)
        {
            mapResearchArmed = false;

            mapResearchStatus =
                "Diagnostic capture stopped after read failure.";

            Log.Warning(
                "DEV8 TEST2 map research failed: " +
                ex.Message
            );
        }
    }

    private string BuildMapResearchReport()
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "=== AETHERNETASSIST DEV8 TEST2 EXACT MARKERS ==="
        );

        builder.AppendLine("Version: " + UiVersion);
        builder.AppendLine("Mode: READ ONLY");
        builder.AppendLine("Status: " + mapResearchStatus);
        builder.AppendLine("Events: " + mapResearchEvents.Count);

        for (int i = 0; i < mapResearchEvents.Count; i++)
        {
            builder.AppendLine(
                "--- Event " + (i + 1) + " ---"
            );

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
            mapResearchStatus = "Capture finished after 45 seconds.";
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (!ImGui.CollapsingHeader(
            "MAP RESEARCH - DEV8 TEST2"))
            return;

        ImGui.TextColored(
            NavMuted,
            "Exact marker resolution - read only"
        );

        ImGui.TextWrapped(
            "Captures native events before and after processing, " +
            "including packed marker context, candidate slot, " +
            "marker IDs, icon, coordinates and possible Aetheryte rows."
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
            mapResearchStatus = "Capture cleared.";
        }

        if (mapResearchEvents.Count > 0)
        {
            ImGui.Spacing();

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