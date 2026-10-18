using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
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

    private Task<List<Vector3>>? mapRouteWalkPathTask;
    private bool mapRouteWalkAttempted;
    private bool mapRouteWalkOwnsPath;
    private DateTime mapRouteWalkStarted;
    private DateTime mapRouteWalkLastProgress;
    private Vector3 mapRouteWalkLastPosition;

    // Never interact on the same frame that movement stops.
    private bool mapRouteSettling;
    private DateTime mapRouteSettleStarted;
    private DateTime mapRouteStillSince = DateTime.MinValue;
    private Vector3 mapRouteStillAnchor;

    // Only nonzero when a direct route has been clipped to
    // a verified mesh point near its source crystal.
    private float mapRouteDirectStopRange;

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

    // DEV9 TEST3
    //
    // A validated map destination must not initiate any
    // route if the player is already beside THAT crystal.
    //
    // Match the actual game object's BaseId and territory,
    // not another shard's name or shared network group.
    private bool MapRouteAlreadyNearDestination(
        uint destinationId,
        uint destinationTerritory)
    {
        var player = Objects.LocalPlayer;

        if (player == null ||
            IsBusy() ||
            Client.TerritoryType != destinationTerritory)
            return false;

        // Only known, visible Aethernet destinations qualify.
        // The map may contain Aetherytes not supported by
        // the local city Aethernet catalogue.
        bool knownDestination = networks.Values.Any(group =>
            group.Any(shard =>
                shard.Id == destinationId &&
                shard.Territory == destinationTerritory &&
                !shard.Invisible
            )
        );

        if (!knownDestination)
            return false;

        float range = Math.Clamp(
            config.OpenRange,
            8.0f,
            11.1f
        );

        foreach (var obj in Objects)
        {
            if (obj == null ||
                !obj.IsTargetable ||
                obj.ObjectKind != ObjectKind.Aetheryte ||
                obj.BaseId != destinationId)
                continue;

            if (Vector3.Distance(
                player.Position,
                obj.Position
            ) <= range)
                return true;
        }

        return false;
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

    // The 11.1y detection range is not the safe
    // automated interaction distance.
    //
    // At the default proximity setting, we walk to 9y.
    // Lower configured ranges get an additional 0.8y margin.
    private float MapRouteSafeInteractionRange() =>
        MathF.Min(9.0f, config.OpenRange - 0.8f);

    private bool MapRouteInApproachRange(Shard target)
    {
        var player = Objects.LocalPlayer;

        if (player == null ||
            nearby == null ||
            !MapRouteNearSource(target))
            return false;

        // A local walking route must finish beside the
        // same real crystal selected before movement.
        if (mapRouteWalkingLocal)
        {
            var remembered = MapRouteGetRememberedSource();

            if (remembered == null ||
                remembered.Address != nearby.Address)
                return false;
        }

        float stoppingRange = mapRouteDirectStopRange > 0f
            ? mapRouteDirectStopRange
            : MapRouteSafeInteractionRange();

        return Vector3.Distance(
            player.Position, nearby.Position
        ) <= stoppingRange;
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
        out bool walkLocal,
        out uint gil,
        out string reason)
    {
        direct = false;
        walkLocal = false;
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

        // Do not open the native Aethernet menu for a
        // self-teleport. The current shard is already
        // the requested destination.
        if (!target.IsMain &&
            MapRouteInApproachRange(target) &&
            nearby != null &&
            nearby.BaseId == target.Id)
        {
            reason = "Already at " + target.Name + ".";
            return false;
        }

        if (!target.IsMain && !mapRouteExplicitPaid)
        {
            if (MapRouteInApproachRange(target))
            {
                direct = true;
                return true;
            }

            if (MapRouteInLocalNetwork(target))
            {
                if (MapRouteFindLocalSource(target) != null)
                {
                    walkLocal = true;
                    return true;
                }

                reason =
                    "No loaded local crystal found nearby. " +
                    "Approach a shard or review the paid alternative.";
                return false;
            }
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

        if (MapRouteAlreadyNearDestination(
            target.Id, target.Territory))
        {
            ClearMapRouteSelection();

            Log.Information(
                "DEV9 TEST3: No-op route; player already near " +
                target.Name + " (ID " + target.Id + ")."
            );

            return;
        }

        mapRouteTarget = target;
        mapRouteMain = main;
        mapRouteWindowOpen = true;
        mapRouteExplicitPaid = false;
        mapRouteWalkingLocal = false;
        mapRouteWalkingToTarget = false;

        mapRouteQuoteReady = MapRouteTryQuote(
            target, main,
            out mapRoutePreviewDirect,
            out mapRoutePreviewWalk,
            out mapRouteQuotedCost,
            out string reason);

        mapRouteStatus = mapRouteQuoteReady
            ? "Ready for confirmation."
            : reason;
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
        mapRoutePreviewWalk = false;
        mapRouteExplicitPaid = false;
        mapRouteQuoteReady = false;
        mapRouteWalkingLocal = false;
        mapRouteWalkingToTarget = false;
        mapRouteLocalSourceAddress = 0;
        mapRouteStatus = "";
    }

    private void ConfirmMapRoute()
    {
        var target = mapRouteTarget;
        var main = mapRouteMain;

        if (target == null ||
            main == null ||
            mapRoutePhase != MapRoutePhase.Idle ||
            !mapRouteQuoteReady)
            return;

        if (!MapRouteTryQuote(
            target,
            main,
            out bool direct,
            out bool walkLocal,
            out uint currentCost,
            out string reason))
        {
            mapRouteQuoteReady = false;
            mapRouteStatus = reason;
            return;
        }

        if (direct != mapRoutePreviewDirect ||
            walkLocal != mapRoutePreviewWalk ||
            currentCost != mapRouteQuotedCost)
        {
            mapRoutePreviewDirect = direct;
            mapRoutePreviewWalk = walkLocal;
            mapRouteQuotedCost = currentCost;
            mapRouteStatus =
                "Route or gil cost changed. Review and confirm again.";
            return;
        }

        ResetMapRoutePathRecovery();

        if (direct)
        {
            // Even nearby crystals must pass the same
            // stationary check before native interaction.
            ScanNearby();

            if (nearby == null ||
                !MapRouteInApproachRange(target))
            {
                mapRouteQuoteReady = false;
                mapRouteStatus =
                    "Source moved. Select the map destination again.";
                return;
            }

            MapRouteRememberLocalSource(nearby);
            mapRouteWalkingLocal = true;
            mapRouteSettling = false;
            mapRoutePhase = MapRoutePhase.WaitingForSource;
            mapRouteStarted = DateTime.UtcNow;
            mapRouteStatus =
                "Preparing local Aethernet interaction...";
            return;
        }

        if (walkLocal)
        {
            var source = MapRouteFindLocalSource(target);

            if (source == null)
            {
                mapRouteQuoteReady = false;
                mapRouteStatus =
                    "Local crystal disappeared. Select the icon again.";
                return;
            }

            MapRouteRememberLocalSource(source);
            mapRouteWalkingLocal = true;

            // This is the key distinction: if the crystal
            // chosen for walking IS the requested destination,
            // arrival on foot completes the route. There is
            // no native teleport to submit.
            mapRouteWalkingToTarget =
                source.BaseId == target.Id;

            mapRouteSettling = false;
            mapRoutePhase = MapRoutePhase.WaitingForSource;
            mapRouteStarted = DateTime.UtcNow;

            mapRouteStatus = mapRouteWalkingToTarget
                ? "Walking directly to " + target.Name + "..."
                : "Approaching " + mapRouteLocalSourceName +
                  " for free Aethernet travel...";

            Log.Information(
                "DEV9 TEST2 FIX2: local source BaseID=" +
                source.BaseId +
                ", destination AetheryteID=" +
                target.Id +
                ", walkingDirectlyToTarget=" +
                mapRouteWalkingToTarget
            );

            return;
        }

        mapRouteWalkingLocal = false;
        mapRouteWalkingToTarget = false;

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

    // DEV9 TEST1 FIX2
    //
    // Pathfinding is requested asynchronously. Only after
    // it succeeds do we submit the path to vnavmesh.
    //
    // This prevents a delayed pathfinding result from
    // initiating movement after the route was cancelled.

    private void StopMapRouteWalk()
    {
        bool owned = mapRouteWalkOwnsPath;

        mapRouteWalkOwnsPath = false;
        mapRouteWalkPathTask = null;
        mapRouteWalkAttempted = false;
        mapRouteDirectActive = false;

        if (!owned)
            return;

        try
        {
            Pi.GetIpcSubscriber<object>(
                "vnavmesh.Path.Stop"
            ).InvokeAction();
        }
        catch (Exception ex)
        {
            Log.Warning(
                "Map route movement stop failed: " + ex.Message
            );
        }
    }

    private bool BeginMapRouteWalk(Shard main)
    {
        var player = Objects.LocalPlayer;

        uint expectedTerritory = mapRouteWalkingLocal
            ? mapRouteLocalSourceTerritory
            : main.Territory;

        if (player == null ||
            Client.TerritoryType != expectedTerritory)
        {
            StopMapRoute(
                "Arrival position is no longer valid."
            );
            return false;
        }

        if (HousingMovementKeysPressed())
        {
            StopMapRoute(
                "Cancelled by manual movement."
            );
            return false;
        }

        // Locate the actual loaded main Aetheryte rather
        // than using a hardcoded coordinate or map marker.
        var crystal = mapRouteWalkingLocal
            ? MapRouteGetRememberedSource()
            : Objects.FirstOrDefault(obj =>
                obj != null &&
                obj.IsTargetable &&
                obj.ObjectKind == ObjectKind.Aetheryte &&
                obj.BaseId == main.Id &&
                Vector3.Distance(
                    player.Position, obj.Position
                ) <= 75f
            );

        if (crystal == null)
        {
            StopMapRoute(
                "Arrival Aetheryte not found nearby. " +
                "Move to the crystal manually."
            );
            return false;
        }

        // Short and apparently unobstructed approaches
        // use direct movement first. Every other situation
        // retains the existing navmesh pathfinder.
        if (TryStartMapRouteDirectApproach(
            crystal, player.Position))
            return true;

        try
        {
            bool running = Pi.GetIpcSubscriber<bool>(
                "vnavmesh.Path.IsRunning"
            ).InvokeFunc();

            bool finding = Pi.GetIpcSubscriber<bool>(
                "vnavmesh.SimpleMove.PathfindInProgress"
            ).InvokeFunc();

            if (running || finding)
            {
                StopMapRoute(
                    "Another vnavmesh movement is active. " +
                    "Route stopped to avoid interference."
                );
                return false;
            }

            bool ready = Pi.GetIpcSubscriber<bool>(
                "vnavmesh.Nav.IsReady"
            ).InvokeFunc();

            if (!ready)
            {
                mapRouteStatus =
                    "Waiting for vnavmesh navigation data...";
                return false;
            }

            BuildMapRoutePathGoals(crystal);

            mapRouteWalkAttempted = true;
            mapRouteWalkLastProgress = DateTime.UtcNow;
            mapRouteWalkLastPosition = player.Position;

            if (!SubmitNextMapRoutePath(
                player.Position, crystal))
            {
                RecoverMapRoutePath(
                    main,
                    "No path requests could be started."
                );

                return mapRoutePhase ==
                    MapRoutePhase.WaitingForSource;
            }

            return true;
        }
        catch (Exception ex)
        {
            StopMapRoute(
                "vnavmesh unavailable: " + ex.Message
            );
            return false;
        }
    }

    private void TickMapRouteWalk(Shard main)
    {
        if (mapRoutePhase != MapRoutePhase.WaitingForSource)
            return;

        var player = Objects.LocalPlayer;

        if (player == null || IsBusy() ||
            Client.TerritoryType != (
                mapRouteWalkingLocal
                    ? mapRouteLocalSourceTerritory
                    : main.Territory))
        {
            StopMapRoute(
                "Arrival state changed during approach."
            );
            return;
        }

        if (mapRouteWalkAttempted &&
            HousingMovementKeysPressed())
        {
            StopMapRoute("Cancelled by manual movement.");
            return;
        }

        if (!mapRouteWalkAttempted)
        {
            BeginMapRouteWalk(main);
            return;
        }

        var now = DateTime.UtcNow;

        if (mapRouteWalkPathTask is { } pending)
        {
            if (!pending.IsCompleted)
            {
                if ((now - mapRouteWalkStarted).TotalSeconds > 12)
                {
                    StopMapRoute(
                        "vnavmesh pathfinding timed out."
                    );
                }

                return;
            }

            // Discard the task before doing anything that
            // might stop this route.
            mapRouteWalkPathTask = null;

            if (pending.IsFaulted || pending.IsCanceled)
            {
                RecoverMapRoutePath(
                    main,
                    "Pathfinding task failed or was cancelled."
                );
                return;
            }

            List<Vector3> points;

            try
            {
                points = pending.Result;
            }
            catch (Exception ex)
            {
                RecoverMapRoutePath(
                    main,
                    "Path result error: " + ex.Message
                );
                return;
            }

            var selectedCrystal =
                MapRouteGetActiveWalkCrystal(main);

            bool validEndpoint =
                selectedCrystal != null &&
                points != null &&
                points.Count > 0 &&
                points.Count <= 256 &&
                Vector3.Distance(
                    points[points.Count - 1],
                    selectedCrystal.Position
                ) <= MapRouteSafeInteractionRange() + 0.35f;

            if (!validEndpoint)
            {
                string detail = points == null
                    ? "NULL path"
                    : "Waypoints=" + points.Count +
                      (points.Count > 0 && selectedCrystal != null
                        ? "; endpoint distance=" +
                          Vector3.Distance(
                              points[points.Count - 1],
                              selectedCrystal.Position
                          ).ToString("0.00")
                        : "");

                Log.Warning(
                    "DEV9 FIX2: invalid path result: " +
                    detail
                );

                RecoverMapRoutePath(
                    main,
                    "No valid path endpoint within " +
                    MapRouteSafeInteractionRange().ToString("0.0") +
                    "y of the selected crystal."
                );
                return;
            }

            try
            {
                if (HousingMovementKeysPressed() ||
                    Pi.GetIpcSubscriber<bool>(
                        "vnavmesh.Path.IsRunning"
                    ).InvokeFunc())
                {
                    StopMapRoute(
                        "Movement was overridden before walking."
                    );
                    return;
                }

                Pi.GetIpcSubscriber<
                    List<Vector3>, bool, object>(
                        "vnavmesh.Path.MoveTo"
                    ).InvokeAction(points, false);

                mapRouteWalkOwnsPath = true;
                mapRouteWalkLastProgress = now;
                mapRouteWalkLastPosition = player.Position;

                mapRouteStatus =
                    "Walking towards " + main.Name + "...";

                Log.Information(
                    $"DEV9 FIX2: following {points.Count} " +
                    "Aetheryte approach waypoints."
                );
            }
            catch (Exception ex)
            {
                StopMapRoute(
                    "Could not start walking: " + ex.Message
                );
            }

            return;
        }

        if (mapRouteDirectActive)
        {
            TickMapRouteDirectApproach(main);
            return;
        }

        if (!mapRouteWalkOwnsPath)
        {
            StopMapRoute("Walking path was lost.");
            return;
        }

        if (Vector3.Distance(
            player.Position,
            mapRouteWalkLastPosition
        ) >= 0.35f)
        {
            mapRouteWalkLastPosition = player.Position;
            mapRouteWalkLastProgress = now;
        }

        if ((now - mapRouteWalkLastProgress).TotalSeconds > 5)
        {
            StopMapRoute(
                "Walking stalled. Stopped automatic movement."
            );
            return;
        }

        try
        {
            bool running = Pi.GetIpcSubscriber<bool>(
                "vnavmesh.Path.IsRunning"
            ).InvokeFunc();

            if (!running &&
                (now - mapRouteWalkStarted).TotalSeconds > 2)
            {
                StopMapRoute(
                    "Walking ended outside Aetheryte range."
                );
            }
        }
        catch (Exception ex)
        {
            StopMapRoute(
                "Movement status unavailable: " + ex.Message
            );
        }
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

                mapRouteSettling = false;
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

            if (mapRouteWalkingLocal &&
                HousingMovementKeysPressed())
            {
                StopMapRoute("Cancelled by manual movement.");
                return;
            }

            uint requiredTerritory = mapRouteWalkingLocal
                ? mapRouteLocalSourceTerritory
                : main.Territory;

            if (Client.TerritoryType != requiredTerritory)
            {
                StopMapRoute("Unexpected destination territory.");
                return;
            }

            ScanNearby();

            var player = Objects.LocalPlayer;
            var now = DateTime.UtcNow;

            if (player == null)
            {
                StopMapRoute("Player unavailable during approach.");
                return;
            }

            bool closeEnough = MapRouteInApproachRange(target);

            if (closeEnough && !mapRouteSettling)
            {
                // Stop first. Do NOT dispatch native callbacks
                // while the path or character is still moving.
                StopMapRouteWalk();

                mapRouteSettling = true;
                mapRouteSettleStarted = now;
                mapRouteStillSince = DateTime.MinValue;
                mapRouteStillAnchor = player.Position;
                mapRouteStatus = "Stopping near Aethernet crystal...";

                Log.Information(
                    "DEV9 FIX1: approach threshold reached; " +
                    $"distance={Vector3.Distance(player.Position, nearby!.Position):0.00}y, " +
                    $"safe limit={MapRouteSafeInteractionRange():0.00}y. " +
                    "Stopping before native interaction."
                );

                return;
            }

            if (mapRouteSettling)
            {
                if (!closeEnough)
                {
                    StopMapRoute(
                        "Moved away from the selected crystal " +
                        "during interaction preparation."
                    );
                    return;
                }

                if (HousingMovementKeysPressed())
                {
                    StopMapRoute("Cancelled by manual movement.");
                    return;
                }

                if ((now - mapRouteSettleStarted).TotalSeconds > 6)
                {
                    StopMapRoute(
                        "Movement did not settle in time. " +
                        "No native interaction attempted."
                    );
                    return;
                }

                bool pathRunning;

                try
                {
                    pathRunning = Pi.GetIpcSubscriber<bool>(
                        "vnavmesh.Path.IsRunning"
                    ).InvokeFunc();
                }
                catch (Exception ex)
                {
                    StopMapRoute(
                        "Could not verify stopped navigation: " +
                        ex.Message
                    );
                    return;
                }

                if (pathRunning)
                {
                    mapRouteStillSince = DateTime.MinValue;
                    mapRouteStillAnchor = player.Position;
                    mapRouteStatus = "Waiting for vnavmesh to stop...";
                    return;
                }

                // Measure cumulative movement, not just one
                // frame's displacement. This prevents slowly
                // gliding characters from appearing stationary.
                if (Vector3.Distance(
                    player.Position, mapRouteStillAnchor
                ) > 0.06f)
                {
                    mapRouteStillAnchor = player.Position;
                    mapRouteStillSince = now;
                    mapRouteStatus = "Waiting for character to stop...";
                    return;
                }

                if (mapRouteStillSince == DateTime.MinValue)
                {
                    mapRouteStillSince = now;
                    return;
                }

                if ((now - mapRouteStillSince).TotalMilliseconds < 800)
                {
                    mapRouteStatus =
                        "Confirming stable position before interaction...";
                    return;
                }

                // Don't reuse a native menu from an earlier
                // failed attempt or unrelated interaction.
                if (GetAddon("TelepotTown") != null)
                {
                    StopMapRoute(
                        "An Aethernet menu is already open. " +
                        "Close it before starting another route."
                    );
                    return;
                }

                mapRouteSettling = false;

                // The selected local crystal may itself be
                // the requested destination. The game cannot
                // teleport an Aethernet shard to itself.
                //
                // Only complete when the previously selected
                // live object still matches the destination ID.
                if (mapRouteWalkingToTarget)
                {
                    var arrivedSource = MapRouteGetRememberedSource();

                    if (arrivedSource == null ||
                        arrivedSource.BaseId != target.Id ||
                        !MapRouteInApproachRange(target))
                    {
                        StopMapRoute(
                            "Direct-walk destination identity changed."
                        );
                        return;
                    }

                    Log.Information(
                        "DEV9 FIX2: reached " + target.Name +
                        " on foot. No Aethernet callback needed."
                    );

                    CompleteMapRoute();
                    return;
                }

                Log.Information(
                    "DEV9 FIX1: character stationary for 800ms; " +
                    $"distance={Vector3.Distance(player.Position, nearby!.Position):0.00}y. " +
                    "Beginning native Aethernet interaction."
                );

                StartTravel(target);

                if (phase != TravelPhase.Idle)
                {
                    mapRoutePhase = MapRoutePhase.WaitingForShard;
                    mapRouteStarted = DateTime.UtcNow;
                    mapRouteStatus =
                        "Aethernet travel to " + target.Name + "...";
                    return;
                }

                StopMapRoute(
                    "Could not start native Aethernet interaction."
                );
                return;
            }

            if (elapsed > 60)
            {
                StopMapRoute(
                    "Aetheryte approach timed out. " +
                    "No further travel attempted."
                );
                return;
            }

            // Remain in walking state until safely within
            // ~9y. Merely entering the 11.1y detection
            // radius is no longer sufficient.
            TickMapRouteWalk(main);
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
        StopMapRouteWalk();
        ResetMapRoutePathRecovery();
        mapRouteSettling = false;

        Log.Information(
            "DEV9 map route completed: " +
            (mapRouteTarget?.Name ?? "Unknown")
        );

        mapRoutePhase = MapRoutePhase.Idle;
        mapRouteTarget = null;
        mapRouteMain = null;
        mapRouteWindowOpen = false;
        mapRouteWalkingLocal = false;
        mapRouteWalkingToTarget = false;
        mapRouteQuoteReady = false;
        mapRouteLocalSourceAddress = 0;
        mapRouteStatus = "";
    }

    private void StopMapRoute(string reason)
    {
        StopMapRouteWalk();
        ResetMapRoutePathRecovery();
        mapRouteSettling = false;

        var previous = mapRoutePhase;
        mapRoutePhase = MapRoutePhase.Idle;
        mapRouteTarget = null;
        mapRouteMain = null;
        mapRouteWalkingLocal = false;
        mapRouteWalkingToTarget = false;
        mapRouteQuoteReady = false;
        mapRouteLocalSourceAddress = 0;
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
                    if (mapRouteQuoteReady)
                    {
                        string routeLabel = target.IsMain
                            ? "Direct to main Aetheryte"
                            : mapRoutePreviewDirect
                                ? "Local Aethernet -> " + target.Name
                                : mapRoutePreviewWalk
                                    ? MapRouteFindLocalSource(target)?.BaseId ==
                                      target.Id
                                        ? "Walk directly to " + target.Name
                                        : "Walk to local crystal -> " + target.Name
                                    : main.Name + " -> " + target.Name;

                        ImGui.TextDisabled(routeLabel);

                        ImGui.TextWrapped(
                            mapRoutePreviewDirect || mapRoutePreviewWalk
                                ? "No paid teleport required."
                                : $"Paid teleport: {mapRouteQuotedCost:N0} gil"
                        );
                    }

                    if (mapRouteStatus != "Ready for confirmation.")
                        ImGui.TextWrapped(mapRouteStatus);

                    if (mapRouteQuoteReady)
                    {
                        if (ImGui.Button("Confirm route##map-confirm"))
                            ConfirmMapRoute();

                        ImGui.SameLine();
                    }

                    if (ImGui.Button("Cancel##map-cancel"))
                        ClearMapRouteSelection();

                    // Paid travel is a deliberate secondary
                    // action, never an automatic fallback.
                    if (MapRouteInLocalNetwork(target))
                    {
                        ImGui.Spacing();

                        if (mapRouteExplicitPaid)
                        {
                            if (ImGui.SmallButton(
                                "Prefer free Aethernet##map-free"))
                            {
                                MapRouteSelectPaidAlternative(false);
                            }
                        }
                        else if (ImGui.SmallButton(
                            "Review paid alternative##map-paid"))
                        {
                            MapRouteSelectPaidAlternative(true);
                        }
                    }
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