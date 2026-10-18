using System;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    // Only windows opened by proximity are auto-closed.
    private bool autoWindowOwnsVisibility;
    private bool autoWindowWasInRange;

    private uint autoWindowTerritory;
    private string autoWindowSource = "None";

    // Tab availability from the last confirmed proximity scan.
    private int autoWindowConfirmedTabMask;

    private void OpenWindowManually()
    {
        if (!windowOpen)
            RestoreNavigationPage();

        autoWindowOwnsVisibility = false;
        windowOpen = true;
    }

    private void UpdateProximityWindow()
    {
        // Re-evaluate after zoning rather than carrying a
        // previous territory's detection into the new zone.
        if (autoWindowTerritory != Client.TerritoryType)
        {
            if (autoWindowOwnsVisibility &&
                housingPhase == HousingPhase.Idle &&
                phase == TravelPhase.Idle &&
                worldVisitPhase == WorldVisitPhase.Idle)
            {
                windowOpen = false;
            }

            autoWindowOwnsVisibility = false;
            autoWindowWasInRange = false;
            autoWindowTerritory = Client.TerritoryType;
        }

        bool nearResidentialShard =
            TryFindResidentialShard(out _);

        bool nearCosmicShard = cosmicLocalNearby != null;

        bool nearAethernet =
            nearby != null || nearResidentialShard ||
            nearCosmicShard || overworldNearby != null;

        bool nearHousingEntrance = TryFindWardEntrance(
            out _,
            out _,
            out bool isGate
        );

        bool nearCityAetheryte = TryFindHousingAetheryte(
            out _,
            out _
        );

        bool inRange =
            nearAethernet ||
            nearHousingEntrance ||
            nearCityAetheryte;

        autoWindowSource = nearHousingEntrance
            ? isGate
                ? "Housing walk-through gate"
                : "Housing entrance NPC"
            : nearCosmicShard
                ? "Cosmic Aethernet shard"
            : nearResidentialShard
                ? "Residential Aethernet shard"
            : overworldNearby != null
                ? "Overworld Aetheryte"
            : nearAethernet
                ? "Aethernet shard / Aetheryte"
                : nearCityAetheryte
                    ? "Main city Aetheryte"
                    : "None";

        if (!inRange)
        {
            // Never interrupt an active navigation operation.
            if (autoWindowOwnsVisibility &&
                housingPhase == HousingPhase.Idle &&
                phase == TravelPhase.Idle &&
                worldVisitPhase == WorldVisitPhase.Idle)
            {
                windowOpen = false;
                autoWindowOwnsVisibility = false;
            }

            autoWindowWasInRange = false;
            return;
        }

        if (!autoWindowWasInRange &&
            config.AutoOpen &&
            !windowOpen)
        {
            RestoreNavigationPage();

            windowOpen = true;
            autoWindowOwnsVisibility = true;
        }

        // Only change the displayed tab after confirming that
        // a supported proximity source is still available.
        // Do not overwrite the user's saved tab preference.
        if (autoWindowOwnsVisibility && windowOpen)
        {
            if (SelectedNavigationPageUnavailable())
                RestoreNavigationPage();

            autoWindowConfirmedTabMask =
                AvailableNavigationTabsMask();
        }

        // If the user closes the window while still nearby,
        // do not immediately force it open again.
        autoWindowWasInRange = true;
    }
}