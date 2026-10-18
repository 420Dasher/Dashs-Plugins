using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AethernetAssist;

public sealed unsafe partial class Plugin
{
    private sealed record OverworldStop(
        uint Id,
        uint Territory,
        string Name
    );

    private readonly Dictionary<uint, List<OverworldStop>> overworldStops = new();
    private readonly List<string> overworldMenuSnapshots = new();

    private IGameObject? overworldNearby;
    private string overworldPreviousMenu = "";

    private enum OverworldPhase
    {
        Idle,
        WaitingForArrival
    }

    private OverworldPhase overworldPhase = OverworldPhase.Idle;

    private uint overworldArmedDestination;
    private uint overworldArmedSource;
    private uint overworldArmedCost;
    private DateTime overworldArmedUntil = DateTime.MinValue;

    private uint overworldOriginTerritory;
    private Vector3 overworldOriginPosition;

    private uint overworldTargetId;
    private string overworldTargetName = "";

    private DateTime overworldRequestedUtc;
    private bool overworldSawLoading;

    private string overworldStatus =
        "Select an overworld destination to review its cost.";

    private void LoadOverworldAetherytes()
    {
        foreach (var row in Data.GetExcelSheet<Aetheryte>())
        {
            if (!row.IsAetheryte || row.AethernetGroup != 0)
                continue;

            uint territory = row.Territory.Value.RowId;

            if (territory == 0)
                continue;

            string name = row.PlaceName.Value.Name.ToString().Trim();

            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!overworldStops.TryGetValue(territory, out var list))
            {
                list = new List<OverworldStop>();
                overworldStops[territory] = list;
            }

            if (list.All(x => x.Id != row.RowId))
                list.Add(new OverworldStop(row.RowId, territory, name));
        }

        foreach (var list in overworldStops.Values)
        {
            list.Sort((a, b) =>
                string.Compare(
                    a.Name,
                    b.Name,
                    StringComparison.OrdinalIgnoreCase
                ));
        }

        Log.Information(
            $"Overworld catalogue: {overworldStops.Count} territories; " +
            $"{overworldStops.Values.Sum(x => x.Count)} main crystals."
        );
    }

    private void ScanOverworldNearby()
    {
        overworldNearby = null;

        var player = Objects.LocalPlayer;

        if (player == null ||
            IsBusy() ||
            nearby != null ||
            cosmicLocalNearby != null ||
            !overworldStops.TryGetValue(
                Client.TerritoryType, out var stops) ||
            stops.Count < 2)
            return;

        float closest = config.OpenRange;

        foreach (var obj in Objects)
        {
            if (obj == null ||
                !obj.IsTargetable ||
                obj.ObjectKind != ObjectKind.Aetheryte)
                continue;

            bool matches = stops.Any(x =>
                x.Id == obj.BaseId ||
                string.Equals(
                    x.Name,
                    obj.Name.ToString(),
                    StringComparison.OrdinalIgnoreCase
                ));

            if (!matches)
                continue;

            float distance = Vector3.Distance(
                player.Position,
                obj.Position
            );

            if (distance >= closest)
                continue;

            closest = distance;
            overworldNearby = obj;
        }
    }

    // Read-only capture. No callbacks, interactions or teleports.

    private void CaptureOverworldNativeMenu()
    {
        if (overworldNearby == null)
        {
            overworldPreviousMenu = "";
            return;
        }

        var menus = new List<string>();

        var select = (AddonSelectString*)GetAddon("SelectString");

        if (select != null)
        {
            var popup = select->PopupMenu.PopupMenu;

            if (popup.EntryNames != null)
            {
                int count = Math.Clamp(popup.EntryCount, 0, 32);
                var labels = new List<string>();

                for (int i = 0; i < count; i++)
                {
                    var pointer = popup.EntryNames[i].Value;

                    if (pointer == null)
                        continue;

                    try
                    {
                        string label = Clean(
                            Marshal.PtrToStringUTF8(
                                (nint)pointer
                            ) ?? ""
                        );

                        if (label.Length > 100)
                            label = label.Substring(0, 100);

                        labels.Add($"{i}: {label}");
                    }
                    catch
                    {
                        labels.Add($"{i}: unreadable label");
                    }
                }

                menus.Add(
                    "SelectString entries=" + count +
                    " [" + string.Join(" | ", labels) + "]"
                );
            }
        }

        foreach (string addonName in new[]
        {
            "SelectIconString",
            "TelepotTown",
            "SelectYesno"
        })
        {
            var addon = GetAddon(addonName);

            if (addon == null)
                continue;

            if (addon->AtkValues == null ||
                addon->AtkValuesCount <= 0)
            {
                menus.Add(addonName + " visible (no AtkValues)");
                continue;
            }

            int count = Math.Min((int)addon->AtkValuesCount, 350);
            var labels = new List<string>();

            for (int i = 0; i < count && labels.Count < 26; i++)
            {
                var value = addon->AtkValues[i];

                if (value.Type != AtkValueType.String &&
                    value.Type != AtkValueType.ConstString &&
                    value.Type != AtkValueType.ManagedString)
                    continue;

                var ptr = value.String.Value;

                if (ptr == null)
                    continue;

                try
                {
                    string label = Clean(
                        MemoryHelper.ReadSeStringNullTerminated(
                            (nint)ptr
                        ).TextValue
                    );

                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    if (label.Length > 100)
                        label = label.Substring(0, 100);

                    labels.Add($"{i}: {label}");
                }
                catch
                {
                    labels.Add($"{i}: unreadable string");
                }
            }

            menus.Add(
                addonName + " values=" +
                addon->AtkValuesCount +
                " [" + string.Join(" | ", labels) + "]"
            );
        }

        if (menus.Count == 0)
        {
            overworldPreviousMenu = "";
            return;
        }

        string detail = string.Join("\n", menus);

        if (detail == overworldPreviousMenu)
            return;

        overworldPreviousMenu = detail;

        string entry =
            $"[{DateTime.Now:HH:mm:ss}] " +
            $"Territory {Client.TerritoryType}; " +
            $"crystal {overworldNearby.Name} " +
            $"(BaseId={overworldNearby.BaseId})\n" +
            detail;

        overworldMenuSnapshots.Add(entry);

        if (overworldMenuSnapshots.Count > 20)
            overworldMenuSnapshots.RemoveAt(0);
    }

    private string BuildOverworldNativeMenuReport()
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "=== OVERWORLD NATIVE MENU - DEV7 TEST4 ==="
        );

        builder.AppendLine(
            "Current territory: " + Client.TerritoryType
        );

        builder.AppendLine(
            "Nearby source: " +
            (overworldNearby?.Name.ToString() ?? "None")
        );

        builder.AppendLine(
            "Snapshots: " + overworldMenuSnapshots.Count
        );

        foreach (string snapshot in overworldMenuSnapshots)
        {
            builder.AppendLine("---");
            builder.AppendLine(snapshot);
        }

        if (overworldMenuSnapshots.Count == 0)
        {
            builder.AppendLine(
                "No native menu observed. Interact with an overworld crystal."
            );
        }

        return builder.ToString();
    }

    private bool IsCurrentOverworldStop(OverworldStop stop)
    {
        if (overworldNearby == null)
            return false;

        return overworldNearby.BaseId == stop.Id ||
            string.Equals(
                overworldNearby.Name.ToString(),
                stop.Name,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private void ClearOverworldSelection()
    {
        overworldArmedDestination = 0;
        overworldArmedSource = 0;
        overworldArmedCost = 0;
        overworldArmedUntil = DateTime.MinValue;
    }

    private void ArmOverworldTeleport(
        OverworldStop stop,
        uint quotedCost)
    {
        if (overworldNearby == null ||
            overworldPhase != OverworldPhase.Idle ||
            IsCurrentOverworldStop(stop))
            return;

        overworldArmedDestination = stop.Id;
        overworldArmedSource = overworldNearby.BaseId;
        overworldArmedCost = quotedCost;
        overworldArmedUntil = DateTime.UtcNow.AddSeconds(15);

        overworldStatus =
            $"Review teleport to {stop.Name}: {quotedCost:N0} gil.";
    }

    private void StartOverworldTeleport(OverworldStop stop)
    {
        var player = Objects.LocalPlayer;

        if (overworldPhase != OverworldPhase.Idle ||
            overworldNearby == null ||
            player == null ||
            IsBusy() ||
            !overworldNearby.IsTargetable ||
            overworldNearby.ObjectKind != ObjectKind.Aetheryte ||
            Vector3.Distance(
                player.Position,
                overworldNearby.Position
            ) > config.OpenRange + 0.5f ||
            stop.Territory != Client.TerritoryType ||
            IsCurrentOverworldStop(stop) ||
            phase != TravelPhase.Idle ||
            housingPhase != HousingPhase.Idle ||
            residentialPhase != ResidentialPhase.Idle ||
            cosmicLocalPhase != CosmicLocalPhase.Idle ||
            cosmicPhase != CosmicPhase.Idle ||
            worldVisitPhase != WorldVisitPhase.Idle)
        {
            ClearOverworldSelection();
            overworldStatus = "Travel unavailable in the current state.";
            return;
        }

        if (overworldArmedDestination != stop.Id ||
            overworldArmedSource != overworldNearby.BaseId ||
            DateTime.UtcNow > overworldArmedUntil)
        {
            ClearOverworldSelection();
            overworldStatus =
                "Confirmation expired or source changed. Select again.";
            return;
        }

        // Confirm against the live teleport list, not only the
        // dynamic Excel destination catalogue.
        var entry = Aetherytes.FirstOrDefault(x =>
            x.AetheryteId == stop.Id &&
            x.TerritoryId == stop.Territory &&
            x.SubIndex == 0 &&
            !x.IsSharedHouse &&
            !x.IsApartment
        );

        if (entry == null || !IsUnlocked(stop.Id))
        {
            ClearOverworldSelection();
            overworldStatus = "Destination is not attuned or available.";
            return;
        }

        uint currentCost = (uint)entry.GilCost;

        if (currentCost != overworldArmedCost)
        {
            uint oldCost = overworldArmedCost;
            ClearOverworldSelection();

            overworldStatus =
                $"Cost changed ({oldCost:N0} to {currentCost:N0} gil). " +
                "Select again to confirm the new price.";
            return;
        }

        var telepo = Telepo.Instance();

        if (telepo == null)
        {
            ClearOverworldSelection();
            overworldStatus = "Native teleport service unavailable.";
            return;
        }

        // Snapshot BEFORE submitting the native request.
        uint sourceTerritory = Client.TerritoryType;
        Vector3 sourcePosition = player.Position;

        // Exactly one request. No retries on failure or timeout.
        bool accepted = telepo->Teleport(
            entry.AetheryteId,
            entry.SubIndex
        );

        ClearOverworldSelection();

        if (!accepted)
        {
            overworldStatus =
                "The game rejected the teleport request. No retry.";
            Log.Warning("Overworld teleport request rejected: " + stop.Name);
            return;
        }

        RememberNavigationPage(NavigationPage.Aethernet);

        overworldOriginTerritory = sourceTerritory;
        overworldOriginPosition = sourcePosition;
        overworldTargetId = stop.Id;
        overworldTargetName = stop.Name;

        overworldRequestedUtc = DateTime.UtcNow;
        overworldSawLoading = false;
        overworldPhase = OverworldPhase.WaitingForArrival;

        overworldStatus = $"Teleport requested: {stop.Name}...";
        windowOpen = false;

        Log.Information(
            $"Overworld teleport requested: {stop.Name} " +
            $"(ID {stop.Id}, quoted cost {currentCost:N0} gil)."
        );
    }

    private void TickOverworldTeleport()
    {
        if (overworldPhase != OverworldPhase.WaitingForArrival)
            return;

        var player = Objects.LocalPlayer;

        if (IsBusy() || player == null)
        {
            overworldSawLoading = true;
            return;
        }

        if (Client.TerritoryType != overworldOriginTerritory)
        {
            StopOverworldTeleport(
                "Territory changed unexpectedly. No retry.",
                false
            );
            return;
        }

        // A distance change alone is not arrival evidence:
        // the player could simply have walked away.
        float travelled = Vector3.Distance(
            player.Position,
            overworldOriginPosition
        );

        if (travelled > 20f)
        {
            foreach (var obj in Objects)
            {
                if (obj == null ||
                    obj.ObjectKind != ObjectKind.Aetheryte ||
                    obj.BaseId != overworldTargetId)
                    continue;

                if (Vector3.Distance(
                    player.Position,
                    obj.Position
                ) > 35f)
                    continue;

                StopOverworldTeleport(
                    "Arrived at " + overworldTargetName,
                    true
                );
                return;
            }
        }

        if ((DateTime.UtcNow - overworldRequestedUtc)
            .TotalSeconds > 60)
        {
            StopOverworldTeleport(
                "Arrival not confirmed (loading observed: " +
                overworldSawLoading +
                "). No second teleport request was sent.",
                false
            );
        }
    }

    private void StopOverworldTeleport(
        string message,
        bool arrived)
    {
        overworldPhase = OverworldPhase.Idle;
        overworldStatus = message;
        ClearOverworldSelection();

        if (arrived)
        {
            overworldNearby = null;
            autoWindowWasInRange = false;
            Log.Information("AethernetAssist: " + message);
        }
        else
        {
            windowOpen = true;
            Log.Warning("AethernetAssist: " + message);
        }
    }

    private void DrawOverworldPage()
    {
        if (overworldNearby == null ||
            !overworldStops.TryGetValue(
                Client.TerritoryType, out var stops))
            return;

        ImGui.TextUnformatted(GetAreaName(Client.TerritoryType));
        ImGui.SameLine();
        ImGui.TextDisabled("OVERWORLD");

        ImGui.Spacing();

        ImGui.TextColored(
            NavMuted,
            "Paid teleport - current game quote"
        );

        ImGui.Spacing();

        var live = Aetherytes.Where(x =>
            x.TerritoryId == Client.TerritoryType &&
            x.SubIndex == 0 &&
            !x.IsSharedHouse &&
            !x.IsApartment
        ).GroupBy(x => x.AetheryteId)
         .ToDictionary(x => x.Key, x => x.First());

        var visible = stops.Where(x =>
            config.ShowUnlearned || live.ContainsKey(x.Id)
        ).ToList();

        if (overworldArmedDestination != 0 &&
            DateTime.UtcNow > overworldArmedUntil)
        {
            ClearOverworldSelection();
            overworldStatus = "Confirmation expired. Select again.";
        }

        float height = Math.Clamp(
            visible.Count * 32f + 8f,
            65f,
            440f
        );

        if (ImGui.BeginChild(
            "##OverworldDestinations",
            new Vector2(0f, height),
            false))
        {
            foreach (var stop in visible)
            {
                bool current = IsCurrentOverworldStop(stop);

                bool known = live.TryGetValue(
                    stop.Id,
                    out var entry
                ) && IsUnlocked(stop.Id);

                uint cost = known
                    ? (uint)entry!.GilCost
                    : 0;

                string suffix = current
                    ? "Current"
                    : known
                        ? $"{cost:N0} gil"
                        : "Not attuned";

                bool enabled =
                    known &&
                    !current &&
                    overworldPhase == OverworldPhase.Idle;

                string label =
                    $"{stop.Name}  -  {suffix}##ow-{stop.Id}";

                ImGui.BeginDisabled(!enabled);

                if (ImGui.Button(
                    label,
                    new Vector2(-1f, 26f)))
                {
                    ArmOverworldTeleport(stop, cost);
                }

                ImGui.EndDisabled();
            }
        }

        ImGui.EndChild();

        ImGui.Spacing();

        if (overworldArmedDestination != 0 &&
            overworldPhase == OverworldPhase.Idle)
        {
            var selected = stops.FirstOrDefault(x =>
                x.Id == overworldArmedDestination
            );

            if (selected != null)
            {
                ImGui.Separator();

                ImGui.TextWrapped(
                    $"Teleport to {selected.Name} for " +
                    $"{overworldArmedCost:N0} gil?"
                );

                if (ImGui.Button(
                    "Confirm paid teleport##ow-confirm"))
                {
                    StartOverworldTeleport(selected);
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel##ow-cancel"))
                {
                    ClearOverworldSelection();
                    overworldStatus = "Teleport selection cancelled.";
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextColored(
            overworldPhase == OverworldPhase.Idle
                ? NavReady
                : NavMuted,
            "\u25CF"
        );

        ImGui.SameLine(0f, 5f);
        ImGui.TextWrapped(overworldStatus);

        if (config.ShowDebug)
        {
            ImGui.Spacing();

            ImGui.TextDisabled(
                $"Captured menu states: {overworldMenuSnapshots.Count}"
            );

            if (ImGui.SmallButton("Copy overworld diagnostics"))
            {
                ImGui.SetClipboardText(
                    BuildOverworldNativeMenuReport()
                );
            }

            ImGui.SameLine();

            if (ImGui.SmallButton("Clear##overworld-capture"))
            {
                overworldMenuSnapshots.Clear();
                overworldPreviousMenu = "";
            }
        }
    }
}