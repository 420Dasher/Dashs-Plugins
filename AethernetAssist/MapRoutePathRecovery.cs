using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    // Path recovery for crystals whose exact center is
    // not a useful navmesh destination.
    //
    // Try a small number of reachable surface points,
    // then a different loaded crystal from the SAME
    // Aethernet network if necessary.
    //
    // No movement is submitted until a path succeeds.

    private readonly HashSet<nint> mapRouteFailedSources = new();
    private readonly List<Vector3> mapRoutePossibleGoals = new();

    private int mapRouteNextGoal;
    private int mapRoutePathAttemptNumber;
    private nint mapRoutePathSourceAddress;

    private void ResetMapRoutePathRecovery()
    {
        mapRouteFailedSources.Clear();
        mapRoutePossibleGoals.Clear();
        mapRouteNextGoal = 0;
        mapRoutePathAttemptNumber = 0;
        mapRoutePathSourceAddress = 0;
        mapRouteDirectAttempted = false;
        mapRouteDirectActive = false;
    }

    private void BuildMapRoutePathGoals(IGameObject crystal)
    {
        mapRoutePossibleGoals.Clear();
        mapRouteNextGoal = 0;
        mapRoutePathSourceAddress = crystal.Address;

        // Keep the existing, proven approach first.
        mapRoutePossibleGoals.Add(crystal.Position);

        var player = Objects.LocalPlayer;

        if (player == null)
            return;

        int reachableHits = 0;
        int generalHits = 0;
        int rejectedDistance = 0;
        int rejectedHeight = 0;
        int rejectedDuplicate = 0;
        int nullResults = 0;

        // Distinguish a missing starting surface from
        // missing geometry around the destination.
        try
        {
            Vector3? startPoint = Pi.GetIpcSubscriber<
                Vector3, float, float, Vector3?>(
                    "vnavmesh.Query.Mesh.NearestPoint"
                ).InvokeFunc(player.Position, 3f, 6f);

            Log.Information(
                "DEV9 FIX2: start mesh=" +
                (startPoint.HasValue
                    ? startPoint.Value.ToString()
                    : "NONE") +
                "; player=" + player.Position +
                "; crystal=" + crystal.Position +
                "; physical distance=" +
                Vector3.Distance(
                    player.Position,
                    crystal.Position).ToString("0.00")
            );
        }
        catch (Exception ex)
        {
            Log.Warning(
                "DEV9 FIX2: start mesh query failed: " +
                ex.Message
            );
        }

        Vector3[] offsets =
        {
            Vector3.Zero,
            new(3f, 0f, 0f),
            new(-3f, 0f, 0f),
            new(0f, 0f, 3f),
            new(0f, 0f, -3f),
            new(6f, 0f, 0f),
            new(-6f, 0f, 0f),
            new(0f, 0f, 6f),
            new(0f, 0f, -6f),
            new(4.5f, 0f, 4.5f),
            new(-4.5f, 0f, 4.5f),
            new(4.5f, 0f, -4.5f),
            new(-4.5f, 0f, -4.5f)
        };

        float maximumDistance =
            MapRouteSafeInteractionRange() - 0.4f;

        // First query reachable polygons. If those yield
        // insufficient candidates, also query ordinary
        // navmesh geometry.
        //
        // Ordinary geometry is NOT automatically trusted:
        // each returned position must still produce a
        // valid walking path before any movement starts.
        foreach (bool reachableOnly in new[] { true, false })
        {
            foreach (var offset in offsets)
            {
                if (mapRoutePossibleGoals.Count >= 16)
                    break;

                Vector3 probe = crystal.Position + offset;
                Vector3? result;

                try
                {
                    string channel = reachableOnly
                        ? "vnavmesh.Query.Mesh.NearestPointReachable"
                        : "vnavmesh.Query.Mesh.NearestPoint";

                    result = Pi.GetIpcSubscriber<
                        Vector3, float, float, Vector3?>(
                            channel
                        ).InvokeFunc(probe, 3f, 6f);
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        "DEV9 FIX2: mesh query failed " +
                        (reachableOnly ? "reachable" : "general") +
                        ": " + ex.Message
                    );

                    // The remaining candidates, if any,
                    // can still be independently validated.
                    break;
                }

                if (!result.HasValue)
                {
                    nullResults++;
                    continue;
                }

                if (reachableOnly)
                    reachableHits++;
                else
                    generalHits++;

                Vector3 point = result.Value;

                if (!float.IsFinite(point.X) ||
                    !float.IsFinite(point.Y) ||
                    !float.IsFinite(point.Z))
                    continue;

                if (MathF.Abs(
                    point.Y - crystal.Position.Y) > 4.5f)
                {
                    rejectedHeight++;
                    continue;
                }

                if (Vector3.Distance(
                    point, crystal.Position
                ) > maximumDistance)
                {
                    rejectedDistance++;
                    continue;
                }

                if (mapRoutePossibleGoals.Any(existing =>
                    Vector3.DistanceSquared(
                        existing, point) < 1.5f))
                {
                    rejectedDuplicate++;
                    continue;
                }

                mapRoutePossibleGoals.Add(point);
            }
        }

        Log.Information(
            "DEV9 FIX2: source BaseID=" + crystal.BaseId +
            "; accepted goals=" + mapRoutePossibleGoals.Count +
            "; reachable hits=" + reachableHits +
            "; general hits=" + generalHits +
            "; null results=" + nullResults +
            "; rejected distance=" + rejectedDistance +
            "; rejected height=" + rejectedHeight +
            "; duplicates=" + rejectedDuplicate
        );
    }

    private bool SubmitNextMapRoutePath(
        Vector3 playerPosition,
        IGameObject crystal)
    {
        if (mapRoutePathSourceAddress != crystal.Address)
            return false;

        while (mapRouteNextGoal < mapRoutePossibleGoals.Count)
        {
            int index = mapRouteNextGoal++;
            Vector3 goal = mapRoutePossibleGoals[index];

            // Preserve the proven original tolerance.
            // Projected points are actual nearby walkable
            // destinations and get a tighter tolerance.
            float tolerance = index == 0 ? 6f : 0.75f;

            mapRoutePathAttemptNumber++;

            try
            {
                mapRouteWalkPathTask = Pi.GetIpcSubscriber<
                    Vector3,
                    Vector3,
                    bool,
                    float,
                    Task<List<Vector3>>>(
                        "vnavmesh.Nav.PathfindWithTolerance"
                    ).InvokeFunc(
                        playerPosition,
                        goal,
                        false,
                        tolerance
                    );

                if (mapRouteWalkPathTask == null)
                    continue;

                mapRouteWalkStarted = DateTime.UtcNow;
                mapRouteWalkLastProgress = mapRouteWalkStarted;
                mapRouteWalkLastPosition = playerPosition;

                mapRouteStatus =
                    "Finding a walkable route to the crystal...";

                Log.Information(
                    "DEV9 TEST4: path attempt " +
                    mapRoutePathAttemptNumber +
                    ", source BaseID=" + crystal.BaseId +
                    ", goal " + (index + 1) +
                    "/" + mapRoutePossibleGoals.Count +
                    ", goal distance=" +
                    Vector3.Distance(
                        goal, crystal.Position).ToString("0.00")
                );

                return true;
            }
            catch (Exception ex)
            {
                mapRouteWalkPathTask = null;

                Log.Warning(
                    "DEV9 TEST4: path request failed: " +
                    ex.Message
                );
            }
        }

        return false;
    }

    private IGameObject? MapRouteGetActiveWalkCrystal(
        Shard main)
    {
        if (mapRouteWalkingLocal)
            return MapRouteGetRememberedSource();

        return Objects.FirstOrDefault(obj =>
            obj != null &&
            obj.IsTargetable &&
            obj.ObjectKind == ObjectKind.Aetheryte &&
            obj.BaseId == main.Id &&
            Client.TerritoryType == main.Territory
        );
    }

    private void RecoverMapRoutePath(
        Shard main,
        string failureReason)
    {
        if (mapRoutePhase != MapRoutePhase.WaitingForSource)
            return;

        var player = Objects.LocalPlayer;
        var crystal = MapRouteGetActiveWalkCrystal(main);

        if (player == null || crystal == null)
        {
            StopMapRoute(
                "Walking source disappeared during pathfinding."
            );
            return;
        }

        Log.Warning(
            "DEV9 TEST4: rejected route goal: " +
            failureReason +
            "; source=" + crystal.BaseId +
            "; attempt=" + mapRoutePathAttemptNumber
        );

        if (SubmitNextMapRoutePath(
            player.Position, crystal))
            return;

        // All projected positions for this crystal failed.
        // A free same-network route may try another real,
        // unlocked and currently loaded Aethernet crystal.
        if (mapRouteWalkingLocal &&
            mapRouteTarget is { } target)
        {
            mapRouteFailedSources.Add(crystal.Address);

            if (mapRouteFailedSources.Count < 6)
            {
                var alternate = MapRouteFindLocalSource(
                    target,
                    mapRouteFailedSources
                );

                if (alternate != null)
                {
                    MapRouteRememberLocalSource(alternate);
                    mapRouteDirectAttempted = false;

                    mapRouteWalkingToTarget =
                        alternate.BaseId == target.Id;

                    mapRouteWalkPathTask = null;
                    mapRouteWalkAttempted = false;
                    mapRoutePossibleGoals.Clear();
                    mapRouteNextGoal = 0;
                    mapRoutePathSourceAddress = 0;

                    mapRouteStatus =
                        "Trying another Aethernet crystal...";

                    Log.Information(
                        "DEV9 TEST4: switching path source to " +
                        alternate.Name +
                        " BaseID=" + alternate.BaseId +
                        ", distance=" +
                        Vector3.Distance(
                            player.Position,
                            alternate.Position).ToString("0.0")
                    );

                    return;
                }
            }
        }

        StopMapRoute(
            "No navigable walking path to a usable Aetheryte. " +
            "No interaction or paid teleport attempted."
        );
    }
}