using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private enum MapRoutePhase
    {
        Idle,
        WaitingForPaidArrival,
        WaitingForSource,
        WaitingForShard
    }

    private MapRoutePhase mapRoutePhase = MapRoutePhase.Idle;
    private Shard? mapRouteTarget;
    private Shard? mapRouteMain;
    private bool mapRouteInputEnabled = true;
    private bool mapRouteWindowOpen;
    private bool mapRoutePreviewDirect;
    private bool mapRouteSawLoading;
    private uint mapRouteQuotedCost;
    private DateTime mapRouteStarted;
    private string mapRouteStatus = "";

    private bool MapRouteCanStart()
    {
        return mapRoutePhase == MapRoutePhase.Idle &&
               phase == TravelPhase.Idle &&
               overworldPhase == OverworldPhase.Idle &&
               housingPhase == HousingPhase.Idle &&
               residentialPhase == ResidentialPhase.Idle &&
               cosmicPhase == CosmicPhase.Idle &&
               cosmicLocalPhase == CosmicLocalPhase.Idle &&
               worldVisitPhase == WorldVisitPhase.Idle &&
               Objects.LocalPlayer != null &&
               !IsBusy();
    }

    private bool MapRouteNearSource(Shard target)
    {
        var player = Objects.LocalPlayer;

        return nearby != null &&
               player != null &&
               nearby.IsTargetable &&
               activeGroup == target.Group &&
               activeTerritory == Client.TerritoryType &&
               Vector3.Distance(
                   player.Position, nearby.Position
               ) <= config.OpenRange;
    }

    private bool MapRouteAtMain(Shard main)
    {
        var player = Objects.LocalPlayer;

        if (player == null ||
            Client.TerritoryType != main.Territory)
            return false;

        foreach (var obj in Objects)
        {
            if (obj == null ||
                obj.ObjectKind != ObjectKind.Aetheryte ||
                obj.BaseId != main.Id)
                continue;

            if (Vector3.Distance(
                player.Position,
                obj.Position
            ) < 28f)
                return true;
        }

        return false;
    }

    private bool MapRouteTryQuote(
        Shard target,
        Shard main,
        out bool direct,
        out uint gil,
        out string reason)
    {
        direct = false;
        gil = 0;
        reason = "";

        if (!MapRouteCanStart())
        {
            reason = "Another action or loading state is active.";
            return false;
        }

        if (!DestinationAvailable(target))
        {
            reason = "Destination is not attuned or available.";
            return false;
        }

        ScanNearby();

        if (target.IsMain && MapRouteAtMain(main))
        {
            reason = "You are already at this Aetheryte.";
            return false;
        }

        if (!target.IsMain && MapRouteNearSource(target))
        {
            direct = true;
            return true;
        }

        if (!IsUnlocked(main.Id))
        {
            reason = "The network's main Aetheryte is not attuned.";
            return false;
        }

        var entry = Aetherytes.FirstOrDefault(x =>
            x.AetheryteId == main.Id &&
            x.TerritoryId == main.Territory &&
            x.SubIndex == 0 &&
            !x.IsSharedHouse &&
            !x.IsApartment
        );

        if (entry == null)
        {
            reason = "No live teleport entry for the main Aetheryte.";
            return false;
        }

        gil = (uint)entry.GilCost;
        return true;
    }

    // Called only after the map handler validates a
    // completed left-click on a unique, live map marker.
    private void PrepareMapRoute(uint destinationId)
    {
        if (mapRoutePhase != MapRoutePhase.Idle)
            return;

        Shard? target = null;

        foreach (var group in networks.Values)
        {
            var candidate = group.FirstOrDefault(x =>
                x.Id == destinationId &&
                !x.Invisible);

            if (candidate == null)
                continue;

            if (target != null)
                return;

            target = candidate;
        }

        if (target == null ||
            !networks.TryGetValue(target.Group, out var members))
            return;

        var main = members.FirstOrDefault(x => x.IsMain);
        if (main == null)
            return;

        mapRouteTarget = target;
        mapRouteMain = main;
        mapRouteWindowOpen = true;

        if (MapRouteTryQuote(
            target, main,
            out mapRoutePreviewDirect,
            out mapRouteQuotedCost,
            out string reason))
        {
            mapRouteStatus = "Ready for confirmation.";
        }
        else
        {
            mapRouteStatus = reason;
        }
    }

    private void ClearMapRouteSelection()
    {
        if (mapRoutePhase != MapRoutePhase.Idle)
            return;

        mapRouteTarget = null;
        mapRouteMain = null;
        mapRouteWindowOpen = false;
        mapRouteQuotedCost = 0;
        mapRoutePreviewDirect = false;
        mapRouteStatus = "";
    }

    private void ConfirmMapRoute()
    {
        var target = mapRouteTarget;
        var main = mapRouteMain;

        if (target == null ||
            main == null ||
            mapRoutePhase != MapRoutePhase.Idle)
            return;

        if (!MapRouteTryQuote(
            target,
            main,
            out bool direct,
            out uint currentCost,
            out string reason))
        {
            mapRouteStatus = reason;
            return;
        }

        if (direct != mapRoutePreviewDirect ||
            currentCost != mapRouteQuotedCost)
        {
            mapRoutePreviewDirect = direct;
            mapRouteQuotedCost = currentCost;
            mapRouteStatus =
                "Route or gil cost changed. Review and confirm again.";
            return;
        }

        if (direct)
        {
            StartTravel(target);

            if (phase == TravelPhase.Idle)
            {
                mapRouteStatus =
                    "Unable to start local Aethernet travel.";
                return;
            }

            mapRoutePhase = MapRoutePhase.WaitingForShard;
            mapRouteStarted = DateTime.UtcNow;
            mapRouteStatus = "Local Aethernet travel started.";
            mapRouteWindowOpen = true;
            return;
        }

        // Revalidate the native teleport entry immediately
        // before submitting an irrevocable paid request.
        var entry = Aetherytes.FirstOrDefault(x =>
            x.AetheryteId == main.Id &&
            x.TerritoryId == main.Territory &&
            x.SubIndex == 0 &&
            !x.IsSharedHouse &&
            !x.IsApartment
        );

        if (entry == null ||
            (uint)entry.GilCost != currentCost ||
            !IsUnlocked(main.Id))
        {
            mapRouteStatus =
                "The live teleport quote changed. Select again.";
            mapRouteTarget = null;
            mapRouteMain = null;
            return;
        }

        var telepo = Telepo.Instance();

        if (telepo == null)
        {
            mapRouteStatus = "Native teleport service unavailable.";
            return;
        }

        mapRouteSawLoading = false;

        // One paid request. No automatic retry.
        bool accepted = telepo->Teleport(
            entry.AetheryteId,
            entry.SubIndex
        );

        if (!accepted)
        {
            mapRouteStatus = "Native teleport request rejected.";
            return;
        }

        mapRoutePhase = MapRoutePhase.WaitingForPaidArrival;
        mapRouteStarted = DateTime.UtcNow;
        mapRouteStatus = "Teleporting to " + main.Name + "...";

        Log.Information(
            $"DEV9 map route paid teleport: {main.Name}, " +
            $"ID={main.Id}, cost={currentCost:N0} gil."
        );
    }

    private void TickMapRoute()
    {
        if (mapRoutePhase == MapRoutePhase.Idle)
            return;

        var target = mapRouteTarget;
        var main = mapRouteMain;

        if (target == null || main == null)
        {
            StopMapRoute("Route target was lost.");
            return;
        }

        double elapsed =
            (DateTime.UtcNow - mapRouteStarted).TotalSeconds;

        if (mapRoutePhase == MapRoutePhase.WaitingForPaidArrival)
        {
            if (IsBusy() || Objects.LocalPlayer == null)
            {
                mapRouteSawLoading = true;
                return;
            }

            if (mapRouteSawLoading &&
                Client.TerritoryType == main.Territory &&
                MapRouteAtMain(main))
            {
                if (target.IsMain)
                {
                    CompleteMapRoute();
                    return;
                }

                mapRoutePhase = MapRoutePhase.WaitingForSource;
                mapRouteStarted = DateTime.UtcNow;
                mapRouteStatus = "Finding arrival Aethernet crystal...";
                return;
            }

            if (elapsed > 65)
            {
                StopMapRoute(
                    "Paid teleport arrival not confirmed. " +
                    "No further travel was attempted."
                );
            }

            return;
        }

        if (mapRoutePhase == MapRoutePhase.WaitingForSource)
        {
            if (IsBusy() || Objects.LocalPlayer == null)
                return;

            if (Client.TerritoryType != main.Territory)
            {
                StopMapRoute("Unexpected destination territory.");
                return;
            }

            ScanNearby();

            if (MapRouteNearSource(target))
            {
                StartTravel(target);

                if (phase != TravelPhase.Idle)
                {
                    mapRoutePhase = MapRoutePhase.WaitingForShard;
                    mapRouteStarted = DateTime.UtcNow;
                    mapRouteStatus =
                        "Aethernet travel to " + target.Name + "...";
                    return;
                }
            }

            if (elapsed > 25)
            {
                StopMapRoute(
                    "No usable arrival crystal found. Route stopped."
                );
            }

            return;
        }

        if (mapRoutePhase == MapRoutePhase.WaitingForShard)
        {
            // The existing Aethernet travel state machine
            // performs the actual native menu interaction.
            if (phase == TravelPhase.Idle)
            {
                if (status.StartsWith(
                    "Arrived:", StringComparison.Ordinal))
                {
                    CompleteMapRoute();
                }
                else
                {
                    StopMapRoute(
                        "Aethernet stage ended: " + status
                    );
                }

                return;
            }

            if (elapsed > 65)
            {
                Cancel("Map route Aethernet stage timed out.");
                StopMapRoute("Aethernet stage timed out.");
            }
        }
    }

    private void CompleteMapRoute()
    {
        Log.Information(
            "DEV9 map route completed: " +
            (mapRouteTarget?.Name ?? "Unknown")
        );

        mapRoutePhase = MapRoutePhase.Idle;
        mapRouteTarget = null;
        mapRouteMain = null;
        mapRouteWindowOpen = false;
        mapRouteStatus = "";
    }

    private void StopMapRoute(string reason)
    {
        var previous = mapRoutePhase;
        mapRoutePhase = MapRoutePhase.Idle;
        mapRouteTarget = null;
        mapRouteMain = null;
        mapRouteStatus = reason;
        mapRouteWindowOpen = true;

        if (previous == MapRoutePhase.WaitingForShard &&
            phase != TravelPhase.Idle)
        {
            Cancel(reason);
        }

        Log.Warning("DEV9 map route stopped: " + reason);
    }

    private void DrawMapRouteWindow()
    {
        if (!mapRouteWindowOpen)
            return;

        PushTheme();

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(356f, 156f),
                ImGuiCond.FirstUseEver
            );

            bool open = mapRouteWindowOpen;

            if (!ImGui.Begin(
                "Aethernet Assist - Map Route###AethernetAssistMapRoute",
                ref open,
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                mapRouteWindowOpen = open;
                return;
            }

            ImGui.TextColored(NavAccent, "MAP ROUTE");
            ImGui.Separator();

            if (mapRouteTarget is { } target &&
                mapRouteMain is { } main)
            {
                ImGui.TextWrapped(
                    "Destination: " + target.Name
                );

                if (mapRoutePhase == MapRoutePhase.Idle)
                {
                    ImGui.TextDisabled(
                        target.IsMain
                            ? "Direct to main Aetheryte"
                            : mapRoutePreviewDirect
                                ? "Local Aethernet -> " + target.Name
                                : main.Name + " -> " + target.Name
                    );

                    ImGui.TextWrapped(
                        mapRoutePreviewDirect
                            ? "No paid teleport required."
                            : $"Paid teleport: {mapRouteQuotedCost:N0} gil"
                    );

                    if (mapRouteStatus != "Ready for confirmation.")
                        ImGui.TextWrapped(mapRouteStatus);

                    if (ImGui.Button("Confirm route##map-confirm"))
                        ConfirmMapRoute();

                    ImGui.SameLine();

                    if (ImGui.Button("Cancel##map-cancel"))
                        ClearMapRouteSelection();
                }
                else
                {
                    ImGui.TextWrapped(mapRouteStatus);

                    if (ImGui.Button("Stop tracking##map-stop"))
                    {
                        StopMapRoute(
                            "Tracking stopped. An accepted teleport " +
                            "cannot be reversed."
                        );
                    }
                }
            }
            else
            {
                ImGui.TextWrapped(mapRouteStatus);

                if (ImGui.Button("Close##map-close"))
                    mapRouteWindowOpen = false;
            }

            ImGui.End();

            if (!open &&
                mapRoutePhase != MapRoutePhase.Idle)
            {
                StopMapRoute(
                    "Route window closed. An accepted teleport " +
                    "cannot be reversed."
                );
            }

            mapRouteWindowOpen = open && mapRouteWindowOpen;
        }
        finally
        {
            ImGui.PopStyleColor(ThemePushCount);
        }
    }
}