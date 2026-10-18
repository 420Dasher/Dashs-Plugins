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
        Vector3 goal)
    {
        float length = Vector3.Distance(start, goal);

        if (length <= 0.1f)
            return false;

        int samples = Math.Clamp(
            (int)MathF.Ceiling(length / 2.5f),
            2,
            18
        );

        try
        {
            var query = Pi.GetIpcSubscriber<
                Vector3,
                float,
                float,
                Vector3?>(
                    "vnavmesh.Query.Mesh.NearestPoint"
                );

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;

                Vector3 probe = Vector3.Lerp(
                    start, goal, t
                );

                Vector3? nearest = query.InvokeFunc(
                    probe, 2.5f, 4f
                );

                if (!nearest.HasValue)
                {
                    Log.Information(
                        "DEV9 TEST5: direct corridor rejected: " +
                        "missing mesh at sample " +
                        i + "/" + samples
                    );

                    return false;
                }

                var projected = nearest.Value;

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

                // Intermediate corridor samples must remain
                // close to the navigation mesh.
                //
                // The last sample is our stopping point near
                // the crystal, not the crystal's center.
                // Allow a small, nearly-level navmesh seam
                // there without weakening the whole route.
                bool finalSample = i == samples;

                float horizontalLimit =
                    finalSample && vertical <= 0.5f
                        ? 3.2f
                        : 1.5f;

                if (horizontal > horizontalLimit ||
                    vertical > 2.3f)
                {
                    Log.Information(
                        "DEV9 TEST5 FIX1: direct corridor rejected " +
                        $"at {i}/{samples}: " +
                        $"horizontal={horizontal:0.00}, " +
                        $"limit={horizontalLimit:0.00}, " +
                        $"vertical={vertical:0.00}."
                    );

                    return false;
                }

                if (finalSample && horizontal > 1.5f)
                {
                    Log.Information(
                        "DEV9 TEST5 FIX1: ENDPOINT SEAM ACCEPTED; " +
                        $"horizontal={horizontal:0.00}, " +
                        $"vertical={vertical:0.00}; " +
                        "intermediate samples passed."
                    );
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(
                "DEV9 TEST5: corridor validation unavailable: " +
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
            start, goal))
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
                    new List<Vector3> { goal },
                    false
                );

            mapRouteWalkOwnsPath = true;
            mapRouteWalkAttempted = true;
            mapRouteDirectActive = true;

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
                $"waypoint={goal}."
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