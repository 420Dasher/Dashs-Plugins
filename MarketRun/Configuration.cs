using Dalamud.Configuration;

namespace MarketRun;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string DataCenter { get; set; } = "Light";

    // Estimated purchase tax, enabled by default.
    public bool IncludePurchaseTax { get; set; } = true;

    public string ActiveListId { get; set; } = "";

    public List<ShoppingList> Lists { get; set; } = new();

    public bool UseDalamudColors { get; set; }

    public uint[] MatchedStyleColors { get; set; }
        = Array.Empty<uint>();
}

public sealed class ShoppingList
{
    public string Id { get; set; }
        = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "My Shopping List";

    public List<ShoppingItem> Items { get; set; } = new();
}

public sealed class ShoppingItem
{
    public uint ItemId { get; set; }

    public string Name { get; set; } = "";

    public int Quantity { get; set; } = 1;

    public int Acquired { get; set; }

    // 0 = Any, 1 = NQ, 2 = HQ.
    public int Quality { get; set; }
}