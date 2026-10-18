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
    private bool mapRouteQuoteReady;

    private nint mapRouteLocalSourceAddress;
    private uint mapRouteLocalSourceTerritory;
    private Vector3 mapRouteLocalSourcePosition;
    private string mapRouteLocalSourceName = "";

    // Network membership must come from the same catalogue
    // already used by the native Aethernet navigation UI.
    private bool MapRouteInLocalNetwork(Shard target)
    {
        return !target.IsMain &&
               territories.TryGetValue(
                   Client.TerritoryType, out var groups) &&
               groups.Contains(target.Group);
    }

    // Choose the nearest identifiable, unlocked and loaded
    // crystal belonging to the actual destination network.
    //
    // No guessing from nearby unrelated GameObjects.
    // vnavmesh will independently validate walkability.
    private IGameObject? MapRouteFindLocalSource(Shard target)
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
                !IsShardObject(obj))
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
            if (distance > 130f || distance >= nearestDistance)
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