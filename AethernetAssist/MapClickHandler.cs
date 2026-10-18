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
using DalamudAgentId = Dalamud.Game.Agent.AgentId;

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
        Vector2 HoverScreenPosition,
        DateTime ObservedAt
    );

    private readonly List<string> mapClickRecords = new();
    private MapHoverCandidate? mapClickHovered;
    private MapHoverCandidate? mapClickPressedHover;

    private const float MapClickCursorTolerance = 28f;
    private const int MapClickHoverMaximumAgeMs = 220;


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

        AgentLifecycle.RegisterListener(
            AgentEvent.PostReceiveEvent,
            DalamudAgentId.Map,
            OnMapRoutingHoverEvent
        );

        Log.Information(
            "DEV9 TEST1: passive map-click observer initialized."
        );
    }

    private void DisposeMapClickResearch()
    {
        AddonLifecycle.UnregisterListener(
            AddonEvent.PreReceiveEvent,
            "AreaMap",
            OnAreaMapInputResearch
        );

        AgentLifecycle.UnregisterListener(OnMapRoutingHoverEvent);
        mapRouteInputEnabled = false;
    }

    private bool MapClickCaptureActive() =>
        mapRouteInputEnabled &&
        mapRoutePhase == MapRoutePhase.Idle;

    private void OnMapRoutingHoverEvent(
        AgentEvent stage,
        AgentArgs args)
    {
        if (!MapClickCaptureActive())
            return;

        try
        {
            ObserveMapClickHover(stage, args);
        }
        catch (Exception ex)
        {
            mapRouteInputEnabled = false;
            Log.Warning("Map hover listener disabled: " + ex);
        }
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
            ImGui.GetMousePos(),
            DateTime.UtcNow
        );
    }

    // Check temporal and screen-space continuity instead of
    // assuming a recently hovered marker was clicked.
    // All checks are observational; native input is untouched.
    private static bool IsFreshMapHover(MapHoverCandidate? candidate)
    {
        if (candidate == null)
            return false;

        double age =
            (DateTime.UtcNow - candidate.ObservedAt).TotalMilliseconds;

        return age >= 0 &&
               age <= MapClickHoverMaximumAgeMs;
    }

    private static bool IsMapCursorConsistent(
        Vector2 sampled,
        Vector2 actual)
    {
        if (!float.IsFinite(sampled.X) ||
            !float.IsFinite(sampled.Y) ||
            !float.IsFinite(actual.X) ||
            !float.IsFinite(actual.Y))
            return false;

        return Vector2.DistanceSquared(sampled, actual) <=
               MapClickCursorTolerance * MapClickCursorTolerance;
    }

    private static bool SameMapMarker(
        MapHoverCandidate first,
        MapHoverCandidate second)
    {
        return first.Territory == second.Territory &&
               first.MapId == second.MapId &&
               first.DataType == second.DataType &&
               first.DataKey == second.DataKey &&
               first.SubKey == second.SubKey &&
               first.IconId == second.IconId &&
               first.Slot == second.Slot &&
               string.Equals(
                   first.Tooltip,
                   second.Tooltip,
                   StringComparison.Ordinal
               );
    }

    // Verify the selected map still exposes exactly the
    // marker recorded at the start of this interaction.
    private static bool IsLiveMapMarker(
        MapHoverCandidate candidate,
        NativeMap* nativeMap)
    {
        if (nativeMap == null ||
            nativeMap->SelectedTerritoryId != candidate.Territory ||
            nativeMap->SelectedMapId != candidate.MapId)
            return false;

        int count = Math.Min(
            (int)nativeMap->MapMarkerCount,
            nativeMap->MapMarkers.Length
        );

        if (candidate.Slot < 0 ||
            candidate.Slot >= count)
            return false;

        var marker = nativeMap->MapMarkers[candidate.Slot];

        if (marker.DataType != candidate.DataType ||
            marker.DataKey != candidate.DataKey ||
            marker.MapMarkerSubKey != candidate.SubKey ||
            marker.MapMarker.IconId != candidate.IconId)
            return false;

        // Tooltip is a second independent UI signal.
        // A change to a trader, summoning bell, or other
        // map object must not reuse a prior travel marker.
        string currentTooltip =
            Clean(nativeMap->TooltipString.ToString());

        return string.Equals(
            currentTooltip,
            candidate.Tooltip,
            StringComparison.Ordinal
        );
    }

    private MapHoverCandidate? ReadPressedMapMarker(
        Vector2 pressPosition)
    {
        var hovered = mapClickHovered;
        var nativeMap = NativeMap.Instance();

        if (!IsFreshMapHover(hovered) ||
            hovered == null ||
            nativeMap == null)
            return null;

        if (!IsMapCursorConsistent(
            hovered.HoverScreenPosition,
            pressPosition
        ))
            return null;

        if (!IsLiveMapMarker(hovered, nativeMap))
            return null;

        return hovered;
    }
    private void OnAreaMapInputResearch(
        AddonEvent stage,
        AddonArgs args)
    {
        if (!MapClickCaptureActive() ||
            args is not AddonReceiveEventArgs evt ||
            evt.AtkEventData == 0)
            return;

        string researchStage = "Reading AreaMap input";

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

                    // Anchor the marker identity when the
                    // left mouse button is actually pressed.
                    // If input arrived without a fresh hover,
                    // the subsequent release cannot select it.
                    researchStage = "Anchoring mouse-down marker";
                    mapClickPressedHover =
                        ReadPressedMapMarker(point);
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

                    var pressedMarker = mapClickPressedHover;
                    mapClickPressedHover = null;

                    // No route may survive an unsuccessful
                    // click, an unrelated icon, or a map drag.
                    mapClickPreview = "No destination selected.";
                    ClearMapRouteSelection();

                    if (!clicked)
                    {
                        AppendMapClickRecord(
                            "Left-button release ignored: " +
                            "no matching press or map drag. " +
                            "Previous preview cleared."
                        );
                        return;
                    }

                    researchStage = "Validating click and destination";
                    InspectMapClickCandidate(point, pressedMarker);
                    break;
            }
        }
        catch (Exception ex)
        {
            mapRouteInputEnabled = false;

            string error =
                "DEV9 TEST1 error at " +
                researchStage + ": " +
                ex.GetType().FullName + ": " +
                ex.Message;

            AppendMapClickRecord(
                error + "\n" + ex.StackTrace
            );

            mapClickStatus =
                "Capture stopped: " +
                ex.GetType().Name +
                " during " + researchStage + ".";

            Log.Warning(error + "\n" + ex);
        }
    }

    private void InspectMapClickCandidate(
        Vector2 mousePosition,
        MapHoverCandidate? pressedMarker)
    {
        var hover = pressedMarker;
        var currentHover = mapClickHovered;
        var nativeMap = NativeMap.Instance();

        if (hover == null)
        {
            AppendMapClickRecord(
                "CLICK REJECTED: No fresh, position-matched " +
                "Aetheryte marker was present at mouse-down."
            );
            return;
        }

        if (!IsFreshMapHover(currentHover) ||
            currentHover == null)
        {
            AppendMapClickRecord(
                "CLICK REJECTED: No fresh marker context " +
                "was present at mouse-up."
            );
            return;
        }

        if (!SameMapMarker(hover, currentHover))
        {
            AppendMapClickRecord(
                "CLICK REJECTED: Marker identity or tooltip " +
                "changed between mouse-down and mouse-up. " +
                $"Pressed={hover.DataType}/{hover.DataKey}/" +
                $"{hover.MapId}; " +
                $"Released={currentHover.DataType}/" +
                $"{currentHover.DataKey}/{currentHover.MapId}."
            );
            return;
        }

        if (!IsMapCursorConsistent(
            currentHover.HoverScreenPosition,
            mousePosition
        ))
        {
            AppendMapClickRecord(
                "CLICK REJECTED: Pointer moved away from " +
                "the sampled marker position. " +
                $"Hover={currentHover.HoverScreenPosition}; " +
                $"Release={mousePosition}."
            );
            return;
        }

        if (nativeMap == null ||
            !IsLiveMapMarker(hover, nativeMap))
        {
            AppendMapClickRecord(
                "CLICK REJECTED: Live map layer, territory, " +
                "marker slot, or tooltip no longer matches " +
                "the mouse-down candidate."
            );
            return;
        }

        // This is still an observational safety gate.
        // A match proves continuity of the hovered marker,
        // not permission to teleport.
        // Resolve the actual Aetheryte row rather than
        // incorrectly treating MapMarker.DataKey as the
        // destination ID for both kinds of marker.
        var matches = new List<Aetheryte>();

        foreach (var row in Data.GetExcelSheet<Aetheryte>())
        {
            if (row.Territory.RowId != hover.Territory ||
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

        // A click on the crystal the player is already
        // standing beside requires no route, popup or
        // native Aethernet interaction.
        if (MapRouteAlreadyNearDestination(
            destination.RowId,
            hover.Territory))
        {
            mapClickPreview = "No destination selected.";

            AppendMapClickRecord(
                "CLICK IGNORED: Already within interaction range " +
                $"of destination ID {destination.RowId}. " +
                "No route created."
            );

            return;
        }

        string? catalogName = null;

        if (networks.TryGetValue(
            (uint)destination.AethernetGroup,
            out var nameGroup))
        {
            catalogName = nameGroup.FirstOrDefault(
                x => x.Id == destination.RowId
            )?.Name;
        }

        string name = !string.IsNullOrWhiteSpace(catalogName)
            ? catalogName
            : !string.IsNullOrWhiteSpace(hover.Tooltip)
                ? hover.Tooltip
                : "Aetheryte " + destination.RowId;

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

        PrepareMapRoute(destination.RowId);
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

        Log.Information("DEV9 map click: " + value);
    }

}
