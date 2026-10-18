using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private readonly record struct HousingDistrict(
        string Name,
        string CityName,
        uint CityTerritory,
        uint ResidentialTerritory
    );

    private static readonly HousingDistrict[] HousingDistricts =
    {
        new("Mist", "Limsa Lominsa", 129, 339),
        new("Lavender Beds", "Gridania", 132, 340),
        new("Goblet", "Ul'dah", 130, 341),
        new("Shirogane", "Kugane", 628, 641),
        new("Empyreum", "Ishgard", 418, 979)
    };

    private enum HousingPhase
    {
        Idle,
        Interact,
        SelectResidential,
        SelectGoToWard,
        SelectWard,
        ConfirmWard,
        ConfirmTravel,
        WaitForArrival,
        SelectExit,
        WaitForExit
    }

    private HousingPhase housingPhase = HousingPhase.Idle;

    private HousingDistrict housingTarget;
    private int housingTargetWard;
    private int housingInitialWard;
    private nint housingOriginAddress;


    private DateTime housingPhaseStarted;
    private DateTime housingLastAction = DateTime.MinValue;



    private string housingStatus =
        "";

    private string housingDiagnostic =
        "No housing travel attempted yet.";

    // Main-city crystal IDs sourced from the Aetheryte sheet.
    private readonly HashSet<uint> housingMainAetheryteIds = new();

    // Aethernet shards and main Aetherytes are different objects.
    // Housing only accepts an actual main-city Aetheryte.
    private bool TryFindHousingAetheryte(
        out HousingDistrict district,
        out IGameObject? aetheryte)
    {
        district = default;
        aetheryte = null;

        var player = Objects.LocalPlayer;

        if (player == null)
            return false;

        foreach (var candidate in HousingDistricts)
        {
            if (candidate.CityTerritory != Client.TerritoryType)
                continue;

            district = candidate;
            break;
        }

        if (district.CityTerritory == 0)
            return false;

        float bestDistance = config.OpenRange;

        foreach (var obj in Objects)
        {
            if (obj == null ||
                !obj.IsTargetable ||
                obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte ||
                !housingMainAetheryteIds.Contains(obj.BaseId))
                continue;

            float distance = Vector3.Distance(
                player.Position,
                obj.Position
            );

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            aetheryte = obj;
        }

        return aetheryte != null;
    }

    private bool CanStartHousing()
    {
        return housingPhase == HousingPhase.Idle &&
               phase == TravelPhase.Idle &&
               !IsBusy() &&
               (TryFindHousingAetheryte(out _, out _) ||
                TryFindWardEntrance(out _, out _, out _));
    }

    private void SetHousingPhase(HousingPhase next)
    {
        housingPhase = next;
        housingPhaseStarted = DateTime.UtcNow;
    }

    private static int GetCurrentHousingWard()
    {
        var housing = HousingManager.Instance();

        if (housing == null)
            return 0;

        int index = housing->GetCurrentWard();

        return index >= 0 && index < 30
            ? index + 1
            : 0;
    }

    private void StartHousingTravel(int ward)
    {
        if (ward < 1 || ward > 30 || !CanStartHousing())
            return;

        bool fromCity = TryFindHousingAetheryte(
            out var cityDistrict,
            out var cityCrystal);

        HousingDistrict district;
        IGameObject? source;
        bool gate = false;

        if (fromCity)
        {
            district = cityDistrict;
            source = cityCrystal;
        }
        else if (TryFindWardEntrance(
            out var wardDistrict,
            out var wardNpc,
            out gate))
        {
            district = wardDistrict;
            source = wardNpc;
        }
        else
        {
            return;
        }

        if (!fromCity && GetCurrentHousingWard() == ward)
        {
            housingStatus =
                "Already in Ward " + ward + ".";

            housingDiagnostic =
                "Travel skipped: already in Ward " + ward + ".";

            return;
        }
        // Allow an already-open gate menu, but don't take
        // control of unrelated confirmation windows.
        if (GetAddon("HousingSelectBlock") != null ||
            GetAddon("SelectYesno") != null ||
            (fromCity && GetAddon("SelectString") != null))
        {
            housingStatus = "Close existing game menus first.";
            return;
        }

        RememberNavigationPage(NavigationPage.Housing);

        housingTarget = district;
        housingTargetWard = ward;
        housingInitialWard = fromCity
            ? 0
            : GetCurrentHousingWard();
        housingOriginAddress = source?.Address ?? nint.Zero;

        housingFromWard = !fromCity;
        housingLeavingDistrict = false;
        housingGateSource = gate;
        housingSawLoading = false;
        housingLastAction = DateTime.MinValue;
        PrepareHousingGateWalk();

        config.HousingWard = ward;
        Save();

        housingDiagnostic =
            "Requested " + district.Name +
            ", Ward " + ward +
            (fromCity ? " from city." : " from ward entrance.");

        housingStatus = gate
            ? "Walk through the exit to open ward selection."
            : "Opening Ward " + ward + " travel...";

        SetHousingPhase(gate
            ? HousingPhase.SelectGoToWard
            : HousingPhase.Interact);
    }

    private void StopHousing(string message)
    {
        StopHousingGateWalk();
        housingDiagnostic += " | " + message;
        housingStatus = message;
        housingPhase = HousingPhase.Idle;

        currentPage = NavigationPage.Housing;
        windowOpen = true;

        Log.Warning($"AethernetAssist Housing: {message}");
    }

    private void CompleteHousing()
    {
        StopHousingGateWalk();
        housingPhase = HousingPhase.Idle;

        housingStatus =
            $"Arrived: {housingTarget.Name}, Ward {housingTargetWard}.";

        housingDiagnostic += " | Arrival confirmed.";

        currentPage = NavigationPage.Housing;

        Log.Information(
            $"AethernetAssist Housing: {housingStatus}"
        );
    }

    private bool TrySelectHousingMenu(bool residential)
    {
        var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString*)
            GetAddon("SelectString");

        if (addon == null)
            return false;

        var menu = addon->PopupMenu.PopupMenu;

        if (menu.EntryNames == null)
            return false;

        int count = Math.Clamp(menu.EntryCount, 0, 32);
        var observed = new System.Collections.Generic.List<string>();

        for (int i = 0; i < count; i++)
        {
            var pointer = menu.EntryNames[i].Value;

            if (pointer == null)
                continue;

            string label = Clean(
                Marshal.PtrToStringUTF8((nint)pointer) ?? ""
            );

            observed.Add(label);

            bool match = residential
                ? label.Contains(
                    "Residential",
                    StringComparison.OrdinalIgnoreCase)
                : label.Contains(
                    "ward",
                    StringComparison.OrdinalIgnoreCase) &&
                  (label.Contains(
                      "Go to",
                      StringComparison.OrdinalIgnoreCase) ||
                   label.Contains(
                      "specified",
                      StringComparison.OrdinalIgnoreCase));

            if (!match)
                continue;

            var value = new AtkValue
            {
                Type = AtkValueType.Int,
                Int = i
            };

            addon->AtkUnitBase.FireCallback(
                1,
                &value,
                true
            );

            housingDiagnostic =
                $"Selected native menu entry {i}: {label}.";

            housingLastAction = DateTime.UtcNow;
            return true;
        }

        housingDiagnostic =
            $"Housing menu ({housingPhase}): " +
            string.Join(" | ", observed);

        return false;
    }

    private static string GetHousingYesNoPrompt(AtkUnitBase* addon)
    {
        if (addon == null ||
            addon->UldManager.NodeList == null ||
            addon->UldManager.NodeListCount <= 15)
            return "";

        var node = addon->UldManager.NodeList[15];

        if (node == null)
            return "";

        var textNode = node->GetAsAtkTextNode();

        if (textNode == null)
            return "";

        return Clean(textNode->NodeText.ToString());
    }

    private void TickHousing()
    {
        if (housingPhase == HousingPhase.Idle)
            return;

        var player = Objects.LocalPlayer;

        TickHousingGateWalk();

        // Movement interruption may have cancelled Housing.
        // Never process the old travel phase afterward.
        if (housingPhase == HousingPhase.Idle)
            return;

        if (housingPhase == HousingPhase.WaitForArrival)
        {


            if (IsBusy())
                housingSawLoading = true;

            int currentWard =
                Client.TerritoryType ==
                    housingTarget.ResidentialTerritory
                    ? GetCurrentHousingWard()
                    : 0;

            bool travelDialogsClosed =
                GetAddon("SelectYesno") == null &&
                GetAddon("HousingSelectBlock") == null;

            if (player != null &&
                !IsBusy() &&
                travelDialogsClosed &&
                Client.TerritoryType ==
                    housingTarget.ResidentialTerritory &&
                currentWard == housingTargetWard &&
                (!housingFromWard ||
                    housingSawLoading ||
                    currentWard != housingInitialWard))
            {
                CompleteHousing();
                return;
            }

            if ((DateTime.UtcNow - housingPhaseStarted)
                .TotalSeconds > 45)
            {
                StopHousing(
                    "Housing travel could not be confirmed.");
            }

            return;
        }

        if (housingPhase == HousingPhase.WaitForExit)
        {
            if (IsBusy())
                housingSawLoading = true;

            if (player != null &&
                !IsBusy() &&
                Client.TerritoryType !=
                    housingTarget.ResidentialTerritory)
            {
                CompleteHousingExit();
                return;
            }

            // Some exits request a native Yes/No confirmation.
            var yesno = GetAddon("SelectYesno");

            if (yesno != null &&
                (DateTime.UtcNow - housingLastAction)
                    .TotalMilliseconds > 500)
            {
                string prompt = GetHousingYesNoPrompt(yesno);

                bool recognized =
                    prompt.Contains(
                        "leave residential",
                        StringComparison.OrdinalIgnoreCase) ||
                    prompt.Contains(
                        "travel to",
                        StringComparison.OrdinalIgnoreCase);

                if (recognized)
                {
                    yesno->FireCallbackInt(0);
                    housingLastAction = DateTime.UtcNow;
                    housingDiagnostic +=
                        " | Confirmed: " + prompt;
                }
                else
                {
                    housingStatus =
                        "Please confirm the native exit dialog manually.";
                }
            }

            if ((DateTime.UtcNow - housingPhaseStarted)
                .TotalSeconds > 45)
            {
                StopHousing(
                    "Exit could not be confirmed. " +
                    housingDiagnostic);
            }

            return;
        }
        if ((DateTime.UtcNow - housingPhaseStarted)
            .TotalSeconds > 14)
        {
            StopHousing(
                $"Timed out during {housingPhase}. " +
                housingDiagnostic);
            return;
        }

        if (player == null)
        {
            StopHousing("Player unavailable.");
            return;
        }

        if (IsBusy())
            return;

        switch (housingPhase)
        {
            case HousingPhase.Interact:
            {
                if (GetAddon("SelectString") != null)
                {
                    SetHousingPhase(
                        housingFromWard
                            ? housingLeavingDistrict
                                ? HousingPhase.SelectExit
                                : HousingPhase.SelectGoToWard
                            : HousingPhase.SelectResidential);
                    return;
                }

                // Advance only the NPC conversation started
                // by our own Housing travel operation.
                var talk = GetAddon("Talk");

                if (talk != null)
                {
                    if (housingFromWard &&
                        !housingGateSource &&
                        (DateTime.UtcNow - housingLastAction)
                            .TotalMilliseconds >= 550)
                    {
                        talk->FireCallbackInt(0);
                        housingLastAction = DateTime.UtcNow;

                        housingStatus =
                            "Advancing entrance NPC dialogue...";
                    }

                    return;
                }

                if ((DateTime.UtcNow - housingLastAction)
                    .TotalMilliseconds < 700)
                    return;

                var obj = Objects.FirstOrDefault(x =>
                    x != null &&
                    x.Address == housingOriginAddress);

                if (obj == null ||
                    !obj.IsTargetable ||
                    Vector3.Distance(
                        player.Position,
                        obj.Position
                    ) > config.OpenRange + 1f)
                {
                    StopHousing(
                        "Main Aetheryte is no longer in range.");
                    return;
                }

                var target = TargetSystem.Instance();

                if (target == null)
                {
                    StopHousing("Target system unavailable.");
                    return;
                }

                target->InteractWithObject(
                    (GameObject*)obj.Address,
                    false
                );

                housingLastAction = DateTime.UtcNow;
                housingStatus = "Opening residential travel menu...";
                return;
            }

            case HousingPhase.SelectResidential:
            {
                if (TrySelectHousingMenu(true))
                {
                    housingStatus =
                        "Opening residential ward options...";
                    SetHousingPhase(HousingPhase.SelectGoToWard);
                }

                return;
            }

            case HousingPhase.SelectGoToWard:
            {
                if (GetAddon("HousingSelectBlock") != null)
                {
                    SetHousingPhase(HousingPhase.SelectWard);
                    return;
                }

                if ((DateTime.UtcNow - housingLastAction)
                    .TotalMilliseconds < 250)
                    return;

                if (TrySelectHousingMenu(false))
                {
                    housingStatus =
                        $"Selecting Ward {housingTargetWard}...";
                    SetHousingPhase(HousingPhase.SelectWard);
                }

                return;
            }

            case HousingPhase.SelectExit:
            {
                if (TrySelectLeaveDistrict())
                {
                    housingStatus =
                        "Leaving residential district...";
                    SetHousingPhase(HousingPhase.WaitForExit);
                }

                return;
            }
            case HousingPhase.SelectWard:
            {
                var addon = GetAddon("HousingSelectBlock");

                if (addon == null)
                    return;

                if ((DateTime.UtcNow - housingLastAction)
                    .TotalMilliseconds < 250)
                    return;

                if (housingTargetWard > 1)
                {
                    AtkValue* args = stackalloc AtkValue[2];

                    args[0] = new AtkValue
                    {
                        Type = AtkValueType.Int,
                        Int = 1
                    };

                    args[1] = new AtkValue
                    {
                        Type = AtkValueType.Int,
                        Int = housingTargetWard - 1
                    };

                    addon->FireCallback(2, args, true);

                    housingDiagnostic =
                        $"Selected ward index {housingTargetWard - 1}.";
                }
                else
                {
                    housingDiagnostic =
                        "Using default Ward 1 selection.";
                }

                housingLastAction = DateTime.UtcNow;
                SetHousingPhase(HousingPhase.ConfirmWard);
                return;
            }

            case HousingPhase.ConfirmWard:
            {
                if (GetAddon("SelectYesno") != null)
                {
                    SetHousingPhase(HousingPhase.ConfirmTravel);
                    return;
                }

                if ((DateTime.UtcNow - housingLastAction)
                    .TotalMilliseconds < 400)
                    return;

                var addon = GetAddon("HousingSelectBlock");

                if (addon == null)
                    return;

                var button = addon->GetComponentButtonById(34);

                if (button == null || !button->IsEnabled)
                    return;

                // Native HousingSelectBlock confirmation event.
                // Equivalent to the game's ward Select action.
                addon->FireCallbackInt(0);

                housingDiagnostic +=
                    " | Ward selection submitted.";

                housingLastAction = DateTime.UtcNow;
                housingStatus = "Waiting for travel confirmation...";

                SetHousingPhase(HousingPhase.ConfirmTravel);
                return;
            }

            case HousingPhase.ConfirmTravel:
            {
                var addon = GetAddon("SelectYesno");

                if (addon == null)
                    return;

                if ((DateTime.UtcNow - housingLastAction)
                    .TotalMilliseconds < 250)
                    return;

                string prompt = GetHousingYesNoPrompt(addon);

                housingDiagnostic +=
                    $" | Confirmation: {prompt}";

                // Never approve an unrelated Yes/No dialog.
                bool expected =
                    prompt.Contains(
                        "ward",
                        StringComparison.OrdinalIgnoreCase) ||
                    (prompt.Contains(
                        "Travel to",
                        StringComparison.OrdinalIgnoreCase) &&
                     prompt.Contains(
                        housingTarget.Name,
                        StringComparison.OrdinalIgnoreCase));

                if (!expected)
                {
                    StopHousing(
                        "Housing confirmation not recognized. " +
                        "Please confirm manually.");
                    return;
                }

                // The native Yes callback.
                addon->FireCallbackInt(0);

                housingLastAction = DateTime.UtcNow;


                housingStatus =
                    $"Traveling to {housingTarget.Name}, " +
                    $"Ward {housingTargetWard}...";

                SetHousingPhase(HousingPhase.WaitForArrival);
                return;
            }
        }
    }

    private void DrawHousingPage()
    {
        ImGui.Spacing();

        bool atCity = TryFindHousingAetheryte(
            out var cityDistrict,
            out _);

        bool atEntrance = TryFindWardEntrance(
            out var wardDistrict,
            out _,
            out bool gate);

        bool detected = atCity || atEntrance;
        bool canTravel = CanStartHousing();

        if (detected)
        {
            var district = atCity
                ? cityDistrict
                : wardDistrict;

            ImGui.TextColored(
                NavAccent,
                district.Name.ToUpperInvariant());

            ImGui.SameLine();
            ImGui.TextColored(
                NavMuted,
                atCity ? district.CityName : "Ward entrance");
        }
        else if (TryGetResidentialDistrict(out var localDistrict))
        {
            ImGui.TextColored(
                NavAccent,
                localDistrict.Name.ToUpperInvariant());

            ImGui.TextDisabled(
                "Approach the ward entrance to travel.");
        }
        else
        {
            ImGui.TextColored(NavAccent, "HOUSING");
            ImGui.TextDisabled(
                "No supported Aetheryte or ward entrance nearby.");
        }

        ImGui.Spacing();
        ImGui.TextColored(NavMuted, "WARD 1 - 30");


        ImGui.Spacing();

        const int columns = 6;
        const float gap = 6f;
        const float height = 29f;

        float available = ImGui.GetContentRegionAvail().X;
        float width = MathF.Max(
            30f,
            MathF.Floor(
                (available - (columns - 1) * gap) /
                columns
            )
        );

        int occupiedWard =
            TryGetResidentialDistrict(out _)
                ? GetCurrentHousingWard()
                : 0;

        for (int ward = 1; ward <= 30; ward++)
        {
            int column = (ward - 1) % columns;
            bool isCurrent = ward == occupiedWard;
            bool enabled = canTravel && !isCurrent;

            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 size = new(width, height);

            if (!enabled)
                ImGui.BeginDisabled();

            bool clicked = ImGui.InvisibleButton(
                "##housingWard" + ward,
                size
            );

            bool hovered = enabled &&
                           ImGui.IsItemHovered();

            var draw = ImGui.GetWindowDrawList();

            Vector4 background =
                isCurrent || hovered
                    ? ThemeColor(6)
                    : ThemeColor(10);

            draw.AddRectFilled(
                pos,
                pos + size,
                ImGui.ColorConvertFloat4ToU32(background),
                3f
            );

            string label = ward.ToString();

            float labelWidth = ImGui.CalcTextSize(label).X;

            Vector4 foreground = isCurrent || hovered
                ? NavAccent
                : enabled
                    ? ThemeColor(2)
                    : NavMuted;

            draw.AddText(
                pos + new Vector2(
                    (width - labelWidth) / 2f,
                    6f
                ),
                ImGui.ColorConvertFloat4ToU32(foreground),
                label
            );

            if (!enabled)
                ImGui.EndDisabled();

            if (clicked && enabled)
                StartHousingTravel(ward);

            if (column < columns - 1)
                ImGui.SameLine(0f, gap);
        }

        ImGui.Spacing();

        if (housingPhase == HousingPhase.Idle &&
            atEntrance)
        {
            if (ImGui.SmallButton("Leave district"))
                StartHousingExit();

            ImGui.Spacing();
        }
        if (housingPhase != HousingPhase.Idle)
        {
            ImGui.TextColored(
                NavAccent,
                "Travel: " + housingPhase
            );

            ImGui.SameLine();

            if (ImGui.SmallButton("Cancel"))
                StopHousing("Housing travel cancelled.");
        }
        else if (!detected)
        {
            ImGui.TextDisabled(
                "Approach an Aetheryte or ward entrance."
            );
        }

        if (!string.IsNullOrWhiteSpace(housingStatus) &&
            housingStatus != "Select a ward to travel." &&
            housingPhase == HousingPhase.Idle)
        {
            ImGui.TextWrapped(housingStatus);
        }
    }

    // Centralized diagnostics for Settings.
    // Includes active World Visit diagnostics.
    private string BuildDiagnosticsReport()
    {
        bool found = TryFindHousingAetheryte(
            out var district,
            out var crystal
        );

        string detected = found
            ? district.Name + " (" + district.CityName + ")"
            : "No main housing Aetheryte nearby";

        return
            "=== AETHERNET ===\n" +
            "Territory: " + Client.TerritoryType + "\n" +
            "Network group: " + activeGroup + "\n" +
            "Nearby object: " + (nearby?.BaseId ?? 0) + "\n" +
            "Travel phase: " + phase + "\n" +
            "Callbacks: " + callbackAttempts + "/2\n" +
            "Native menu: " +
            (GetAddon("TelepotTown") != null) + "\n" +
            lastMenuReadout + "\n\n" +
            "=== HOUSING ===\n" +
            "Proximity source: " + autoWindowSource + "\n" +
            "Auto-window ownership: " +
            autoWindowOwnsVisibility + "\n" +
            "Detected: " + detected + "\n" +
            "Entrance: " + DescribeWardEntrance() + "\n" +
            "Aetheryte DataId: " +
            (crystal == null ? "none" : crystal.BaseId.ToString()) +
            "\n" +
            "Phase: " + housingPhase + "\n" +
            "Current ward: " + GetCurrentHousingWard() + "\n" +
            "Initial ward: " + housingInitialWard + "\n" +
            "Gate movement: " + housingGateWalkDiagnostic + "\n" +
            "Target: " + housingTarget.Name +
            ", Ward " + housingTargetWard + "\n" +
            "Status: " + housingStatus + "\n" +
            housingDiagnostic +
            "\n\n=== RESIDENTIAL AETHERNET ===\n" +
            "Phase: " + residentialPhase + "\n" +
            "Status: " + residentialStatus + "\n" +
            residentialMenuReadout +
            "\n\n" + BuildCosmicResearchReport() +

            BuildCosmicPlanetResearchReport() +
            BuildCosmicTravelReport() +


            BuildCosmicLocalTravelReport() +
            BuildWorldVisitSelectionReport() +
            BuildWorldVisitJourneyReport() +
            "\n\n" + BuildOverworldNativeMenuReport();
    }
}
