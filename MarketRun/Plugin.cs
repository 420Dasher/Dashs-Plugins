using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MarketRun;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IDalamudPluginInterface Pi { get; set; } = null!;

    [PluginService]
    private static ICommandManager Commands { get; set; } = null!;

    [PluginService]
    private static IDataManager Data { get; set; } = null!;

    [PluginService]
    private static IPluginLog Log { get; set; } = null!;

    private static readonly string[] DataCenters =
    {
        "Light", "Chaos",
        "Aether", "Primal", "Crystal", "Dynamis",
        "Materia",
        "Elemental", "Gaia", "Mana", "Meteor"
    };

    private readonly Configuration cfg;
    private readonly MaterialImporter importer;
    private readonly MarketPricing pricing = new();

    private readonly Dictionary<
        (uint Id, int Quality, int Remaining),
        (MarketSnapshot Snapshot, PurchasePlan Plan)> planCache = new();

    private bool mainOpen;
    private bool settingsOpen;
    private bool confirmDelete;
    private bool replaceList;
    private bool showTextEditor;
    private bool renameList;
    private string renameBuffer = "";
    private string selectedItemKey = "";

    private string importText = "";
    private string importStatus = "";

    private List<ImportRow> preview = new();

    private ShoppingList Active =>
        cfg.Lists.First(x => x.Id == cfg.ActiveListId);

    public Plugin()
    {
        cfg = Pi.GetPluginConfig() as Configuration
            ?? new Configuration();

        cfg.Lists ??= new();

        if (cfg.Lists.Count == 0)
        {
            cfg.Lists.Add(new ShoppingList());
            cfg.ActiveListId = cfg.Lists[0].Id;
            Save();
        }

        if (!cfg.Lists.Any(x => x.Id == cfg.ActiveListId))
        {
            cfg.ActiveListId = cfg.Lists[0].Id;
            Save();
        }

        importer = new MaterialImporter(Data);

        Commands.AddHandler("/mrun", new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open MarketRun. /mrun settings opens settings."
        });

        Commands.AddHandler("/shoplist", new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /mrun."
        });

        Pi.UiBuilder.Draw += Draw;
        Pi.UiBuilder.OpenMainUi += Open;
        Pi.UiBuilder.OpenConfigUi += OpenSettings;

        Log.Information("MarketRun v0.1.0 DEV2 initialized.");
    }

    private void Save() => Pi.SavePluginConfig(cfg);

    private void Open() => mainOpen = true;

    private void OpenSettings() => settingsOpen = true;

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals(
            "settings", StringComparison.OrdinalIgnoreCase))
            settingsOpen = true;
        else
            mainOpen = !mainOpen;
    }

    public void Dispose()
    {
        Pi.UiBuilder.Draw -= Draw;
        Pi.UiBuilder.OpenMainUi -= Open;
        Pi.UiBuilder.OpenConfigUi -= OpenSettings;

        Commands.RemoveHandler("/mrun");
        Commands.RemoveHandler("/shoplist");
        pricing.Dispose();
    }

    private void Draw()
    {
        if (!mainOpen && !settingsOpen)
            return;

        // Capture client colors before our own style overrides.
        uint[] capturedColors = Theme.Capture();

        DrawMain();
        DrawSettings(capturedColors);
    }

    private void DrawMain()
    {
        if (!mainOpen)
            return;

        Theme.Push(cfg);

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(540f, 460f),
                ImGuiCond.Once);

            bool open = mainOpen;

            bool visible = ImGui.Begin(
                "MarketRun###MarketRunMain",
                ref open,
                ImGuiWindowFlags.NoCollapse);

            mainOpen = open;

            if (visible)
            {
                DrawHeader();
                DrawLists();

                pricing.Ensure(
                    cfg.DataCenter,
                    Active.Items
                        .Where(x => x.Acquired < x.Quantity)
                        .Select(x => x.ItemId));

                if (ImGui.BeginTabBar("##MarketRunTabs"))
                {
                    if (ImGui.BeginTabItem("Shopping List"))
                    {
                        DrawPriceControls();
                        DrawMaterials();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Import"))
                    {
                        DrawImport();
                        ImGui.EndTabItem();
                    }

                    ImGui.EndTabBar();
                }
            }

            ImGui.End();
        }
        finally
        {
            ImGui.PopStyleColor(Theme.PushCount);
        }
    }

    private void DrawHeader()
    {
        ImGui.TextColored(Theme.Get(cfg, 4), "MARKET RUN");

        ImGui.SameLine();
        ImGui.TextDisabled("v0.1.0 DEV2");

        ImGui.SameLine(
            MathF.Max(280f, ImGui.GetWindowWidth() - 91f));

        if (ImGui.SmallButton("Settings"))
            settingsOpen = true;

        ImGui.Separator();

        ImGui.TextUnformatted("Data Center");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(135f);

        if (ImGui.BeginCombo(
            "##DataCenter", cfg.DataCenter))
        {
            foreach (string dc in DataCenters)
            {
                bool selected = dc == cfg.DataCenter;

                if (ImGui.Selectable(dc, selected))
                {
                    cfg.DataCenter = dc;
                    Save();
                }
            }

            ImGui.EndCombo();
        }

        // Pricing controls will occupy this space in DEV2.
    }

    private void DrawLists()
    {
        ImGui.Spacing();

        float available = ImGui.GetContentRegionAvail().X;

        ImGui.SetNextItemWidth(
            MathF.Max(125f, available - 225f));

        if (ImGui.BeginCombo("##ShoppingLists", Active.Name))
        {
            foreach (var list in cfg.Lists)
            {
                bool selected = list.Id == cfg.ActiveListId;

                if (ImGui.Selectable(
                    list.Name + "##" + list.Id, selected))
                {
                    cfg.ActiveListId = list.Id;

                    preview.Clear();
                    importStatus = "";
                    confirmDelete = false;
                    renameList = false;
                    selectedItemKey = "";

                    Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("New"))
        {
            var list = new ShoppingList
            {
                Name = "Shopping List " + (cfg.Lists.Count + 1)
            };

            cfg.Lists.Add(list);
            cfg.ActiveListId = list.Id;

            preview.Clear();
            confirmDelete = false;
            renameList = false;
            selectedItemKey = "";

            Save();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Rename"))
        {
            renameBuffer = Active.Name;
            renameList = !renameList;
            confirmDelete = false;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Delete"))
        {
            confirmDelete = !confirmDelete;
            renameList = false;
        }

        if (renameList)
        {
            ImGui.SetNextItemWidth(
                MathF.Max(
                    130f,
                    ImGui.GetContentRegionAvail().X - 155f));

            ImGui.InputText(
                "##RenameListInput",
                ref renameBuffer,
                96);

            ImGui.SameLine();

            if (ImGui.SmallButton("Save name"))
            {
                string name = renameBuffer.Trim();

                if (name.Length > 0)
                {
                    Active.Name = name;
                    Save();
                }

                renameList = false;
            }

            ImGui.SameLine();

            if (ImGui.SmallButton("Cancel##Rename"))
            {
                renameList = false;
            }
        }

        if (confirmDelete)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.68f, 0.35f, 1f),
                "Delete this shopping list?");

            if (ImGui.SmallButton("Confirm delete"))
            {
                cfg.Lists.Remove(Active);

                if (cfg.Lists.Count == 0)
                    cfg.Lists.Add(new ShoppingList());

                cfg.ActiveListId = cfg.Lists[0].Id;

                confirmDelete = false;
                selectedItemKey = "";
                preview.Clear();

                Save();
            }

            ImGui.SameLine();

            if (ImGui.SmallButton("Cancel##Delete"))
                confirmDelete = false;
        }
    }

    private PurchasePlan? GetPlan(ShoppingItem item)
    {
        int remaining = Math.Max(
            0, item.Quantity - item.Acquired);

        if (remaining == 0)
            return null;

        var snapshot = pricing.Get(cfg.DataCenter, item.ItemId);

        if (snapshot == null)
            return null;

        var key = (item.ItemId, item.Quality, remaining);

        if (planCache.TryGetValue(key, out var existing) &&
            ReferenceEquals(existing.Snapshot, snapshot))
            return existing.Plan;

        var plan = PriceOptimizer.Calculate(
            snapshot.Offers,
            remaining,
            item.Quality);

        planCache[key] = (snapshot, plan);

        // Remove stale optimization results occasionally.
        if (planCache.Count > 512)
        {
            planCache.Clear();
            planCache[key] = (snapshot, plan);
        }

        return plan;
    }

    private static string Age(long timestamp)
    {
        if (timestamp <= 0 || timestamp > 253402300799)
            return "age unknown";

        var elapsed = DateTimeOffset.UtcNow -
            DateTimeOffset.FromUnixTimeSeconds(timestamp);

        if (elapsed.TotalSeconds < 0)
            return "time uncertain";

        if (elapsed.TotalMinutes < 60)
            return $"{Math.Max(0, (int)elapsed.TotalMinutes)}m ago";

        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours}h ago";

        return $"{(int)elapsed.TotalDays}d ago";
    }

    private void DrawPriceControls()
    {
        ImGui.Spacing();

        if (ImGui.SmallButton("Refresh prices"))
        {
            pricing.Refresh(
                cfg.DataCenter,
                Active.Items
                    .Where(x => x.Acquired < x.Quantity)
                    .Select(x => x.ItemId));
        }

        ImGui.SameLine();

        if (pricing.IsLoading)
            ImGui.TextDisabled("Fetching Universalis listings...");
        else if (pricing.LastSuccessUtc != DateTime.MinValue)
            ImGui.TextDisabled(
                "Updated " + Age(
                    new DateTimeOffset(
                        DateTime.SpecifyKind(
                            pricing.LastSuccessUtc,
                            DateTimeKind.Utc))
                    .ToUnixTimeSeconds()));
        else
            ImGui.TextDisabled("No market data loaded.");

        if (pricing.Error.Length > 0)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.65f, 0.35f, 1f),
                pricing.Error);
        }

        long total = 0;
        int quoted = 0;
        int outstanding = 0;
        bool allExact = true;

        foreach (var item in Active.Items)
        {
            if (item.Acquired >= item.Quantity)
                continue;

            outstanding++;

            var plan = GetPlan(item);

            if (plan == null || !plan.Covered)
                continue;

            quoted++;
            total += plan.Cost;

            if (!plan.Exact)
                allExact = false;
        }

        if (outstanding == 0)
        {
            ImGui.TextDisabled("No outstanding materials.");
        }
        else
        {
            string label = quoted == outstanding
                ? "Estimated total"
                : "Partial estimate";

            ImGui.TextColored(
                Theme.Get(cfg, 4),
                $"{label}: {total:N0} gil");

            ImGui.SameLine();

            ImGui.TextDisabled(
                $"({quoted}/{outstanding} priced)" +
                (allExact ? "" : "  heuristic"));
        }

        ImGui.TextDisabled(
            "Universalis estimates; prices exclude purchase tax.");
    }

    private void DrawOfferDetails(ShoppingItem item)
    {
        var snapshot = pricing.Get(cfg.DataCenter, item.ItemId);
        var plan = GetPlan(item);

        if (snapshot == null)
            return;

        if (!ImGui.CollapsingHeader(
            "Recommended purchase stacks"))
            return;

        ImGui.Spacing();

        if (plan == null)
        {
            ImGui.TextDisabled("This material is complete.");
            return;
        }

        if (!plan.Covered)
        {
            ImGui.TextWrapped(
                "Not enough reported listings to cover the " +
                "remaining quantity across this data center.");

            ImGui.TextDisabled(
                $"{snapshot.Offers.Count} reported listings.");
            return;
        }

        ImGui.TextColored(
            Theme.Get(cfg, 5),
            $"{plan.Cost:N0} gil  |  {plan.WorldLabel}");

        ImGui.TextDisabled(
            $"{plan.Units:N0} units for {plan.Needed:N0} needed" +
            (plan.Units > plan.Needed
                ? $"  ({plan.Units - plan.Needed:N0} extra)"
                : ""));

        if (!plan.Exact)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.74f, 0.40f, 1f),
                "Heuristic estimate; not a guaranteed minimum.");
        }

        ImGui.TextDisabled(
            "Reported stacks, not live purchase confirmations.");

        if (ImGui.BeginChild(
            "##MarketRunOffers",
            new Vector2(0f, 125f),
            true))
        {
            foreach (var offer in plan.Chosen
                .OrderBy(x => x.World)
                .ThenBy(x => x.PricePerUnit))
            {
                ImGui.TextWrapped(
                    $"{offer.World}  |  {offer.Quantity:N0} x " +
                    $"{offer.PricePerUnit:N0} = {offer.Total:N0} gil");

                ImGui.TextDisabled(
                    (offer.Retainer.Length == 0
                        ? "Retainer unknown"
                        : offer.Retainer) +
                    "  |  " + Age(offer.ReviewedAt));

                ImGui.Separator();
            }
        }

        ImGui.EndChild();

        long oldest = plan.Chosen
            .Select(x => x.ReviewedAt)
            .DefaultIfEmpty(0)
            .Min();

        if (oldest == 0)
        {
            ImGui.TextDisabled(
                "Some listing timestamps are unavailable.");
        }
        else if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() -
                 oldest > 86400)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.70f, 0.35f, 1f),
                "Warning: At least one selected listing is over 24h old.");
        }
    }
    private void DrawMaterials()
    {
        var list = Active;

        int completed = list.Items.Count(x =>
            x.Acquired >= x.Quantity);

        ImGui.Spacing();

        ImGui.TextColored(
            Theme.Get(cfg, 4),
            "MATERIALS");

        ImGui.SameLine();

        ImGui.TextDisabled(
            $"{completed} / {list.Items.Count} complete");

        ImGui.Separator();

        if (list.Items.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No materials imported yet.");
            ImGui.TextDisabled(
                "Use the Import tab to add materials.");
            return;
        }

        // Resolve the selected item before drawing the list.
        // The editor remains visible above the scrollable rows.
        var selectedItem = list.Items.FirstOrDefault(
            x => $"{x.ItemId}-{x.Quality}" == selectedItemKey);

        if (selectedItem == null)
        {
            selectedItem = list.Items.FirstOrDefault(
                x => x.Acquired < x.Quantity)
                ?? list.Items[0];

            selectedItemKey =
                $"{selectedItem.ItemId}-{selectedItem.Quality}";
        }

        string selectedQuality = selectedItem.Quality switch
        {
            2 => " [HQ]",
            1 => " [NQ]",
            _ => ""
        };

        ImGui.TextColored(
            Theme.Get(cfg, 4),
            "SELECTED");

        ImGui.SameLine();

        ImGui.TextUnformatted(
            selectedItem.Name + selectedQuality);

        int needed = selectedItem.Quantity;
        int acquired = selectedItem.Acquired;

        ImGui.SetNextItemWidth(84f);

        if (ImGui.InputInt(
            "Needed##Selected", ref needed, 0, 0))
        {
            selectedItem.Quantity = Math.Clamp(
                needed, 1, 999999);

            selectedItem.Acquired = Math.Min(
                selectedItem.Acquired,
                selectedItem.Quantity);

            Save();
        }

        ImGui.SameLine();

        ImGui.SetNextItemWidth(84f);

        if (ImGui.InputInt(
            "Acquired##Selected", ref acquired, 0, 0))
        {
            selectedItem.Acquired = Math.Clamp(
                acquired, 0, selectedItem.Quantity);

            Save();
        }

        bool done =
            selectedItem.Acquired >= selectedItem.Quantity;

        if (ImGui.SmallButton(
            done ? "Reset progress" : "Mark complete"))
        {
            selectedItem.Acquired =
                done ? 0 : selectedItem.Quantity;

            Save();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Remove selected"))
        {
            list.Items.Remove(selectedItem);
            selectedItemKey = "";

            Save();
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Size the list according to its content.
        // One material no longer reserves a huge empty area.
        // Large lists remain scrollable.
        float rowHeight =
            ImGui.GetTextLineHeightWithSpacing() + 4f;

        float desiredHeight = MathF.Min(
            210f,
            12f + list.Items.Count * rowHeight);

        float availableHeight =
            ImGui.GetContentRegionAvail().Y;

        float listHeight = MathF.Min(
            desiredHeight,
            MathF.Max(35f, availableHeight - 8f));

        if (ImGui.BeginChild(
            "##MarketRunCompactItems",
            new Vector2(0f, listHeight),
            true))
        {
            foreach (var item in list.Items)
            {
                string key =
                    $"{item.ItemId}-{item.Quality}";

                bool itemDone =
                    item.Acquired >= item.Quantity;

                int remaining = Math.Max(
                    0,
                    item.Quantity - item.Acquired);

                string quality = item.Quality switch
                {
                    2 => " [HQ]",
                    1 => " [NQ]",
                    _ => ""
                };

                var quote = GetPlan(item);

                string priceText = remaining == 0
                    ? "Done"
                    : quote == null
                        ? "Price pending"
                        : quote.Covered
                            ? $"{quote.Cost:N0} g  |  {quote.WorldLabel}" +
                              (quote.Exact ? "" : " *")
                            : "No complete offer";

                string label =
                    (itemDone ? "[x] " : "[ ] ") +
                    item.Name + quality +
                    $"  -  {remaining:N0} left  |  {priceText}";

                bool selected =
                    selectedItemKey == key;

                if (ImGui.Selectable(
                    label + "##" + key,
                    selected))
                {
                    selectedItemKey = key;
                }
            }
        }

        ImGui.EndChild();

        DrawOfferDetails(selectedItem);
    }

    private void DrawImport()
    {
        ImGui.Spacing();

        ImGui.TextColored(
            Theme.Get(cfg, 4),
            "IMPORT MATERIALS");

        ImGui.TextDisabled(
            "Copy a material list, then import it here.");

        if (ImGui.Button("Import from Clipboard"))
        {
            try
            {
                string clipboard = ImGui.GetClipboardText();

                if (string.IsNullOrWhiteSpace(clipboard))
                {
                    importStatus = "Clipboard is empty or contains no text.";
                    preview.Clear();
                }
                else if (clipboard.Length > 100000)
                {
                    importStatus =
                        "Clipboard text is too long (maximum 100,000 characters).";
                    preview.Clear();
                }
                else
                {
                    importText = clipboard;
                    preview = importer.Parse(importText);
                    showTextEditor = false;

                    importStatus = preview.Count == 0
                        ? "No materials detected in clipboard."
                        : "";
                }
            }
            catch (Exception ex)
            {
                preview.Clear();
                importStatus =
                    "Could not read clipboard: " + ex.Message;
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(
            showTextEditor ? "Hide text editor" : "Edit text"))
        {
            showTextEditor = !showTextEditor;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Clear"))
        {
            importText = "";
            preview.Clear();
            importStatus = "";
        }

        if (showTextEditor)
        {
            ImGui.Spacing();

            ImGui.InputTextMultiline(
                "##MarketRunImportText",
                ref importText,
                100000,
                new Vector2(-1f, 115f));

            if (ImGui.Button("Preview edited text"))
            {
                preview = importer.Parse(importText);
                importStatus = preview.Count == 0
                    ? "No materials found."
                    : "";
            }
        }

        ImGui.Spacing();

        ImGui.Checkbox(
            "Replace existing materials",
            ref replaceList);

        if (preview.Count > 0)
        {
            ImGui.Separator();

            int valid = preview.Count(x => x.Valid);
            int invalid = preview.Count - valid;

            ImGui.TextColored(
                Theme.Get(cfg, 4),
                "IMPORT PREVIEW");

            ImGui.TextDisabled(
                $"{valid} recognized / {invalid} unresolved");

            if (ImGui.BeginChild(
                "##MarketRunImportPreview",
                new Vector2(0f, 195f),
                true))
            {
                foreach (var row in preview)
                {
                    if (row.Valid)
                    {
                        string quality = row.Quality switch
                        {
                            2 => " [HQ]",
                            1 => " [NQ]",
                            _ => ""
                        };

                        ImGui.TextColored(
                            Theme.Get(cfg, 5),
                            $"{row.Quantity:N0}x {row.Name}{quality}");
                    }
                    else
                    {
                        ImGui.TextColored(
                            new Vector4(1f, 0.65f, 0.35f, 1f),
                            $"Line {row.Line}: {row.Error}");

                        ImGui.TextWrapped(row.Original);
                    }
                }
            }

            ImGui.EndChild();

            if (invalid > 0)
            {
                ImGui.TextWrapped(
                    "Some entries could not be recognized. " +
                    "Use Edit text to correct them before importing.");
            }
            else if (valid > 0)
            {
                if (ImGui.Button($"Confirm import ({valid} entries)"))
                {
                    ApplyImport();
                }
            }
        }

        if (importStatus.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(importStatus);
        }
    }

    private void ApplyImport()
    {
        if (preview.Count == 0 ||
            preview.Any(x => !x.Valid))
            return;

        var list = Active;

        // Prevalidate quantities before changing the saved list.
        var quantities = new Dictionary<(uint, int), long>();

        if (!replaceList)
        {
            foreach (var item in list.Items)
                quantities[(item.ItemId, item.Quality)] =
                    item.Quantity;
        }

        foreach (var row in preview)
        {
            var key = (row.ItemId, row.Quality);

            quantities.TryGetValue(key, out long current);

            long combined = current + row.Quantity;

            if (combined > 999999)
            {
                importStatus =
                    $"Quantity limit exceeded for {row.Name}.";
                return;
            }

            quantities[key] = combined;
        }

        if (replaceList)
            list.Items.Clear();

        foreach (var row in preview)
        {
            var existing = list.Items.FirstOrDefault(x =>
                x.ItemId == row.ItemId &&
                x.Quality == row.Quality);

            if (existing == null)
            {
                list.Items.Add(new ShoppingItem
                {
                    ItemId = row.ItemId,
                    Name = row.Name,
                    Quantity = row.Quantity,
                    Acquired = 0,
                    Quality = row.Quality
                });
            }
            else
            {
                existing.Quantity += row.Quantity;
            }
        }

        Save();

        importText = "";
        preview.Clear();

        importStatus = "Materials imported and saved.";
    }

    private void DrawSettings(uint[] clientColors)
    {
        if (!settingsOpen)
            return;

        Theme.Push(cfg);

        try
        {
            ImGui.SetNextWindowSize(
                new Vector2(410f, 285f),
                ImGuiCond.FirstUseEver);

            bool open = settingsOpen;

            bool visible = ImGui.Begin(
                "MarketRun Settings###MarketRunSettings",
                ref open,
                ImGuiWindowFlags.NoCollapse);

            settingsOpen = open;

            if (visible)
            {
                ImGui.TextColored(
                    Theme.Get(cfg, 4),
                    "APPEARANCE");

                ImGui.Separator();

                ImGui.TextWrapped(
                    "Match your current Dalamud color scheme " +
                    "or use MarketRun's built-in dark palette.");

                if (ImGui.Button("Match Dalamud Colors"))
                {
                    cfg.MatchedStyleColors = clientColors;
                    cfg.UseDalamudColors = true;
                    Save();
                }

                bool matched = cfg.UseDalamudColors;

                if (ImGui.Checkbox(
                    "Use captured colors",
                    ref matched))
                {
                    cfg.UseDalamudColors = matched;
                    Save();
                }

                ImGui.TextDisabled(
                    cfg.UseDalamudColors
                        ? "Captured theme active"
                        : "Built-in theme active");

                ImGui.Separator();

                ImGui.TextDisabled("MarketRun v0.1.0 DEV2");

                ImGui.TextDisabled(
                    "Universalis integration begins in DEV2.");
            }

            ImGui.End();
        }
        finally
        {
            ImGui.PopStyleColor(Theme.PushCount);
        }
    }
}