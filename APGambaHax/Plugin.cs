using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace APGambaHax;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IDalamudPluginInterface Pi { get; set; } = null!;

    [PluginService]
    private static ICommandManager Commands { get; set; } = null!;

    private enum Page
    {
        Overview,
        Rewards,
        Barkeep,
        Missions,
        Originals,
        Blackjack,
        Challenges
    }

    private static readonly string[] Pages =
    {
        "Overview", "Rewards", "Barkeep", "Missions",
        "Originals", "Blackjack", "Challenges"
    };

    [PluginService]
    private static IFramework Framework { get; set; } = null!;

    private readonly Configuration cfg;
    private readonly AetherphoneBridge bridge = new();
    private readonly BarkeepAutomation automation = new();
    private readonly RewardsAutomation rewardAutomation = new();
    private readonly MissionsAutomation missionAutomation = new();
    private readonly UniversalRoundTracker roundTracker = new();

    private string currentDailyAccount = "";
    private DateTime currentDailyDate = DateTime.MinValue;
    private string lastSavedDailyRound = "";

    private bool mainOpen;
    private bool settingsOpen;
    private bool statsOpen;
    private Page page = Page.Overview;

    private uint[] sampledTheme = Array.Empty<uint>();

    private static string VersionText =>
        "v" + (typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "?");

    public Plugin()
    {
        cfg = Pi.GetPluginConfig() as Configuration ?? new Configuration();

        if (cfg.ModulesEnabled is not { Length: 6 })
        {
            cfg.ModulesEnabled = new[]
            {
                true, true, true, true, true, true
            };
            Save();
        }

        if (cfg.Version < 3)
        {
            cfg.WagerAutomationEnabled = false;
            cfg.AutoContinueWagers = false;
            cfg.WagerStopAfter = 1;
            cfg.WagerStopAtProfit = 0;
            cfg.WagerStopAtLoss = 0;
            cfg.WagerDelaySeconds = 3;
            cfg.Version = 3;
            Save();
        }

        if (cfg.AutoBonusKinds is not { Length: 7 })
        {
            cfg.AutoBonusKinds = new[]
            {
                true, true, true, true, true, true, true
            };
            Save();
        }

        Commands.AddHandler("/apgamba", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open APGambaHax. Subcommands: rewards, barkeep, missions, originals, blackjack, challenges, settings."
        });

        Framework.Update += OnFrameworkUpdate;
        Pi.UiBuilder.Draw += Draw;
        Pi.UiBuilder.OpenMainUi += Open;
        Pi.UiBuilder.OpenConfigUi += OpenSettings;
    }

    private void Save() => Pi.SavePluginConfig(cfg);

    private void RestoreDailyIfNeeded()
    {
        string account = bridge.AccountKey;
        DateTime day = DateTime.UtcNow.Date;

        if (account != currentDailyAccount || day != currentDailyDate)
        {
            currentDailyAccount = account;
            currentDailyDate = day;
            lastSavedDailyRound = "";

            cfg.BarkeepDailyRecords ??=
                new Dictionary<string, BarkeepDailyRecord>();

            long? restoredNet = null;
            bool restoredCap = false;

            long todayStart =
                new DateTimeOffset(
                    day,
                    TimeSpan.Zero).ToUnixTimeSeconds();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (account.Length > 0 &&
                cfg.BarkeepDailyRecords.TryGetValue(
                    account, out var record) &&
                record.ObservedAtUnix >= todayStart &&
                record.ObservedAtUnix <= now)
            {
                restoredNet = record.NetWinToday;
                restoredCap = record.Capped;
            }

            automation.RestoreDailySnapshot(restoredNet, restoredCap);
        }

        automation.SetHistoryObservation(
            bridge.BarkeepHistoryLoaded,
            bridge.BarkeepHistoryPositiveNet,
            bridge.BarkeepHistorySignedNet,
            bridge.BarkeepHistoryRounds,
            bridge.BarkeepHistoryHasMore);
    }

    private void SaveFreshDailySettlement()
    {
        string fresh = automation.FreshSettlementRoundId;

        if (currentDailyAccount.Length == 0 ||
            fresh.Length == 0 ||
            string.Equals(fresh, lastSavedDailyRound,
                StringComparison.Ordinal) ||
            automation.NetWinToday is not long net)
            return;

        lastSavedDailyRound = fresh;

        cfg.BarkeepDailyRecords[currentDailyAccount] =
            new BarkeepDailyRecord
            {
                NetWinToday = net,
                Capped = automation.Capped,
                ObservedAtUnix =
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

        Save();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (cfg.ModulesEnabled[0] || cfg.ModulesEnabled[1] ||
            cfg.ModulesEnabled[2] || cfg.ModulesEnabled[4] ||
            automation.ManagingWager || automation.BatchArmed)
            bridge.Tick(cfg.ModulesEnabled[2], true);
        else
            bridge.Clear();

        RestoreDailyIfNeeded();

        // Observe settled rounds without changing wager logic.
        roundTracker.Tick(bridge);

        // Reward claiming is independent of the Barkeep module.
        rewardAutomation.Tick(
            bridge,
            cfg.ModulesEnabled[0] && cfg.AutoClaimBonuses,
            cfg.AutoBonusKinds);

        missionAutomation.Tick(
            bridge,
            cfg.ModulesEnabled[2] && cfg.AutoClaimMissions);

        if (cfg.ModulesEnabled[1] ||
            automation.ManagingWager || automation.BatchArmed)
        {
            automation.Tick(
                bridge.BarkeepCabinet,
                cfg.ModulesEnabled[1] && cfg.PracticeAutomationEnabled,
                cfg.ModulesEnabled[1] && cfg.WagerAutomationEnabled,
                cfg.AutoContinueWagers,
                Math.Clamp(cfg.WagerStopAfter, 1, 9999),
                Math.Clamp(cfg.WagerStopAtProfit, 0, 1000000),
                Math.Clamp(cfg.WagerStopAtLoss, 0, 1000000),
                Math.Clamp(cfg.WagerDelaySeconds, 1, 60));
        }

        SaveFreshDailySettlement();

        if (automation.DisableWagersRequested)
        {
            cfg.WagerAutomationEnabled = false;
            cfg.AutoContinueWagers = false;
            automation.AcknowledgeDisable();
            Save();
        }
    }

    private static string TimeUntil(long unix)
    {
        if (unix <= 0)
            return "unknown";

        var remaining = DateTimeOffset.FromUnixTimeSeconds(unix)
            - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
            return "available now";

        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";

        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    private void Open() => mainOpen = true;
    private void OpenSettings() => settingsOpen = true;

    private void OnCommand(string command, string arguments)
    {
        string input = arguments.Trim().ToLowerInvariant();

        if (input == "settings")
        {
            settingsOpen = true;
            return;
        }

        if (Enum.TryParse<Page>(input, true, out var requested) &&
            Enum.IsDefined(requested))
        {
            int index = (int)requested;
            if (index == 0 || cfg.ModulesEnabled[index - 1])
                page = requested;
        }

        mainOpen = true;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Pi.UiBuilder.Draw -= Draw;
        Pi.UiBuilder.OpenMainUi -= Open;
        Pi.UiBuilder.OpenConfigUi -= OpenSettings;
        Commands.RemoveHandler("/apgamba");
    }

    private void Draw()
    {
        // Capture the real client colors before overriding them.
        if (settingsOpen)
            sampledTheme = Theme.Capture();

        DrawSettings();
        DrawMain();
        DrawStatistics();
    }

    private void DrawMain()
    {
        if (!mainOpen)
            return;

        Theme.Push(cfg);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7f, 4f));

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(480f, 340f), ImGuiCond.FirstUseEver);

            bool open = mainOpen;

            if (ImGui.Begin(
                "APGambaHax###APGambaHaxMain",
                ref open,
                ImGuiWindowFlags.NoCollapse))
            {
                ImGui.TextDisabled(VersionText);
                ImGui.SameLine(ImGui.GetWindowWidth() - 91f);

                if (ImGui.SmallButton("Settings"))
                    settingsOpen = true;

                ImGui.Spacing();

                float gap = 6f;
                float buttonWidth = MathF.Max(
                    90f, (ImGui.GetContentRegionAvail().X - 2f * gap) / 3f);

                for (int i = 0; i < Pages.Length; i++)
                {
                    if (i % 3 != 0)
                        ImGui.SameLine(0f, gap);

                    bool enabled = i == 0 || cfg.ModulesEnabled[i - 1];

                    if (!enabled)
                        ImGui.BeginDisabled();

                    bool selected = (int)page == i;

                    if (selected)
                        ImGui.PushStyleColor(
                            ImGuiCol.Button, Theme.Get(cfg, 14));

                    if (ImGui.Button(Pages[i] + "##page" + i,
                        new Vector2(buttonWidth, 25f)))
                    {
                        page = (Page)i;
                    }

                    if (selected)
                        ImGui.PopStyleColor();

                    if (!enabled)
                        ImGui.EndDisabled();
                }

                ImGui.Spacing();
                ImGui.Separator();

                switch (page)
                {
                    case Page.Overview: DrawOverview(); break;
                    case Page.Rewards: DrawRewards(); break;
                    case Page.Barkeep: DrawBarkeep(); break;
                    case Page.Missions: DrawMissions(); break;
                    case Page.Originals: DrawPlaceholder("ORIGINALS"); break;
                    case Page.Blackjack: DrawBlackjack(); break;
                    case Page.Challenges: DrawPlaceholder("CHALLENGES"); break;
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextDisabled("Aetherphone: " + bridge.Status + " | Barkeep bridge");
            }

            ImGui.End();
            mainOpen = open;
        }
        finally
        {
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(Theme.PushCount);
        }
    }

    private void Heading(string text) =>
        ImGui.TextColored(Theme.Get(cfg, 4), text);

    private void DrawOverview()
    {
        Heading("OVERVIEW");

        ImGui.TextUnformatted("Aetherphone: " + bridge.Status);

        if (bridge.HasCasinoState)
        {
            if (bridge.Chips.HasValue)
                ImGui.TextUnformatted($"Bankroll: {bridge.Chips.Value:N0} chips");
            else
                ImGui.TextDisabled("Bankroll: no active sitting");

            if (bridge.Level.HasValue)
                ImGui.TextUnformatted("Casino level: " + bridge.Level.Value);

            if (bridge.StakesPaused == true)
                ImGui.TextDisabled("Wagers are currently paused by Aetherphone.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Currency: 1 coin = 1,000 chips");

        ImGui.Spacing();
        Heading("PROFIT LIMITS");

        ImGui.TextUnformatted(
            "Barkeep daily net cap: 20,000 chips");

        if (bridge.MaxWinPerBet is long perBet)
            ImGui.TextWrapped(
                $"House-game net-win ceiling per bet: {perBet:N0} chips");

        ImGui.TextDisabled(
            "Per-bet clipping is not a daily profit cap.");

        ImGui.TextDisabled(
            "Other game-specific daily caps: not confirmed.");

        ImGui.Spacing();
        ImGui.TextUnformatted(
            $"Enabled modules: {cfg.ModulesEnabled.Count(x => x)} / 6");

        ImGui.Spacing();
        ImGui.Separator();

        Heading("STATISTICS");

        ImGui.TextDisabled(roundTracker.Status);

        if (ImGui.Button(
            "View detailed statistics##OverviewStats",
            new Vector2(-1f, 0f)))
        {
            statsOpen = true;
        }

        ImGui.TextDisabled(
            "Expandable session statistics for supported casino games.");

        ImGui.Spacing();

        ImGui.TextDisabled(
            "Wager automation is disabled by default.");
    }

    private static string BonusLabel(string kind) =>
        kind.Trim().ToLowerInvariant() switch
        {
            "welcome" => "Welcome bonus",
            "timed" => "Timed bonus",
            "reload" => "Reload bonus",
            "streak" => "Daily streak",
            "levelup" => "Level-up bonus",
            "broke" => "Broke bonus",
            "rebate" => "Rebate",
            _ => string.IsNullOrWhiteSpace(kind) ? "Bonus" : kind
        };

    private void DrawRewards()
    {
        Heading("REWARDS");

        ImGui.TextDisabled(
            "Bound runtime: " + bridge.RuntimeBinding);

        ImGui.TextDisabled(
            "Account: " +
            (bridge.AccountKey.Length > 0
                ? "Authenticated"
                : "Not authenticated"));

        if (bridge.HasCasinoState)
        {
            if (bridge.DailySpinClaimed.HasValue)
            {
                ImGui.TextUnformatted(
                    "Daily spin: " +
                    (bridge.DailySpinClaimed.Value
                        ? "Claimed" : "Available"));

                if (bridge.DailySpinNextAtUnix is long nextSpin &&
                    nextSpin > 0)
                    ImGui.TextDisabled(
                        "Next spin: " + TimeUntil(nextSpin));
            }
            else
            {
                ImGui.TextDisabled("Daily spin state not loaded.");
            }

            ImGui.TextDisabled(
                "Daily spin claiming will be developed separately.");

            ImGui.Spacing();
            ImGui.Separator();

            Heading("AUTO CLAIM");

            bool autoClaim = cfg.AutoClaimBonuses;

            if (ImGui.Checkbox(
                "Auto claim eligible bonuses", ref autoClaim))
            {
                cfg.AutoClaimBonuses = autoClaim;
                Save();
            }

            ImGui.TextDisabled(rewardAutomation.Status);

            if (ImGui.CollapsingHeader(
                "Choose bonus types##RewardTypes"))
            {
                for (int i = 0;
                     i < RewardsAutomation.Kinds.Length;
                     i++)
                {
                    bool selected = cfg.AutoBonusKinds[i];

                    if (ImGui.Checkbox(
                        BonusLabel(RewardsAutomation.Kinds[i]) +
                        "##autoReward" + i,
                        ref selected))
                    {
                        cfg.AutoBonusKinds[i] = selected;
                        Save();
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();

            Heading("AVAILABLE BONUSES");

            if (bridge.Bonuses.Count == 0)
            {
                ImGui.TextDisabled(
                    "No live bonus data in Aetherphone's cache.");
            }

            foreach (var bonus in bridge.Bonuses)
            {
                bool claimable = bonus.Ready && bonus.Eligible;

                string status = claimable ? "READY" :
                    bonus.Ready ? "Not eligible" :
                    bonus.NextAtUnix > 0
                        ? TimeUntil(bonus.NextAtUnix)
                        : "Not ready";

                ImGui.TextUnformatted(
                    $"{BonusLabel(bonus.Kind)} - " +
                    $"{bonus.Amount:N0} chips");

                if (claimable)
                {
                    ImGui.SameLine();

                    if (ImGui.SmallButton(
                        "Claim##reward-" + bonus.Kind))
                    {
                        rewardAutomation.RequestManual(
                            bridge, bonus.Kind);
                    }
                }
                else
                {
                    ImGui.TextDisabled(status);
                }

                ImGui.Spacing();
            }

            ImGui.Separator();
            ImGui.TextDisabled(
                "Last: " + rewardAutomation.LastRequest);

            ImGui.TextDisabled(
                "Aetherphone claim in flight: " +
                (bridge.ClaimingBonus.Length > 0
                    ? bridge.ClaimingBonus
                    : "None"));

            ImGui.TextDisabled(
                "Live bonus types: " + bridge.Bonuses.Count);
            ImGui.TextDisabled(
                $"Claim requests this session: " +
                $"{rewardAutomation.Requests}");

            ImGui.TextDisabled(
                "Requests are not confirmed payouts.");
            ImGui.TextDisabled(
                "Aetherphone processes the server results.");

            return;
        }

        Heading("CONNECTION");

        ImGui.TextDisabled(
            "Open Gamba in Aetherphone to load live rewards.");

        ImGui.TextDisabled(
            "Auto claiming waits for authenticated casino data.");
    }

    private void DrawBarkeep()
    {
        Heading("BARKEEP");

        // No BeginChild: the main Dalamud window is the only scroll region.
        ImGui.TextUnformatted("Aetherphone: " + bridge.BarkeepStatus);
        string automationLabel = automation.Status;

        if (!automation.HasError &&
            !automation.BatchArmed &&
            !automation.ManagingWager &&
            automation.LastStopReason.Length == 0)
        {
            bool waiting =
                automationLabel == "Idle" ||
                automationLabel == "Automation disabled" ||
                automationLabel == "Practice automation disabled" ||
                automationLabel == "Wager settled";

            if (waiting)
            {
                if (cfg.WagerAutomationEnabled)
                    automationLabel = "Ready - waiting for wager or batch";
                else if (cfg.PracticeAutomationEnabled)
                    automationLabel = "Ready - waiting for practice";
            }
        }

        if (cfg.WagerAutomationEnabled &&
            !automation.ManagingWager &&
            !automation.BatchArmed &&
            !automation.HasError &&
            automation.LastStopReason.Length == 0)
        {
            if (automation.DailyCapConfirmed)
                automationLabel = "Daily Barkeep cap confirmed";
            else if (automation.HistoryCapSuspected)
                automationLabel = "Possible daily Barkeep cap - batch locked";
            else if (!automation.DailyNetConfirmed)
                automationLabel = "Daily net unconfirmed - batch locked";
        }

        ImGui.TextDisabled("Automation: " + automationLabel);

        if (automation.LastStopReason.Length > 0)
            ImGui.TextDisabled("Last stop: " + automation.LastStopReason);

        if (bridge.BarkeepCompleted.HasValue &&
            bridge.BarkeepPatrons.HasValue)
        {
            int complete = bridge.BarkeepCompleted.Value;
            int count = bridge.BarkeepPatrons.Value;

            ImGui.TextUnformatted($"Patrons: {complete}/{count}");

            if (bridge.BarkeepScore.HasValue)
            {
                int score = bridge.BarkeepScore.Value;
                int maximum = count * 100;

                if (complete == count)
                {
                    double accuracy = maximum > 0
                        ? score * 100d / maximum
                        : 0d;

                    ImGui.TextUnformatted(
                        $"Final score: {score:N0}/{maximum:N0} ({accuracy:0.#}%)");
                }
                else
                {
                    ImGui.TextUnformatted(
                        $"Progress score: {score:N0}/{maximum:N0}");

                    if (complete > 0)
                    {
                        double quality = score * 100d / (complete * 100d);

                        ImGui.TextDisabled(
                            $"Average served quality: {quality:0.#}%");
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        Heading("AUTOMATION");

        bool practice = cfg.PracticeAutomationEnabled;

        if (ImGui.Checkbox("Automate practice shifts", ref practice))
        {
            cfg.PracticeAutomationEnabled = practice;
            Save();
        }

        bool wagers = cfg.WagerAutomationEnabled;

        if (ImGui.Checkbox("Automate wager shifts", ref wagers))
        {
            cfg.WagerAutomationEnabled = wagers;

            // Only stop a real running batch.
            if (!wagers && automation.BatchArmed)
                automation.StopBatch();

            Save();
        }

        ImGui.TextDisabled("Always aims for perfect grades.");

        ImGui.Spacing();
        ImGui.Separator();

        Heading("WAGER SAFETY");

        if (automation.NetWinToday.HasValue)
        {
            ImGui.TextDisabled(
                $"Server daily net: {automation.NetWinToday.Value:N0} / " +
                $"{CasinoProfitLimits.BarkeepDailyNetCap:N0} chips");
        }
        else
        {
            ImGui.TextDisabled(
                "Server daily net: unconfirmed.");
        }

        if (automation.DailyCapConfirmed)
            ImGui.TextDisabled(
                "Daily Barkeep cap confirmed by server.");
        else if (automation.HistoryCapSuspected)
            ImGui.TextDisabled(
                "Possible daily cap in history - batch locked.");

        ImGui.TextWrapped(
            "Last wager: " + automation.LastResult);

        ImGui.Spacing();

        if (automation.HasBatch)
        {
            ImGui.Spacing();

            Heading("LAST / CURRENT BATCH");

            ImGui.TextUnformatted(
                $"Shifts: {automation.BatchWagers}/{automation.BatchEffectiveLimit}" +
                $" | Perfect: {automation.BatchPerfect}");

            ImGui.TextUnformatted(
                $"Net: {automation.BatchNet:+#,0;-#,0;0} chips");

            if (automation.BatchArmed &&
                !automation.ManagingWager &&
                automation.NextShiftInSeconds >= 0)
            {
                ImGui.TextDisabled(
                    $"Next shift: {automation.NextShiftInSeconds}s");
            }
        }

        if (wagers)
        {
            ImGui.Spacing();
            ImGui.Separator();

            Heading("WAGER CONTROLS");

            if (!automation.BatchArmed)
            {
                if (automation.DailyCapReached)
                {
                    ImGui.TextDisabled(
                        automation.DailyCapConfirmed
                            ? "Start locked: server-confirmed daily cap."
                            : "Start locked: possible daily cap in history.");
                }
                else if (!automation.CanStartBatch)
                {
                    ImGui.TextDisabled(
                        "Start locked: daily net not confirmed.");
                    ImGui.TextDisabled(
                        "A fresh server settlement is required.");
                }
                else if (ImGui.Button("Start batch"))
                {
                    automation.StartBatch(cfg.WagerStopAfter);
                }
            }
            else
            {
                if (ImGui.Button(automation.Paused
                    ? "Resume batch"
                    : "Pause batch"))
                {
                    if (automation.Paused)
                        automation.Resume();
                    else
                        automation.Pause();
                }

                ImGui.SameLine();

                if (ImGui.Button("Stop batch"))
                    automation.StopBatch();
            }

            ImGui.TextDisabled(
                $"Limit: {automation.EffectiveBatchLimit(cfg.WagerStopAfter)} " +
                $"of {cfg.WagerStopAfter} configured shift(s) | " +
                (cfg.AutoContinueWagers
                    ? "Auto continuation on"
                    : "Auto continuation off"));

            ImGui.Spacing();

            if (ImGui.CollapsingHeader("Batch settings##BarkeepBatchSettings"))
            {
                bool repeat = cfg.AutoContinueWagers;

                if (ImGui.Checkbox("Continue automatically", ref repeat))
                {
                    cfg.AutoContinueWagers = repeat;
                    Save();
                }

                ImGui.Spacing();

                if (ImGui.BeginTable(
                    "##BarkeepBatchGrid",
                    2,
                    ImGuiTableFlags.SizingStretchSame))
                {
                    try
                    {
                        ImGui.TableNextColumn();

                        ImGui.TextDisabled("Shift limit");

                        // Editing preferences never requires permission
                        // to start a wager. The batch engine enforces
                        // actual daily and session limits separately.
                        int count = Math.Clamp(cfg.WagerStopAfter, 1, 9999);

                        ImGui.SetNextItemWidth(-1f);

                        if (ImGui.InputInt(
                            "##BarkeepShiftLimit", ref count, 1, 10))
                        {
                            cfg.WagerStopAfter = Math.Clamp(
                                count, 1, 9999);
                            Save();
                        }

                        ImGui.TableNextColumn();

                        ImGui.TextDisabled("Delay (seconds)");

                        int delay = cfg.WagerDelaySeconds;

                        ImGui.SetNextItemWidth(-1f);

                        if (ImGui.InputInt(
                            "##BarkeepDelay", ref delay, 1, 10))
                        {
                            cfg.WagerDelaySeconds = Math.Clamp(
                                delay, 1, 60);
                            Save();
                        }

                        ImGui.TableNextColumn();

                        ImGui.TextDisabled("Profit stop (chips)");

                        int profit = cfg.WagerStopAtProfit;

                        ImGui.SetNextItemWidth(-1f);

                        if (ImGui.InputInt(
                            "##BarkeepProfitStop", ref profit, 100, 1000))
                        {
                            cfg.WagerStopAtProfit = Math.Clamp(
                                profit, 0, 1000000);
                            Save();
                        }

                        ImGui.TableNextColumn();

                        ImGui.TextDisabled("Loss stop (chips)");

                        int loss = cfg.WagerStopAtLoss;

                        ImGui.SetNextItemWidth(-1f);

                        if (ImGui.InputInt(
                            "##BarkeepLossStop", ref loss, 100, 1000))
                        {
                            cfg.WagerStopAtLoss = Math.Clamp(
                                loss, 0, 1000000);
                            Save();
                        }
                    }
                    finally {
                        ImGui.EndTable();
                    }
                }

                ImGui.TextDisabled(
                    "Profit/loss stop: 0 = disabled.");
            }
        }

        if (automation.HasError)
        {
            ImGui.Spacing();
            ImGui.Separator();

            if (ImGui.SmallButton("Reset bridge error"))
                automation.ResetFault();
        }
    }

    private static string MissionGuidance(LiveMission mission)
    {
        string metric = mission.Metric.Trim().ToLowerInvariant();
        string game = mission.GameKind.Trim().ToLowerInvariant();

        bool unrestricted = game.Length == 0;
        bool barkeepOnly =
            game == "bartender" || game == "casino.bartender";

        return metric switch
        {
            "wins" when unrestricted =>
                "Barkeep: confirmed for unrestricted profitable wins.",

            "wins" when barkeepOnly =>
                "Barkeep: compatible; restricted variant unverified.",

            "rounds" when unrestricted =>
                "Barkeep: likely compatible; server confirmation pending.",

            "rounds" when barkeepOnly =>
                "Barkeep: likely compatible; server confirmation pending.",

            "games" =>
                "Barkeep may count as one distinct game; verify on server.",

            "hits" =>
                "Multiplier objective: normal perfect Barkeep is 2x.",

            "maxbet" =>
                "Manual only; maximum-bet automation is not planned.",

            "hosted" =>
                "Hosted-table participation required; manual for now.",

            _ =>
                "Tracking only; no verified automatic completion route."
        };
    }

    private void DrawMissions()
    {
        Heading("MISSIONS");

        if (!bridge.HasCasinoState ||
            bridge.AccountKey.Length == 0)
        {
            ImGui.TextDisabled(
                "Connect and sign in to Aetherphone Gamba.");
            return;
        }

        Heading("AUTO CLAIM");

        bool autoClaim = cfg.AutoClaimMissions;

        if (ImGui.Checkbox(
            "Auto claim completed missions", ref autoClaim))
        {
            cfg.AutoClaimMissions = autoClaim;
            Save();
        }

        ImGui.TextDisabled(missionAutomation.Status);

        ImGui.Spacing();
        ImGui.Separator();

        if (!bridge.MissionsLoaded)
        {
            ImGui.TextDisabled(
                "Waiting for Aetherphone mission data...");
            return;
        }

        int completed = 0;
        int claimed = 0;

        foreach (var mission in bridge.Missions)
        {
            if (mission.Complete)
                completed++;

            if (mission.Claimed)
                claimed++;
        }

        ImGui.TextUnformatted(
            $"Completed: {completed}/{bridge.Missions.Count}" +
            $" | Claimed: {claimed}");

        ImGui.TextDisabled(
            "Mission progress is confirmed by Aetherphone's server.");

        if (bridge.MissionsResetsAtUnix > 0)
        {
            ImGui.TextDisabled(
                "Mission reset: " +
                TimeUntil(bridge.MissionsResetsAtUnix));
        }

        if (bridge.Missions.Count == 0)
        {
            ImGui.TextDisabled(
                "No missions available in Aetherphone's cache.");
        }

        ImGui.Spacing();

        foreach (var mission in bridge.Missions)
        {
            ImGui.PushID(mission.Id);

            try
            {
                ImGui.Separator();

                ImGui.TextWrapped(mission.Description);

                float progress = mission.Target <= 0
                    ? (mission.Complete ? 1f : 0f)
                    : Math.Clamp(
                        (float)mission.Progress / mission.Target,
                        0f, 1f);

                long shownProgress = Math.Max(0L, mission.Progress);
                long shownTarget = Math.Max(0L, mission.Target);

                ImGui.ProgressBar(
                    progress,
                    new Vector2(-1f, 0f),
                    $"{shownProgress:N0} / {shownTarget:N0}");

                ImGui.TextDisabled(
                    $"Reward: {mission.Reward:N0} chips");

                ImGui.TextWrapped(
                    "Assist: " + MissionGuidance(mission));

                if (mission.Claimed)
                {
                    ImGui.TextDisabled("Claimed");
                }
                else if (mission.Complete)
                {
                    bool busy = bridge.ClaimingMission.Length > 0;

                    if (busy)
                        ImGui.BeginDisabled();

                    if (ImGui.SmallButton("Claim reward"))
                    {
                        missionAutomation.RequestManual(
                            bridge, mission.Id);
                    }

                    if (busy)
                        ImGui.EndDisabled();
                }
                else
                {
                    ImGui.TextDisabled("In progress");
                }

                ImGui.Spacing();
            }
            finally
            {
                ImGui.PopID();
            }
        }

        ImGui.Separator();

        ImGui.TextDisabled(
            "Last: " + missionAutomation.LastRequest);

        ImGui.TextDisabled(
            $"Mission claim requests: {missionAutomation.Requests}");

        ImGui.TextDisabled(
            "Claims are verified by Aetherphone and the server.");

        ImGui.TextDisabled(
            "Claim requests are not confirmed payouts.");
    }

    private void DrawRoundStats(string gameKind)
    {
        var stats = roundTracker.Totals(gameKind);

        ImGui.TextUnformatted(
            $"Rounds: {stats.Rounds:N0}  |  " +
            $"Wins: {stats.Wins:N0}  |  " +
            $"Losses: {stats.Losses:N0}  |  " +
            $"Pushes: {stats.Pushes:N0}");

        if (stats.Rounds > 0)
        {
            float winRate = stats.Wins * 100f / stats.Rounds;
            ImGui.TextUnformatted($"Win rate: {winRate:F1}%");
        }

        ImGui.TextUnformatted(
            $"Net result: {stats.Net:+#,0;-#,0;0} chips");

        if (stats.AmountsKnown > 0)
        {
            ImGui.TextUnformatted(
                $"Known stakes: {stats.Staked:N0} chips");

            ImGui.TextUnformatted(
                $"Known payouts: {stats.Returned:N0} chips");
        }

        if (stats.AmountsKnown < stats.Rounds)
        {
            ImGui.TextDisabled(
                "Some rounds have net-only history records; " +
                "stake/payout totals are partial.");
        }
    }

    private void DrawBlackjack()
    {
        Heading("BLACKJACK");

        ImGui.TextDisabled(
            "Live hand tracking: " + roundTracker.Status);

        ImGui.TextDisabled(
            "Detailed session statistics: Overview > Statistics.");

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextDisabled(
            "Tracks live settled Blackjack hands and hosted " +
            "chip-table history.");

        ImGui.TextDisabled(
            "Practice and gil tables are excluded.");

        ImGui.TextDisabled(
            "Statistics begin when this plugin session starts.");

        ImGui.TextDisabled(
            "No betting or Blackjack strategy automation yet.");
    }

    private void DrawPlaceholder(string name)
    {
        Heading(name);
        ImGui.TextWrapped(
            "Module foundation ready. Additional game automation is not implemented in MISSIONS DEV1.");

        if (name == "ORIGINALS")
            ImGui.TextDisabled(
                "Aetherphone already includes Auto Bet for supported games.");
        else if (name == "BLACKJACK")
            ImGui.TextDisabled(
                "Strategy and hand guidance are planned.");
        else
            ImGui.TextDisabled(
                "Live state and progress tracking are planned.");
    }

    private void DrawGameStatistics(string label, string kind)
    {
        var data = roundTracker.Totals(kind);

        if (ImGui.CollapsingHeader(
            $"{label} ({data.Rounds:N0})##Stats-{kind}"))
        {
            DrawRoundStats(kind);

            if (data.Rounds == 0)
                ImGui.TextDisabled(
                    "No settled rounds observed this session.");

            ImGui.Spacing();
        }
    }

    private void DrawStatistics()
    {
        if (!statsOpen)
            return;

        Theme.Push(cfg);

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(490f, 530f), ImGuiCond.FirstUseEver);

            bool open = statsOpen;

            if (ImGui.Begin(
                "APGambaHax - Statistics###APGambaHaxStatistics",
                ref open, ImGuiWindowFlags.NoCollapse))
            {
                Heading("UNIVERSAL ROUNDTRACKER");

                ImGui.TextDisabled(roundTracker.Status);

                ImGui.TextDisabled(
                    "Read-only observations from Aetherphone.");

                ImGui.Spacing();

                if (ImGui.CollapsingHeader("All games##StatsAll"))
                {
                    DrawRoundStats("");
                    ImGui.Spacing();
                }

                DrawGameStatistics(
                    "Blackjack", "casino.blackjack");

                var barkeepStats =
                    roundTracker.Totals("casino.bartender");

                if (ImGui.CollapsingHeader(
                    $"Barkeep ({barkeepStats.Rounds:N0})##StatsBarkeep"))
                {
                    Heading("SETTLED CHIP WAGERS");

                    DrawRoundStats("casino.bartender");

                    ImGui.Spacing();
                    Heading("AUTOMATION SESSION");

                    ImGui.TextUnformatted(
                        $"Wagers: {automation.Wagers}  |  " +
                        $"Perfect: {automation.PerfectWagers}  |  " +
                        $"Practice: {automation.PracticeRuns}");

                    ImGui.TextUnformatted(
                        $"Automation net: " +
                        $"{automation.SessionNet:+#,0;-#,0;0} chips");

                    ImGui.Spacing();
                    Heading("DAILY / HISTORY OBSERVATIONS");

                    if (automation.NetWinToday.HasValue)
                    {
                        ImGui.TextUnformatted(
                            $"Server daily net ({automation.DailyObservationSource}): " +
                            $"{automation.NetWinToday.Value:N0} / " +
                            $"{CasinoProfitLimits.BarkeepDailyNetCap:N0}");

                        ImGui.TextDisabled(
                            $"Perfect shifts remaining: " +
                            $"{automation.DailyShiftsRemaining}");
                    }
                    else
                    {
                        ImGui.TextDisabled(
                            "Server daily net unconfirmed.");
                    }

                    if (automation.HistoryLoaded)
                    {
                        ImGui.TextUnformatted(
                            $"History positive net (UTC day, capped): " +
                            $"{automation.HistoryPositiveNet:N0} chips");

                        ImGui.TextUnformatted(
                            $"History signed net: " +
                            $"{automation.HistorySignedNet:+#,0;-#,0;0} chips");

                        ImGui.TextDisabled(
                            $"History rounds: {automation.HistoryRounds}" +
                            (automation.HistoryHasMore
                                ? " | More history pages exist"
                                : ""));
                    }
                    else
                    {
                        ImGui.TextDisabled(
                            "Waiting for Aetherphone round history.");
                    }

                    ImGui.Spacing();
                }

                if (ImGui.CollapsingHeader(
                    "Originals##StatsOriginals"))
                {
                    DrawGameStatistics("Mines", "casino.mines");
                    DrawGameStatistics("Dice", "casino.dice");
                    DrawGameStatistics("Limbo", "casino.limbo");
                    DrawGameStatistics("Keno", "casino.keno");
                    DrawGameStatistics("Hi-Lo", "casino.hilo");
                }

                if (ImGui.CollapsingHeader(
                    "Slots##StatsSlots"))
                {
                    DrawGameStatistics("Slots", "casino.slots");
                    DrawGameStatistics(
                        "Bird Slots", "casino.slots.bird");
                    DrawGameStatistics(
                        "Cascade Slots", "casino.slots.cascade");
                    DrawGameStatistics(
                        "Moogle Slots", "casino.slots.moogle");
                    DrawGameStatistics(
                        "Gamble", "casino.slots.gamble");
                }

                if (ImGui.CollapsingHeader(
                    "Other casino games##StatsOther"))
                {
                    DrawGameStatistics("Scratch", "casino.scratch");
                    DrawGameStatistics("Plinko", "casino.plinko");
                    DrawGameStatistics("Wheel", "casino.wheel");
                    DrawGameStatistics("Bingo", "casino.bingo");
                    DrawGameStatistics("Racing", "casino.race");
                    DrawGameStatistics("Hold'em", "casino.holdem");
                    DrawGameStatistics(
                        "Dealer Hold'em", "casino.dealerholdem");
                    DrawGameStatistics(
                        "Dice Table", "casino.dicetable");
                    DrawGameStatistics(
                        "Deathroll", "casino.deathroll");
                    DrawGameStatistics("Raffle", "casino.raffle");
                    DrawGameStatistics("Book", "casino.book");
                }

                ImGui.Spacing();
                ImGui.Separator();

                ImGui.TextDisabled(
                    "Statistics begin when APGambaHax loads.");

                ImGui.TextWrapped(
                    "Games without an accessible unique settlement ID " +
                    "may not appear until Aetherphone history provides one.");

                ImGui.TextDisabled(
                    "Practice wagers and rewards are excluded.");
            }

            ImGui.End();
            statsOpen = open;
        }
        finally
        {
            ImGui.PopStyleColor(Theme.PushCount);
        }
    }

    private void DrawSettings()
    {
        if (!settingsOpen)
            return;

        Theme.Push(cfg);

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(390f, 370f), ImGuiCond.FirstUseEver);

            bool open = settingsOpen;

            if (ImGui.Begin(
                "APGambaHax - Settings###APGambaHaxSettings",
                ref open, ImGuiWindowFlags.NoCollapse))
            {
                Heading("MODULES");
                ImGui.TextDisabled("Disabled modules have no active features.");

                for (int i = 0; i < cfg.ModulesEnabled.Length; i++)
                {
                    bool enabled = cfg.ModulesEnabled[i];

                    if (ImGui.Checkbox("Enable " + Pages[i + 1],
                        ref enabled))
                    {
                        cfg.ModulesEnabled[i] = enabled;

                        if (!enabled && (int)page == i + 1)
                            page = Page.Overview;

                        Save();
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                Heading("APPEARANCE");

                ImGui.TextDisabled(cfg.UseDalamudColors
                    ? "Using matched Dalamud colors"
                    : "Using default dark palette");

                if (ImGui.Button("Match Dalamud colors") &&
                    sampledTheme.Length == 17)
                {
                    cfg.MatchedStyleColors = (uint[])sampledTheme.Clone();
                    cfg.UseDalamudColors = true;
                    Save();
                }

                ImGui.SameLine();

                if (ImGui.Button("Default dark"))
                {
                    cfg.UseDalamudColors = false;
                    Save();
                }

                ImGui.Spacing();
                ImGui.TextDisabled("APGambaHax " + VersionText + " - ROUNDTRACKER DEV1");
                ImGui.TextDisabled("Bridge: " + bridge.Status);
                ImGui.TextDisabled("Aetherphone assembly: " + bridge.Version);
            }

            ImGui.End();
            settingsOpen = open;
        }
        finally
        {
            ImGui.PopStyleColor(Theme.PushCount);
        }
    }
}
