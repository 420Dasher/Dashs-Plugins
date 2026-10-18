using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    // DEV9 TEST5: Direct waypoint approach.
    //
    // Uses the existing vnavmesh waypoint follower, but
    // avoids a full navigation path calculation when a
    // short, same-level corridor appears traversable.
    //
    // We never bypass movement cancellation, the approach
    // distance check, or the native callback safeguards.
    //
    // Failed direct movement returns to the established
    // navmesh route planner exactly once.

    private bool mapRouteDirectAttempted;
    private bool mapRouteDirectActive;

    private DateTime mapRouteDirectStarted;
    private DateTime mapRouteDirectLastProgress;
    private Vector3 mapRouteDirectLastPosition;

    private bool MapRouteDirectCorridorLooksWalkable(
        Vector3 start,
        Vector3 goal,
        Vector3 crystalPosition,
        out Vector3 safeGoal,
        out float stoppingRange)
    {
        safeGoal = goal;
        stoppingRange = 0f;

        float length = Vector3.Distance(start, goal);

        if (length <= 0.1f)
            return false;

        int samples = Math.Clamp(
            (int)MathF.Ceiling(length / 2.5f),
            2,
            18
        );

        Vector3 lastAccepted = start;

        try
        {
            var query = Pi.GetIpcSubscriber<
                Vector3, float, float, Vector3?>(
                    "vnavmesh.Query.Mesh.NearestPoint"
                );

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;

                Vector3 probe = Vector3.Lerp(start, goal, t);
                Vector3? nearest = query.InvokeFunc(
                    probe, 2.5f, 4f
                );

                if (!nearest.HasValue)
                {
                    Log.Information(
                        "DEV9 FIX2: corridor has no mesh at " +
                        i + "/" + samples
                    );

                    return false;
                }

                Vector3 projected = nearest.Value;

                if (!float.IsFinite(projected.X) ||
                    !float.IsFinite(projected.Y) ||
                    !float.IsFinite(projected.Z))
                    return false;

                float horizontal = Vector2.Distance(
                    new Vector2(probe.X, probe.Z),
                    new Vector2(projected.X, projected.Z)
                );

                float vertical = MathF.Abs(
                    probe.Y - projected.Y
                );

                // Never drive toward a goal that is more
                // than 1.5y from its nearest mesh surface.
                // The relaxed 3.2y endpoint rule is removed.
                if (horizontal > 1.5f || vertical > 2.3f)
                {
                    // The final sample may be near a platform
                    // boundary. Try ending at the preceding
                    // accepted mesh point, but only if it is
                    // already inside the configured interaction
                    // radius with a small safety margin.
                    if (i == samples &&
                        horizontal <= 3.2f &&
                        vertical <= 0.5f)
                    {
                        float distance = Vector3.Distance(
                            lastAccepted, crystalPosition
                        );

                        float maximumRange = MathF.Min(
                            10.4f,
                            config.OpenRange - 0.45f
                        );

                        if (distance <= maximumRange)
                        {
                            safeGoal = lastAccepted;

                            stoppingRange = MathF.Max(
                                MapRouteSafeInteractionRange(),
                                MathF.Min(
                                    maximumRange,
                                    distance + 0.3f
                                )
                            );

                            Log.Information(
                                "DEV9 FIX2: DIRECT GOAL CLIPPED; " +
                                $"unsafe offset={horizontal:0.00}y; " +
                                $"clipped crystal distance={distance:0.00}y; " +
                                $"stop range={stoppingRange:0.00}y; " +
                                $"goal={safeGoal}."
                            );

                            return true;
                        }

                        Log.Information(
                            "DEV9 FIX2: clipped point outside " +
                            $"interaction range: {distance:0.00}y."
                        );
                    }

                    Log.Information(
                        "DEV9 FIX2: direct corridor rejected at " +
                        $"{i}/{samples}; horizontal={horizontal:0.00}, " +
                        $"vertical={vertical:0.00}."
                    );

                    return false;
                }

                // Use a verified mesh projection, not the
                // position beyond the last valid sample.
                lastAccepted = projected;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(
                "DEV9 FIX2: corridor query failed: " +
                ex.Message
            );

            return false;
        }
    }

    private bool TryStartMapRouteDirectApproach(
        IGameObject crystal,
        Vector3 start)
    {
        if (mapRouteDirectAttempted)
            return false;

        mapRouteDirectAttempted = true;

        if (mapRoutePhase != MapRoutePhase.WaitingForSource ||
            mapRouteWalkAttempted ||
            !crystal.IsTargetable ||
            HousingMovementKeysPressed() ||
            IsBusy())
            return false;

        Vector3 difference = crystal.Position - start;

        float horizontal = new Vector2(
            difference.X, difference.Z
        ).Length();

        float height = MathF.Abs(difference.Y);

        // This is deliberately a conservative first pass.
        // Longer distances and elevation changes continue
        // to use the established navigation pathfinder.
        if (horizontal < 11f ||
            horizontal > 38f ||
            height > 2.3f)
            return false;

        Vector2 direction = Vector2.Normalize(
            new Vector2(difference.X, difference.Z)
        );

        // Finish a little inside our ~9y interaction limit.
        // Do not walk into the middle of the crystal.
        const float remainingDistance = 7.2f;

        float travel = horizontal - remainingDistance;

        if (travel < 2f)
            return false;

        Vector3 goal = new(
            start.X + direction.X * travel,
            start.Y + difference.Y * (travel / horizontal),
            start.Z + direction.Y * travel
        );

        if (!MapRouteDirectCorridorLooksWalkable(
            start,
            goal,
            crystal.Position,
            out Vector3 movementGoal,
            out float clippedStopRange))
        {
            Log.Information(
                "DEV9 TEST5: direct approach unsuitable; " +
                "using normal pathfinding."
            );

            return false;
        }

        try
        {
            if (!Pi.GetIpcSubscriber<bool>(
                "vnavmesh.Nav.IsReady"
            ).InvokeFunc())
                return false;

            if (Pi.GetIpcSubscriber<bool>(
                "vnavmesh.Path.IsRunning"
            ).InvokeFunc())
                return false;

            if (Pi.GetIpcSubscriber<bool>(
                "vnavmesh.SimpleMove.PathfindInProgress"
            ).InvokeFunc())
                return false;

            // A single direct waypoint: unlike a calculated
            // navmesh route, this does not include a detour.
            // The corridor check and progress watchdog
            // determine whether to keep using it.
            Pi.GetIpcSubscriber<
                List<Vector3>,
                bool,
                object>(
                    "vnavmesh.Path.MoveTo"
                ).InvokeAction(
                    new List<Vector3> { movementGoal },
                    false
                );

            mapRouteOwnedPathEnd = movementGoal;
            mapRouteLastOwnershipCheck = DateTime.UtcNow;
            mapRouteWalkOwnsPath = true;
            mapRouteWalkAttempted = true;
            mapRouteDirectActive = true;
            mapRouteDirectStopRange = clippedStopRange;

            mapRouteDirectStarted = DateTime.UtcNow;
            mapRouteDirectLastProgress = mapRouteDirectStarted;
            mapRouteDirectLastPosition = start;

            mapRouteWalkStarted = mapRouteDirectStarted;
            mapRouteWalkLastProgress = mapRouteDirectStarted;
            mapRouteWalkLastPosition = start;

            mapRouteStatus =
                "Walking directly towards Aetheryte...";

            Log.Information(
                "DEV9 TEST5: DIRECT APPROACH STARTED; " +
                $"source={crystal.BaseId}; " +
                $"distance={Vector3.Distance(start, crystal.Position):0.0}y; " +
                $"waypoint={movementGoal}; " +
                $"stopRange={clippedStopRange:0.00}."
            );

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(
                "DEV9 TEST5: direct movement unavailable: " +
                ex.Message
            );

            mapRouteDirectActive = false;
            return false;
        }
    }

    private void FallBackFromMapRouteDirect(
        string reason)
    {
        Log.Warning(
            "DEV9 TEST5: DIRECT APPROACH FALLBACK: " + reason
        );

        // Stops only movement that this route started.
        StopMapRouteWalk();
        mapRouteDirectStopRange = 0f;

        // Do not attempt the direct approach again to the
        // same source. BeginMapRouteWalk will use navmesh.
        mapRouteDirectAttempted = true;
        mapRouteDirectActive = false;

        mapRouteStatus =
            "Direct approach interrupted; calculating " +
            "a navigation path...";
    }

    private void TickMapRouteDirectApproach(
        Shard main)
    {
        if (!mapRouteDirectActive)
            return;

        if (mapRoutePhase != MapRoutePhase.WaitingForSource)
        {
            StopMapRouteWalk();
            return;
        }

        var player = Objects.LocalPlayer;
        var source = MapRouteGetActiveWalkCrystal(main);

        if (player == null ||
            source == null ||
            IsBusy())
        {
            StopMapRoute(
                "Direct approach interrupted by game state."
            );
            return;
        }

        if (HousingMovementKeysPressed())
        {
            StopMapRoute("Cancelled by manual movement.");
            return;
        }

        var now = DateTime.UtcNow;

        float traveled = Vector3.Distance(
            player.Position,
            mapRouteDirectLastPosition
        );

        if (traveled >= 0.2f)
        {
            mapRouteDirectLastPosition = player.Position;
            mapRouteDirectLastProgress = now;
        }

        if ((now - mapRouteDirectStarted).TotalSeconds > 12 ||
            (now - mapRouteDirectLastProgress).TotalSeconds > 1.8)
        {
            FallBackFromMapRouteDirect(
                "Walking stalled or direct approach timed out."
            );
            return;
        }

        try
        {
            bool running = Pi.GetIpcSubscriber<bool>(
                "vnavmesh.Path.IsRunning"
            ).InvokeFunc();

            // The normal route controller will stop our path
            // at the safe interaction distance, then wait
            // for the character to become stationary.
            if (!running &&
                (now - mapRouteDirectStarted).TotalSeconds > 0.7)
            {
                FallBackFromMapRouteDirect(
                    "Direct waypoint ended outside interaction range."
                );
            }
        }
        catch (Exception ex)
        {
            FallBackFromMapRouteDirect(
                "Navigation state unavailable: " + ex.Message
            );
        }
    }
}