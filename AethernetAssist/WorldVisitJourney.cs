using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private bool WorldVisitPageAvailable()
    {
        if (Client.TerritoryType is not (129 or 130 or 132))
            return false;

        // Only eligible main-city Aetherytes.
        return TryFindHousingAetheryte(
            out _, out var crystal) &&
            crystal != null;
    }

    private enum WorldVisitPhase
    {
        Idle,
        Interact,
        SelectVisitMenu,
        SelectWorld,
        AwaitNativeWorldSelection,
        Confirm,
        ManualConfirmation,
        WaitForArrival
    }

    private WorldVisitPhase worldVisitPhase =
        WorldVisitPhase.Idle;

    private WorldVisitDestination? worldVisitTarget;
    private uint worldVisitOriginWorld;
    private uint worldVisitOriginTerritory;
    private nint worldVisitSourceAddress;

    private DateTime worldVisitStageStarted;

    private DateTime worldVisitLastAction;

    private bool worldVisitConfirmationSent;
    private bool worldVisitSawQueue;

    private enum WorldVisitManualDecision
    {
        Unknown,
        Accepted,
        Declined
    }

    private WorldVisitManualDecision worldVisitManualDecision =
        WorldVisitManualDecision.Unknown;

    private int worldVisitInteractions;
    private int worldVisitMenuCallbacks;
    private bool worldVisitDcSwitchSubmitted;
    private int worldVisitRequestedDc = -1;

    private string worldVisitJourneyStatus =
        "Select a destination to begin World Visit.";

    private string worldVisitJourneyDetails =
        "No one-click World Visit attempted yet.";

    private DateTime worldVisitPrelistUpdated;
    private uint worldVisitPrelistWorld;

    private bool WorldVisitOtherTravelActive()
    {
        return phase != TravelPhase.Idle ||
               housingPhase != HousingPhase.Idle ||
               residentialPhase != ResidentialPhase.Idle ||
               cosmicPhase != CosmicPhase.Idle ||
               cosmicLocalPhase != CosmicLocalPhase.Idle;
    }

    private bool WorldVisitCanStop()
    {
        // Stopping monitoring is safe before FFXIV queues
        // the transfer. It never cancels an accepted
        // native World Visit.
        return worldVisitPhase != WorldVisitPhase.Idle &&
               !worldVisitSawQueue &&
               !IsBusy();
    }

    private void TryStopWorldVisitFromUser()
    {
        if (worldVisitPhase == WorldVisitPhase.Idle)
            return;

        if (!WorldVisitCanStop())
        {
            worldVisitJourneyStatus =
                "Transfer is controlled by FFXIV. " +
                "Queued World Visits cannot be cancelled.";
            return;
        }

        if (worldVisitPhase == WorldVisitPhase.WaitForArrival)
        {
            StopWorldVisit(
                "Stopped monitoring. Any accepted transfer " +
                "remains controlled by FFXIV.");
            return;
        }

        StopWorldVisit(
            "Automation stopped. Close any remaining " +
            "native game menu manually.");
    }

    private void SetWorldVisitPhase(WorldVisitPhase next)
    {
        worldVisitPhase = next;
        worldVisitStageStarted = DateTime.UtcNow;
    }

    private void StopWorldVisit(
        string message,
        bool arrived = false)
    {
        worldVisitJourneyDetails +=
            " | Final: " + message;

        worldVisitJourneyStatus = message;
        worldVisitPhase = WorldVisitPhase.Idle;
        worldVisitTarget = null;
        worldVisitSourceAddress = nint.Zero;
        worldVisitPending = false;
        worldVisitUiSelectedWorldId = 0;

        worldVisitCachedDestinations.Clear();
        worldVisitCachedAddon = nint.Zero;
        worldVisitPrelistUpdated = DateTime.MinValue;

        if (arrived)
        {
            // Close on successful travel, matching
            // other AethernetAssist travel features.
            windowOpen = false;
            autoWindowOwnsVisibility = false;
            autoWindowWasInRange = false;
            resetDestinationScroll = true;
        }
        else
        {
            windowOpen = true;
        }
    }

    // Display worlds before the native menu opens.
    // This is only a candidate list: the actual target
    // is revalidated against WorldTravelSelect later.
    private static readonly string[] WorldVisitLightNames =
    {
        "Alpha", "Lich", "Odin", "Phoenix",
        "Raiden", "Shiva", "Twintania", "Zodiark"
    };

    private static readonly string[] WorldVisitChaosNames =
    {
        "Cerberus", "Louisoix", "Moogle", "Omega",
        "Phantom", "Ragnarok", "Sagittarius", "Spriggan"
    };

    private static string WorldVisitDcName(string name) =>
        WorldVisitLightNames.Contains(
            name, StringComparer.OrdinalIgnoreCase)
                ? "Light"
                : WorldVisitChaosNames.Contains(
                    name, StringComparer.OrdinalIgnoreCase)
                    ? "Chaos"
                    : "Unknown";

    private bool TryBuildEuropeanWorldChoices(
        out List<WorldVisitDestination> result,
        out string error)
    {
        result = new List<WorldVisitDestination>();
        error = "";

        var player = Objects.LocalPlayer;

        if (player == null)
        {
            error = "Player unavailable.";
            return false;
        }

        var sheet = Data.GetExcelSheet<World>();

        uint currentId = player.CurrentWorld.RowId;

        var publicWorlds = sheet
            .Where(x =>
                x.RowId != 0 &&
                x.IsPublic &&
                !string.IsNullOrWhiteSpace(
                    x.Name.ToString()))
            .ToArray();

        string[] names = WorldVisitLightNames
            .Concat(WorldVisitChaosNames)
            .ToArray();

        foreach (string name in names)
        {
            var matches = publicWorlds
                .Where(x => string.Equals(
                    x.Name.ToString(),
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();

            if (matches.Length != 1)
            {
                error = "World identity ambiguous: " + name;
                result.Clear();
                return false;
            }

            var world = matches[0];

            result.Add(new WorldVisitDestination(
                -1,
                world.RowId,
                name,
                world.RowId == currentId));
        }

        if (result.Count != 16 ||
            result.Select(x => x.WorldId).Distinct().Count() != 16 ||
            result.Count(x => x.Current) != 1)
        {
            error = "European destination catalogue incomplete.";
            result.Clear();
            return false;
        }

        return true;
    }

    // The plugin-side list is independent of the
    // native window's selected data center.
    private void RefreshWorldVisitChoices()
    {
        if (worldVisitPhase != WorldVisitPhase.Idle)
            return;

        var player = Objects.LocalPlayer;

        if (!WorldVisitPageAvailable() || player == null)
        {
            worldVisitCachedDestinations.Clear();
            worldVisitCachedError =
                "Approach a supported main city Aetheryte.";
            return;
        }

        uint currentId = player.CurrentWorld.RowId;
        DateTime now = DateTime.UtcNow;

        if (worldVisitPrelistWorld == currentId &&
            (now - worldVisitPrelistUpdated).TotalMilliseconds < 1500 &&
            worldVisitCachedDestinations.Count == 16)
            return;

        worldVisitPrelistWorld = currentId;
        worldVisitPrelistUpdated = now;

        if (TryBuildEuropeanWorldChoices(
            out var choices, out var error))
        {
            worldVisitCachedDestinations.Clear();
            worldVisitCachedDestinations.AddRange(choices);
            worldVisitCachedError = "";
        }
        else
        {
            worldVisitCachedDestinations.Clear();
            worldVisitCachedError = error;
        }
    }
    private void StartWorldVisit(
        WorldVisitDestination choice)
    {
        if (worldVisitPhase != WorldVisitPhase.Idle ||
            WorldVisitOtherTravelActive() ||
            IsBusy() ||
            GetAddon("SelectYesno") != null ||
            !WorldVisitPageAvailable())
            return;

        var player = Objects.LocalPlayer;

        if (player == null || choice.Current)
            return;

        var sheet = Data.GetExcelSheet<World>();

        var from = sheet.GetRow(
            player.CurrentWorld.RowId);

        var to = sheet.GetRow(choice.WorldId);

        if (from.RowId == 0 ||
            to.RowId == 0 ||
            to.RowId == from.RowId ||
            !to.IsPublic ||
            !string.Equals(
                to.Name.ToString(),
                choice.Name,
                StringComparison.OrdinalIgnoreCase) ||
            !TryBuildEuropeanWorldChoices(
                out var verifiedChoices, out string catalogError) ||
            verifiedChoices.Count(x =>
                x.WorldId == choice.WorldId &&
                x.Name == choice.Name &&
                !x.Current) != 1)
        {
            worldVisitJourneyStatus =
                "Destination verification failed.";
            return;
        }

        if (!TryFindHousingAetheryte(
            out _, out var source) ||
            source == null)
        {
            worldVisitJourneyStatus =
                "Main city Aetheryte unavailable.";
            return;
        }

        RememberNavigationPage(NavigationPage.DcTravel);

        worldVisitTarget = choice;
        worldVisitOriginWorld = from.RowId;
        worldVisitOriginTerritory = Client.TerritoryType;
        worldVisitSourceAddress = source.Address;


        worldVisitLastAction = DateTime.MinValue;

        worldVisitInteractions = 0;
        worldVisitMenuCallbacks = 0;
        worldVisitConfirmationSent = false;
        worldVisitSawQueue = false;
        worldVisitPending = false;
        worldVisitManualDecision =
            WorldVisitManualDecision.Unknown;
        worldVisitDcSwitchSubmitted = false;
        worldVisitRequestedDc = -1;

        worldVisitJourneyDetails =
            "From=" + from.Name.ToString() +
            " | To=" + to.Name.ToString() +
            " | Destination DC=" + WorldVisitDcName(choice.Name);

        worldVisitJourneyStatus =
            "Preparing World Visit to " + choice.Name + "...";

        if (GetAddon("WorldTravelSelect") != null)
            SetWorldVisitPhase(WorldVisitPhase.SelectWorld);
        else if (GetAddon("SelectString") != null)
            SetWorldVisitPhase(WorldVisitPhase.SelectVisitMenu);
        else
            SetWorldVisitPhase(WorldVisitPhase.Interact);
    }

    private bool TrySubmitVerifiedWorldVisitTarget(
        WorldVisitDestination target,
        out string error)
    {
        error = "";

        if (worldVisitPending ||
            GetAddon("SelectYesno") != null)
        {
            error = "Native selection already pending.";
            return false;
        }

        if (!TryReadWorldVisitDestinations(
            out var nativeWorlds, out error))
            return false;

        var matches = nativeWorlds
            .Where(x =>
                x.WorldId == target.WorldId &&
                string.Equals(
                    x.Name, target.Name,
                    StringComparison.OrdinalIgnoreCase) &&
                !x.Current)
            .ToArray();

        if (matches.Length != 1)
        {
            error = "Target not found in native list: " +
                target.Name;
            return false;
        }

        SelectWorldVisitDestination(matches[0]);

        if (!worldVisitPending)
        {
            error = worldVisitSelectionStatus;
            return false;
        }

        worldVisitJourneyDetails +=
            " | Selected index=" + matches[0].Index;

        return true;
    }
    private bool TryChooseWorldVisitMenu()
    {
        var selector =
            (AddonSelectString*)GetAddon("SelectString");

        if (selector == null)
            return false;

        var menu = selector->PopupMenu.PopupMenu;

        if (menu.EntryNames == null)
            return false;

        int count = Math.Clamp(menu.EntryCount, 0, 32);
        int index = -1;

        for (int i = 0; i < count; i++)
        {
            var pointer = menu.EntryNames[i].Value;

            if (pointer == null)
                continue;

            string label = Clean(
                Marshal.PtrToStringUTF8(
                    (nint)pointer) ?? "");

            // Match the actual World Visit action,
            // never a fixed SelectString menu index.
            // Native labels may contain icon prefixes,
            // e.g. "F Visit Another World Server."
            // Require a unique matching menu entry.
            if (!label.Contains(
                "Visit Another World",
                StringComparison.OrdinalIgnoreCase))
                continue;

            if (index >= 0)
                return false;

            index = i;
        }

        if (index < 0)
            return false;

        AtkValue value = new()
        {
            Type = AtkValueType.Int,
            Int = index
        };

        selector->AtkUnitBase.FireCallback(
            1, &value, true);

        worldVisitMenuCallbacks++;
        worldVisitJourneyDetails +=
            " | SelectString index=" + index;

        return true;
    }

    private void TickWorldVisit()
    {
        if (worldVisitPhase == WorldVisitPhase.Idle)
            return;

        var target = worldVisitTarget;

        if (target == null)
        {
            StopWorldVisit("No target world selected.");
            return;
        }

        var now = DateTime.UtcNow;
        var player = Objects.LocalPlayer;
        bool busy = IsBusy();

        if (player != null &&
            player.CurrentWorld.RowId == target.WorldId &&
            !busy)
        {
            StopWorldVisit(
                "Arrived: " + target.Name, true);
            return;
        }

        if (worldVisitPhase is
            WorldVisitPhase.WaitForArrival or
            WorldVisitPhase.ManualConfirmation)
        {
            bool queue =
                Conditions[ConditionFlag.WaitingToVisitOtherWorld] ||
                Conditions[ConditionFlag.ReadyingVisitOtherWorld];

            if (queue)
            {
                worldVisitSawQueue = true;
                worldVisitJourneyStatus =
                    "Queued for " + target.Name +
                    ". Waiting for FFXIV.";
                return;
            }

            if (busy)
            {
                worldVisitJourneyStatus =
                    "Transferring to " + target.Name + "...";
                return;
            }
        }

        double stageSeconds =
            (now - worldVisitStageStarted).TotalSeconds;

        if (worldVisitPhase ==
            WorldVisitPhase.AwaitNativeWorldSelection)
        {
            if (stageSeconds > 12)
            {
                StopWorldVisit(
                    "Native DC switch verification timed out.");
                return;
            }

            var addon = GetAddon("WorldTravelSelect");

            if (addon == null ||
                addon->AtkValues == null ||
                addon->AtkValuesCount != 15)
            {
                worldVisitJourneyStatus =
                    "Waiting for native World Visit window...";
                return;
            }

            var selectedDc = addon->AtkValues[11];

            if (selectedDc.Type != AtkValueType.UInt ||
                selectedDc.UInt != (uint)worldVisitRequestedDc)
            {
                worldVisitJourneyStatus =
                    "Waiting for " +
                    WorldVisitDcName(target.Name) +
                    " selection...";
                return;
            }

            if (!TrySubmitVerifiedWorldVisitTarget(
                target, out var error))
            {
                worldVisitJourneyStatus =
                    "Validating switched DC: " + error;
                return;
            }

            worldVisitJourneyDetails +=
                " | Native DC switch verified";

            SetWorldVisitPhase(WorldVisitPhase.Confirm);

            worldVisitJourneyStatus =
                "Selected " + target.Name +
                ". Awaiting native confirmation...";
            return;
        }
        if (worldVisitPhase == WorldVisitPhase.WaitForArrival)
        {
            // Never repeat native selection or confirmation.
            // FFXIV owns any accepted transfer.
            if (worldVisitManualDecision ==
                    WorldVisitManualDecision.Declined &&
                !worldVisitSawQueue &&
                !busy)
            {
                StopWorldVisit(
                    "World Visit declined. Ready for another destination.");
                return;
            }

            // A recognized Yes allows longer for the game
            // to start its transfer. An unrecognized close
            // gets a conservative observation window.
            double graceSeconds =
                worldVisitManualDecision ==
                    WorldVisitManualDecision.Accepted
                    ? 35d : 15d;

            if (!worldVisitSawQueue &&
                !busy &&
                stageSeconds > graceSeconds)
            {
                StopWorldVisit(
                    "No World Visit transfer observed. " +
                    "Ready for another destination.");
            }

            return;
        }

        if (worldVisitPhase == WorldVisitPhase.ManualConfirmation)
        {
            // Check the observed manual decision first.
            if (worldVisitManualDecision ==
                    WorldVisitManualDecision.Declined &&
                !worldVisitSawQueue &&
                !busy)
            {
                StopWorldVisit(
                    "World Visit declined. Ready for another destination.");
                return;
            }

            if (GetAddon("SelectYesno") != null)
            {
                worldVisitJourneyStatus =
                    "Confirm " + target.Name +
                    " manually in the game's dialogue.";
                return;
            }

            // Dialog closed. Do not assume this means
            // the player accepted the transfer.
            SetWorldVisitPhase(
                WorldVisitPhase.WaitForArrival);

            worldVisitJourneyStatus =
                worldVisitManualDecision ==
                    WorldVisitManualDecision.Accepted
                    ? "Transfer accepted. Waiting for FFXIV..."
                    : "Confirmation closed. Checking for transfer...";
            return;
        }

        if (player == null || busy ||
            Client.TerritoryType != worldVisitOriginTerritory ||
            player.CurrentWorld.RowId != worldVisitOriginWorld)
        {
            StopWorldVisit(
                "World Visit interrupted.");
            return;
        }

        if (WorldVisitOtherTravelActive())
        {
            StopWorldVisit(
                "Another travel operation became active.");
            return;
        }

        if (stageSeconds > 18)
        {
            StopWorldVisit(
                "World Visit native step timed out.");
            return;
        }

        if (worldVisitPhase == WorldVisitPhase.Interact)
        {
            if (GetAddon("WorldTravelSelect") != null)
            {
                SetWorldVisitPhase(
                    WorldVisitPhase.SelectWorld);
                return;
            }

            if (GetAddon("SelectString") != null)
            {
                SetWorldVisitPhase(
                    WorldVisitPhase.SelectVisitMenu);
                return;
            }

            if (GetAddon("SelectYesno") != null ||
                GetAddon("TelepotTown") != null)
            {
                StopWorldVisit(
                    "Another game menu is open.");
                return;
            }

            if (worldVisitInteractions >= 3)
                return;

            if ((now - worldVisitLastAction)
                .TotalMilliseconds < 1100)
                return;

            if (!TryFindHousingAetheryte(
                out _, out var source) ||
                source == null ||
                source.Address != worldVisitSourceAddress)
            {
                StopWorldVisit(
                    "Moved away from the main Aetheryte.");
                return;
            }

            var system = TargetSystem.Instance();

            if (system == null)
            {
                StopWorldVisit(
                    "Native interaction unavailable.");
                return;
            }

            system->InteractWithObject(
                (GameObject*)source.Address, false);

            worldVisitInteractions++;
            worldVisitLastAction = now;

            worldVisitJourneyStatus =
                "Opening World Visit menu...";
            return;
        }

        if (worldVisitPhase == WorldVisitPhase.SelectVisitMenu)
        {
            if (GetAddon("WorldTravelSelect") != null)
            {
                SetWorldVisitPhase(
                    WorldVisitPhase.SelectWorld);
                return;
            }

            if (GetAddon("SelectString") == null)
                return;

            if (worldVisitMenuCallbacks != 0)
                return;

            if (!TryChooseWorldVisitMenu())
            {
                StopWorldVisit(
                    "Could not verify Visit Another World.");
                return;
            }

            worldVisitJourneyStatus =
                "Opening destination list...";
            return;
        }

        if (worldVisitPhase == WorldVisitPhase.SelectWorld)
        {
            var addon = GetAddon("WorldTravelSelect");

            if (addon == null)
                return;

            if (GetAddon("SelectYesno") != null)
            {
                StopWorldVisit(
                    "Another confirmation is already open.");
                return;
            }

            if (!TryReadWorldVisitDestinations(
                out var initialWorlds, out var error))
            {
                worldVisitJourneyStatus =
                    "Validating native list: " + error;
                return;
            }

            int requestedDc =
                WorldVisitDcName(target.Name) == "Light"
                    ? 1 : 0;

            var dc = addon->AtkValues[11];

            if (dc.Type != AtkValueType.UInt ||
                dc.UInt > 1)
            {
                StopWorldVisit(
                    "Native DC state is invalid.");
                return;
            }

            worldVisitRequestedDc = requestedDc;

            if (dc.UInt != (uint)requestedDc)
            {
                if (worldVisitDcSwitchSubmitted)
                {
                    StopWorldVisit(
                        "Duplicate DC callback prevented.");
                    return;
                }

                AtkValue* values = stackalloc AtkValue[2];

                values[0] = new AtkValue
                {
                    Type = AtkValueType.Int,
                    Int = 1
                };

                values[1] = new AtkValue
                {
                    Type = AtkValueType.Int,
                    Int = requestedDc
                };

                // Native tab callback: (1, DC index).
                // Observed mapping: Chaos=0, Light=1.
                worldVisitDcSwitchSubmitted = true;

                worldVisitJourneyDetails +=
                    " | DC callback=1," + requestedDc;

                addon->FireCallback(2, values, true);

                SetWorldVisitPhase(
                    WorldVisitPhase.AwaitNativeWorldSelection);

                worldVisitJourneyStatus =
                    "Switching native World Visit to " +
                    WorldVisitDcName(target.Name) + "...";
                return;
            }

            // No DC change needed.
            if (!TrySubmitVerifiedWorldVisitTarget(
                target, out error))
            {
                worldVisitJourneyStatus =
                    "Validating destination: " + error;
                return;
            }

            SetWorldVisitPhase(WorldVisitPhase.Confirm);

            worldVisitJourneyStatus =
                "Selected " + target.Name +
                ". Awaiting native confirmation...";
            return;
        }
        if (worldVisitPhase == WorldVisitPhase.Confirm)
        {
            var yesno =
                (AddonSelectYesno*)GetAddon("SelectYesno");

            if (yesno == null)
                return;

            string prompt = yesno->PromptText == null
                ? ""
                : Clean(
                    yesno->PromptText->NodeText.ToString());

            worldVisitJourneyDetails +=
                " | Confirmation=" + prompt;

            string expected =
                "Travel to " + target.Name + "?";

            if (!string.Equals(
                prompt, expected,
                StringComparison.OrdinalIgnoreCase))
            {
                StopWorldVisit(
                    "Native confirmation mismatch. " +
                    "No travel confirmation sent.");
                return;
            }

            // DEV6: Player must confirm or decline.
            // Never submit the native Yes callback.
            SetWorldVisitPhase(
                WorldVisitPhase.ManualConfirmation);

            worldVisitJourneyStatus =
                "Verified: " + prompt +
                " Confirm or decline manually.";
            return;
        }
    }
    private string BuildWorldVisitJourneyReport()
    {
        return
            "\n=== WORLD VISIT JOURNEY ===\n" +
            "Phase: " + worldVisitPhase + "\n" +
            "Target: " +
                (worldVisitTarget?.Name ?? "None") + "\n" +
            "Origin world: " + worldVisitOriginWorld + "\n" +
            "Current world: " +
                (Objects.LocalPlayer?.CurrentWorld.RowId ?? 0) +
                "\n" +
            "Status: " + worldVisitJourneyStatus + "\n" +
            "Interactions: " + worldVisitInteractions + "\n" +
            "Menu callbacks: " + worldVisitMenuCallbacks + "\n" +
            "DC switch submitted: " +
                worldVisitDcSwitchSubmitted + "\n" +
            "Requested DC flag: " +
                worldVisitRequestedDc + "\n" +
            "Native selection submitted: " +
                worldVisitPending + "\n" +
            "Confirmation submitted: " +
                worldVisitConfirmationSent + "\n" +
            "Manual decision observed: " +
                worldVisitManualDecision + "\n" +
            "Saw queue/loading: " + worldVisitSawQueue +
                "\n" +
            "Details: " + worldVisitJourneyDetails + "\n";
    }
}