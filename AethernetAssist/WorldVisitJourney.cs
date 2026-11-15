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

    private int worldVisitInteractions;
    private int worldVisitMenuCallbacks;

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
        return worldVisitPhase != WorldVisitPhase.Idle &&
               worldVisitPhase != WorldVisitPhase.WaitForArrival &&
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
            // No world selection or travel callback in DEV5.
            // The native menu is available for manual testing.
            if (GetAddon("SelectYesno") != null)
            {
                worldVisitJourneyStatus =
                    "Native confirmation opened. " +
                    "No automatic transfer submitted.";
                return;
            }

            if (GetAddon("WorldTravelSelect") != null)
            {
                worldVisitJourneyStatus =
                    "Native menu open: select " +
                    WorldVisitDcName(target.Name) +
                    " / " + target.Name +
                    " manually.";
                return;
            }

            StopWorldVisit(
                "Native World Visit menu closed.");
            return;
        }

        if (worldVisitPhase == WorldVisitPhase.WaitForArrival)
        {
            // FFXIV owns the accepted transfer.
            // Never timeout or repeat the travel callback.
            // Only verified arrival ends monitoring.
            return;
        }

        if (worldVisitPhase == WorldVisitPhase.ManualConfirmation)
        {
            if (GetAddon("SelectYesno") != null)
            {
                worldVisitJourneyStatus =
                    "Confirm " + target.Name +
                    " manually in the game's dialogue.";

                return;
            }

            // The player may have accepted or declined.
            // Monitor only; never send another callback.
            SetWorldVisitPhase(
                WorldVisitPhase.WaitForArrival);

            worldVisitJourneyStatus =
                "Waiting for World Visit result...";
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
            if (GetAddon("WorldTravelSelect") == null)
                return;

            SetWorldVisitPhase(
                WorldVisitPhase.AwaitNativeWorldSelection);

            worldVisitJourneyStatus =
                "Native menu opened for " + target.Name +
                ". Manual DC/world selection required in DEV5.";
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

            // Live World Visit diagnostic confirmed the
            // native prompt format: "Travel to Shiva?"
            //
            // Only approve an exact destination match.
            // Localized or unrecognized prompts fall
            // back to manual confirmation.
            string expectedPrompt =
                "Travel to " + target.Name + "?";

            bool verifiedPrompt = string.Equals(
                prompt,
                expectedPrompt,
                StringComparison.OrdinalIgnoreCase);

            if (!verifiedPrompt ||
                yesno->YesButton == null ||
                !yesno->YesButton->IsEnabled)
            {
                SetWorldVisitPhase(
                    WorldVisitPhase.ManualConfirmation);

                worldVisitJourneyStatus =
                    "Verify and confirm " + target.Name +
                    " manually.";
                return;
            }

            AtkValue answer = new()
            {
                Type = AtkValueType.Int,
                Int = 0
            };

            yesno->AtkUnitBase.FireCallback(
                1, &answer, true);

            worldVisitConfirmationSent = true;

            SetWorldVisitPhase(
                WorldVisitPhase.WaitForArrival);

            worldVisitJourneyStatus =
                "World Visit requested: " + target.Name;

            return;
        }
    }

    private string BuildWorldVisitJourneyReport()
    {
        return
            "\n=== SAME-DC WORLD VISIT ===\n" +
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
            "Native selection submitted: " +
                worldVisitPending + "\n" +
            "Confirmation submitted: " +
                worldVisitConfirmationSent + "\n" +
            "Saw queue/loading: " + worldVisitSawQueue +
                "\n" +
            "Details: " + worldVisitJourneyDetails + "\n";
    }
}