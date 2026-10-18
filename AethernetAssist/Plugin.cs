using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AetheryteSheet = Lumina.Excel.Sheets.Aetheryte;
using TerritorySheet = Lumina.Excel.Sheets.TerritoryType;
using NativeQuestManager = FFXIVClientStructs.FFXIV.Client.Game.QuestManager;

namespace AethernetAssist;

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IDalamudPluginInterface Pi { get; set; } = null!;

    [PluginService]
    private static ICommandManager Commands { get; set; } = null!;

    [PluginService]
    private static IFramework Framework { get; set; } = null!;

    [PluginService]
    private static IClientState Client { get; set; } = null!;

    [PluginService]
    private static IObjectTable Objects { get; set; } = null!;

    [PluginService]
    private static IDataManager Data { get; set; } = null!;

    [PluginService]
    private static IAetheryteList Aetherytes { get; set; } = null!;

    [PluginService]
    private static IGameGui Gui { get; set; } = null!;

    [PluginService]
    private static ICondition Conditions { get; set; } = null!;

    [PluginService]
    private static IKeyState KeyState { get; set; } = null!;

    [PluginService]
    private static IPluginLog Log { get; set; } = null!;

    [PluginService]
    private static IAddonLifecycle AddonLifecycle { get; set; } = null!;

    [PluginService]
    private static IAgentLifecycle AgentLifecycle { get; set; } = null!;

    [PluginService]
    private static IChatGui Chat { get; set; } = null!;

    private sealed record Shard(
        uint Id,
        uint Territory,
        uint Group,
        string Name,
        bool IsMain,
        bool Invisible,
        uint RequiredQuest
    );

    private readonly Configuration config;

    private readonly Dictionary<uint, List<Shard>> networks = new();
    private readonly Dictionary<uint, HashSet<uint>> territories = new();

    private static readonly uint[] GridaniaOrder =
    {
        2, 25, 26, 27, 28, 29, 30, 31, 32, 54, 94
    };

    private static readonly uint[] LimsaOrder =
    {
        8, 43, 44, 49, 41, 42, 48, 45, 46, 93
    };

    private static readonly uint[] UldahOrder =
    {
        9, 33, 34, 35, 36, 47, 50, 125,
        37, 51, 38, 39, 40, 95
    };

    private static readonly uint[] EulmoreOrder =
    {
        134, 135, 158, 159, 157, 160
    };

    private static readonly uint[] RadzOrder =
    {
        183, 191, 192, 193, 194, 195,
        196, 198, 199, 197
    };

    private enum TravelPhase
    {
        Idle,
        Interact,
        SelectAethernet,
        SelectDestination,
        WaitForArrival
    }

    private TravelPhase phase = TravelPhase.Idle;

    private Shard? destination;
    private nint originAddress;
    private uint originTerritory;
    private Vector3 originPosition;

    private DateTime phaseStarted;
    private DateTime lastInteraction = DateTime.MinValue;
    private DateTime lastScan = DateTime.MinValue;

    private int callbackAttempts;
    private bool sawLoading;

    // Wait until the native destination entry has remained
    // stable before submitting the first callback.
    private DateTime nativeDestinationReadySince = DateTime.MinValue;
    private int nativeDestinationReadyRow = -1;
    private uint nativeDestinationReadyCallback;
    private bool nativeAutomaticRetryUsed;

    private IGameObject? nearby;
    private nint nearbyAddress;
    private uint activeGroup;
    private uint activeTerritory;

    private bool windowOpen;
    private string status = "Approach an Aethernet shard.";
    private string lastMenuReadout = "No destination inspected yet.";

    public Plugin()
    {
        config = Pi.GetPluginConfig() as Configuration
            ?? new Configuration();

        // Migrate older settings to the new proximity default.
        if (config.Version < 2)
        {
            config.Version = 2;
            config.OpenRange = 11.1f;
            Save();
        }

        config.OpenRange = Math.Clamp(
            config.OpenRange, 8f, 11.1f);

        LoadNetworks();
        LoadResidentialNetworks();
        LoadCosmicLocalNetworks();
        LoadOverworldAetherytes();
        InitializeMapClickResearch();

        Commands.AddHandler("/anet", new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open AethernetAssist. /anet debug toggles diagnostics. " +
                "/anet cancel stops automation."
        });

        Framework.Update += Update;

        Pi.UiBuilder.Draw += Draw;
        Pi.UiBuilder.OpenMainUi += Open;
        Pi.UiBuilder.OpenConfigUi += OpenConfig;

        Log.Information("AethernetAssist " + UiVersion + " initialized.");
    }

    public void Dispose()
    {
        StopMapRouteWalk();
        StopHousingGateWalk();
        StopCosmicPath();
        // Temporary native addon capture removed.
        cosmicPhase = CosmicPhase.Idle;
        DisposeMapClickResearch();
        Framework.Update -= Update;

        Pi.UiBuilder.Draw -= Draw;
        Pi.UiBuilder.OpenMainUi -= Open;
        Pi.UiBuilder.OpenConfigUi -= OpenConfig;

        Commands.RemoveHandler("/anet");

        phase = TravelPhase.Idle;
    }

    private void LoadNetworks()
    {
        foreach (var row in Data.GetExcelSheet<AetheryteSheet>())
        {
            // The Aetheryte object kind also includes small
            // Aethernet shards. Use the game's sheet flag.
            if (row.IsAetheryte &&
                HousingDistricts.Any(d =>
                    d.CityTerritory == row.Territory.Value.RowId))
                housingMainAetheryteIds.Add(row.RowId);

            if (row.AethernetGroup == 0)
                continue;

            var name = row.AethernetName.Value.Name
                .ToString()
                .Trim();

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var shard = new Shard(
                row.RowId,
                row.Territory.Value.RowId,
                (uint)row.AethernetGroup,
                name,
                row.IsAetheryte,
                row.Invisible,
                row.RequiredQuest.RowId
            );

            if (!networks.TryGetValue(shard.Group, out var entries))
            {
                entries = new List<Shard>();
                networks[shard.Group] = entries;
            }

            entries.Add(shard);

            if (!shard.Invisible)
            {
                if (!territories.TryGetValue(shard.Territory, out var groups))
                {
                    groups = new HashSet<uint>();
                    territories[shard.Territory] = groups;
                }

                groups.Add(shard.Group);
            }
        }

        foreach (var entries in networks.Values)
        {
            var root = entries.FirstOrDefault(x => x.IsMain);

            uint[] order = root?.Id switch
            {
                2 => GridaniaOrder,
                8 => LimsaOrder,
                9 => UldahOrder,
                134 => EulmoreOrder,
                183 => RadzOrder,
                _ => Array.Empty<uint>()
            };

            entries.Sort((a, b) =>
                SortKey(a, order).CompareTo(SortKey(b, order)));
        }

        Log.Information(
            $"Indexed {networks.Count} Aethernet networks, " +
            $"{networks.Values.Sum(x => x.Count)} destinations.");
    }

    private static int SortKey(Shard shard, uint[] order)
    {
        var index = Array.IndexOf(order, shard.Id);

        if (index >= 0)
            return index;

        return shard.IsMain
            ? 0
            : 1000 + (int)shard.Id;
    }

    private static bool IsBusy() =>
        Conditions[ConditionFlag.BetweenAreas] ||
        Conditions[ConditionFlag.BetweenAreas51] ||
        Conditions[ConditionFlag.WaitingToVisitOtherWorld] ||
        Conditions[ConditionFlag.ReadyingVisitOtherWorld];

    private static bool IsUnlocked(uint id)
    {
        var state = UIState.Instance();

        return state != null &&
               state->IsAetheryteUnlocked(id);
    }

    private bool DestinationAvailable(Shard shard)
    {
        if (!shard.Invisible)
            return IsUnlocked(shard.Id);

        if (!networks.TryGetValue(shard.Group, out var entries))
            return false;

        // Standard additional destinations require the local
        // physical Aethernet network to be fully attuned.
        foreach (var entry in entries)
        {
            if (!entry.Invisible && !IsUnlocked(entry.Id))
                return false;
        }

        // Some exits additionally require quest completion.
        if (shard.RequiredQuest != 0 &&
            !NativeQuestManager.IsQuestComplete(shard.RequiredQuest))
            return false;

        return true;
    }

    private string GetAreaName(uint territory)
    {
        try
        {
            var name = Data.GetExcelSheet<TerritorySheet>()
                .GetRow(territory).PlaceName.Value.Name.ToString();

            return string.IsNullOrWhiteSpace(name)
                ? $"Area {territory}"
                : name;
        }
        catch
        {
            return $"Area {territory}";
        }
    }

    private void DrawDestination(Shard shard)
    {
        bool locked = !DestinationAvailable(shard);

        if (locked && !config.ShowUnlearned)
            return;

        Vector2 pos = ImGui.GetCursorScreenPos();
        Vector2 size = new(
            MathF.Max(100f, ImGui.GetContentRegionAvail().X - 4f),
            26f);

        ImGui.InvisibleButton($"##destination-{shard.Id}", size);

        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked();

        var draw = ImGui.GetWindowDrawList();

        if (hovered && !locked)
        {
            draw.AddRectFilled(
                pos, pos + size,
                ImGui.ColorConvertFloat4ToU32(
                    ThemeColor(6)),
                3f);
        }

        Vector4 iconColor = locked ? NavMuted : NavAccent;
        Vector4 textColor = locked
            ? ThemeColor(3)
            : ThemeColor(2);

        string label = locked
            ? $"{shard.Name}  (locked)"
            : shard.Name;

        ImGui.PushClipRect(pos, pos + size, true);

        draw.AddText(
            pos + new Vector2(10f, 5f),
            ImGui.ColorConvertFloat4ToU32(iconColor),
            "\u25C6");

        draw.AddText(
            pos + new Vector2(29f, 5f),
            ImGui.ColorConvertFloat4ToU32(textColor),
            label);

        if (hovered && !locked)
        {
            draw.AddText(
                pos + new Vector2(size.X - 18f, 5f),
                ImGui.ColorConvertFloat4ToU32(NavAccent),
                ">");
        }

        ImGui.PopClipRect();

        if (clicked && !locked)
            StartTravel(shard);
    }

    private static bool IsShardObject(IGameObject obj)
    {
        if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte)
            return true;

        return obj.BaseId is 2000151 or 2014665;
    }

    private void Save() => Pi.SavePluginConfig(config);

    private void Open()
    {
        OpenWindowManually();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "debug":
                config.ShowDebug = !config.ShowDebug;
                Save();
                settingsWindowOpen = true;
                break;

            case "settings":
            case "config":
                settingsWindowOpen = true;
                break;


            case "housing":
                OpenWindowManually();

                currentPage = HousingPageAvailable()
                    ? NavigationPage.Housing
                    : NavigationPage.Aethernet;

                if (currentPage == NavigationPage.Housing)
                    RememberNavigationPage(NavigationPage.Housing);

                break;

            case "sinus":
                StartCosmicTravel(0);
                break;

            case "phaenna":
                StartCosmicTravel(1);
                break;

            case "oizys":
                StartCosmicTravel(2);
                break;

            case "auxesia":
                StartCosmicTravel(3);
                break;

            case "cancel":
                if (mapRoutePhase != MapRoutePhase.Idle)
                    StopMapRoute(
                        "Tracking stopped. An accepted teleport " +
                        "cannot be reversed.");
                else if (mapRouteTarget != null)
                    ClearMapRouteSelection();
                else if (overworldPhase != OverworldPhase.Idle)
                    StopOverworldTeleport(
                        "Stopped tracking. An accepted teleport cannot be cancelled here.",
                        false);
                else if (overworldArmedDestination != 0)
                {
                    ClearOverworldSelection();
                    overworldStatus = "Teleport selection cancelled.";
                }
                else if (worldVisitPhase != WorldVisitPhase.Idle)
                    TryStopWorldVisitFromUser();
                else if (cosmicLocalPhase != CosmicLocalPhase.Idle)
                    StopCosmicLocal("Cancelled by user.");
                else if (cosmicPhase != CosmicPhase.Idle)
                    StopCosmicTravel("Cancelled by user.");
                else if (residentialPhase != ResidentialPhase.Idle)
                    StopResidential("Cancelled by user.");
                else if (housingPhase != HousingPhase.Idle)
                    StopHousing("Cancelled by user.");
                else
                    Cancel("Cancelled by user.");
                break;

            default:
                OpenWindowManually();
                break;
        }
    }

    private void Update(IFramework framework)
    {
        try
        {
            TickMapRoute();

            if (mapRoutePhase == MapRoutePhase.WaitingForPaidArrival ||
                mapRoutePhase == MapRoutePhase.WaitingForSource)
                return;

            if (worldVisitPhase != WorldVisitPhase.Idle)
            {
                TickWorldVisit();
                return;
            }

            if (cosmicLocalPhase != CosmicLocalPhase.Idle)
            {
                TickCosmicLocal();
                return;
            }

            if (cosmicPhase != CosmicPhase.Idle)
            {
                TickCosmicTravel();
                return;
            }

            if (residentialPhase != ResidentialPhase.Idle)
            {
                TickResidentialTravel();
                return;
            }

            if (housingPhase != HousingPhase.Idle)
            {
                TickHousing();
                return;
            }

            if (phase != TravelPhase.Idle)
            {
                TickTravel();
                return;
            }

            if (overworldPhase != OverworldPhase.Idle)
            {
                TickOverworldTeleport();
                return;
            }


            if ((DateTime.UtcNow - lastScan).TotalMilliseconds < 200)
                return;

            lastScan = DateTime.UtcNow;
            ScanNearby();
            ScanCosmicLocalNearby();
            ScanOverworldNearby();
            CaptureOverworldNativeMenu();
            UpdateProximityWindow();
        }
        catch (Exception ex)
        {
            Log.Error($"AethernetAssist: {ex}");

            if (mapRoutePhase != MapRoutePhase.Idle)
                StopMapRoute(
                    "Map route stopped after an internal error."
                );
            else if (overworldPhase != OverworldPhase.Idle)
                StopOverworldTeleport(
                    "Stopped tracking after an internal error.",
                    false);
            else if (worldVisitPhase != WorldVisitPhase.Idle)
                StopWorldVisit("Stopped after an internal error.");
            else if (cosmicLocalPhase != CosmicLocalPhase.Idle)
                StopCosmicLocal("Stopped after an internal error.");
            else if (cosmicPhase != CosmicPhase.Idle)
                StopCosmicTravel("Stopped after an internal error.");
            else if (residentialPhase != ResidentialPhase.Idle)
                StopResidential("Stopped after an internal error.");
            else if (housingPhase != HousingPhase.Idle)
                StopHousing("Stopped after an internal error.");
            else
                Cancel("Stopped after an internal error.");
        }
    }

    private void ScanNearby()
    {
        var player = Objects.LocalPlayer;

        if (player == null || IsBusy() ||
            !territories.TryGetValue(Client.TerritoryType, out var groups))
        {
            ClearNearby();
            return;
        }

        IGameObject? nearest = null;

        var maxRange = config.OpenRange;

        var closest = maxRange;

        foreach (var obj in Objects)
        {
            if (obj == null ||
                !obj.IsTargetable ||
                !IsShardObject(obj))
                continue;

            var distance = Vector3.Distance(
                player.Position,
                obj.Position
            );

            if (distance >= closest)
                continue;

            nearest = obj;
            closest = distance;
        }

        if (nearest == null)
        {
            ClearNearby();
            return;
        }

        uint resolvedGroup = 0;

        if (groups.Count == 1)
        {
            resolvedGroup = groups.First();
        }
        else
        {
            // Never silently select the wrong network.
            var matching = groups.Where(group =>
                networks[group].Any(shard =>
                    string.Equals(
                        shard.Name,
                        nearest.Name.ToString(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            ).ToArray();

            if (matching.Length == 1)
                resolvedGroup = matching[0];
        }

        if (resolvedGroup == 0)
        {
            ClearNearby();
            status = "Could not identify the local network.";
            return;
        }

        bool changed =
            nearbyAddress != nearest.Address ||
            activeGroup != resolvedGroup ||
            activeTerritory != Client.TerritoryType;

        nearby = nearest;
        nearbyAddress = nearest.Address;
        activeGroup = resolvedGroup;
        activeTerritory = Client.TerritoryType;

        if (changed)
        {
            status = "Ready";
            resetDestinationScroll = true;
        }


    }

    private void ClearNearby()
    {
        nearby = null;
        nearbyAddress = 0;
        activeGroup = 0;
        activeTerritory = 0;
    }

    private void StartTravel(Shard chosen)
    {
        if (phase != TravelPhase.Idle ||
            nearby == null ||
            Objects.LocalPlayer == null ||
            IsBusy() ||
            !DestinationAvailable(chosen))
            return;

        if (!nearby.IsTargetable ||
            Vector3.Distance(
                Objects.LocalPlayer.Position,
                nearby.Position
            ) > config.OpenRange + 1f)
            return;

        RememberNavigationPage(NavigationPage.Aethernet);

        destination = chosen;
        originAddress = nearby.Address;
        originTerritory = Client.TerritoryType;
        originPosition = Objects.LocalPlayer.Position;

        callbackAttempts = 0;
        sawLoading = false;
        nativeAutomaticRetryUsed = false;
        nativeDestinationReadySince = DateTime.MinValue;
        nativeDestinationReadyRow = -1;

        lastInteraction = DateTime.MinValue;

        lastMenuReadout = $"Requested destination: {chosen.Name}";
        status = $"Opening Aethernet for {chosen.Name}...";

        GoTo(TravelPhase.Interact);

        windowOpen = false;
    }

    private void GoTo(TravelPhase next)
    {
        if (next == TravelPhase.SelectDestination &&
            phase != TravelPhase.SelectDestination)
        {
            nativeDestinationReadySince = DateTime.MinValue;
            nativeDestinationReadyRow = -1;
        }

        phase = next;
        phaseStarted = DateTime.UtcNow;
    }

    private void TickTravel()
    {
        if (destination == null)
        {
            Cancel("No destination selected.");
            return;
        }

        if (phase == TravelPhase.WaitForArrival)
        {
            var player = Objects.LocalPlayer;
            bool busy = IsBusy();
            bool menuOpen = GetAddon("TelepotTown") != null;
            bool territoryChanged =
                Client.TerritoryType != originTerritory;

            float moved = player == null
                ? 0f
                : Vector3.Distance(player.Position, originPosition);

            if (busy || territoryChanged)
                sawLoading = true;

            // Confirm that the game has actually accepted the
            // teleport instead of assuming the callback succeeded.
            if (player != null && !busy && !menuOpen &&
                (sawLoading || territoryChanged || moved > 12f))
            {
                status = $"Arrived: {destination.Name}";
                phase = TravelPhase.Idle;
                destination = null;
                ClearNearby();
                return;
            }

            double elapsed =
                (DateTime.UtcNow - phaseStarted).TotalSeconds;

            // If the first validated callback pair was ignored,
            // give the fully opened native menu one more chance.
            // Never retry once loading or travel has begun.
            if (menuOpen && !busy && !territoryChanged &&
                !sawLoading && !nativeAutomaticRetryUsed &&
                elapsed > 2.75)
            {
                nativeAutomaticRetryUsed = true;
                callbackAttempts = 0;

                status =
                    $"Native menu still open; retrying {destination.Name}...";

                Log.Warning(
                    "AethernetAssist: first native callback pair " +
                    "was not acknowledged; retrying once.");

                GoTo(TravelPhase.SelectDestination);
                return;
            }

            // A visible native menu after two callbacks means
            // that destination selection was not acknowledged.
            if (menuOpen && !busy && !territoryChanged &&
                elapsed > 5)
            {
                lastMenuReadout +=
                    $" | Unacknowledged: menu open, callbacks={callbackAttempts}," +
                    $" territory={Client.TerritoryType}, moved={moved:0.0}y";

                Cancel("Destination not accepted: native Aethernet menu stayed open.");
                return;
            }

            if (!menuOpen && !sawLoading && moved <= 12f &&
                elapsed > 9)
            {
                Cancel("Native menu closed, but no teleport or movement was detected.");
                return;
            }

            if (elapsed > 35)
                Cancel("Teleport arrival could not be confirmed.");

            return;
        }

        if (callbackAttempts > 0 &&
            (IsBusy() || GetAddon("TelepotTown") == null))
        {
            GoTo(TravelPhase.WaitForArrival);
            return;
        }

        if (Objects.LocalPlayer == null || IsBusy())
        {
            Cancel("Travel unavailable in the current game state.");
            return;
        }

        if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 10)
        {
            Cancel($"Timed out during {phase}.");
            return;
        }

        if (phase == TravelPhase.Interact)
        {
            if (GetAddon("TelepotTown") != null)
            {
                GoTo(TravelPhase.SelectDestination);
                return;
            }

            if (GetAddon("SelectString") != null)
            {
                GoTo(TravelPhase.SelectAethernet);
                return;
            }

            if ((DateTime.UtcNow - lastInteraction).TotalMilliseconds < 900)
                return;

            // Re-resolve the object instead of trusting a stale pointer.
            var obj = Objects.FirstOrDefault(x =>
                x != null && x.Address == originAddress);

            if (obj == null ||
                !obj.IsTargetable ||
                Objects.LocalPlayer == null ||
                Vector3.Distance(
                    Objects.LocalPlayer.Position,
                    obj.Position
                ) > config.OpenRange + 1f)
            {
                Cancel("Moved out of Aethernet range.");
                return;
            }

            var target = TargetSystem.Instance();

            if (target == null)
            {
                Cancel("Target system unavailable.");
                return;
            }

            target->InteractWithObject(
                (GameObject*)obj.Address,
                false
            );

            lastInteraction = DateTime.UtcNow;
            return;
        }

        if (phase == TravelPhase.SelectAethernet)
        {
            if (GetAddon("TelepotTown") != null)
            {
                GoTo(TravelPhase.SelectDestination);
                return;
            }

            var addon = (AddonSelectString*)GetAddon("SelectString");

            if (addon == null)
                return;

            var menu = addon->PopupMenu.PopupMenu;

            if (menu.EntryNames == null)
                return;

            var count = Math.Clamp(menu.EntryCount, 0, 32);

            for (int i = 0; i < count; i++)
            {
                var namePtr = menu.EntryNames[i].Value;

                if (namePtr == null)
                    continue;

                var name = Marshal.PtrToStringUTF8(
                    (nint)namePtr) ?? "";

                // The native selector currently uses the English Aethernet menu label.
                if (!name.Contains(
                    "Aethernet",
                    StringComparison.OrdinalIgnoreCase))
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

                status = "Opening Aethernet destination list...";
                GoTo(TravelPhase.SelectDestination);
                return;
            }

            return;
        }

        if (phase == TravelPhase.SelectDestination)
        {
            var addon = GetAddon("TelepotTown");

            if (addon == null)
            {
                lastMenuReadout = "Waiting for TelepotTown addon.";
                return;
            }

            if (addon->AtkValues == null)
            {
                lastMenuReadout = "TelepotTown has no AtkValues.";
                return;
            }

            var values = addon->AtkValues;
            int valueCount = addon->AtkValuesCount;

            if (valueCount <= 262)
            {
                lastMenuReadout =
                    $"TelepotTown AtkValuesCount={valueCount}; expected at least 263.";
                return;
            }

            // Read populated native slots directly. Do not trust
            // AtkValue[0] as the number of destination-name rows.
            int slots = Math.Min(20, valueCount - 262);
            var observed = new List<string>();

            for (int i = 0; i < slots; i++)
            {
                var nameValue = values[262 + i];

                if ((int)nameValue.Type == 0)
                    break;

                if (nameValue.Type != AtkValueType.String &&
                    nameValue.Type != AtkValueType.ConstString &&
                    nameValue.Type != AtkValueType.WideString &&
                    nameValue.Type != AtkValueType.ManagedString)
                {
                    observed.Add($"{i}: invalid name type {nameValue.Type}");
                    continue;
                }

                var namePtr = nameValue.String.Value;

                if (namePtr == null)
                {
                    observed.Add($"{i}: null name pointer");
                    continue;
                }

                string name;

                try
                {
                    name = MemoryHelper.ReadSeStringNullTerminated(
                        (nint)namePtr).TextValue;
                }
                catch (Exception ex)
                {
                    observed.Add($"{i}: decode error {ex.Message}");
                    continue;
                }

                name = Clean(name);
                observed.Add($"{i}: {name}");

                if (!string.Equals(
                    name,
                    Clean(destination.Name),
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!DestinationAvailable(destination))
                {
                    Cancel("Destination is not attuned.");
                    return;
                }

                int callbackIndex = 6 + i * 4 + 3;

                if (callbackIndex >= valueCount)
                {
                    Cancel($"Callback data missing for row {i}.");
                    return;
                }

                var callbackValue = values[callbackIndex];

                if (callbackValue.Type != AtkValueType.UInt)
                {
                    lastMenuReadout =
                        $"Matched {name}, but callback type is {callbackValue.Type}.";
                    Cancel("Unexpected Aethernet callback data.");
                    return;
                }

                uint callbackData = callbackValue.UInt;

                int dataStart = 6 + i * 4;
                var nativeType = values[dataStart];
                var nativeState = values[dataStart + 1];

                lastMenuReadout =
                    $"Matched row {i}: {name}; callback={callbackData}; " +
                    $"Type={nativeType.UInt} ({nativeType.Type}); " +
                    $"State={nativeState.UInt} ({nativeState.Type}); " +
                    $"AtkValuesCount={valueCount}.";

                // Only real destination entries may receive
                // native callbacks.
                if (nativeType.Type != AtkValueType.UInt ||
                    nativeState.Type != AtkValueType.UInt ||
                    nativeType.UInt != 0)
                {
                    Cancel("Unexpected native Aethernet destination state.");
                    return;
                }

                // Both 0 and 4 have been observed on usable
                // city Aethernet destinations. In particular,
                // the Ul'dah Aetheryte Plaza uses state 4.
                // Attunement and destination-name checks above
                // must still pass before submitting callbacks.
                if (nativeState.UInt != 0 &&
                    nativeState.UInt != 4)
                {
                    Cancel(
                        $"Unrecognized native destination state {nativeState.UInt}. Travel blocked.");
                    return;
                }

                // At least 300ms with the same validated
                // destination row and callback ID.
                // The second callback still follows on the
                // next framework update, preserving verified callback timing.
                if (callbackAttempts == 0)
                {
                    DateTime now = DateTime.UtcNow;

                    if (nativeDestinationReadySince == DateTime.MinValue ||
                        nativeDestinationReadyRow != i ||
                        nativeDestinationReadyCallback != callbackData)
                    {
                        nativeDestinationReadySince = now;
                        nativeDestinationReadyRow = i;
                        nativeDestinationReadyCallback = callbackData;
                        return;
                    }

                    if ((now - nativeDestinationReadySince)
                        .TotalMilliseconds < 300)
                        return;
                }

                if (callbackAttempts < 2)
                {
                    AtkValue* args = stackalloc AtkValue[2];

                    args[0] = new AtkValue
                    {
                        Type = AtkValueType.Int,
                        Int = 11
                    };

                    args[1] = new AtkValue
                    {
                        Type = AtkValueType.UInt,
                        UInt = callbackData
                    };

                    bool dispatchResult = addon->FireCallback(2, args, true);

                    lastInteraction = DateTime.UtcNow;
                    callbackAttempts++;
                    lastMenuReadout +=
                        $" | Pass={(nativeAutomaticRetryUsed ? "retry" : "initial")}" +
                        $" | Callback {callbackAttempts}/2" +
                        $" | handlerReturn={dispatchResult}";


                    status = $"Teleporting to {destination.Name}...";

                    if (config.ShowDebug)
                        Log.Information(lastMenuReadout);

                    if (callbackAttempts >= 2)
                        GoTo(TravelPhase.WaitForArrival);
                }

                return;
            }

            lastMenuReadout =
                $"AtkValuesCount={valueCount}; " +
                $"entry hint={values[0].UInt}; " +
                $"scanned slots={slots}; " +
                $"names=[{string.Join(" | ", observed)}]";

            if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 5)
            {
                Log.Warning(
                    $"Could not match destination {destination.Name}. " +
                    lastMenuReadout);

                Cancel(
                    $"Could not find {destination.Name} in native menu.");
            }
        }
    }

    private static string Clean(string text)
    {
        return new string(
            text.Where(c => !char.IsControl(c)).ToArray()
        ).Trim();
    }

    private void Cancel(string message)
    {
        phase = TravelPhase.Idle;
        destination = null;
        originAddress = 0;
        callbackAttempts = 0;

        status = message;
        windowOpen = true;

        Log.Warning(message);
    }

    private static AtkUnitBase* GetAddon(string name)
    {
        var handle = Gui.GetAddonByName(name);

        if (handle == null || handle.Address == nint.Zero)
            return null;

        var addon = (AtkUnitBase*)handle.Address;

        return addon != null &&
               addon->IsReady &&
               addon->IsVisible
            ? addon
            : null;
    }

    // 17 UI colors in a stable, serialized order.
    // The default palette preserves our existing dark theme.
    private static readonly Vector4[] DefaultColors =
    {
        new(0.065f, 0.080f, 0.105f, 0.97f), // 0 Window
        new(0.077f, 0.095f, 0.120f, 0.95f), // 1 Child
        new(0.86f, 0.89f, 0.93f, 1f),        // 2 Text
        new(0.51f, 0.58f, 0.65f, 1f),        // 3 Muted
        new(0.51f, 0.76f, 0.92f, 1f),        // 4 Accent
        new(0.49f, 0.80f, 0.66f, 1f),        // 5 Ready
        new(0.14f, 0.20f, 0.26f, 0.95f),     // 6 Hover
        new(0.075f, 0.09f, 0.115f, 1f),      // 7 Scroll BG
        new(0.26f, 0.34f, 0.42f, 0.9f),       // 8 Scroll grab
        new(0.40f, 0.55f, 0.67f, 1f),        // 9 Scroll hover
        new(0.10f, 0.14f, 0.18f, 1f),        // 10 Frame
        new(0.18f, 0.25f, 0.32f, 1f),        // 11 Frame hover
        new(0.11f, 0.16f, 0.20f, 1f),        // 12 Button
        new(0.22f, 0.27f, 0.32f, 1f),        // 13 Border
        new(0.18f, 0.26f, 0.32f, 1f),        // 14 Button active
        new(0.055f, 0.065f, 0.085f, 1f),     // 15 Title
        new(0.085f, 0.12f, 0.15f, 1f)        // 16 Active title
    };

    private uint[] sampledClientColors = Array.Empty<uint>();

    private Vector4 NavAccent => ThemeColor(4);
    private Vector4 NavMuted => ThemeColor(3);
    private Vector4 NavReady => ThemeColor(5);

    private Vector4 ThemeColor(int index)
    {
        var colors = config.MatchedStyleColors;

        if (!config.UseDalamudColors ||
            colors == null ||
            colors.Length != DefaultColors.Length)
            return DefaultColors[index];

        uint packed = colors[index];

        // ImGui packs colors as ABGR.
        return new Vector4(
            (packed & 255u) / 255f,
            ((packed >> 8) & 255u) / 255f,
            ((packed >> 16) & 255u) / 255f,
            ((packed >> 24) & 255u) / 255f);
    }

    private static uint[] CaptureDalamudColors()
    {
        // Always called before our own style overrides.
        ImGuiCol[] sources =
        {
            ImGuiCol.WindowBg,
            ImGuiCol.ChildBg,
            ImGuiCol.Text,
            ImGuiCol.TextDisabled,
            ImGuiCol.CheckMark,
            ImGuiCol.PlotHistogram,
            ImGuiCol.HeaderHovered,
            ImGuiCol.ScrollbarBg,
            ImGuiCol.ScrollbarGrab,
            ImGuiCol.ScrollbarGrabHovered,
            ImGuiCol.FrameBg,
            ImGuiCol.FrameBgHovered,
            ImGuiCol.Button,
            ImGuiCol.Border,
            ImGuiCol.ButtonActive,
            ImGuiCol.TitleBg,
            ImGuiCol.TitleBgActive
        };

        var result = new uint[sources.Length];

        for (int i = 0; i < sources.Length; i++)
        {
            result[i] = ImGui.ColorConvertFloat4ToU32(
                *ImGui.GetStyleColorVec4(sources[i]));
        }

        return result;
    }

    private const int ThemePushCount = 19;

    private void PushTheme()
    {
        // Only this plugins windows are affected.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeColor(0));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemeColor(1));
        ImGui.PushStyleColor(ImGuiCol.Text, ThemeColor(2));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, ThemeColor(3));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, ThemeColor(4));
        ImGui.PushStyleColor(ImGuiCol.Button, ThemeColor(12));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ThemeColor(6));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ThemeColor(14));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ThemeColor(6));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ThemeColor(10));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ThemeColor(11));
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeColor(13));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, ThemeColor(7));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ThemeColor(8));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ThemeColor(9));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, ThemeColor(4));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, ThemeColor(4));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, ThemeColor(15));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ThemeColor(16));
    }

    private enum NavigationPage
    {
        Aethernet,
        Housing,
        DcTravel
    }

    private NavigationPage currentPage = NavigationPage.Aethernet;

    private static string UiVersion =>
        "v" + (typeof(Plugin).Assembly.GetName().Version?.ToString(3)
               ?? "Unknown");

    private void RememberNavigationPage(NavigationPage page)
    {
        int value = (int)page;

        if (config.LastSelectedNavigationPage == value)
            return;

        config.LastSelectedNavigationPage = value;
        Save();
    }

    private void RestoreNavigationPage()
    {
        // A user's saved preference takes priority over proximity.
        // Housing detection must never force Housing when the
        // user last explicitly selected Aethernet.

        int available = AvailableNavigationTabsMask();

        NavigationPage remembered =
            config.LastSelectedNavigationPage switch
            {
                0 => NavigationPage.Aethernet,
                1 => NavigationPage.Housing,
                2 => NavigationPage.DcTravel,
                _ => NavigationPage.Aethernet
            };

        // Aethernet is the universal fallback tab.
        // Do not save this fallback: the original preference
        // must survive zoning and temporary unavailability.
        NavigationPage next = remembered switch
        {
            NavigationPage.Housing when (available & 2) != 0
                => NavigationPage.Housing,

            NavigationPage.DcTravel when (available & 4) != 0
                => NavigationPage.DcTravel,

            _ => NavigationPage.Aethernet
        };

        currentPage = next;

        if (next == NavigationPage.Aethernet)
            resetDestinationScroll = true;
    }
    private bool settingsWindowOpen;
    private bool resetDestinationScroll = true;

    private void OpenConfig()
    {
        settingsWindowOpen = true;
    }

    private float GetDestinationContentHeight()
    {
        if (nearby == null ||
            !networks.TryGetValue(activeGroup, out var entries))
            return 0f;

        // Size the viewport using ONLY normal city shards.
        // Additional destinations remain below the fold.
        var city = entries.Where(x =>
            !x.Invisible &&
            (config.ShowUnlearned || DestinationAvailable(x)))
            .ToList();

        int groups = city.GroupBy(x => x.Territory).Count();

        if (city.Count == 0)
            return 72f;

        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float rowHeight = 26f + spacing;
        float groupHeight = ImGui.GetTextLineHeightWithSpacing() + 8f;

        float height = city.Count * rowHeight +
                       groups * groupHeight + 10f;

        return Math.Clamp(height, 72f, 590f);
    }

    private void DrawPageTab(
        string label,
        NavigationPage target,
        float width)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        Vector2 size = new(width, 30f);

        ImGui.InvisibleButton(
            $"##navigation-{(int)target}", size);

        bool hovered = ImGui.IsItemHovered();
        bool selected = currentPage == target;

        var draw = ImGui.GetWindowDrawList();

        if (hovered && !selected)
        {
            draw.AddRectFilled(
                pos, pos + size,
                ImGui.ColorConvertFloat4ToU32(
                    ThemeColor(6)),
                3f);
        }

        Vector4 color = selected ? NavAccent : NavMuted;
        float textWidth = ImGui.CalcTextSize(label).X;

        draw.AddText(
            pos + new Vector2((width - textWidth) * 0.5f, 6f),
            ImGui.ColorConvertFloat4ToU32(color),
            label);

        if (selected)
        {
            draw.AddRectFilled(
                pos + new Vector2(14f, 28f),
                pos + new Vector2(width - 14f, 30f),
                ImGui.ColorConvertFloat4ToU32(NavAccent),
                1f);
        }

        if (ImGui.IsItemClicked())
        {
            // Explicit clicks always establish user preference,
            // including clicking the sole visible or selected tab.
            // RememberNavigationPage only writes when changed.
            currentPage = target;
            RememberNavigationPage(target);

            if (target == NavigationPage.Aethernet && !selected)
                resetDestinationScroll = true;
        }
    }

    private void DrawAethernetPage()
    {
        if (IsCosmicTerritory(Client.TerritoryType))
        {
            DrawCosmicLocalAethernetPage();
            return;
        }

        if (TryFindResidentialShard(out _))
        {
            DrawResidentialAethernetPage();
            return;
        }

        if (nearby == null && overworldNearby != null)
        {
            DrawOverworldPage();
            return;
        }

        if (nearby == null ||
            !networks.TryGetValue(activeGroup, out var entries))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No Aethernet shard nearby.");
            ImGui.TextDisabled("Approach an Aethernet crystal.");
            return;
        }

        var physical = entries.Where(x => !x.Invisible).ToList();
        var main = entries.FirstOrDefault(x => x.IsMain);

        string name = main?.Name ?? "Local Aethernet";
        int unlocked = physical.Count(x => IsUnlocked(x.Id));

        string attuned = $"{unlocked}/{physical.Count} attuned";

        ImGui.TextUnformatted(name);
        ImGui.SameLine(
            MathF.Max(210f,
                ImGui.GetWindowWidth() -
                ImGui.CalcTextSize(attuned).X - 16f));
        ImGui.TextColored(NavReady, attuned);

        ImGui.Spacing();

        float viewport = GetDestinationContentHeight();

        if (ImGui.BeginChild(
            "##AethernetDestinations",
            new Vector2(0f, viewport),
            false))
        {
            if (resetDestinationScroll)
            {
                ImGui.SetScrollY(0f);
                resetDestinationScroll = false;
            }

            foreach (var area in physical.GroupBy(x => x.Territory))
            {
                var visible = area.Where(x =>
                    config.ShowUnlearned || DestinationAvailable(x))
                    .ToList();

                if (visible.Count == 0)
                    continue;

                ImGui.TextColored(
                    NavMuted,
                    GetAreaName(area.Key).ToUpperInvariant());
                ImGui.Spacing();

                foreach (var shard in visible)
                    DrawDestination(shard);

                ImGui.Spacing();
            }

            var extras = entries.Where(x =>
                x.Invisible &&
                (config.ShowUnlearned || DestinationAvailable(x)))
                .ToList();

            if (extras.Count > 0)
            {
                // This gap starts AFTER the physical shards.
                // The Other heading is initially below the
                // visible scroll viewport, not in the main list.
                ImGui.Dummy(new Vector2(0f, 38f));

                ImGui.TextColored(NavMuted, "OTHER");
                ImGui.Spacing();

                foreach (var shard in extras)
                    DrawDestination(shard);
            }
        }

        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.Separator();

        bool ready = phase == TravelPhase.Idle &&
            (status == "Ready" || status.StartsWith("Arrived:"));

        ImGui.TextColored(
            ready ? NavReady : NavMuted,
            "\u25CF");

        ImGui.SameLine(0f, 5f);
        ImGui.TextDisabled(status);

        if (entries.Any(x => x.Invisible))
        {
            string hint = "Scroll for more";
            float hintWidth = ImGui.CalcTextSize(hint).X;

            // Only show the hint when the footer has space.
            if (ImGui.GetWindowWidth() >
                ImGui.CalcTextSize(status).X + hintWidth + 65f)
            {
                ImGui.SameLine(
                    ImGui.GetWindowWidth() - hintWidth - 16f);
                ImGui.TextColored(NavMuted, hint);
            }
        }
    }

    private void DrawSettings()
    {
        if (!settingsWindowOpen)
            return;

        PushTheme();

        try
        {
        ImGui.SetNextWindowSize(
            new Vector2(380f, 320f),
            ImGuiCond.FirstUseEver);

        bool open = settingsWindowOpen;

        if (!ImGui.Begin(
            "Aethernet Assist - Settings###AethernetAssistSettings",
            ref open,
            ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            settingsWindowOpen = open;
            return;
        }

        ImGui.TextColored(NavAccent, "SETTINGS");
        ImGui.SameLine();
        ImGui.TextDisabled(UiVersion);
        ImGui.Separator();

        bool autoOpen = config.AutoOpen;

        if (ImGui.Checkbox(
            "Auto-open near Aethernet",
            ref autoOpen))
        {
            config.AutoOpen = autoOpen;
            Save();
        }

        bool autoWalk = config.AutoWalkHousingGates;

        if (ImGui.Checkbox(
            "Auto-walk through housing gates (vnavmesh)",
            ref autoWalk))
        {
            config.AutoWalkHousingGates = autoWalk;
            Save();
        }
        bool showUnlearned = config.ShowUnlearned;

        if (ImGui.Checkbox(
            "Show unlearned destinations",
            ref showUnlearned))
        {
            config.ShowUnlearned = showUnlearned;
            Save();
        }

        ImGui.Spacing();

        float range = config.OpenRange;

        if (ImGui.SliderFloat(
            "Proximity range",
            ref range,
            8f,
            11.1f,
            "%.1f yalms"))
        {
            config.OpenRange = Math.Clamp(
                MathF.Round(range, 1),
                8f,
                11.1f);
            Save();
        }

        ImGui.Spacing();

        bool diagnostics = config.ShowDebug;

        if (ImGui.Checkbox(
            "Enable diagnostics",
            ref diagnostics))
        {
            config.ShowDebug = diagnostics;
            Save();
        }


        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(NavAccent, "APPEARANCE");

        ImGui.TextDisabled(config.UseDalamudColors
            ? "Using matched Dalamud colors"
            : "Using default dark palette");

        if (ImGui.Button("Match Dalamud colors"))
        {
            // Saved from the active client theme before
            // either plugin window applies its own colors.
            if (sampledClientColors.Length == DefaultColors.Length)
            {
                config.MatchedStyleColors =
                    (uint[])sampledClientColors.Clone();

                config.UseDalamudColors = true;
                Save();
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Default dark"))
        {
            config.UseDalamudColors = false;
            Save();
        }

        ImGui.Spacing();
        if (config.ShowDebug &&
            ImGui.CollapsingHeader("Diagnostics"))
        {
            ImGui.TextColored(NavMuted, "NAVIGATION MEMORY");

            string savedTab = config.LastSelectedNavigationPage switch
            {
                0 => "Aethernet",
                1 => "Housing",
                2 => "DC Travel",
                _ => "Unset (Aethernet default)"
            };

            ImGui.TextUnformatted("Saved tab: " + savedTab);
            ImGui.TextUnformatted("Displayed tab: " + currentPage);
            ImGui.TextUnformatted(
                "Available tabs mask: " + AvailableNavigationTabsMask());

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.TextColored(NavMuted, "AETHERNET");

            ImGui.TextUnformatted(
                "Territory: " + Client.TerritoryType
            );

            ImGui.TextUnformatted(
                "Network group: " + activeGroup
            );

            ImGui.TextUnformatted(
                "Nearby object: " + (nearby?.BaseId ?? 0)
            );

            ImGui.TextUnformatted(
                "Travel phase: " + phase
            );

            ImGui.TextUnformatted("Overworld source: " +
                (overworldNearby?.Name.ToString() ?? "None"));
            ImGui.TextUnformatted("Overworld phase: " + overworldPhase);
            ImGui.TextWrapped(overworldStatus);
            ImGui.TextWrapped("Native menu snapshots: " +
                overworldMenuSnapshots.Count);

            ImGui.TextUnformatted(
                "Callbacks: " + callbackAttempts + "/2"
            );

            ImGui.TextWrapped(lastMenuReadout);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(NavMuted, "HOUSING");

            bool found = TryFindHousingAetheryte(
                out var district,
                out var crystal
            );

            string foundText = found
                ? district.Name + " (" + district.CityName + ")"
                : DescribeWardEntrance();

            ImGui.TextUnformatted(
                "Detected: " + foundText
            );

            ImGui.TextUnformatted(
                "Aetheryte ID: " +
                (crystal == null
                    ? "none"
                    : crystal.BaseId.ToString())
            );

            ImGui.TextUnformatted(
                "Travel phase: " + housingPhase
            );

            ImGui.TextUnformatted(
                "Entrance source: " +
                (housingFromWard
                    ? housingGateSource ? "Gate" : "NPC"
                    : "City")
            );

            ImGui.TextUnformatted(
                "Current ward: " + GetCurrentHousingWard()
            );

            ImGui.TextUnformatted(
                "Target ward: " + housingTargetWard
            );

            ImGui.TextUnformatted(
                "Proximity source: " + autoWindowSource
            );

            ImGui.TextUnformatted(
                "Proximity auto-opened: " +
                autoWindowOwnsVisibility
            );

            ImGui.TextUnformatted(
                "Gate movement: " + housingGateWalkDiagnostic
            );

            ImGui.TextWrapped(
                "Status: " + housingStatus
            );

            ImGui.TextWrapped(housingDiagnostic);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(
                NavMuted, "RESIDENTIAL AETHERNET");


            ImGui.TextWrapped(
                "Residential travel: " + residentialPhase +
                " | " + residentialStatus);

            ImGui.TextWrapped(residentialMenuReadout);

            ImGui.Spacing();

            if (ImGui.Button("Copy diagnostics"))
                ImGui.SetClipboardText(
                    BuildDiagnosticsReport()
                );
        }

        ImGui.End();
        settingsWindowOpen = open;
        }
        finally
        {
            ImGui.PopStyleColor(ThemePushCount);
        }
    }

    // Only show Housing where a supported housing
    // interaction is available, or while one is active.
    private bool HousingPageAvailable()
    {
        return housingPhase != HousingPhase.Idle ||
               TryFindHousingAetheryte(out _, out _) ||
               TryFindWardEntrance(out _, out _, out _);
    }

    // Bit 0: Aethernet, bit 1: Housing, bit 2: DC Travel.
    private int AvailableNavigationTabsMask()
    {
        int mask = 1;

        if (HousingPageAvailable())
            mask |= 2;

        if (WorldVisitPageAvailable() ||
            worldVisitPhase != WorldVisitPhase.Idle)
            mask |= 4;

        return mask;
    }

    private bool SelectedNavigationPageUnavailable()
    {
        int mask = AvailableNavigationTabsMask();

        return
            (currentPage == NavigationPage.Housing &&
             (mask & 2) == 0) ||
            (currentPage == NavigationPage.DcTravel &&
             (mask & 4) == 0);
    }

    private void Draw()
    {
        // Sample the real client palette BEFORE pushing our
        // own style. This prevents matching our own colors.
        sampledClientColors = settingsWindowOpen
            ? CaptureDalamudColors()
            : Array.Empty<uint>();

        // Settings, routes and navigation are separate.
        DrawSettings();
        DrawMapRouteWindow();

        if (!windowOpen)
            return;

        // Avoid the one-frame fallback to Aethernet when
        // tabs disappear before the next proximity scan.
        // The proximity controller will either close this
        // window or select another valid page.
        if (autoWindowOwnsVisibility &&
            (autoWindowConfirmedTabMask &
             ~AvailableNavigationTabsMask()) != 0)
            return;

        const float width = 386f;

        PushTheme();

        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(7f, 3f));

        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 7f);

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(width, 195f),
                ImGuiCond.FirstUseEver);

            bool open = windowOpen;

            if (!ImGui.Begin(
                "Aethernet Assist###AethernetAssist",
                ref open,
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoScrollbar))
            {
                ImGui.End();
                windowOpen = open;
                return;
            }

            ImGui.TextDisabled(UiVersion);

            ImGui.SameLine(ImGui.GetWindowWidth() - 87f);

            ImGui.PushStyleColor(
                ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(
                ImGuiCol.ButtonHovered,
                ThemeColor(6));
            ImGui.PushStyleColor(
                ImGuiCol.ButtonActive,
                ThemeColor(14));

            if (ImGui.SmallButton("Settings"))
                settingsWindowOpen = true;

            ImGui.PopStyleColor(3);
            ImGui.Spacing();

            // Same-DC World Visit is only accessible at
            // the three eligible main city Aetherytes.
            bool housingAvailable = HousingPageAvailable();
            bool dcTravelAvailable =
                WorldVisitPageAvailable() ||
                worldVisitPhase != WorldVisitPhase.Idle;

            // A previously selected tab must never leave
            // the UI displaying an unavailable feature.
            if ((!housingAvailable &&
                 currentPage == NavigationPage.Housing) ||
                (!dcTravelAvailable &&
                 currentPage == NavigationPage.DcTravel))
            {
                currentPage = NavigationPage.Aethernet;
                resetDestinationScroll = true;
            }

            const float tabGap = 7f;

            int visibleTabs = 1 +
                (housingAvailable ? 1 : 0) +
                (dcTravelAvailable ? 1 : 0);

            float available =
                ImGui.GetContentRegionAvail().X;

            float tabWidth = MathF.Max(
                85f,
                (available - (visibleTabs - 1) * tabGap) /
                visibleTabs);

            DrawPageTab(
                "Aethernet",
                NavigationPage.Aethernet,
                tabWidth);

            if (housingAvailable)
            {
                ImGui.SameLine(0f, tabGap);

                DrawPageTab(
                    "Housing",
                    NavigationPage.Housing,
                    tabWidth);
            }

            if (dcTravelAvailable)
            {
                ImGui.SameLine(0f, tabGap);

                DrawPageTab(
                    "DC Travel",
                    NavigationPage.DcTravel,
                    tabWidth);
            }

            ImGui.Spacing();

            switch (currentPage)
            {
                case NavigationPage.Aethernet:
                    DrawAethernetPage();
                    break;

                case NavigationPage.Housing:
                    DrawHousingPage();
                    break;

                case NavigationPage.DcTravel:
                    DrawWorldVisitSelectionPage();
                    break;
            }

            // Match the actual content immediately, on the
            // current frame. No delayed resize animation.
            float naturalHeight =
                ImGui.GetCursorPosY() +
                ImGui.GetStyle().WindowPadding.Y + 3f;

            ImGui.SetWindowSize(
                new Vector2(width, MathF.Max(132f, naturalHeight)),
                ImGuiCond.Always);

            ImGui.End();
            windowOpen = open;
        }
        finally
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(ThemePushCount);
        }
    }
}
