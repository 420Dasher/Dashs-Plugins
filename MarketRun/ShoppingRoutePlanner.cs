using System.Threading;

namespace MarketRun;

internal sealed class RouteMaterial
{
    public string Key { get; init; } = "";
    public int Needed { get; init; }
    public int Quality { get; init; }
    public MarketOffer[] Offers { get; init; } = [];
}

internal sealed class RouteChoice
{
    public bool Complete { get; init; }
    public bool Exact { get; init; }
    public long Cost { get; init; }
    public long Units { get; init; }
    public int Quoted { get; init; }
    public int Total { get; init; }

    public string[] Worlds { get; init; } = [];

    public Dictionary<string, PurchasePlan> Plans { get; init; }
        = new();

    public int ExtraWorlds(string home)
    {
        return Worlds.Count(x =>
            !string.Equals(
                x, home,
                StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class RouteComparison
{
    public string StartingWorld { get; init; } = "";

    public RouteChoice Cheapest { get; init; } = new();
    public RouteChoice Practical { get; init; } = new();

    public RouteChoice? CurrentOnly { get; init; }
    public RouteChoice? BestOneHop { get; init; }

    public long RequiredSavings { get; init; }

    public string Advisory { get; init; } = "";
}

internal static class ShoppingRoutePlanner
{
    private static readonly StringComparer WorldComparer =
        StringComparer.OrdinalIgnoreCase;

    public static RouteComparison Calculate(
        RouteMaterial[] materials,
        string currentWorld,
        bool includeTax,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        RouteChoice absolute = Evaluate(
            materials, null, includeTax, cancellation);

        string[] worlds = materials
            .SelectMany(x => x.Offers)
            .Select(x => x.World)
            .Where(x =>
                !string.IsNullOrWhiteSpace(x) &&
                !x.StartsWith(
                    "World #",
                    StringComparison.OrdinalIgnoreCase) &&
                !x.Equals(
                    "Unknown world",
                    StringComparison.OrdinalIgnoreCase))
            .Distinct(WorldComparer)
            .OrderBy(x => x)
            .ToArray();

        if (string.IsNullOrWhiteSpace(currentWorld))
        {
            return new RouteComparison
            {
                StartingWorld = "",
                Cheapest = absolute,
                Practical = absolute,
                Advisory =
                    "Current world unavailable; showing DC-wide prices."
            };
        }

        var homeSet = new HashSet<string>(
            WorldComparer) { currentWorld };

        RouteChoice home = Evaluate(
            materials,
            homeSet,
            includeTax,
            cancellation);

        long threshold = home.Complete
            ? RequiredSaving(home.Cost)
            : 0;

        RouteChoice? bestExtra = null;

        // A single additional world can be used together
        // with the current world. Materials already available
        // at home do not require an unnecessary return trip.
        foreach (string world in worlds)
        {
            cancellation.ThrowIfCancellationRequested();

            if (WorldComparer.Equals(world, currentWorld))
                continue;

            var allowed = new HashSet<string>(
                WorldComparer)
            {
                currentWorld,
                world
            };

            RouteChoice candidate = Evaluate(
                materials,
                allowed,
                includeTax,
                cancellation);

            if (!candidate.Complete)
                continue;

            if (candidate.ExtraWorlds(currentWorld) > 1)
                continue;

            if (bestExtra == null ||
                candidate.Cost < bestExtra.Cost ||
                (candidate.Cost == bestExtra.Cost &&
                 candidate.ExtraWorlds(currentWorld) <
                 bestExtra.ExtraWorlds(currentWorld)) ||
                (candidate.Cost == bestExtra.Cost &&
                 candidate.ExtraWorlds(currentWorld) ==
                 bestExtra.ExtraWorlds(currentWorld) &&
                 candidate.Units < bestExtra.Units))
            {
                bestExtra = candidate;
            }
        }

        RouteChoice practical;
        string advisory;

        if (home.Complete)
        {
            practical = home;
            advisory = "Current world is sufficient.";

            if (bestExtra != null &&
                bestExtra.Cost < home.Cost)
            {
                long savings = home.Cost - bestExtra.Cost;

                if (savings >= threshold)
                {
                    practical = bestExtra;
                    advisory =
                        "An additional world saves enough gil " +
                        "to justify a visit.";
                }
                else
                {
                    advisory =
                        "Extra World Visit savings are below " +
                        "the practical threshold.";
                }
            }
        }
        else if (bestExtra != null)
        {
            // Finishing the shopping list takes priority.
            practical = bestExtra;
            advisory =
                "Current world cannot complete this list. " +
                "One additional world is recommended.";
        }
        else
        {
            practical = absolute;

            advisory = absolute.Complete
                ? "Completing the list requires broader " +
                  "DC-wide shopping than a single extra world."
                : "Universalis does not report enough " +
                  "eligible listings to complete this list.";
        }

        return new RouteComparison
        {
            StartingWorld = currentWorld,
            Cheapest = absolute,
            Practical = practical,
            CurrentOnly = home,
            BestOneHop = bestExtra,
            RequiredSavings = threshold,
            Advisory = advisory
        };
    }

    public static long RequiredSaving(long homeCost)
    {
        // Dynamic travel inconvenience threshold:
        // 10k minimum, scaling with the square root of
        // the current-world shopping trip cost.
        return Math.Max(
            10000L,
            (long)Math.Ceiling(
                Math.Sqrt(Math.Max(0L, homeCost) * 1000.0)));
    }

    private static RouteChoice Evaluate(
        RouteMaterial[] materials,
        HashSet<string>? allowedWorlds,
        bool includeTax,
        CancellationToken cancellation)
    {
        var plans = new Dictionary<string, PurchasePlan>();

        int quoted = 0;
        long cost = 0;
        long units = 0;
        bool exact = true;

        var worlds = new HashSet<string>(WorldComparer);

        foreach (RouteMaterial material in materials)
        {
            cancellation.ThrowIfCancellationRequested();

            IEnumerable<MarketOffer> eligible =
                material.Offers;

            if (allowedWorlds != null)
            {
                eligible = eligible.Where(offer =>
                    allowedWorlds.Contains(offer.World));
            }

            PurchasePlan plan = PriceOptimizer.Calculate(
                eligible,
                material.Needed,
                material.Quality,
                includeTax);

            plans[material.Key] = plan;

            if (!plan.Covered)
            {
                exact = false;
                continue;
            }

            quoted++;
            cost += plan.Cost;
            units += plan.Units;

            if (!plan.Exact)
                exact = false;

            foreach (var offer in plan.Chosen)
                worlds.Add(offer.World);
        }

        return new RouteChoice
        {
            Complete = quoted == materials.Length,
            Exact = exact,
            Quoted = quoted,
            Total = materials.Length,
            Cost = cost,
            Units = units,
            Worlds = worlds.OrderBy(x => x).ToArray(),
            Plans = plans
        };
    }
}