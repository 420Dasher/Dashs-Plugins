using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativeMap = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    // Original DEV8 research implementation.
    // Reads game UI events; never changes the event or input data.

    private sealed record MapHoverCandidate(
        uint Territory,
        uint MapId,
        ushort DataType,
        ushort DataKey,
        byte SubKey,
        uint IconId,
        int Slot,
        string Tooltip,
        DateTime ObservedAt
    );

    private readonly List<string> mapClickRecords = new();
    private MapHoverCandidate? mapClickHovered;

    private bool mapClickArmed;
    private DateTime mapClickUntil;
    private bool mapClickButtonDown;
    private bool mapClickDragged;
    private Vector2 mapClickStart;
    private DateTime mapClickPressTime;
    private string mapClickStatus = "Not recording.";
    private string mapClickPreview = "No destination selected.";
    private int mapClickInputEvents;

    private void InitializeMapClickResearch()
    {
        AddonLifecycle.RegisterListener(
            AddonEvent.PreReceiveEvent,
            "AreaMap",
            OnAreaMapInputResearch
        );

        Log.Information(
            "DEV8 TEST3: original read-only AreaMap observer initialized."
        );
    }

    private void DisposeMapClickResearch()
    {
        AddonLifecycle.UnregisterListener(
            AddonEvent.PreReceiveEvent,
            "AreaMap",
            OnAreaMapInputResearch
        );

        mapClickArmed = false;
    }

    private void ArmMapClickResearch()
    {
        mapClickRecords.Clear();
        mapClickHovered = null;
        mapClickButtonDown = false;
        mapClickDragged = false;
        mapClickInputEvents = 0;
        mapClickPreview = "No destination selected.";
        mapClickUntil = DateTime.UtcNow.AddSeconds(45);
        mapClickArmed = true;
        mapClickStatus = "Listening for left-clicks for 45 seconds.";
    }

    private bool MapClickCaptureActive()
    {
        if (!mapClickArmed)
            return false;

        if (DateTime.UtcNow <= mapClickUntil)
            return true;

        mapClickArmed = false;
        mapClickStatus = "Capture finished after 45 seconds.";
        return false;
    }

    // The Map agent emits a context identifying the marker
    // currently under the cursor. Capture its typed identity
    // independently from TEST2's diagnostic event buffer.
    private void ObserveMapClickHover(
        AgentEvent stage,
        AgentArgs args)
    {
        if (!MapClickCaptureActive() ||
            stage != AgentEvent.PostReceiveEvent ||
            args is not AgentReceiveEventArgs received ||
            received.AtkValues == 0 ||
            received.ValueCount < 2)
            return;

        var values = (AtkValue*)received.AtkValues;

        uint action;
        if (values[0].Type == AtkValueType.UInt)
            action = values[0].UInt;
        else if (values[0].Type == AtkValueType.Int)
            action = unchecked((uint)values[0].Int);
        else
            return;

        if (action != 9)
            return;

        uint context;
        if (values[1].Type == AtkValueType.UInt)
            context = values[1].UInt;
        else if (values[1].Type == AtkValueType.Int)
            context = unchecked((uint)values[1].Int);
        else
            return;

        var map = NativeMap.Instance();

        if (map == null || (context >> 24) != 2)
        {
            mapClickHovered = null;
            return;
        }

        uint slot = context & 0x00FFFFFFu;
        int count = Math.Min(
            (int)map->MapMarkerCount,
            map->MapMarkers.Length
        );

        if (slot >= (uint)count)
        {
            mapClickHovered = null;
            return;
        }

        var marker = map->MapMarkers[(int)slot];

        if (marker.DataType != 3 && marker.DataType != 4)
        {
            mapClickHovered = null;
            return;
        }

        string tooltip = Clean(map->TooltipString.ToString());

        mapClickHovered = new MapHoverCandidate(
            map->SelectedTerritoryId,
            map->SelectedMapId,
            marker.DataType,
            marker.DataKey,
            marker.MapMarkerSubKey,
            marker.MapMarker.IconId,
            (int)slot,
            tooltip,
            DateTime.UtcNow
        );
    }

    private void OnAreaMapInputResearch(
        AddonEvent stage,
        AddonArgs args)
    {
        if (!MapClickCaptureActive() ||
            args is not AddonReceiveEventArgs evt ||
            evt.AtkEventData == 0)
            return;

        try
        {
            var data = (AtkEventData*)evt.AtkEventData;
            var point = new Vector2(
                data->MouseData.PosX,
                data->MouseData.PosY
            );

            switch (evt.AtkEventType)
            {
                case AddonEventType.MouseDown:
                    if (data->MouseData.ButtonId != 0)
                        return;

                    mapClickButtonDown = true;
                    mapClickDragged = false;
                    mapClickStart = point;
                    mapClickPressTime = DateTime.UtcNow;
                    break;

                case AddonEventType.MouseMove:
                    if (mapClickButtonDown &&
                        Vector2.DistanceSquared(
                            mapClickStart,
                            point
                        ) > 64f)
                    {
                        mapClickDragged = true;
                    }
                    break;

                case AddonEventType.MouseUp:
                    if (data->MouseData.ButtonId != 0)
                        return;

                    mapClickInputEvents++;

                    bool clicked =
                        mapClickButtonDown &&
                        !mapClickDragged &&
                        Vector2.DistanceSquared(
                            mapClickStart, point
                        ) <= 64f &&
                        DateTime.UtcNow - mapClickPressTime
                            < TimeSpan.FromSeconds(3);

                    mapClickButtonDown = false;

                    if (!clicked)
                    {
                        AppendMapClickRecord(
                            "Left-button release ignored: " +
                            "no matching press or map drag."
                        );
                        return;
                    }

                    InspectMapClickCandidate(point);
                    break;
            }
        }
        catch (Exception ex)
        {
            mapClickArmed = false;
            mapClickStatus = "Input diagnostic stopped after an error.";
            Log.Warning("DEV8 map-click research: " + ex.Message);
        }
    }

    private void InspectMapClickCandidate(Vector2 mousePosition)
    {
        var hover = mapClickHovered;
        var nativeMap = NativeMap.Instance();

        if (hover == null || nativeMap == null)
        {
            AppendMapClickRecord(
                "Left-click detected, but no Aetheryte marker " +
                "was identified at the cursor."
            );
            return;
        }

        if (DateTime.UtcNow - hover.ObservedAt >
            TimeSpan.FromMilliseconds(350) ||
            hover.MapId != nativeMap->SelectedMapId ||
            hover.Territory != nativeMap->SelectedTerritoryId)
        {
            AppendMapClickRecord(
                "Left-click detected; cached marker context " +
                "was stale or belonged to another map."
            );
            return;
        }

        // Resolve the actual Aetheryte row rather than
        // incorrectly treating MapMarker.DataKey as the
        // destination ID for both kinds of marker.
        var matches = new List<Aetheryte>();

        foreach (var row in Data.GetExcelSheet<Aetheryte>())
        {
            if (row.Territory.Value.RowId != hover.Territory ||
                row.Invisible)
                continue;

            bool matchingMain =
                hover.DataType == 3 &&
                row.IsAetheryte &&
                row.RowId == hover.DataKey;

            bool matchingShard =
                hover.DataType == 4 &&
                !row.IsAetheryte &&
                row.AethernetGroup != 0 &&
                row.AethernetName.RowId == hover.DataKey;

            if (matchingMain || matchingShard)
                matches.Add(row);
        }

        if (matches.Count == 0)
        {
            AppendMapClickRecord(
                $"Left-click at {mousePosition}: marker slot " +
                $"{hover.Slot}, type={hover.DataType}, " +
                $"key={hover.DataKey}, sub={hover.SubKey}, " +
                $"tooltip='{hover.Tooltip}'. No matching destination row."
            );
            return;
        }

        // Do not guess if the map data refers to multiple
        // candidate rows. The diagnostic remains read-only.
        if (matches.Count != 1)
        {
            string ids = string.Join(
                ", ",
                matches.Select(x => x.RowId)
            );

            AppendMapClickRecord(
                $"Ambiguous map destination: type={hover.DataType}, " +
                $"key={hover.DataKey}, candidate IDs=[{ids}]."
            );
            return;
        }

        var destination = matches[0];

        string name = destination.AethernetName.Value.Name
            .ToString().Trim();

        if (string.IsNullOrWhiteSpace(name))
            name = "Aetheryte " + destination.RowId;

        string route;

        if (destination.IsAetheryte)
        {
            route = $"Paid teleport candidate -> {name}";
        }
        else if (networks.TryGetValue(
            (uint)destination.AethernetGroup,
            out var group))
        {
            var main = group.FirstOrDefault(x => x.IsMain);

            route = main == null
                ? $"Aethernet destination -> {name}"
                : $"Potential route: {main.Name} -> {name}";
        }
        else
        {
            route = $"Aethernet destination -> {name}";
        }

        mapClickPreview =
            $"{name} (Aetheryte ID {destination.RowId})\n" +
            route;

        AppendMapClickRecord(
            $"LEFT CLICK RESOLVED: {name}; " +
            $"AetheryteId={destination.RowId}; " +
            $"Territory={hover.Territory}; Map={hover.MapId}; " +
            $"MarkerType={hover.DataType}; Key={hover.DataKey}; " +
            $"SubKey={hover.SubKey}; Icon={hover.IconId}; " +
            $"Slot={hover.Slot}; Mouse={mousePosition}; " +
            $"Tooltip='{hover.Tooltip}'\n" +
            route
        );
    }

    private void AppendMapClickRecord(string value)
    {
        mapClickRecords.Add(
            $"[{DateTime.Now:HH:mm:ss.fff}] {value}"
        );

        if (mapClickRecords.Count > 32)
            mapClickRecords.RemoveAt(0);

        mapClickStatus =
            $"{mapClickRecords.Count} interaction records captured.";
    }

    private string BuildMapClickReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "=== AETHERNETASSIST DEV8 TEST3 MAP CLICKS ==="
        );
        builder.AppendLine("Version: " + UiVersion);
        builder.AppendLine("Mode: READ ONLY");
        builder.AppendLine("Status: " + mapClickStatus);
        builder.AppendLine(
            "Left-button release events: " + mapClickInputEvents
        );
        builder.AppendLine("Preview: " + mapClickPreview);

        foreach (var record in mapClickRecords)
        {
            builder.AppendLine("---");
            builder.AppendLine(record);
        }

        return builder.ToString();
    }

    private void DrawMapClickResearchControls()
    {
        ImGui.Spacing();
        ImGui.Separator();

        if (!ImGui.CollapsingHeader(
            "MAP LEFT-CLICK RESEARCH - DEV8 TEST3"))
            return;

        MapClickCaptureActive();

        ImGui.TextDisabled(
            "Original implementation | read-only | no teleport"
        );

        ImGui.TextWrapped(
            "Observes native AreaMap left-button presses and " +
            "releases. Dragging is excluded. Matching destinations " +
            "are resolved through the game's Aetheryte data."
        );

        if (mapClickArmed)
        {
            if (ImGui.Button("Stop left-click capture"))
            {
                mapClickArmed = false;
                mapClickStatus = "Stopped manually.";
            }
        }
        else if (ImGui.Button("Arm left-click capture (45s)"))
        {
            ArmMapClickResearch();
        }

        ImGui.TextWrapped(mapClickStatus);
        ImGui.TextWrapped("Last preview: " + mapClickPreview);

        if (ImGui.Button("Copy left-click diagnostics"))
        {
            ImGui.SetClipboardText(BuildMapClickReport());
        }

        if (mapClickRecords.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(mapClickRecords[^1]);
        }
    }
}