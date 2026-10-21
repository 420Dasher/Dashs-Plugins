using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MarketRun;

internal sealed class MarketOffer
{
    public string World { get; init; } = "";
    public int WorldId { get; init; }
    public string Retainer { get; init; } = "";
    public int Quantity { get; init; }
    public int PricePerUnit { get; init; }
    public bool Hq { get; init; }
    public long ReviewedAt { get; init; }

    public long Total => (long)Quantity * PricePerUnit;
}

internal sealed class MarketSnapshot
{
    public uint ItemId { get; init; }
    public DateTime ReceivedUtc { get; init; }
    public long LastUploadMs { get; set; }
    public List<MarketOffer> Offers { get; } = new();

    // Per-world, per-item upload timestamps in milliseconds.
    // Do not replace these with the newest DC-wide upload.
    public Dictionary<int, long> WorldUploadTimes { get; } = new();

    public long UploadedAtSeconds(MarketOffer offer)
    {
        if (offer.WorldId <= 0)
            return 0;

        return WorldUploadTimes.TryGetValue(
            offer.WorldId, out long milliseconds)
            && milliseconds > 0
                ? milliseconds / 1000
                : 0;
    }
}

internal sealed class MarketPricing : IDisposable
{
    private readonly HttpClient http = new();
    private readonly object gate = new();

    private readonly Dictionary<uint, MarketSnapshot> snapshots = new();

    private string activeDc = "";
    private string error = "";
    private bool loading;
    private bool disposed;
    private int generation;

    private DateTime retryAfterUtc = DateTime.MinValue;
    private DateTime nextManualRefreshUtc = DateTime.MinValue;
    private DateTime lastSuccessUtc = DateTime.MinValue;

    private static readonly TimeSpan CacheLifetime =
        TimeSpan.FromMinutes(5);

    public MarketPricing()
    {
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MarketRun/0.1.0.6");
    }

    public bool IsLoading
    {
        get
        {
            lock (gate)
                return loading;
        }
    }

    public string Error
    {
        get
        {
            lock (gate)
                return error;
        }
    }

    public DateTime LastSuccessUtc
    {
        get
        {
            lock (gate)
                return lastSuccessUtc;
        }
    }

    public MarketSnapshot? Get(string dc, uint itemId)
    {
        lock (gate)
        {
            if (!string.Equals(
                dc, activeDc, StringComparison.OrdinalIgnoreCase))
                return null;

            return snapshots.GetValueOrDefault(itemId);
        }
    }

    public void Ensure(string dc, IEnumerable<uint> itemIds)
    {
        Request(dc, itemIds, false);
    }

    public int ManualRefreshWaitSeconds
    {
        get
        {
            lock (gate)
            {
                double seconds =
                    (nextManualRefreshUtc - DateTime.UtcNow)
                    .TotalSeconds;

                return Math.Max(0, (int)Math.Ceiling(seconds));
            }
        }
    }

    public bool Refresh(string dc, IEnumerable<uint> itemIds)
    {
        lock (gate)
        {
            if (disposed || loading ||
                DateTime.UtcNow < nextManualRefreshUtc)
                return false;

            nextManualRefreshUtc =
                DateTime.UtcNow.AddSeconds(30);
        }

        Request(dc, itemIds, true);
        return true;
    }

    private void Request(
        string dc,
        IEnumerable<uint> itemIds,
        bool force)
    {
        var requested = itemIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();

        int requestId;
        uint[] needed;

        lock (gate)
        {
            if (disposed)
                return;

            if (!string.Equals(
                activeDc, dc, StringComparison.OrdinalIgnoreCase))
            {
                activeDc = dc;
                snapshots.Clear();
                generation++;
                loading = false;
                error = "";
                retryAfterUtc = DateTime.MinValue;
                lastSuccessUtc = DateTime.MinValue;
            }

            if (requested.Length == 0 || loading)
                return;

            DateTime now = DateTime.UtcNow;

            if (!force && now < retryAfterUtc)
                return;

            needed = force
                ? requested
                : requested.Where(id =>
                    !snapshots.TryGetValue(id, out var snapshot) ||
                    now - snapshot.ReceivedUtc >= CacheLifetime)
                    .ToArray();

            if (needed.Length == 0)
                return;

            requestId = ++generation;
            loading = true;
            error = "";
        }

        _ = FetchAsync(dc, needed, requestId);
    }

    private async Task FetchAsync(
        string dc,
        uint[] ids,
        int requestId)
    {
        var results = new Dictionary<uint, MarketSnapshot>();

        DateTime fetched = DateTime.UtcNow;

        try
        {
            // Universalis supports at most 100 IDs per request.
            for (int offset = 0; offset < ids.Length; offset += 100)
            {
                uint[] batch = ids
                    .Skip(offset)
                    .Take(100)
                    .ToArray();

                foreach (uint id in batch)
                {
                    results[id] = new MarketSnapshot
                    {
                        ItemId = id,
                        ReceivedUtc = fetched
                    };
                }

                string items = string.Join(",", batch.Select(
                    x => x.ToString(CultureInfo.InvariantCulture)));

                string url =
                    "https://universalis.app/api/v2/" +
                    Uri.EscapeDataString(dc) + "/" +
                    items + "?entries=0";

                string response = await http.GetStringAsync(url)
                    .ConfigureAwait(false);

                using var json = JsonDocument.Parse(response);

                JsonElement root = json.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    throw new FormatException(
                        "Unexpected Universalis response.");

                // Multiple items: { "items": { "123": {...} } }
                if (root.TryGetProperty("items", out var itemsObject) &&
                    itemsObject.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in itemsObject.EnumerateObject())
                    {
                        if (!uint.TryParse(
                            property.Name,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out uint itemId))
                            continue;

                        if (!results.TryGetValue(itemId, out var snapshot))
                            continue;

                        ReadItem(property.Value, snapshot);
                    }
                }
                // One item: { "itemID": 123, "listings": [...] }
                else if (batch.Length == 1)
                {
                    ReadItem(root, results[batch[0]]);
                }
                else
                {
                    throw new FormatException(
                        "Universalis did not return an items object.");
                }
            }

            lock (gate)
            {
                if (disposed || generation != requestId)
                    return;

                foreach (var item in results)
                    snapshots[item.Key] = item.Value;

                lastSuccessUtc = DateTime.UtcNow;
                retryAfterUtc = DateTime.MinValue;
                loading = false;
                error = "";
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (disposed || generation != requestId)
                    return;

                loading = false;
                retryAfterUtc =
                    DateTime.UtcNow.AddSeconds(60);

                error = ex is TaskCanceledException
                    ? "Universalis request timed out."
                    : "Universalis: " + ex.Message;
            }
        }
    }

    private static int Int(JsonElement e, string property)
    {
        if (e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int result))
            return result;

        return 0;
    }

    private static long Long(JsonElement e, string property)
    {
        if (e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long result))
            return result;

        return 0;
    }

    private static bool Bool(JsonElement e, string property)
    {
        return e.ValueKind == JsonValueKind.Object &&
               e.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static string Str(JsonElement e, string property)
    {
        if (e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";

        return "";
    }

    private static void ReadItem(
        JsonElement item,
        MarketSnapshot snapshot)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        snapshot.LastUploadMs = Long(item, "lastUploadTime");

        // Universalis DC responses provide the last upload
        // separately for each world. Keys are world IDs.
        if (item.TryGetProperty(
                "worldUploadTimes", out var uploads) &&
            uploads.ValueKind == JsonValueKind.Object)
        {
            foreach (var world in uploads.EnumerateObject())
            {
                if (!int.TryParse(
                    world.Name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int worldId))
                    continue;

                if (world.Value.ValueKind ==
                        JsonValueKind.Number &&
                    world.Value.TryGetInt64(out long timestamp) &&
                    timestamp > 0)
                {
                    snapshot.WorldUploadTimes[worldId] = timestamp;
                }
            }
        }

        if (!item.TryGetProperty("listings", out var listings) ||
            listings.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in listings.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            int quantity = Int(entry, "quantity");
            int unitPrice = Int(entry, "pricePerUnit");

            if (quantity <= 0 || unitPrice <= 0)
                continue;

            // Mannequin offers may not behave as normal
            // Market Board listings. Exclude them for DEV2.
            if (Bool(entry, "onMannequin"))
                continue;

            string world = Str(entry, "worldName");

            if (world.Length == 0)
            {
                int worldId = Int(entry, "worldID");

                world = worldId > 0
                    ? "World #" + worldId
                    : "Unknown world";
            }

            snapshot.Offers.Add(new MarketOffer
            {
                World = world,
                WorldId = Int(entry, "worldID"),
                Retainer = Str(entry, "retainerName"),
                Quantity = quantity,
                PricePerUnit = unitPrice,
                Hq = Bool(entry, "hq"),
                ReviewedAt = Long(entry, "lastReviewTime")
            });
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            generation++;
            loading = false;
            snapshots.Clear();
        }

        http.Dispose();
    }
}

internal sealed class PurchasePlan
{
    public bool Covered { get; init; }
    public bool Exact { get; init; }
    public long Cost { get; init; }
    public long Units { get; init; }
    public int Needed { get; init; }
    public List<MarketOffer> Chosen { get; init; } = new();

    public string WorldLabel
    {
        get
        {
            var worlds = Chosen
                .Select(x => x.World)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return worlds.Length switch
            {
                0 => "No offers",
                1 => worlds[0],
                _ => worlds.Length + " worlds"
            };
        }
    }
}

internal static class PriceOptimizer
{
    public static long ListingCost(
        MarketOffer offer,
        bool includeTax)
    {
        long baseCost = offer.Total;

        // Approximate 5% purchase fee, rounded down
        // per complete Market Board listing.
        return includeTax
            ? baseCost + baseCost / 20
            : baseCost;
    }

    // Exact optimization for ordinary shopping quantities.
    // Larger cases use a labeled heuristic to avoid
    // excessive CPU and memory use on Dalamud's UI thread.
    // Work-based limit rather than an arbitrary listing count.
    // 32 required units with thousands of listings remains exact.
    // Very large problems retain the labeled heuristic fallback.
    private const int ExactQuantityLimit = 12000;
    private const long ExactWorkLimit = 6000000;

    public static PurchasePlan Calculate(
        IEnumerable<MarketOffer> offers,
        int needed,
        int quality,
        bool includeTax)
    {
        if (needed <= 0)
        {
            return new PurchasePlan
            {
                Covered = true,
                Exact = true,
                Needed = 0
            };
        }

        var eligible = offers
            .Where(x =>
                x.Quantity > 0 &&
                x.PricePerUnit > 0 &&
                (quality == 0 ||
                 (quality == 2 && x.Hq) ||
                 (quality == 1 && !x.Hq)))
            .OrderBy(x => x.PricePerUnit)
            .ThenBy(x => x.Total)
            .ToList();

        long available = eligible.Sum(x => (long)x.Quantity);

        if (available < needed)
        {
            return new PurchasePlan
            {
                Covered = false,
                Needed = needed,
                Exact = false
            };
        }

        if (needed <= ExactQuantityLimit &&
            (long)eligible.Count * (needed + 1L)
                <= ExactWorkLimit)
            return Exact(eligible, needed, includeTax);

        return Heuristic(eligible, needed, includeTax);
    }

    private static PurchasePlan Exact(
        List<MarketOffer> offers,
        int needed,
        bool includeTax)
    {
        const long Infinity = long.MaxValue / 4;

        long[] costs = new long[needed + 1];
        long[] units = new long[needed + 1];

        Array.Fill(costs, Infinity);
        Array.Fill(units, Infinity);

        costs[0] = 0;
        units[0] = 0;

        // Each stage records where an improved state came from.
        // Reconstruction cannot reuse one listing twice.
        var predecessors = new List<int[]>();

        foreach (var offer in offers)
        {
            long[] next = (long[])costs.Clone();
            long[] nextUnits = (long[])units.Clone();

            int[] selectedFrom = new int[needed + 1];
            Array.Fill(selectedFrom, -1);

            for (int quantity = 0; quantity < needed; quantity++)
            {
                if (costs[quantity] == Infinity)
                    continue;

                int destination = (int)Math.Min(
                    (long)needed,
                    (long)quantity + offer.Quantity);

                long candidate = costs[quantity] +
                    ListingCost(offer, includeTax);

                long candidateUnits =
                    units[quantity] + offer.Quantity;

                if (candidate > next[destination] ||
                    (candidate == next[destination] &&
                     candidateUnits >= nextUnits[destination]))
                    continue;

                next[destination] = candidate;
                nextUnits[destination] = candidateUnits;
                selectedFrom[destination] = quantity;
            }

            predecessors.Add(selectedFrom);
            costs = next;
            units = nextUnits;
        }

        if (costs[needed] == Infinity)
        {
            return new PurchasePlan
            {
                Covered = false,
                Needed = needed
            };
        }

        var chosen = new List<MarketOffer>();
        int remaining = needed;

        for (int i = offers.Count - 1; i >= 0; i--)
        {
            int previous = predecessors[i][remaining];

            if (previous < 0)
                continue;

            chosen.Add(offers[i]);
            remaining = previous;
        }

        if (remaining != 0)
            throw new InvalidOperationException(
                "Stack reconstruction failed.");

        chosen.Reverse();

        return new PurchasePlan
        {
            Covered = true,
            Exact = true,
            Cost = costs[needed],
            Units = chosen.Sum(x => (long)x.Quantity),
            Needed = needed,
            Chosen = chosen
        };
    }

    private static PurchasePlan Heuristic(
        List<MarketOffer> offers,
        int needed,
        bool includeTax)
    {
        var candidates = new[]
        {
            offers.OrderBy(x => x.PricePerUnit)
                  .ThenBy(x => x.Total),

            offers.OrderBy(x => x.Total)
                  .ThenBy(x => x.PricePerUnit),

            offers.OrderBy(x => x.Quantity)
                  .ThenBy(x => x.Total)
        };

        PurchasePlan? best = null;

        foreach (var ordering in candidates)
        {
            long units = 0;
            long cost = 0;
            var chosen = new List<MarketOffer>();

            foreach (var offer in ordering)
            {
                if (units >= needed)
                    break;

                units += offer.Quantity;
                cost += ListingCost(offer, includeTax);
                chosen.Add(offer);
            }

            if (units < needed)
                continue;

            var candidate = new PurchasePlan
            {
                Covered = true,
                Exact = false,
                Cost = cost,
                Units = units,
                Needed = needed,
                Chosen = chosen
            };

            if (best == null ||
                candidate.Cost < best.Cost ||
                (candidate.Cost == best.Cost &&
                 candidate.Units < best.Units))
                best = candidate;
        }

        return best ?? new PurchasePlan
        {
            Covered = false,
            Needed = needed
        };
    }
}