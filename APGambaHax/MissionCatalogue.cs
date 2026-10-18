using System;
using System.Collections.Generic;

namespace APGambaHax;

// Observed server variants contain no raw account IDs or mission IDs.
// They describe objective shapes, not verified server payout grants.
public sealed class ObservedMissionVariant
{
    public string Metric { get; set; } = "";
    public string GameKind { get; set; } = "";
    public long Target { get; set; }
    public long Threshold { get; set; }
    public long MinimumReward { get; set; }
    public long MaximumReward { get; set; }
    public long FirstSeenAtUnix { get; set; }
    public long LastSeenAtUnix { get; set; }
    public int UtcDatesSeen { get; set; } = 1;
}

internal readonly record struct MissionFamily(
    string Metric,
    string Description);

internal readonly record struct MissionExample(
    string Metric,
    string GameKind,
    long Target,
    long Threshold,
    string Description);

internal static class MissionCatalogue
{
    private const int MaxVariantsPerAccount = 256;

    // Defined by Aetherphone's CasinoMissionMetrics.
    public static readonly MissionFamily[] Families =
    {
        new("rounds", "Play or settle a number of rounds"),
        new("games", "Play different casino games"),
        new("wins", "Receive more than the stake"),
        new("hits", "Meet a payout multiplier threshold"),
        new("maxbet", "Use the game's maximum permitted bet"),
        new("hosted", "Participate at hosted chips tables")
    };

    // Examples from Aetherphone's CasinoClubTests.
    // They are NOT an exhaustive server mission pool.
    public static readonly MissionExample[] ClientExamples =
    {
        new("rounds", "casino.slots", 25, 0,
            "Spin the slots 25 times"),
        new("rounds", "casino.blackjack", 10, 0,
            "Play 10 hands of blackjack"),
        new("rounds", "casino.wheel", 10, 0,
            "Play 10 rounds of Wheel"),
        new("rounds", "casino.plinko", 10, 0,
            "Drop 10 Plinko balls"),
        new("rounds", "casino.race", 2, 0,
            "Bet on 2 different races"),
        new("rounds", "", 5, 0,
            "Play 5 rounds of anything"),
        new("games", "", 3, 0,
            "Play 3 different games"),
        new("wins", "", 5, 0,
            "Win more than you bet, 5 times"),
        new("wins", "casino.slots", 5, 0,
            "Win more than you bet on Slots, 5 times"),
        new("hits", "", 1, 100,
            "Win 10x your bet or more on any game"),
        new("hits", "casino.slots", 1, 100,
            "Win 10x your bet or more on Slots"),
        new("hits", "", 3, 50,
            "Win 5x your bet or more, 3 times"),
        new("maxbet", "", 1, 0,
            "Bet your max once"),
        new("hosted", "", 1, 0,
            "Play a hand at a hosted chips table")
    };

    public static IReadOnlyList<ObservedMissionVariant> Observed(
        Configuration cfg,
        string account)
    {
        if (account.Length == 0 ||
            cfg.MissionCatalogueByAccount is null ||
            !cfg.MissionCatalogueByAccount.TryGetValue(
                account, out var list) ||
            list is null)
        {
            return Array.Empty<ObservedMissionVariant>();
        }

        return list;
    }

    // True only when saved observations actually changed.
    // The calling plugin saves configuration in that case.
    public static bool Observe(
        Configuration cfg,
        AetherphoneBridge bridge)
    {
        if (bridge.AccountKey.Length == 0 ||
            !bridge.MissionsLoaded)
        {
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (bridge.MissionsResetsAtUnix > 0 &&
            now >= bridge.MissionsResetsAtUnix)
        {
            return false;
        }

        cfg.MissionCatalogueByAccount ??=
            new Dictionary<string, List<ObservedMissionVariant>>(
                StringComparer.Ordinal);

        if (!cfg.MissionCatalogueByAccount.TryGetValue(
            bridge.AccountKey, out var list) ||
            list is null)
        {
            list = new List<ObservedMissionVariant>();
            cfg.MissionCatalogueByAccount[bridge.AccountKey] = list;
        }

        bool changed = false;

        foreach (var mission in bridge.Missions)
        {
            string metric = (mission.Metric ?? "").Trim()
                .ToLowerInvariant();

            string game = (mission.GameKind ?? "").Trim()
                .ToLowerInvariant();

            if (metric.Length == 0 || metric.Length > 48 ||
                game.Length > 128 || mission.Target < 0 ||
                mission.Threshold < 0)
            {
                continue;
            }

            ObservedMissionVariant? existing = null;

            foreach (var entry in list)
            {
                if (entry is not null &&
                    entry.Metric == metric &&
                    entry.GameKind == game &&
                    entry.Target == mission.Target &&
                    entry.Threshold == mission.Threshold)
                {
                    existing = entry;
                    break;
                }
            }

            if (existing is null)
            {
                if (list.Count >= MaxVariantsPerAccount)
                    continue;

                list.Add(new ObservedMissionVariant
                {
                    Metric = metric,
                    GameKind = game,
                    Target = mission.Target,
                    Threshold = mission.Threshold,
                    MinimumReward = mission.Reward,
                    MaximumReward = mission.Reward,
                    FirstSeenAtUnix = now,
                    LastSeenAtUnix = now,
                    UtcDatesSeen = 1
                });

                changed = true;
                continue;
            }

            if (mission.Reward < existing.MinimumReward)
            {
                existing.MinimumReward = mission.Reward;
                changed = true;
            }

            if (mission.Reward > existing.MaximumReward)
            {
                existing.MaximumReward = mission.Reward;
                changed = true;
            }

            // Record one observation per UTC date, not per frame.
            if (existing.LastSeenAtUnix / 86400 != now / 86400)
            {
                existing.UtcDatesSeen = Math.Max(
                    1, existing.UtcDatesSeen) + 1;
                existing.LastSeenAtUnix = now;
                changed = true;
            }
        }

        return changed;
    }

    public static string Shape(
        string metric,
        string game,
        long target,
        long threshold)
    {
        string gameLabel = string.IsNullOrWhiteSpace(game)
            ? "any game"
            : game.StartsWith("casino.", StringComparison.Ordinal)
                ? game.Substring("casino.".Length)
                : game;

        string line =
            $"{metric} | {gameLabel} | target {target:N0}";

        if (threshold > 0)
            line += $" | threshold {threshold:N0}";

        return line;
    }

    public static string Guidance(LiveMission mission)
    {
        string metric = (mission.Metric ?? "").Trim()
            .ToLowerInvariant();

        string game = (mission.GameKind ?? "").Trim()
            .ToLowerInvariant();

        bool unrestricted = game.Length == 0;
        bool barkeep =
            game == "bartender" || game == "casino.bartender";

        switch (metric)
        {
            case "wins" when unrestricted:
                return "Confirmed: profitable Barkeep wagers count. " +
                       "No mission-triggered wagering yet.";

            case "wins" when barkeep:
                return "Barkeep likely compatible; restricted variant " +
                       "not separately verified.";

            case "wins":
                return "Game-specific profitable wins required: " +
                       game + ". Manual play for now.";

            case "rounds" when unrestricted || barkeep:
                return "Barkeep rounds are plausible; server " +
                       "confirmation still needed.";

            case "rounds":
                return "Only this game counts: " + game +
                       ". Read-only tracking for now.";

            case "games":
                return "Distinct game types required. Barkeep may " +
                       "count as one; not yet server-verified.";

            case "hits":
                string multiple = mission.Threshold > 0
                    ? (mission.Threshold / 10.0)
                        .ToString("0.#") + "x"
                    : "the specified multiplier";

                return "Requires " + multiple +
                       " or better. Perfect Barkeep normally returns 2x; " +
                       "no multiplier farming automation.";

            case "maxbet":
                return "Manual only. No automatic maximum bets.";

            case "hosted":
                return "Hosted chips table participation required. " +
                       "Manual for now.";

            default:
                return "Unknown metric. Observe before classifying " +
                       "or automating.";
        }
    }
}
