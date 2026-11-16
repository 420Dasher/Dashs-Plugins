using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private bool mapRoutePreviewWalk;
    private bool mapRouteExplicitPaid;
    private bool mapRouteWalkingLocal;
    private bool mapRouteWalkingToTarget;
    private bool mapRouteQuoteReady;

    // DEV11: City routes may cross between the two
    // districts while retaining the same AethernetGroup.
    private const float MapRouteLocalShardDiscoveryRange = 50f;

    private nint mapRouteLocalSourceAddress;
    private uint mapRouteLocalSourceTerritory;
    private Vector3 mapRouteLocalSourcePosition;
    private string mapRouteLocalSourceName = "";

    // Network membership must come from the same catalogue
    // already used by the native Aethernet navigation UI.
    private bool MapRouteInLocalNetwork(Shard target)
    {
        return territories.TryGetValue(
                   Client.TerritoryType, out var groups) &&
               groups.Contains(target.Group);
    }

    // Choose the nearest identifiable, unlocked and loaded
    // crystal belonging to the actual destination network.
    //
    // No guessing from nearby unrelated GameObjects.
    // vnavmesh will independently validate walkability.
    private IGameObject? MapRouteFindLocalSource(
        Shard target,
        HashSet<nint>? excluded = null)
    {
        var player = Objects.LocalPlayer;

        if (player == null ||
            !MapRouteInLocalNetwork(target) ||
            !networks.TryGetValue(target.Group, out var members))
            return null;

        IGameObject? nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (var obj in Objects)
        {
            if (obj == null ||
                !obj.IsTargetable ||
                !IsShardObject(obj) ||
                (excluded != null &&
                 excluded.Contains(obj.Address)))
                continue;

            string objectName = obj.Name.ToString();

            bool recognized = members.Any(shard =>
                !shard.Invisible &&
                shard.Territory == Client.TerritoryType &&
                IsUnlocked(shard.Id) &&
                (
                    shard.Id == obj.BaseId ||
                    string.Equals(
                        shard.Name,
                        objectName,
                        StringComparison.OrdinalIgnoreCase)
                )
            );

            if (!recognized)
                continue;

            float distance = Vector3.Distance(
                player.Position, obj.Position);

            // Only follow a real object currently loaded
            // in the player's local game world.
            if (distance > MapRouteLocalShardDiscoveryRange || distance >= nearestDistance)
                continue;

            nearest = obj;
            nearestDistance = distance;
        }

        return nearest;
    }

    private void MapRouteRememberLocalSource(IGameObject source)
    {
        mapRouteLocalSourceAddress = source.Address;
        mapRouteLocalSourceTerritory = Client.TerritoryType;
        mapRouteLocalSourcePosition = source.Position;
        mapRouteLocalSourceName = source.Name.ToString();
    }

    private IGameObject? MapRouteGetRememberedSource()
    {
        if (!mapRouteWalkingLocal ||
            Client.TerritoryType != mapRouteLocalSourceTerritory ||
            mapRouteLocalSourceAddress == 0)
            return null;

        return Objects.FirstOrDefault(obj =>
            obj != null &&
            obj.IsTargetable &&
            IsShardObject(obj) &&
            obj.Address == mapRouteLocalSourceAddress &&
            Vector3.Distance(
                obj.Position,
                mapRouteLocalSourcePosition) < 3f
        );
    }

    // Called after all suitable local shard paths have
    // been rejected. Paid routing remains subject to the
    // existing user confirmation and AutoPaidMapRouting settings.
    private void MapRouteFallbackToPaid(
        Shard target,
        Shard main,
        string failureReason)
    {
        StopMapRouteWalk();
        ResetMapRoutePathRecovery();

        mapRouteSettling = false;
        mapRoutePhase = MapRoutePhase.Idle;
        mapRouteWalkingLocal = false;
        mapRouteWalkingToTarget = false;
        mapRouteLocalSourceAddress = 0;
        mapRouteDirectStopRange = 0f;
        mapRouteExplicitPaid = true;
        mapRouteAutoPending = false;

        mapRouteQuoteReady = MapRouteTryQuote(
            target,
            main,
            out mapRoutePreviewDirect,
            out mapRoutePreviewWalk,
            out mapRouteQuotedCost,
            out string reason);

        if (!mapRouteQuoteReady)
        {
            mapRouteStatus =
                "No valid free route (" + failureReason +
                "). Paid fallback unavailable: " + reason;

            mapRouteWindowOpen = true;
            return;
        }

        mapRouteStatus =
            "No valid free same-city route. " +
            "Paid teleport is available for review.";

        bool needsConfirmation =
            config.ConfirmMapRoutes ||
            !config.AutoPaidMapRouting;

        if (needsConfirmation)
        {
            mapRouteWindowOpen = true;
        }
        else
        {
            mapRouteWindowOpen = false;
            mapRouteAutoPending = true;

            mapRouteStatus =
                "Free route unavailable. Starting the " +
                "previously opted-in paid teleport.";
        }

        Log.Warning(
            "DEV11: same-city free route exhausted. " +
            "Paid fallback prepared. " +
            "Reason=" + failureReason +
            "; gil=" + mapRouteQuotedCost);
    }

    private void MapRouteSelectPaidAlternative(bool paid)
    {
        var target = mapRouteTarget;
        var main = mapRouteMain;

        if (target == null ||
            main == null ||
            mapRoutePhase != MapRoutePhase.Idle ||
            !MapRouteInLocalNetwork(target))
            return;

        mapRouteExplicitPaid = paid;

        mapRouteQuoteReady = MapRouteTryQuote(
            target,
            main,
            out mapRoutePreviewDirect,
            out mapRoutePreviewWalk,
            out mapRouteQuotedCost,
            out string reason
        );

        mapRouteStatus = mapRouteQuoteReady
            ? paid
                ? "Paid alternative selected. Review the gil cost."
                : "Ready for confirmation."
            : reason;
    }
}