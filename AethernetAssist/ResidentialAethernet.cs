using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private sealed record ResidentialStop(
        uint Id,
        int Index,
        string Name,
        bool Subdivision
    );

    private enum ResidentialPhase
    {
        Idle,
        Interact,
        SelectDestination,
        WaitForArrival
    }

    private readonly Dictionary<uint, List<ResidentialStop>>
        residentialNetworks = new();

    private ResidentialPhase residentialPhase =
        ResidentialPhase.Idle;

    private ResidentialStop? residentialTarget;
    private nint residentialSourceAddress;
    private uint residentialOriginTerritory;
    private Vector3 residentialOriginPosition;
    private DateTime residentialPhaseStarted;
    private DateTime residentialLastAction;
    private bool residentialSawLoading;
    private int residentialCallbackAttempts;

    private string residentialStatus =
        "Approach a residential Aethernet shard.";

    private string residentialMenuReadout =
        "No residential destination selected.";

    private void LoadResidentialNetworks()
    {
        foreach (var group in Data
            .GetExcelSheet<HousingAethernet>()
            .GroupBy(x => x.TerritoryType.RowId))
        {
            var rows = group
                .OrderBy(x => x.Order)
                .ThenBy(x => x.RowId)
                .ToArray();

            int split = rows.Length / 2;
            var stops = new List<ResidentialStop>();

            for (int i = 0; i < rows.Length; i++)
            {
                string name = rows[i].PlaceName.Value.Name
                    .ToString().Trim();

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                stops.Add(new ResidentialStop(
                    rows[i].RowId,
                    i,
                    name,
                    i >= split
                ));
            }

            if (stops.Count > 0)
                residentialNetworks[group.Key] = stops;
        }

        Log.Information(
            "Residential Aethernet: " +
            residentialNetworks.Count + " networks indexed.");
    }

    private bool TryFindResidentialShard(out IGameObject? shard)
    {
        shard = null;

        if (!TryGetResidentialDistrict(out _) ||
            !residentialNetworks.ContainsKey(Client.TerritoryType))
            return false;

        var player = Objects.LocalPlayer;

        if (player == null)
            return false;

        float nearest = config.OpenRange;

        foreach (var obj in Objects)
        {
            if (obj == null ||
                !obj.IsTargetable ||
                obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            // 2004983: verified Lavender Beds shard.
            // 2003395: other known residential shard variant.
            // The English object name also covers shard instances
            // that use different district-specific BaseIDs.
            bool recognized =
                obj.BaseId is 2004983 or 2003395 ||
                string.Equals(
                    obj.Name.ToString(),
                    "Aethernet Shard",
                    StringComparison.OrdinalIgnoreCase);

            if (!recognized)
                continue;

            float distance = Vector3.Distance(
                player.Position, obj.Position);

            if (distance >= nearest)
                continue;

            nearest = distance;
            shard = obj;
        }

        return shard != null;
    }

    private void SetResidentialPhase(ResidentialPhase next)
    {
        residentialPhase = next;
        residentialPhaseStarted = DateTime.UtcNow;
    }

    private void StopResidential(string message)
    {
        residentialPhase = ResidentialPhase.Idle;
        residentialTarget = null;
        residentialSourceAddress = nint.Zero;
        residentialStatus = message;
        currentPage = NavigationPage.Aethernet;
        windowOpen = true;

        Log.Warning("AethernetAssist Residential: " + message);
    }

    private void StartResidentialTravel(ResidentialStop stop)
    {
        if (residentialPhase != ResidentialPhase.Idle ||
            phase != TravelPhase.Idle ||
            housingPhase != HousingPhase.Idle ||
            IsBusy() ||
            !TryFindResidentialShard(out var source) ||
            source == null ||
            Objects.LocalPlayer == null)
            return;

        if (GetAddon("SelectYesno") != null ||
            GetAddon("HousingSelectBlock") != null)
        {
            residentialStatus = "Close existing game menus first.";
            return;
        }

        RememberNavigationPage(NavigationPage.Aethernet);

        residentialTarget = stop;
        residentialSourceAddress = source.Address;
        residentialOriginTerritory = Client.TerritoryType;
        residentialOriginPosition = Objects.LocalPlayer.Position;
        residentialSawLoading = false;
        residentialCallbackAttempts = 0;
        residentialLastAction = DateTime.MinValue;

        residentialMenuReadout =
            "Requested " + stop.Name + ", sheet row " + stop.Id;

        residentialStatus = "Opening " + stop.Name + "...";
        SetResidentialPhase(ResidentialPhase.Interact);
        windowOpen = false;
    }

    private void TickResidentialTravel()
    {
        if (residentialPhase == ResidentialPhase.Idle)
            return;

        if (residentialTarget == null)
        {
            StopResidential("No residential destination selected.");
            return;
        }

        var now = DateTime.UtcNow;
        var player = Objects.LocalPlayer;

        if (residentialPhase == ResidentialPhase.WaitForArrival)
        {
            bool busy = IsBusy();
            bool changedTerritory =
                Client.TerritoryType != residentialOriginTerritory;

            if (busy || changedTerritory)
                residentialSawLoading = true;

            float moved = player == null
                ? 0f
                : Vector3.Distance(
                    player.Position, residentialOriginPosition);

            bool menuOpen = GetAddon("TelepotTown") != null;

            if (player != null &&
                !busy &&
                !menuOpen &&
                (changedTerritory ||
                 moved > 8f ||
                 (residentialSawLoading && moved > 0.5f)))
            {
                string arrived = residentialTarget.Name;

                residentialPhase = ResidentialPhase.Idle;
                residentialTarget = null;
                residentialSourceAddress = nint.Zero;
                residentialStatus = "Arrived: " + arrived;
                autoWindowWasInRange = false;
                resetDestinationScroll = true;

                // Let proximity detection reopen the window
                // if the arrival point is near another shard.
                return;
            }

            double elapsed =
                (now - residentialPhaseStarted).TotalSeconds;

            // The existing city transport may require two
            // native callbacks. Re-enter destination selection
            // only if the menu is still visible after attempt 1.
            //
            // Selection revalidates the destination name,
            // type, state and callback ID before retrying.

            if (menuOpen && !busy && elapsed > 7)
            {
                StopResidential(
                    "Native menu rejected travel (2 attempts).");
                return;
            }

            if (!menuOpen && !residentialSawLoading &&
                moved <= 8f && elapsed > 11)
            {
                StopResidential(
                    "Menu closed, but residential travel was not confirmed.");
                return;
            }

            if (elapsed > 35)
                StopResidential("Residential travel timed out.");

            return;
        }

        if (residentialCallbackAttempts > 0 &&
            (IsBusy() || GetAddon("TelepotTown") == null))
        {
            SetResidentialPhase(ResidentialPhase.WaitForArrival);
            return;
        }

        if (player == null || IsBusy())
        {
            StopResidential("Residential travel unavailable.");
            return;
        }

        if ((now - residentialPhaseStarted).TotalSeconds > 10)
        {
            StopResidential("Residential native menu timed out.");
            return;
        }

        if (residentialPhase == ResidentialPhase.Interact)
        {
            if (GetAddon("TelepotTown") != null)
            {
                SetResidentialPhase(
                    ResidentialPhase.SelectDestination);
                return;
            }

            if ((now - residentialLastAction)
                .TotalMilliseconds < 750)
                return;

            var source = Objects.FirstOrDefault(x =>
                x != null &&
                x.Address == residentialSourceAddress);

            if (source == null ||
                !source.IsTargetable ||
                !TryGetResidentialDistrict(out _) ||
                Vector3.Distance(
                    player.Position,
                    source.Position) > config.OpenRange + 1f)
            {
                StopResidential(
                    "Moved away from residential shard.");
                return;
            }

            var target = TargetSystem.Instance();

            if (target == null)
            {
                StopResidential("Target system unavailable.");
                return;
            }

            target->InteractWithObject(
                (GameObject*)source.Address,
                false);

            residentialLastAction = now;
            return;
        }

        if (residentialPhase != ResidentialPhase.SelectDestination)
            return;

        var addon = GetAddon("TelepotTown");

        if (addon == null || addon->AtkValues == null)
            return;

        var values = addon->AtkValues;
        int valueCount = addon->AtkValuesCount;

        if (valueCount <= 262)
            return;

        int foundRow = -1;
        int maxRows = Math.Min(40, valueCount - 262);

        for (int i = 0; i < maxRows; i++)
        {
            var nameValue = values[262 + i];

            if ((int)nameValue.Type == 0)
                break;

            bool isText =
                nameValue.Type == AtkValueType.String ||
                nameValue.Type == AtkValueType.ConstString ||
                nameValue.Type == AtkValueType.WideString ||
                nameValue.Type == AtkValueType.ManagedString;

            if (!isText || nameValue.String.Value == null)
                continue;

            string name;

            try
            {
                name = Clean(
                    MemoryHelper.ReadSeStringNullTerminated(
                        (nint)nameValue.String.Value)
                    .TextValue);
            }
            catch
            {
                continue;
            }

            if (!string.Equals(
                name,
                Clean(residentialTarget.Name),
                StringComparison.OrdinalIgnoreCase))
                continue;

            if (foundRow >= 0)
            {
                StopResidential(
                    "Duplicate native destination names. Travel blocked.");
                return;
            }

            foundRow = i;
        }

        if (foundRow < 0)
        {
            residentialMenuReadout =
                "Destination not found in native menu: " +
                residentialTarget.Name;

            if ((now - residentialPhaseStarted)
                .TotalSeconds > 4)
                StopResidential(residentialMenuReadout);

            return;
        }

        int offset = 6 + foundRow * 4;

        if (offset + 3 >= valueCount)
        {
            StopResidential("Native destination metadata missing.");
            return;
        }

        var type = values[offset];
        var state = values[offset + 1];
        var callback = values[offset + 3];

        if (type.Type != AtkValueType.UInt ||
            state.Type != AtkValueType.UInt ||
            callback.Type != AtkValueType.UInt)
        {
            StopResidential("Unexpected native destination data types.");
            return;
        }

        residentialMenuReadout =
            "Row=" + foundRow +
            " Name=" + residentialTarget.Name +
            " Type=" + type.UInt +
            " State=" + state.UInt +
            " Callback=" + callback.UInt +
            " Expected=" + residentialTarget.Index;

        // Type 4 marks a non-destination group header
        // in the verified Lavender Beds native menu.
        if (type.UInt != 0)
        {
            StopResidential(
                "Selected native entry is not a destination.");
            return;
        }

        // DEV10 FIX1: The Brimming Heart in the Goblet
        // has been manually verified as accessible despite
        // the native menu reporting state 4.
        // Never permit state 4 for other destinations.
        bool verifiedBrimmingHeart =
            state.UInt == 4 &&
            Client.TerritoryType == 341 &&
            string.Equals(
                Clean(residentialTarget.Name),
                "The Brimming Heart",
                StringComparison.OrdinalIgnoreCase);

        if (state.UInt != 0 && !verifiedBrimmingHeart)
        {
            StopResidential(
                "Destination is current, locked, or unavailable. " +
                "(Native state " + state.UInt + ")");
            return;
        }

        // Preserve ordering integrity. An unexpected menu
        // mapping must not result in travel elsewhere.
        if (callback.UInt != (uint)residentialTarget.Index)
        {
            StopResidential(
                "Destination callback ID does not match sheet order.");
            return;
        }

        // No blind or unlimited callbacks.
        if (residentialCallbackAttempts >= 2)
        {
            SetResidentialPhase(ResidentialPhase.WaitForArrival);
            return;
        }

        AtkValue* args = stackalloc AtkValue[2];

        args[0] = new AtkValue
        {
            Type = AtkValueType.Int,
            Int = 11
        };

        args[1] = new AtkValue
        {
            Type = AtkValueType.UInt,
            UInt = callback.UInt
        };

        bool dispatchResult = addon->FireCallback(2, args, true);

        residentialCallbackAttempts++;

        residentialMenuReadout +=
            " | Callback " + residentialCallbackAttempts + "/2" +
            " handlerReturn=" + dispatchResult;


        residentialStatus =
            "Teleporting to " + residentialTarget.Name + "...";

        if (config.ShowDebug)
            Log.Information(residentialMenuReadout);

        residentialLastAction = now;
        if (residentialCallbackAttempts >= 2)
            SetResidentialPhase(ResidentialPhase.WaitForArrival);
    }

    private void DrawResidentialStop(ResidentialStop stop)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();

        Vector2 size = new(
            MathF.Max(
                100f,
                ImGui.GetContentRegionAvail().X - 4f),
            26f);

        ImGui.InvisibleButton(
            "##residential-destination-" + stop.Id,
            size);

        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked();

        var draw = ImGui.GetWindowDrawList();

        if (hovered)
        {
            draw.AddRectFilled(
                pos,
                pos + size,
                ImGui.ColorConvertFloat4ToU32(
                    ThemeColor(6)),
                3f);
        }

        // Same diamond icon and text spacing as
        // the existing city Aethernet destination rows.
        ImGui.PushClipRect(pos, pos + size, true);

        draw.AddText(
            pos + new Vector2(10f, 5f),
            ImGui.ColorConvertFloat4ToU32(
                NavAccent),
            "\u25C6");

        draw.AddText(
            pos + new Vector2(29f, 5f),
            ImGui.ColorConvertFloat4ToU32(
                ThemeColor(2)),
            stop.Name);

        if (hovered)
        {
            draw.AddText(
                pos + new Vector2(size.X - 18f, 5f),
                ImGui.ColorConvertFloat4ToU32(
                    NavAccent),
                ">");
        }

        ImGui.PopClipRect();

        if (clicked)
            StartResidentialTravel(stop);
    }

    private void DrawResidentialAethernetPage()
    {
        if (!TryGetResidentialDistrict(out var district) ||
            !residentialNetworks.TryGetValue(
                Client.TerritoryType, out var stops))
            return;

        ImGui.TextColored(
            NavAccent,
            district.Name.ToUpperInvariant());

        var manager =
            FFXIVClientStructs.FFXIV.Client.Game
                .HousingManager.Instance();

        string division = manager == null
            ? "Unknown division"
            : manager->GetCurrentDivision() == 2
                ? "Subdivision"
                : "Main Division";

        ImGui.SameLine();
        ImGui.TextDisabled(
            "Ward " + GetCurrentHousingWard() +
            " | " + division);

        ImGui.Spacing();

        // Size the initial viewport using MAIN DIVISION
        // destinations only, matching the city Aethernet
        // behavior where OTHER starts below the fold.
        int mainCount = stops.Count(x => !x.Subdivision);

        float rowHeight =
            26f + ImGui.GetStyle().ItemSpacing.Y;

        float headerHeight =
            ImGui.GetTextLineHeightWithSpacing() + 8f;

        float viewport = Math.Clamp(
            mainCount * rowHeight + headerHeight + 10f,
            100f,
            320f);

        if (ImGui.BeginChild(
            "##ResidentialDestinations",
            new Vector2(0f, viewport),
            false))
        {
            if (resetDestinationScroll)
            {
                ImGui.SetScrollY(0f);
                resetDestinationScroll = false;
            }

            foreach (bool sub in new[] { false, true })
            {
                var group = stops
                    .Where(x => x.Subdivision == sub)
                    .ToArray();

                if (group.Length == 0)
                    continue;

                // Match the city Aethernet OTHER category:
                // keep the secondary group completely
                // below the initially visible viewport.
                if (sub)
                    ImGui.Dummy(new Vector2(0f, 38f));

                ImGui.TextColored(
                    NavMuted,
                    sub ? "SUBDIVISION" : "MAIN DIVISION");

                ImGui.Spacing();

                foreach (var stop in group)
                    DrawResidentialStop(stop);

                ImGui.Spacing();
            }
        }

        ImGui.EndChild();
        ImGui.Separator();

        // This page is shown only while a residential
        // shard is detected. Show Ready instead of the
        // original startup instruction.
        string displayedStatus =
            residentialStatus ==
                "Approach a residential Aethernet shard."
                ? "Ready"
                : residentialStatus;

        bool ready =
            displayedStatus == "Ready" ||
            displayedStatus.StartsWith(
                "Arrived:",
                StringComparison.Ordinal);

        ImGui.TextColored(
            ready ? NavReady : NavMuted,
            "\u25CF");

        ImGui.SameLine();
        ImGui.TextWrapped(displayedStatus);
    }
}