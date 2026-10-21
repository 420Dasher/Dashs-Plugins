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

    private bool mainOpen;
    private bool settingsOpen;
    private bool confirmDelete;
    private bool replaceList;

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

        Log.Information("MarketRun v0.1.0 DEV1 initialized.");
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
            mainOpen = true;
    }

    public void Dispose()
    {
        Pi.UiBuilder.Draw -= Draw;
        Pi.UiBuilder.OpenMainUi -= Open;
        Pi.UiBuilder.OpenConfigUi -= OpenSettings;

        Commands.RemoveHandler("/mrun");
        Commands.RemoveHandler("/shoplist");
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
                new Vector2(500f, 470f),
                ImGuiCond.FirstUseEver);

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
                DrawMaterials();
                DrawImport();
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
        ImGui.TextDisabled("v0.1.0 DEV1");

        ImGui.SameLine(
            MathF.Max(280f, ImGui.GetWindowWidth() - 91f));

        if (ImGui.SmallButton("Settings"))
            settingsOpen = true;

        ImGui.TextDisabled(
            "Material shopping and progress tracking");

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

        ImGui.SameLine();
        ImGui.TextDisabled("Prices available in DEV2");
    }

    private void DrawLists()
    {
        ImGui.Spacing();

        ImGui.SetNextItemWidth(
            MathF.Max(
                130f,
                ImGui.GetContentRegionAvail().X - 177f));

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

                    Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("New list"))
        {
            var list = new ShoppingList
            {
                Name = "Shopping List " + (cfg.Lists.Count + 1)
            };

            cfg.Lists.Add(list);
            cfg.ActiveListId = list.Id;

            preview.Clear();
            confirmDelete = false;

            Save();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Delete"))
            confirmDelete = true;

        if (confirmDelete)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.68f, 0.35f, 1f),
                "Delete the current list?");

            if (ImGui.SmallButton("Confirm delete"))
            {
                cfg.Lists.Remove(Active);

                if (cfg.Lists.Count == 0)
                    cfg.Lists.Add(new ShoppingList());

                cfg.ActiveListId = cfg.Lists[0].Id;

                confirmDelete = false;
                preview.Clear();

                Save();
            }

            ImGui.SameLine();

            if (ImGui.SmallButton("Cancel"))
                confirmDelete = false;
        }

        string name = Active.Name;

        ImGui.SetNextItemWidth(-1f);

        if (ImGui.InputText("##ListName", ref name, 96))
        {
            Active.Name = string.IsNullOrWhiteSpace(name)
                ? "Shopping List"
                : name;

            Save();
        }
    }

    private void DrawMaterials()
    {
        ImGui.Spacing();
        ImGui.Separator();

        var list = Active;

        int completed = list.Items.Count(x =>
            x.Acquired >= x.Quantity);

        ImGui.TextColored(
            Theme.Get(cfg, 4),
            "MATERIALS");

        ImGui.SameLine();

        ImGui.TextDisabled(
            $"{completed} / {list.Items.Count} complete");

        if (list.Items.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No materials imported yet.");
            ImGui.Spacing();
            return;
        }

        if (ImGui.BeginChild(
            "##MarketRunItems",
            new Vector2(0f, 190f),
            true))
        {
            ShoppingItem? remove = null;

            foreach (var item in list.Items)
            {
                ImGui.PushID(
                    $"{item.ItemId}-{item.Quality}");

                try
                {
                    bool done = item.Acquired >= item.Quantity;

                    ImGui.TextColored(
                        done ? Theme.Get(cfg, 5)
                             : Theme.Get(cfg, 2),
                        item.Name);

                    if (item.Quality != 0)
                    {
                        ImGui.SameLine();

                        ImGui.TextDisabled(
                            item.Quality == 2 ? "[HQ]" : "[NQ]");
                    }

                    ImGui.TextDisabled(
                        $"{item.Acquired:N0} / {item.Quantity:N0}");

                    if (ImGui.SmallButton("-"))
                    {
                        item.Acquired = Math.Max(
                            0, item.Acquired - 1);

                        Save();
                    }

                    ImGui.SameLine();

                    if (ImGui.SmallButton("+"))
                    {
                        item.Acquired = Math.Min(
                            item.Quantity,
                            item.Acquired + 1);

                        Save();
                    }

                    ImGui.SameLine();

                    if (ImGui.SmallButton(
                        done ? "Reset" : "Complete"))
                    {
                        item.Acquired = done ? 0 : item.Quantity;
                        Save();
                    }

                    ImGui.SameLine();

                    if (ImGui.SmallButton("Remove"))
                        remove = item;

                    int amount = item.Quantity;

                    ImGui.SetNextItemWidth(105f);

                    if (ImGui.InputInt(
                        "Needed", ref amount, 0, 0))
                    {
                        item.Quantity = Math.Clamp(
                            amount, 1, 999999);

                        item.Acquired = Math.Min(
                            item.Acquired, item.Quantity);

                        Save();
                    }

                    ImGui.Separator();
                }
                finally
                {
                    ImGui.PopID();
                }
            }

            if (remove != null)
            {
                list.Items.Remove(remove);
                Save();
            }
        }

        ImGui.EndChild();
    }

    private void DrawImport()
    {
        ImGui.Spacing();

        if (!ImGui.CollapsingHeader(
            "IMPORT MATERIALS",
            ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled(
            "Paste material text, CSV/TSV or simple item JSON.");

        ImGui.TextDisabled(
            "Examples: 10x Copper Ore | Copper Ore: 10");

        ImGui.InputTextMultiline(
            "##ImportText",
            ref importText,
            100000,
            new Vector2(-1f, 115f));

        if (ImGui.Button("Preview import"))
        {
            preview = importer.Parse(importText);

            importStatus = preview.Count == 0
                ? "No materials found."
                : "";
        }

        ImGui.SameLine();

        if (ImGui.Button("Clear"))
        {
            importText = "";
            preview.Clear();
            importStatus = "";
        }

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
                "##ImportPreview",
                new Vector2(0f, 125f),
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
                    "Resolve all unrecognized entries before importing. " +
                    "No partial imports will be performed.");
            }
            else if (valid > 0)
            {
                if (ImGui.Button($"Import {valid} entries"))
                    ApplyImport();
            }
        }

        if (importStatus.Length > 0)
            ImGui.TextWrapped(importStatus);
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
                new Vector2(410f, 225f),
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

                ImGui.TextDisabled("MarketRun v0.1.0 DEV1");

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