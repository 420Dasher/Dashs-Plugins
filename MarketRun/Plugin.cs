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
    private bool showTextEditor;
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

        Log.Information("MarketRun v0.1.0 DEV1 FIX1 initialized.");
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
                new Vector2(505f, 435f),
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

                if (ImGui.BeginTabBar("##MarketRunTabs"))
                {
                    if (ImGui.BeginTabItem("Shopping List"))
                    {
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
        ImGui.TextDisabled("v0.1.0 DEV1 FIX1");

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

        // Pricing controls will occupy this space in DEV2.
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
        var list = Active;

        int completed = list.Items.Count(x =>
            x.Acquired >= x.Quantity);

        ImGui.Spacing();

        ImGui.TextColored(Theme.Get(cfg, 4), "MATERIALS");
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"{completed} / {list.Items.Count} complete");

        ImGui.Separator();

        if (list.Items.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No materials imported yet.");
            ImGui.TextDisabled(
                "Open the Import tab to add materials.");
            return;
        }

        // Only one selected item exposes editing controls.
        // All other items remain compact single-line entries.
        if (ImGui.BeginChild(
            "##MarketRunCompactItems",
            new Vector2(0f, 215f),
            true))
        {
            foreach (var item in list.Items)
            {
                string key = $"{item.ItemId}-{item.Quality}";

                bool done = item.Acquired >= item.Quantity;
                int remaining = Math.Max(
                    0, item.Quantity - item.Acquired);

                string quality = item.Quality switch
                {
                    2 => " [HQ]",
                    1 => " [NQ]",
                    _ => ""
                };

                string row =
                    $"{(done ? "[x]" : "[ ]")} " +
                    $"{item.Name}{quality}  " +
                    $"{remaining:N0} left";

                bool selected = selectedItemKey == key;

                if (ImGui.Selectable(
                    row + "##" + key, selected))
                    selectedItemKey = key;
            }
        }

        ImGui.EndChild();

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

        ImGui.Spacing();

        ImGui.TextColored(
            Theme.Get(cfg, 4),
            "SELECTED MATERIAL");

        ImGui.TextWrapped(selectedItem.Name);

        int needed = selectedItem.Quantity;
        int acquired = selectedItem.Acquired;

        ImGui.SetNextItemWidth(92f);

        if (ImGui.InputInt(
            "Needed##Selected", ref needed, 0, 0))
        {
            selectedItem.Quantity =
                Math.Clamp(needed, 1, 999999);

            selectedItem.Acquired = Math.Min(
                selectedItem.Acquired, selectedItem.Quantity);

            Save();
        }

        ImGui.SameLine();

        ImGui.SetNextItemWidth(92f);

        if (ImGui.InputInt(
            "Acquired##Selected", ref acquired, 0, 0))
        {
            selectedItem.Acquired = Math.Clamp(
                acquired, 0, selectedItem.Quantity);

            Save();
        }

        bool doneNow =
            selectedItem.Acquired >= selectedItem.Quantity;

        if (ImGui.Button(doneNow ? "Reset" : "Complete"))
        {
            selectedItem.Acquired =
                doneNow ? 0 : selectedItem.Quantity;

            Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("Remove selected"))
        {
            list.Items.Remove(selectedItem);
            selectedItemKey = "";
            Save();
        }
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

                ImGui.TextDisabled("MarketRun v0.1.0 DEV1 FIX1");

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