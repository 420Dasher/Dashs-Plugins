using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SpotifyTrackHonorific.Honorific;
using SpotifyTrackHonorific.Formatting;
using SpotifyTrackHonorific.Filtering;
using SpotifyTrackHonorific.Profiles;
using SpotifyTrackHonorific.Settings;
using SpotifyTrackHonorific.Spotify;
using SpotifyTrackHonorific.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyTrackHonorific;

public sealed class Plugin : IDalamudPlugin
{
    internal const string DisplayVersion = "1.0.19-dev1";
    private const string ShortCommand = "/sth";
    private const string LongCommand = "/spotifytrackhonorific";
    private static readonly TimeSpan NormalPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LocalRenderRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CharacterSelectCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CharacterSelectSettleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CharacterSelectCaptureRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CharacterSelectCaptureWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TerritoryRebindSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TerritoryRebindRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TerritoryRebindWindow = TimeSpan.FromSeconds(10);
    private const int FailureBackoffBaseSeconds = 5;
    private const int FailureBackoffMaxSeconds = 120;
    private const int RateLimitFallbackSeconds = 30;
    private const int QuotaFallbackBaseSeconds = 3600;
    private const int QuotaFallbackMaxSeconds = 43200;
    private static readonly JsonSerializerOptions PortableSettingsJsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;

    private readonly Configuration config;
    private readonly SpotifyApiService spotify;
    private readonly HonorificBridge honorific;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string> characterSelectGetCurrentCharacter;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, string, object> characterSelectCharacterChanged;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly WindowSystem windowSystem = new("SpotifyTrackHonorific");
    private readonly ConfigWindow configWindow;

    private long nextPollUtcTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
    private int pollInProgress;
    private int authenticationInProgress;
    private string? appliedFingerprint;
    private bool hasAppliedTitle;
    private SpotifyTrackInfo? lastTrack;
    private bool lastTrackPaused;
    private long lastTrackObservedUtcTicks;
    private long nextLocalRenderUtcTicks;

    // Continuous Spotify listening streak observed by STH.
    // Starts on PlayingTrack and resets only when Spotify explicitly reports
    // paused/stopped, authorization disappears, STH is disabled, or the
    // character/plugin session resets. Temporary API failures do not break it.
    private long listeningSessionStartedUtcTicks;

    private string lastState = "Starting";
    private string? lastError;
    private bool honorificErrorShown;
    private int consecutivePollFailures;
    private long lastSuccessfulPollUtcTicks;
    private long spotifyCooldownUntilUtcTicks;
    private string? lastLoggedSpotifyError;
    private bool combatAutoHideActive;
    private bool originalHonorificCaptureFinished;
    private long nextOriginalHonorificCaptureAttemptUtcTicks;
    private string? lastAppliedTitle;

    // Last genuine Spotify title successfully handed to Honorific.
    // Unlike hasAppliedTitle/lastAppliedTitle, this survives temporary
    // relinquishes such as combat auto-hide, zoning, CS+ hand-offs,
    // and "keep previous title" filter states.
    private string? retainedSpotifyTitle;
    private string? retainedSpotifyFingerprint;

    private bool patMeHonorificOverrideActive;
    private bool suppressHonorificTitleChanged;
    private string? lastCharacterSelectProfile;
    private bool characterSelectProfileKnown;
    private long nextCharacterSelectCheckUtcTicks;
    private long pendingCharacterSelectCaptureUtcTicks;
    private long characterSelectCaptureDeadlineUtcTicks;
    private string characterSelectPreviousHonorificTitle = string.Empty;
    private string characterSelectPreviousHonorificStyleFingerprint = string.Empty;
    private bool characterSelectRecaptureActive;
    private int characterSelectEventCount;
    private int characterSelectPollChangeCount;
    private string characterSelectLastSignal = "none";
    private string? characterSelectLastIpcError;
    private string? characterSelectSessionInfoPath;
    private string? lastCharacterSelectSessionProfile;
    private bool characterSelectSessionProfileKnown;
    private long lastCharacterSelectSessionWriteUtcTicks;
    private int characterSelectSessionChangeCount;
    private bool territoryRebindActive;
    private long pendingTerritoryRebindUtcTicks;
    private long territoryRebindDeadlineUtcTicks;

    internal Configuration Config => config;
    internal bool IsAuthenticated => spotify.HasRefreshToken;
    internal bool IsAuthenticating => Volatile.Read(ref authenticationInProgress) != 0;
    internal string StateText => lastState;
    internal string? ErrorText => lastError;
    internal string ReliabilityText => BuildReliabilityText();
    internal string PreviewTitle => BuildPreviewTitle();
    internal string PreviewExpandedTitle => BuildPreviewExpandedTitle();
    internal bool PreviewUsesCurrentTrack => lastTrack != null;
    internal IReadOnlyList<TitleProfile> SavedTitleProfiles => config.TitleProfiles;
    internal string RedirectUriText => spotify.RedirectUriText;
    internal string NowPlayingText => lastTrack == null
        ? (IsAuthenticated ? "Waiting for music" : "Spotify not connected")
        : $"{lastTrack.ArtistText} - {lastTrack.Name}";
    internal bool HonorificDetected => HonorificGradientCatalog.GetSnapshot().HonorificTypesFound;
    internal string SpotifyFriendlyStatus => BuildSpotifyFriendlyStatus();
    internal string CachedHonorificTitle => config.CachedHonorificTitle;
    internal bool PatMeHonorificYieldActive => patMeHonorificOverrideActive;
    internal string ActiveProfileName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(config.ActiveTitleProfileName))
            {
                foreach (var profile in config.TitleProfiles)
                {
                    if (string.Equals(
                        profile.Name,
                        config.ActiveTitleProfileName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return profile.Name;
                    }
                }
            }

            foreach (var profile in config.TitleProfiles)
            {
                if (profile.Matches(config))
                    return profile.Name;
            }

            return "Custom";
        }
    }

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (config.EnsureDefaults())
        {
            ReconcileActiveTitleProfile();
            SaveConfig();
        }

        spotify = new SpotifyApiService(config, SaveConfig);
        honorific = new HonorificBridge(PluginInterface);
        honorific.LocalTitleChanged += OnHonorificLocalTitleChanged;
        characterSelectGetCurrentCharacter = PluginInterface.GetIpcSubscriber<string>("CharacterSelect.GetCurrentCharacter");
        characterSelectCharacterChanged = PluginInterface.GetIpcSubscriber<string, string, object>("CharacterSelect.OnCharacterChanged");
        characterSelectCharacterChanged.Subscribe(OnCharacterSelectChanged);

        try
        {
            var pluginConfigDirectory = PluginInterface.GetPluginConfigDirectory();
            var pluginConfigsRoot = Directory.GetParent(pluginConfigDirectory)?.FullName;

            if (!string.IsNullOrWhiteSpace(pluginConfigsRoot))
            {
                characterSelectSessionInfoPath = Path.Combine(
                    pluginConfigsRoot,
                    "CharacterSelectPlugin",
                    "session_info.txt");

                Log.Information(
                    $"Character Select+ session fallback path: {characterSelectSessionInfoPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not initialize Character Select+ session fallback path: {ex.Message}");
        }
        originalHonorificCaptureFinished = !string.IsNullOrWhiteSpace(config.CachedHonorificTitle);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open SpotifyTrackHonorific settings. Use '/sth help' for commands."
        };
        CommandManager.AddHandler(ShortCommand, commandInfo);
        CommandManager.AddHandler(LongCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = commandInfo.HelpMessage
        });

        Framework.Update += OnFrameworkUpdate;
        Condition.ConditionChange += OnConditionChange;
        ClientState.Login += OnClientLogin;
        ClientState.Logout += OnClientLogout;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        // A dev-plugin reload can happen while already logged in, so treat the
        // current character as a fresh session too.
        if (ClientState.IsLoggedIn)
            ResetForCharacterSessionStart();

        ChatGui.Print($"SpotifyTrackHonorific v{DisplayVersion} loaded. Use /sth to open settings.");
    }

    public void Dispose()
    {
        lifetimeCts.Cancel();

        Framework.Update -= OnFrameworkUpdate;
        Condition.ConditionChange -= OnConditionChange;
        ClientState.Login -= OnClientLogin;
        ClientState.Logout -= OnClientLogout;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

        CommandManager.RemoveHandler(ShortCommand);
        CommandManager.RemoveHandler(LongCommand);
        windowSystem.RemoveAllWindows();
        characterSelectCharacterChanged.Unsubscribe(OnCharacterSelectChanged);
        honorific.LocalTitleChanged -= OnHonorificLocalTitleChanged;
        TryClearHonorific();
        honorific.Dispose();
        spotify.Dispose();
        lifetimeCts.Dispose();
    }

    private void OpenConfigUi() => configWindow.IsOpen = true;

    internal void SettingsChanged()
    {
        config.EnsureDefaults();
        ReconcileActiveTitleProfile();
        SaveConfig();
        SchedulePollNow();
        appliedFingerprint = null;
        lastAppliedTitle = null;

        if (!config.EnablePatMeHonorificSupport)
            patMeHonorificOverrideActive = false;

        if (!config.Enabled)
        {
            patMeHonorificOverrideActive = false;
            combatAutoHideActive = false;
            ResetListeningSession();
            var honorificCleared = TryClearHonorific();

            // A cache clear performed while STH owned the title intentionally
            // pauses capture. Once STH is disabled and its IPC title is safely
            // cleared, re-arm capture so the revealed Honorific title can be
            // picked up automatically.
            if (honorificCleared &&
                string.IsNullOrWhiteSpace(config.CachedHonorificTitle))
            {
                originalHonorificCaptureFinished = false;
                Interlocked.Exchange(ref nextOriginalHonorificCaptureAttemptUtcTicks, 0);
            }

            return;
        }

        if (ShouldAutoHideForCombat())
        {
            combatAutoHideActive = true;
            TryClearHonorific(preserveRetainedTitle: true);
            return;
        }

        combatAutoHideActive = false;
        var renderTrack = GetCurrentRenderTrack();
        if (renderTrack != null)
        {
            if (lastTrackPaused && config.ClearOnPause)
            {
                TryClearHonorific();
            }
            else if (IsTrackAllowed(renderTrack))
            {
                var filterMatch = GetContentFilterMatch(renderTrack);
                if (filterMatch != null && config.ContentFilterAction == 1)
                {
                    lastState = $"Triggerword censored ({filterMatch.Field})";
                    TryClearHonorific();
                }
                else if (filterMatch != null && config.ContentFilterAction == 2)
                {
                    lastState = $"Triggerword censored ({filterMatch.Field}) - previous title kept";
                }
                else
                {
                    if (filterMatch != null)
                        lastState = $"Triggerword censored ({filterMatch.Field})";
                    ApplyHonorificTitle(BuildConfiguredTitle(renderTrack, lastTrackPaused), BuildRenderFingerprint(renderTrack, lastTrackPaused));
                }
            }
            else
            {
                TryClearHonorific();
            }
            return;
        }

        if (config.ClearOnPause && hasAppliedTitle && lastState == "Nothing playing / paused")
            TryClearHonorific();
    }

    private void ReconcileActiveTitleProfile()
    {
        if (!string.IsNullOrWhiteSpace(config.ActiveTitleProfileName))
        {
            foreach (var profile in config.TitleProfiles)
            {
                if (!string.Equals(
                    profile.Name,
                    config.ActiveTitleProfileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (profile.Matches(config))
                {
                    config.ActiveTitleProfileName = profile.Name;
                    return;
                }

                break;
            }
        }

        foreach (var profile in config.TitleProfiles)
        {
            if (!profile.Matches(config))
                continue;

            config.ActiveTitleProfileName = profile.Name;
            return;
        }

        config.ActiveTitleProfileName = string.Empty;
    }
    internal void StartAuthentication(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            ChatGui.PrintError("Enter your Spotify Client ID first.", "SpotifyTrackHonorific");
            return;
        }

        // Atomic gate prevents accidental double-clicks from starting two callback
        // listeners/token exchanges at once.
        if (Interlocked.CompareExchange(ref authenticationInProgress, 1, 0) != 0)
            return;

        _ = AuthenticateAsync(clientId);
    }

    internal void RetrySpotifyNow()
    {
        if (TryGetSpotifyCooldownRemaining(out var remaining))
        {
            lastState = $"Spotify cooldown active - retry in {FormatRetryDelay(remaining)}";
            ChatGui.Print($"Spotify asked us to wait. Retry is available in {FormatRetryDelay(remaining)}.");
            return;
        }

        Interlocked.Exchange(ref consecutivePollFailures, 0);
        lastLoggedSpotifyError = null;
        lastState = "Manual Spotify retry requested";
        SchedulePollNow();
    }

    internal void TestHonorificTitle()
    {
        ApplyHonorificTitle(
            "♪ Honorific test",
            "manual-ui-test",
            preserveProvidedTitle: true,
            rememberForRecovery: false);

        SchedulePollNow();
    }

    internal void ClearPluginTitle() => TryClearHonorific();

    internal string BuildDiagnosticsText()
    {
        var track = lastTrack;
        var trackSource = track == null
            ? "none"
            : track.IsLocal
                ? "local file"
                : "Spotify";

        var lines = new[]
        {
            "SpotifyTrackHonorific diagnostics",
            $"Version: {DisplayVersion}",
            $"Enabled: {config.Enabled}",
            $"Spotify authenticated: {IsAuthenticated}",
            $"Spotify status: {SpotifyFriendlyStatus}",
            $"State: {lastState}",
            $"Reliability: {BuildReliabilityText()}",
            $"Honorific detected: {HonorificDetected}",
            $"Track cached: {track != null}",
            $"Playback paused: {lastTrackPaused}",
            $"Track source: {trackSource}",
            $"Title position: {(config.IsPrefix ? "prefix" : "suffix")}",
            $"Show normal tracks: {config.ShowNormalTracks}",
            $"Show local tracks: {config.ShowLocalTracks}",
            $"Clear on pause: {config.ClearOnPause}",
            $"Auto-hide in combat: {config.AutoHideInCombat}",
            $"Currently in combat: {Condition[ConditionFlag.InCombat]}",
            $"Cached original Honorific title: {!string.IsNullOrWhiteSpace(config.CachedHonorificTitle)}",
            $"Match Character Select+ Honorific style: {config.MatchCharacterSelectHonorificStyle}",
            $"Cached underlying Honorific style: {config.CachedHonorificStyleCaptured}",
            $"PatMeHonorific support: {config.EnablePatMeHonorificSupport}",
            $"Yielding to temporary Honorific title: {patMeHonorificOverrideActive}",
            $"Smart-fit long titles: {config.SmartFitLongTitles}",
            $"Strip bracketed extras: {config.StripBracketedTrackParts}",
            $"Content filter enabled: {config.EnableContentFilter}",
            $"Smart content-filter matching: {config.SmartContentFilterMatching}",
            $"Content filter action: {config.ContentFilterAction}",
            $"Supporter gradient enabled: {config.HonorificSupporterConfirmed && config.UseSupporterGradient}",
            $"Last error present: {!string.IsNullOrWhiteSpace(lastError)}",
            "Privacy: Client ID, OAuth tokens, track names and artist names are intentionally excluded."
        };

        return string.Join(Environment.NewLine, lines);
    }

    internal string TestContentFilterText(string text, int testFieldIndex)
    {
        ContentFilterMatch? match;

        if (testFieldIndex <= 0)
        {
            match = ContentFilterMatcher.TestText(
                config.ContentFilterEntries,
                config.UseBuiltInContentFilterList,
                config.DisabledBuiltInContentFilterEntries,
                config.SmartContentFilterMatching,
                text);
        }
        else
        {
            var track = testFieldIndex switch
            {
                1 => new SpotifyTrackInfo(
                    string.Empty,
                    new[] { text },
                    string.Empty,
                    0,
                    0,
                    false,
                    "content-filter-test-artist"),

                2 => new SpotifyTrackInfo(
                    text,
                    new[] { string.Empty },
                    string.Empty,
                    0,
                    0,
                    false,
                    "content-filter-test-track"),

                3 => new SpotifyTrackInfo(
                    string.Empty,
                    new[] { string.Empty },
                    text,
                    0,
                    0,
                    false,
                    "content-filter-test-album"),

                _ => new SpotifyTrackInfo(
                    text,
                    new[] { text },
                    text,
                    0,
                    0,
                    false,
                    "content-filter-test-all"),
            };

            match = ContentFilterMatcher.MatchTrack(
                track,
                config.ContentFilterEntries,
                config.UseBuiltInContentFilterList,
                config.DisabledBuiltInContentFilterEntries,
                config.SmartContentFilterMatching);
        }

        if (match == null)
            return "No blacklist match.";

        var variation = match.UsedFuzzyMatch ? " - smart variation match" : string.Empty;
        var source = match.IsBuiltIn ? "built-in: " : string.Empty;
        return $"Blocked by {source}{match.Entry} ({match.Field}){variation}";
    }

    internal bool SaveTitleProfile(string name, out int profileIndex, out string message)
    {
        profileIndex = -1;
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "Enter a profile name first.";
            return false;
        }

        if (name.Length > 48)
            name = name[..48];

        for (var i = 0; i < config.TitleProfiles.Count; i++)
        {
            if (!string.Equals(config.TitleProfiles[i].Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            config.TitleProfiles[i] = TitleProfile.Capture(config, name);
            config.ActiveTitleProfileName = name;
            SaveConfig();
            profileIndex = i;
            message = $"Updated profile '{name}'.";
            return true;
        }

        if (config.TitleProfiles.Count >= Configuration.MaxTitleProfiles)
        {
            message = $"You can save up to {Configuration.MaxTitleProfiles} profiles. Delete one first.";
            return false;
        }

        config.TitleProfiles.Add(TitleProfile.Capture(config, name));
        config.ActiveTitleProfileName = name;
        SaveConfig();
        profileIndex = config.TitleProfiles.Count - 1;
        message = $"Saved profile '{name}'.";
        return true;
    }

    internal bool LoadTitleProfile(int profileIndex, out string message)
    {
        if (profileIndex < 0 || profileIndex >= config.TitleProfiles.Count)
        {
            message = "Choose a saved profile first.";
            return false;
        }

        var profile = config.TitleProfiles[profileIndex];
        var requestedSupporterGradient = profile.UseSupporterGradient;
        profile.ApplyTo(config);
        config.ActiveTitleProfileName = profile.Name;
        SettingsChanged();

        message = requestedSupporterGradient && !config.HonorificSupporterConfirmed
            ? $"Loaded '{profile.Name}'. Supporter gradient stayed disabled until supporter access is confirmed."
            : $"Loaded profile '{profile.Name}'.";
        return true;
    }

    internal bool DeleteTitleProfile(int profileIndex, out string message)
    {
        if (profileIndex < 0 || profileIndex >= config.TitleProfiles.Count)
        {
            message = "Choose a saved profile first.";
            return false;
        }

        var name = config.TitleProfiles[profileIndex].Name;
        config.TitleProfiles.RemoveAt(profileIndex);

        if (string.Equals(
            config.ActiveTitleProfileName,
            name,
            StringComparison.OrdinalIgnoreCase))
        {
            config.ActiveTitleProfileName = string.Empty;
        }

        ReconcileActiveTitleProfile();
        SaveConfig();
        message = $"Deleted profile '{name}'.";
        return true;
    }

    internal bool UpdateTitleProfile(int profileIndex, out string message)
    {
        if (profileIndex < 0 || profileIndex >= config.TitleProfiles.Count)
        {
            message = "Choose a saved profile first.";
            return false;
        }

        var name = config.TitleProfiles[profileIndex].Name;
        config.TitleProfiles[profileIndex] = TitleProfile.Capture(config, name);
        config.ActiveTitleProfileName = name;
        SaveConfig();
        message = $"Updated profile '{name}' from the current settings.";
        return true;
    }

    internal bool RenameTitleProfile(int profileIndex, string newName, out string message)
    {
        if (profileIndex < 0 || profileIndex >= config.TitleProfiles.Count)
        {
            message = "Choose a saved profile first.";
            return false;
        }

        newName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            message = "Enter a profile name first.";
            return false;
        }

        if (newName.Length > 48)
            newName = newName[..48];

        for (var i = 0; i < config.TitleProfiles.Count; i++)
        {
            if (i == profileIndex)
                continue;

            if (string.Equals(config.TitleProfiles[i].Name, newName, StringComparison.OrdinalIgnoreCase))
            {
                message = $"A profile named '{newName}' already exists.";
                return false;
            }
        }

        var oldName = config.TitleProfiles[profileIndex].Name;
        config.TitleProfiles[profileIndex].Name = newName;

        if (string.Equals(
            config.ActiveTitleProfileName,
            oldName,
            StringComparison.OrdinalIgnoreCase))
        {
            config.ActiveTitleProfileName = newName;
        }

        SaveConfig();
        message = string.Equals(oldName, newName, StringComparison.Ordinal)
            ? $"Profile name is already '{newName}'."
            : $"Renamed profile '{oldName}' to '{newName}'.";
        return true;
    }

    internal bool DuplicateTitleProfile(int profileIndex, out int duplicateIndex, out string message)
    {
        duplicateIndex = -1;

        if (profileIndex < 0 || profileIndex >= config.TitleProfiles.Count)
        {
            message = "Choose a saved profile first.";
            return false;
        }

        if (config.TitleProfiles.Count >= Configuration.MaxTitleProfiles)
        {
            message = $"You can save up to {Configuration.MaxTitleProfiles} profiles. Delete one first.";
            return false;
        }

        var source = config.TitleProfiles[profileIndex];
        var copyNumber = 1;
        string copyName;

        while (true)
        {
            var suffix = copyNumber == 1 ? " Copy" : $" Copy {copyNumber}";
            var maxBaseLength = Math.Max(1, 48 - suffix.Length);
            var baseName = source.Name.Length > maxBaseLength
                ? source.Name[..maxBaseLength]
                : source.Name;
            copyName = baseName + suffix;

            var exists = false;
            for (var i = 0; i < config.TitleProfiles.Count; i++)
            {
                if (!string.Equals(config.TitleProfiles[i].Name, copyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                exists = true;
                break;
            }

            if (!exists)
                break;

            copyNumber++;
        }

        var copy = source.Clone();
        copy.Name = copyName;
        config.TitleProfiles.Insert(profileIndex + 1, copy);
        SaveConfig();

        duplicateIndex = profileIndex + 1;
        message = $"Duplicated profile '{source.Name}' as '{copyName}'.";
        return true;
    }

    internal bool MoveTitleProfile(int profileIndex, int direction, out int newIndex, out string message)
    {
        newIndex = profileIndex;

        if (profileIndex < 0 || profileIndex >= config.TitleProfiles.Count)
        {
            message = "Choose a saved profile first.";
            return false;
        }

        if (direction != -1 && direction != 1)
        {
            message = "Profile move direction is invalid.";
            return false;
        }

        var targetIndex = profileIndex + direction;
        if (targetIndex < 0 || targetIndex >= config.TitleProfiles.Count)
        {
            message = direction < 0
                ? "That profile is already first."
                : "That profile is already last.";
            return false;
        }

        var profile = config.TitleProfiles[profileIndex];
        config.TitleProfiles.RemoveAt(profileIndex);
        config.TitleProfiles.Insert(targetIndex, profile);
        SaveConfig();

        newIndex = targetIndex;
        message = $"Moved profile '{profile.Name}' {(direction < 0 ? "up" : "down")}.";
        return true;
    }
    private static string FormatStyleVector(Vector3? value)
    {
        if (!value.HasValue)
            return "null";

        var v = value.Value;

        return $"{v.X:F4},{v.Y:F4},{v.Z:F4}";
    }

    private static string BuildHonorificStyleFingerprint(
        HonorificTitleState state) =>
        $"color:{FormatStyleVector(state.Color)}" +
        $"|glow:{FormatStyleVector(state.Glow)}" +
        $"|color3:{FormatStyleVector(state.Color3)}" +
        $"|gradient:{state.GradientColourSet?.ToString() ?? "null"}" +
        $"|animation:{state.GradientAnimationStyle?.ToString() ?? "null"}";

    private string BuildCachedHonorificStyleFingerprint()
    {
        if (!config.CachedHonorificStyleCaptured)
            return "none";

        return
            $"color:{FormatStyleVector(config.CachedHonorificColor)}" +
            $"|glow:{FormatStyleVector(config.CachedHonorificGlow)}" +
            $"|color3:{FormatStyleVector(config.CachedHonorificColor3)}" +
            $"|gradient:{config.CachedHonorificGradientColourSet?.ToString() ?? "null"}" +
            $"|animation:{config.CachedHonorificGradientAnimationStyle?.ToString() ?? "null"}";
    }

    private bool CacheHonorificStyle(HonorificTitleState state)
    {
        var changed =
            !config.CachedHonorificStyleCaptured ||
            !Nullable.Equals(config.CachedHonorificColor, state.Color) ||
            !Nullable.Equals(config.CachedHonorificGlow, state.Glow) ||
            !Nullable.Equals(config.CachedHonorificColor3, state.Color3) ||
            config.CachedHonorificGradientColourSet != state.GradientColourSet ||
            config.CachedHonorificGradientAnimationStyle != state.GradientAnimationStyle;

        config.CachedHonorificStyleCaptured = true;
        config.CachedHonorificColor = state.Color;
        config.CachedHonorificGlow = state.Glow;
        config.CachedHonorificColor3 = state.Color3;
        config.CachedHonorificGradientColourSet = state.GradientColourSet;
        config.CachedHonorificGradientAnimationStyle =
            state.GradientAnimationStyle;

        return changed;
    }

    private bool ClearCachedHonorificStyle()
    {
        var changed =
            config.CachedHonorificStyleCaptured ||
            config.CachedHonorificColor.HasValue ||
            config.CachedHonorificGlow.HasValue ||
            config.CachedHonorificColor3.HasValue ||
            config.CachedHonorificGradientColourSet.HasValue ||
            config.CachedHonorificGradientAnimationStyle.HasValue;

        config.CachedHonorificStyleCaptured = false;
        config.CachedHonorificColor = null;
        config.CachedHonorificGlow = null;
        config.CachedHonorificColor3 = null;
        config.CachedHonorificGradientColourSet = null;
        config.CachedHonorificGradientAnimationStyle = null;

        return changed;
    }

    internal bool CacheCurrentHonorificTitle(out string message)
    {
        try
        {
            if (!honorific.TryGetCurrentTitleState(out var currentState))
            {
                message = "Honorific has no current title to cache.";
                return false;
            }

            var currentTitle = currentState.Title;

            if (hasAppliedTitle &&
                !string.IsNullOrWhiteSpace(lastAppliedTitle) &&
                string.Equals(currentTitle, lastAppliedTitle, StringComparison.Ordinal))
            {
                message = "That is STH's own active Spotify title. Disable STH, set the Honorific title you want, then cache it.";
                return false;
            }

            config.CachedHonorificTitle = currentTitle;
            CacheHonorificStyle(currentState);
            originalHonorificCaptureFinished = true;
            SettingsChanged();
            message = $"Cached Honorific title and style: {currentTitle}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Honorific.GetCharacterTitle IPC failed while manually caching: {ex.Message}");
            message = "Could not read the current Honorific title. Make sure Honorific is installed and enabled.";
            return false;
        }
    }

    internal void ClearCachedHonorificTitle(out string message)
    {
        config.CachedHonorificTitle = string.Empty;
        ClearCachedHonorificStyle();

        // If STH currently owns the visible title, keep auto-capture blocked so
        // we cannot immediately cache our own Spotify output. Once STH has been
        // disabled/cleared, hasAppliedTitle is false and auto-capture may resume.
        originalHonorificCaptureFinished = hasAppliedTitle;

        // Allow the framework retry loop to try immediately when capture is safe.
        Interlocked.Exchange(ref nextOriginalHonorificCaptureAttemptUtcTicks, 0);

        SettingsChanged();
        message = hasAppliedTitle
            ? "Cached Honorific title cleared. Auto-capture is paused while STH owns the active title."
            : "Cached Honorific title cleared. Auto-capture is ready.";
    }

    internal string ExportPortableSettings()
    {
        var package = new PortableSettingsPackage
        {
            ExportedFromVersion = DisplayVersion,
            AutoHideInCombat = config.AutoHideInCombat,
            CurrentSettings = TitleProfile.Capture(config, "Current settings"),
            Profiles = new List<TitleProfile>(),
        };

        foreach (var profile in config.TitleProfiles)
            package.Profiles.Add(profile.Clone());

        return JsonSerializer.Serialize(package, PortableSettingsJsonOptions);
    }

    internal bool ImportPortableSettings(string json, out string message)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            message = "Clipboard does not contain portable SpotifyTrackHonorific settings.";
            return false;
        }

        try
        {
            var package = JsonSerializer.Deserialize<PortableSettingsPackage>(json, PortableSettingsJsonOptions);
            if (package == null ||
                !string.Equals(package.Schema, PortableSettingsPackage.ExpectedSchema, StringComparison.Ordinal) ||
                package.FormatVersion <= 0 ||
                package.FormatVersion > PortableSettingsPackage.CurrentFormatVersion ||
                package.CurrentSettings == null)
            {
                message = "Clipboard data is not a supported SpotifyTrackHonorific settings export.";
                return false;
            }

            var importedCurrent = package.CurrentSettings.Clone();
            importedCurrent.EnsureDefaults(0);
            importedCurrent.ApplyTo(config);

            config.TitleProfiles.Clear();
            if (package.Profiles != null)
            {
                foreach (var importedProfile in package.Profiles)
                {
                    if (importedProfile == null || config.TitleProfiles.Count >= Configuration.MaxTitleProfiles)
                        continue;

                    var profile = importedProfile.Clone();
                    profile.EnsureDefaults(config.TitleProfiles.Count);
                    config.TitleProfiles.Add(profile);
                }
            }

            // v1 exports from SpotifyTrackHonorific 1.0.5 remain valid and leave
            // the current combat-visibility preference untouched.
            if (package.FormatVersion >= 2)
                config.AutoHideInCombat = package.AutoHideInCombat;

            // The imported profile set replaces the previous local profile identities.
            // SettingsChanged will infer a saved profile if the imported current
            // settings exactly match one; otherwise the result remains Custom.
            config.ActiveTitleProfileName = string.Empty;

            // SettingsChanged validates/saves the imported data and refreshes the
            // currently applied title. Spotify credentials, onboarding, global enable
            // state and supporter entitlement confirmation are not part of the export.
            SettingsChanged();
            message = $"Imported display settings and {config.TitleProfiles.Count} saved profile(s). Spotify connection data was unchanged.";
            return true;
        }
        catch (JsonException ex)
        {
            message = $"Could not import settings: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Portable settings import failed");
            message = $"Could not import settings: {ex.Message}";
            return false;
        }
    }

    internal void ForgetSpotifyConnection(bool clearClientId = false)
    {
        spotify.ForgetAuthorization(clearClientId);
        ResetFailureCounter();
        ClearSpotifyCooldown();
        ResetListeningSession();
        lastTrack = null;
        lastTrackPaused = false;
        lastTrackObservedUtcTicks = 0;
        Interlocked.Exchange(ref nextLocalRenderUtcTicks, 0);
        lastError = null;
        lastState = "Spotify connection removed";
        appliedFingerprint = null;
        TryClearHonorific();
        ScheduleNextPoll(IdlePollInterval);
        configWindow.SyncClientId();
    }

    internal void ResetDisplaySettings()
    {
        var defaults = new Configuration();

        config.Enabled = defaults.Enabled;
        config.ShowNormalTracks = defaults.ShowNormalTracks;
        config.ShowLocalTracks = defaults.ShowLocalTracks;
        config.ClearOnPause = defaults.ClearOnPause;
        config.AutoHideInCombat = defaults.AutoHideInCombat;
        config.MatchCharacterSelectHonorificStyle =
            defaults.MatchCharacterSelectHonorificStyle;
        config.IsPrefix = defaults.IsPrefix;
        config.TitleFormat = defaults.TitleFormat;
        config.StripBracketedTrackParts = defaults.StripBracketedTrackParts;
        config.SmartFitLongTitles = defaults.SmartFitLongTitles;

        config.UseTitleColor = defaults.UseTitleColor;
        config.TitleColor = defaults.TitleColor;
        config.UseTitleGlow = defaults.UseTitleGlow;
        config.TitleGlowColor = defaults.TitleGlowColor;
        config.UseSupporterGradient = defaults.UseSupporterGradient;
        config.UseCustomDualGradient = defaults.UseCustomDualGradient;
        config.GradientColourSet = defaults.GradientColourSet;
        config.AnimateGradient = defaults.AnimateGradient;
        config.GradientAnimationStyle = defaults.GradientAnimationStyle;
        config.GradientColorA = defaults.GradientColorA;
        config.GradientColorB = defaults.GradientColorB;
        config.GradientColorC = defaults.GradientColorC;

        // Spotify credentials, first-run completion, and the user's explicit
        // supporter-entitlement confirmation are intentionally preserved.
        SettingsChanged();
    }

    private bool ShouldAutoHideForCombat() =>
        config.Enabled &&
        config.AutoHideInCombat &&
        Condition[ConditionFlag.InCombat];

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat)
            return;

        if (!config.Enabled || !config.AutoHideInCombat)
        {
            combatAutoHideActive = false;
            return;
        }

        if (value)
        {
            combatAutoHideActive = true;
            TryClearHonorific(preserveRetainedTitle: true);
            return;
        }

        if (!combatAutoHideActive)
            return;

        combatAutoHideActive = false;
        appliedFingerprint = null;
        RestoreCurrentSpotifyTitle();
    }

    private void RestoreCurrentSpotifyTitle()
    {
        if (characterSelectRecaptureActive)
            return;

        if (!config.Enabled || ShouldAutoHideForCombat())
            return;

        var renderTrack = GetCurrentRenderTrack();

        // When Spotify reports nothing playing and the user explicitly chose to
        // keep the title while stopped, there is no current track from which to
        // rebuild the title. Restore the last successfully rendered Spotify title.
        if (renderTrack == null)
        {
            if (!config.ClearOnPause &&
                string.Equals(
                    lastState,
                    "Nothing playing / paused",
                    StringComparison.Ordinal))
            {
                TryRestoreRetainedSpotifyTitle();
            }

            return;
        }

        if (lastTrackPaused && config.ClearOnPause)
            return;

        if (!IsTrackAllowed(renderTrack))
            return;

        var filterMatch = GetContentFilterMatch(renderTrack);

        if (filterMatch != null)
        {
            // "Keep previous title" deliberately retains the last safe rendered
            // title instead of rendering metadata from this blocked track.
            if (config.ContentFilterAction == 2)
                TryRestoreRetainedSpotifyTitle();

            if (config.ContentFilterAction != 0)
                return;
        }

        var paused = lastTrackPaused;
        var fingerprint = BuildRenderFingerprint(renderTrack, paused);
        ApplyHonorificTitle(BuildConfiguredTitle(renderTrack, paused), fingerprint);
    }

    private void OnClientLogin()
    {
        ResetForCharacterSessionStart();
    }

    private void OnClientLogout(int type, int code)
    {
        // Clear our assignment while the outgoing local player still exists,
        // then forget all ownership/fingerprint state tied to that character.
        if (hasAppliedTitle)
            TryClearHonorific();

        ResetCharacterRuntimeState();
        Log.Information("Character logout detected; STH character-specific title state reset.");
    }

    private void ResetForCharacterSessionStart()
    {
        ResetCharacterRuntimeState();

        // The cached normal Honorific title belongs to the selected character.
        // Drop the previous character's cache and recapture before STH writes.
        if (!string.IsNullOrWhiteSpace(config.CachedHonorificTitle) ||
            config.CachedHonorificStyleCaptured)
        {
            config.CachedHonorificTitle = string.Empty;
            ClearCachedHonorificStyle();
            SaveConfig();
        }

        // Login fires when the local player object exists. Try immediately;
        // if Honorific is slightly later, the existing once-per-second retry
        // path in TryAutoCaptureOriginalHonorificTitle keeps trying.
        TryCaptureOriginalHonorificTitleBeforeFirstWrite();

        // Force a fresh Spotify render for the new local character. Keeping the
        // previous lastTrack would let local progress refresh write too early.
        ScheduleNextPoll(TimeSpan.FromSeconds(2));

        Log.Information("Character login detected; refreshed STH state for the selected character.");
    }

    private void ResetCharacterRuntimeState()
    {
        appliedFingerprint = null;
        hasAppliedTitle = false;
        lastAppliedTitle = null;
        retainedSpotifyTitle = null;
        retainedSpotifyFingerprint = null;
        patMeHonorificOverrideActive = false;
        combatAutoHideActive = false;

        lastTrack = null;
        lastTrackPaused = false;
        lastTrackObservedUtcTicks = 0;
        Interlocked.Exchange(ref nextLocalRenderUtcTicks, 0);
        ResetListeningSession();

        originalHonorificCaptureFinished = false;
        Interlocked.Exchange(ref nextOriginalHonorificCaptureAttemptUtcTicks, 0);

        lastCharacterSelectProfile = null;
        characterSelectProfileKnown = false;
        Interlocked.Exchange(ref nextCharacterSelectCheckUtcTicks, 0);
        Interlocked.Exchange(ref pendingCharacterSelectCaptureUtcTicks, 0);
        Interlocked.Exchange(ref characterSelectCaptureDeadlineUtcTicks, 0);
        characterSelectPreviousHonorificTitle = string.Empty;
        characterSelectPreviousHonorificStyleFingerprint = string.Empty;
        characterSelectRecaptureActive = false;
        characterSelectEventCount = 0;
        characterSelectPollChangeCount = 0;
        characterSelectLastSignal = "none";
        characterSelectLastIpcError = null;
        lastCharacterSelectSessionProfile = null;
        characterSelectSessionProfileKnown = false;
        lastCharacterSelectSessionWriteUtcTicks = 0;
        characterSelectSessionChangeCount = 0;

        territoryRebindActive = false;
        Interlocked.Exchange(ref pendingTerritoryRebindUtcTicks, 0);
        Interlocked.Exchange(ref territoryRebindDeadlineUtcTicks, 0);
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        if (!ClientState.IsLoggedIn)
            return;

        var now = DateTimeOffset.UtcNow;

        // Honorific IPC titles are tied to the local player's current EntityId.
        // Zoning can replace that entity and Honorific then drops the old assignment.
        // Invalidate STH's ownership cache so the v1.0.16 duplicate-write guard
        // cannot mistake the old assignment for one that is still active.
        territoryRebindActive = config.Enabled;
        patMeHonorificOverrideActive = false;
        appliedFingerprint = null;
        hasAppliedTitle = false;
        lastAppliedTitle = null;

        if (!territoryRebindActive)
            return;

        Interlocked.Exchange(
            ref pendingTerritoryRebindUtcTicks,
            now.Add(TerritoryRebindSettleDelay).UtcDateTime.Ticks);

        Interlocked.Exchange(
            ref territoryRebindDeadlineUtcTicks,
            now.Add(TerritoryRebindWindow).UtcDateTime.Ticks);

        Log.Information(
            $"Territory changed to {territoryType}; scheduling Honorific IPC title rebind.");
    }

    private void TryRestoreAfterTerritoryChange()
    {
        if (!territoryRebindActive)
            return;

        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var pendingTicks = Interlocked.Read(ref pendingTerritoryRebindUtcTicks);

        if (pendingTicks > 0 && nowTicks < pendingTicks)
            return;

        if (!config.Enabled || !ClientState.IsLoggedIn)
        {
            FinishTerritoryRebind();
            return;
        }

        // Do not interfere with compatibility states that deliberately relinquish
        // STH's Honorific title.
        if (characterSelectRecaptureActive ||
            patMeHonorificOverrideActive ||
            ShouldAutoHideForCombat())
        {
            RetryTerritoryRebind(nowTicks);
            return;
        }

        var renderTrack = GetCurrentRenderTrack();

        string title;
        string fingerprint;
        var preserveProvidedTitle = false;

        if (renderTrack == null)
        {
            // "Keep title while stopped" has no current SpotifyTrackInfo left.
            // Rebind exactly the title that was previously visible.
            if (!config.ClearOnPause &&
                string.Equals(
                    lastState,
                    "Nothing playing / paused",
                    StringComparison.Ordinal) &&
                TryGetRetainedSpotifyTitle(
                    out title,
                    out fingerprint))
            {
                preserveProvidedTitle = true;
            }
            else
            {
                FinishTerritoryRebind();
                return;
            }
        }
        else
        {
            if ((lastTrackPaused && config.ClearOnPause) ||
                !IsTrackAllowed(renderTrack))
            {
                FinishTerritoryRebind();
                return;
            }

            var filterMatch = GetContentFilterMatch(renderTrack);

            if (filterMatch != null &&
                config.ContentFilterAction != 0)
            {
                // Action 2 means the blocked track must never be rendered.
                // Rebind the previous safe title instead.
                if (config.ContentFilterAction == 2 &&
                    TryGetRetainedSpotifyTitle(
                        out title,
                        out fingerprint))
                {
                    preserveProvidedTitle = true;
                }
                else
                {
                    FinishTerritoryRebind();
                    return;
                }
            }
            else
            {
                var paused = lastTrackPaused;
                title = BuildConfiguredTitle(renderTrack, paused);
                fingerprint =
                    BuildRenderFingerprint(renderTrack, paused);
            }
        }

        // If Honorific already exposes the desired title, just synchronize STH's
        // local ownership state rather than issuing another IPC write.
        try
        {
            if (honorific.TryGetCurrentTitle(out var currentTitle) &&
                string.Equals(
                    currentTitle.Trim(),
                    title,
                    StringComparison.Ordinal))
            {
                hasAppliedTitle = true;
                lastAppliedTitle = title;
                appliedFingerprint = fingerprint;
                originalHonorificCaptureFinished = true;

                FinishTerritoryRebind();

                Log.Information(
                    $"Territory rebind: Honorific already reports the current Spotify title '{title}'.");

                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(
                $"Territory rebind preflight read failed: {ex.Message}");
        }

        // Force a genuine SetCharacterTitle call. The pre-zone EntityId assignment
        // no longer counts as ownership even if the visible cycle stage is unchanged.
        hasAppliedTitle = false;
        lastAppliedTitle = null;
        appliedFingerprint = null;

        ApplyHonorificTitle(title, fingerprint, preserveProvidedTitle);

        // Honorific intentionally no-ops SetCharacterTitle if the indexed local
        // player object is not available yet. Verify that the title actually landed.
        var verified = false;

        try
        {
            verified =
                honorific.TryGetCurrentTitle(out var reboundTitle) &&
                string.Equals(
                    reboundTitle.Trim(),
                    title,
                    StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Debug(
                $"Territory rebind verification read failed: {ex.Message}");
        }

        if (verified)
        {
            FinishTerritoryRebind();

            Log.Information(
                $"Territory rebind restored Spotify title: {title}");

            return;
        }

        // ApplyHonorificTitle optimistically updates STH's state after invoking IPC.
        // Roll it back if Honorific did not actually attach the title so another
        // retry cannot be skipped by the duplicate-write guard.
        hasAppliedTitle = false;
        lastAppliedTitle = null;
        appliedFingerprint = null;

        RetryTerritoryRebind(nowTicks);
    }

    private void RetryTerritoryRebind(long nowTicks)
    {
        var deadlineTicks =
            Interlocked.Read(ref territoryRebindDeadlineUtcTicks);

        if (deadlineTicks > 0 && nowTicks >= deadlineTicks)
        {
            FinishTerritoryRebind();

            appliedFingerprint = null;
            hasAppliedTitle = false;
            lastAppliedTitle = null;

            // A fresh Spotify render remains the final fallback if the local player
            // took unusually long to become available after zoning.
            SchedulePollNow();

            Log.Warning(
                "Territory rebind timed out before Honorific confirmed the title; " +
                "scheduled a fresh Spotify render.");

            return;
        }

        Interlocked.Exchange(
            ref pendingTerritoryRebindUtcTicks,
            DateTimeOffset.UtcNow
                .Add(TerritoryRebindRetryInterval)
                .UtcDateTime.Ticks);
    }

    private void FinishTerritoryRebind()
    {
        territoryRebindActive = false;

        Interlocked.Exchange(
            ref pendingTerritoryRebindUtcTicks,
            0);

        Interlocked.Exchange(
            ref territoryRebindDeadlineUtcTicks,
            0);
    }

    private void OnCharacterSelectChanged(string characterName, string designName)
    {
        var profile = (characterName ?? string.Empty).Trim();
        var design = (designName ?? string.Empty).Trim();

        lastCharacterSelectProfile = profile;
        characterSelectProfileKnown = true;
        characterSelectEventCount++;
        characterSelectLastIpcError = null;
        characterSelectLastSignal = string.IsNullOrWhiteSpace(design)
            ? $"event:{profile}"
            : $"event:{profile}/{design}";

        Log.Information(
            $"Character Select+ IPC event received: profile='{profile}', design='{design}'.");

        if (!config.Enabled)
            return;

        BeginCharacterSelectHonorificRecapture(
            string.IsNullOrWhiteSpace(design)
                ? $"Character Select+ switched to '{profile}'"
                : $"Character Select+ switched to '{profile}' / '{design}'");
    }

    private void BeginCharacterSelectHonorificRecapture(string reason)
    {
        var now = DateTimeOffset.UtcNow;

        characterSelectPreviousHonorificTitle =
            config.CachedHonorificTitle ?? string.Empty;

        characterSelectPreviousHonorificStyleFingerprint =
            BuildCachedHonorificStyleFingerprint();

        characterSelectRecaptureActive = true;
        appliedFingerprint = null;

        // CS+ announces the profile/design change before it executes its macro.
        // Drop STH's IPC assignment immediately and KEEP it dropped while that
        // macro runs. This intentionally mirrors manually disabling STH, which is
        // already known to expose CS+'s new forced Honorific title correctly.
        if (hasAppliedTitle && !TryClearHonorific(preserveRetainedTitle: true))
        {
            characterSelectRecaptureActive = false;
            characterSelectPreviousHonorificTitle = string.Empty;
            characterSelectPreviousHonorificStyleFingerprint = string.Empty;
            Log.Warning("Character Select+ compatibility: could not relinquish STH Honorific title.");
            return;
        }

        Interlocked.Exchange(
            ref pendingCharacterSelectCaptureUtcTicks,
            now.Add(CharacterSelectSettleDelay).UtcDateTime.Ticks);

        Interlocked.Exchange(
            ref characterSelectCaptureDeadlineUtcTicks,
            now.Add(CharacterSelectCaptureWindow).UtcDateTime.Ticks);

        Log.Information($"{reason}; STH Honorific title relinquished while waiting for CS+.");
    }
    private void TryDetectCharacterSelectSessionFileChange()
    {
        var path = characterSelectSessionInfoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            if (characterSelectSessionProfileKnown &&
                writeTicks == lastCharacterSelectSessionWriteUtcTicks)
                return;

            var currentProfile = (File.ReadAllText(path) ?? string.Empty).Trim();

            if (!characterSelectSessionProfileKnown)
            {
                lastCharacterSelectSessionProfile = currentProfile;
                lastCharacterSelectSessionWriteUtcTicks = writeTicks;
                characterSelectSessionProfileKnown = true;
                lastCharacterSelectProfile = currentProfile;
                characterSelectProfileKnown = true;
                characterSelectLastSignal = $"session-init:{currentProfile}";

                Log.Information(
                    $"Character Select+ session fallback initialized: profile='{currentProfile}'.");

                return;
            }

            if (writeTicks == lastCharacterSelectSessionWriteUtcTicks)
                return;

            var previousProfile = lastCharacterSelectSessionProfile ?? string.Empty;

            lastCharacterSelectSessionWriteUtcTicks = writeTicks;
            lastCharacterSelectSessionProfile = currentProfile;
            lastCharacterSelectProfile = currentProfile;
            characterSelectProfileKnown = true;
            characterSelectSessionChangeCount++;
            characterSelectLastSignal = $"session:{previousProfile}->{currentProfile}";

            Log.Information(
                $"Character Select+ session fallback detected: '{previousProfile}' -> '{currentProfile}'.");

            if (config.Enabled && !characterSelectRecaptureActive)
            {
                BeginCharacterSelectHonorificRecapture(
                    $"Character Select+ session fallback detected '{previousProfile}' -> '{currentProfile}'");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Character Select+ session fallback read failed: {ex.Message}");
        }
    }
    private void TryDetectCharacterSelectProfileChange()
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;

        var pendingCaptureTicks = Interlocked.Read(ref pendingCharacterSelectCaptureUtcTicks);
        if (pendingCaptureTicks > 0 && nowTicks >= pendingCaptureTicks)
        {
            Interlocked.Exchange(ref pendingCharacterSelectCaptureUtcTicks, 0);
            RefreshHonorificAfterCharacterSelectProfileChange();
        }

        if (nowTicks < Interlocked.Read(ref nextCharacterSelectCheckUtcTicks))
            return;

        Interlocked.Exchange(
            ref nextCharacterSelectCheckUtcTicks,
            DateTimeOffset.UtcNow.Add(CharacterSelectCheckInterval).UtcDateTime.Ticks);

        try
        {
            var currentProfile = (characterSelectGetCurrentCharacter.InvokeFunc() ?? string.Empty).Trim();
            characterSelectLastIpcError = null;

            if (characterSelectSessionProfileKnown &&
                string.IsNullOrWhiteSpace(currentProfile))
                return;

            if (!characterSelectProfileKnown)
            {
                lastCharacterSelectProfile = currentProfile;
                characterSelectProfileKnown = true;
                characterSelectLastSignal = $"poll-init:{currentProfile}";

                Log.Information(
                    $"Character Select+ polling initialized: profile='{currentProfile}'.");

                return;
            }

            if (string.Equals(currentProfile, lastCharacterSelectProfile, StringComparison.Ordinal))
                return;

            var previousProfile = lastCharacterSelectProfile ?? string.Empty;
            lastCharacterSelectProfile = currentProfile;
            characterSelectPollChangeCount++;
            characterSelectLastSignal = $"poll:{previousProfile}->{currentProfile}";

            Log.Information(
                $"Character Select+ polling change detected: '{previousProfile}' -> '{currentProfile}'.");

            if (config.Enabled && !characterSelectRecaptureActive)
            {
                BeginCharacterSelectHonorificRecapture(
                    $"Character Select+ polling fallback detected '{previousProfile}' -> '{currentProfile}'");
            }
        }
        catch (Exception ex)
        {
            var error = ex.GetBaseException().Message;

            if (!string.Equals(characterSelectLastIpcError, error, StringComparison.Ordinal))
                Log.Warning($"Character Select+ GetCurrentCharacter IPC unavailable: {error}");

            characterSelectLastIpcError = error;
        }
    }

    private void RefreshHonorificAfterCharacterSelectProfileChange()
    {
        if (!characterSelectRecaptureActive)
            return;

        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var deadlineTicks = Interlocked.Read(ref characterSelectCaptureDeadlineUtcTicks);
        var deadlineReached = deadlineTicks > 0 && nowTicks >= deadlineTicks;

        try
        {
            var hasUnderlyingTitle =
                honorific.TryGetCurrentTitleState(out var currentState);

            var currentTitle =
                hasUnderlyingTitle
                    ? currentState.Title.Trim()
                    : string.Empty;

            var currentStyleFingerprint =
                hasUnderlyingTitle
                    ? BuildHonorificStyleFingerprint(currentState)
                    : "none";

            Log.Information(
                $"Character Select+ recapture probe: hasTitle={hasUnderlyingTitle}, " +
                $"current='{currentTitle}', previous='{characterSelectPreviousHonorificTitle}', " +
                $"styleChanged={!string.Equals(currentStyleFingerprint, characterSelectPreviousHonorificStyleFingerprint, StringComparison.Ordinal)}, " +
                $"deadlineReached={deadlineReached}.");

            var changedFromPrevious =
                hasUnderlyingTitle &&
                (
                    !string.Equals(
                        currentTitle,
                        characterSelectPreviousHonorificTitle,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        currentStyleFingerprint,
                        characterSelectPreviousHonorificStyleFingerprint,
                        StringComparison.Ordinal)
                );

            // A different underlying title means CS+'s Honorific macro has landed.
            // If nothing changes, keep STH relinquished until the timeout; this also
            // handles profiles whose Honorific command is delayed by other macro work.
            if (!changedFromPrevious && !deadlineReached)
            {
                Interlocked.Exchange(
                    ref pendingCharacterSelectCaptureUtcTicks,
                    DateTimeOffset.UtcNow.Add(CharacterSelectCaptureRetryInterval).UtcDateTime.Ticks);
                return;
            }

            var changedCache = !string.Equals(
                config.CachedHonorificTitle,
                currentTitle,
                StringComparison.Ordinal);

            config.CachedHonorificTitle = currentTitle;

            if (hasUnderlyingTitle)
                changedCache |= CacheHonorificStyle(currentState);
            else
                changedCache |= ClearCachedHonorificStyle();

            originalHonorificCaptureFinished = true;
            appliedFingerprint = null;

            if (changedCache)
                SaveConfig();

            characterSelectRecaptureActive = false;
            characterSelectPreviousHonorificTitle = string.Empty;
            characterSelectPreviousHonorificStyleFingerprint = string.Empty;
            Interlocked.Exchange(ref pendingCharacterSelectCaptureUtcTicks, 0);
            Interlocked.Exchange(ref characterSelectCaptureDeadlineUtcTicks, 0);

            if (string.IsNullOrWhiteSpace(currentTitle))
                Log.Information("Character Select+ compatibility: underlying Honorific title is empty.");
            else
                Log.Information($"Character Select+ compatibility: cached underlying Honorific title '{currentTitle}'.");

            RestoreCurrentSpotifyTitle();
        }
        catch (Exception ex)
        {
            Log.Debug($"Character Select+ Honorific recapture failed: {ex.Message}");

            if (!deadlineReached)
            {
                Interlocked.Exchange(
                    ref pendingCharacterSelectCaptureUtcTicks,
                    DateTimeOffset.UtcNow.Add(CharacterSelectCaptureRetryInterval).UtcDateTime.Ticks);
                return;
            }

            // Do not destroy a known-good cache just because the IPC read failed.
            characterSelectRecaptureActive = false;
            characterSelectPreviousHonorificTitle = string.Empty;
            characterSelectPreviousHonorificStyleFingerprint = string.Empty;
            Interlocked.Exchange(ref pendingCharacterSelectCaptureUtcTicks, 0);
            Interlocked.Exchange(ref characterSelectCaptureDeadlineUtcTicks, 0);
            appliedFingerprint = null;

            RestoreCurrentSpotifyTitle();
        }
    }
    private void OnFrameworkUpdate(IFramework framework)
    {
        TryDetectCharacterSelectSessionFileChange();
        TryDetectCharacterSelectProfileChange();
        TryAutoCaptureOriginalHonorificTitle();
        TryRestoreAfterTerritoryChange();

        if (!config.Enabled)
        {
            if (hasAppliedTitle)
                TryClearHonorific();
            return;
        }

        RefreshCachedProgressTitle();

        if (DateTimeOffset.UtcNow.UtcDateTime.Ticks < Interlocked.Read(ref nextPollUtcTicks))
            return;

        // Framework ticks are frequent. The atomic gate guarantees a slow request
        // can never overlap with another poll even if completion happens off-thread.
        if (Interlocked.CompareExchange(ref pollInProgress, 1, 0) != 0)
            return;

        _ = PollSpotifyAsync();
    }

    private async Task PollSpotifyAsync()
    {
        try
        {
            var result = await spotify.PollCurrentlyPlayingAsync(lifetimeCts.Token).ConfigureAwait(false);

            switch (result.State)
            {
                case SpotifyPollState.PlayingTrack when result.Track != null:
                    MarkSpotifyPollHealthy();
                    MarkListeningSessionPlaying();
                    lastTrack = result.Track;
                    lastTrackPaused = false;
                    lastTrackObservedUtcTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
                    Interlocked.Exchange(ref nextLocalRenderUtcTicks, lastTrackObservedUtcTicks);
                    lastError = null;
                    ScheduleNextPoll(NormalPollInterval);

                    if (!IsTrackAllowed(result.Track))
                    {
                        lastState = result.Track.IsLocal
                            ? "Local track hidden by settings"
                            : "Normal Spotify track hidden by settings";

                        if (hasAppliedTitle)
                            await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                        appliedFingerprint = null;
                        break;
                    }

                    var playingFilterMatch = GetContentFilterMatch(result.Track);
                    if (playingFilterMatch != null)
                    {
                        lastState = $"Triggerword censored ({playingFilterMatch.Field})";

                        if (config.ContentFilterAction == 1)
                        {
                            if (hasAppliedTitle)
                                await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                            appliedFingerprint = null;
                            break;
                        }

                        if (config.ContentFilterAction == 2)
                        {
                            lastState += " - previous title kept";
                            break;
                        }
                    }
                    else
                    {
                        lastState = result.Track.IsLocal ? "Playing local track" : "Playing Spotify track";
                    }

                    var renderFingerprint = BuildRenderFingerprint(result.Track, paused: false);
                    if (!string.Equals(appliedFingerprint, renderFingerprint, StringComparison.Ordinal))
                    {
                        var title = BuildConfiguredTitle(result.Track, paused: false);
                        await Framework.RunOnFrameworkThread(() => ApplyHonorificTitle(title, renderFingerprint)).ConfigureAwait(false);
                    }
                    break;

                case SpotifyPollState.PausedTrack when result.Track != null:
                    MarkSpotifyPollHealthy();
                    ResetListeningSession();
                    lastTrack = result.Track;
                    lastTrackPaused = true;
                    lastTrackObservedUtcTicks = 0;
                    Interlocked.Exchange(ref nextLocalRenderUtcTicks, 0);
                    lastState = "Playback paused";
                    lastError = null;

                    // Spotify does not push a "playback resumed" event to the plugin.
                    // Keep checking paused playback at the normal 15-second cadence so
                    // resuming is detected promptly. Truly idle/not-playing stays at
                    // the 60-second cadence to preserve the low-quota behavior.
                    ScheduleNextPoll(NormalPollInterval);

                    if (!IsTrackAllowed(result.Track))
                    {
                        if (hasAppliedTitle)
                            await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                        appliedFingerprint = null;
                        break;
                    }

                    if (config.ClearOnPause)
                    {
                        if (hasAppliedTitle)
                            await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                        break;
                    }

                    var pausedFilterMatch = GetContentFilterMatch(result.Track);
                    if (pausedFilterMatch != null)
                    {
                        lastState = $"Triggerword censored ({pausedFilterMatch.Field})";

                        if (config.ContentFilterAction == 1)
                        {
                            if (hasAppliedTitle)
                                await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                            appliedFingerprint = null;
                            break;
                        }

                        if (config.ContentFilterAction == 2)
                        {
                            lastState += " - previous title kept";
                            break;
                        }
                    }

                    var pausedFingerprint = BuildRenderFingerprint(result.Track, paused: true);
                    if (!string.Equals(appliedFingerprint, pausedFingerprint, StringComparison.Ordinal))
                    {
                        var pausedTitle = BuildConfiguredTitle(result.Track, paused: true);
                        await Framework.RunOnFrameworkThread(() => ApplyHonorificTitle(pausedTitle, pausedFingerprint)).ConfigureAwait(false);
                    }
                    break;

                case SpotifyPollState.NotPlaying:
                    MarkSpotifyPollHealthy();
                    ResetListeningSession();
                    lastTrack = null;
                    lastTrackPaused = false;
                    lastTrackObservedUtcTicks = 0;
                    Interlocked.Exchange(ref nextLocalRenderUtcTicks, 0);
                    lastState = "Nothing playing / paused";
                    lastError = null;
                    ScheduleNextPoll(IdlePollInterval);
                    if (config.ClearOnPause && hasAppliedTitle)
                        await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                    break;

                case SpotifyPollState.NotAuthenticated:
                    ResetFailureCounter();
                    ClearSpotifyCooldown();
                    ResetListeningSession();
                    lastTrack = null;
                    lastTrackPaused = false;
                    lastTrackObservedUtcTicks = 0;
                    Interlocked.Exchange(ref nextLocalRenderUtcTicks, 0);
                    lastState = string.IsNullOrWhiteSpace(result.Error)
                        ? "Not authenticated"
                        : "Spotify re-authentication required";
                    lastError = result.Error;
                    ScheduleNextPoll(IdlePollInterval);
                    if (hasAppliedTitle)
                        await Framework.RunOnFrameworkThread(TryClearHonorific).ConfigureAwait(false);
                    break;

                case SpotifyPollState.RateLimited:
                {
                    var delay = ScheduleSpotifyFailure(
                        result.Error,
                        result.RetryAfterSeconds,
                        useExponentialBackoff: false,
                        isQuotaExceeded: false);
                    lastState = $"Spotify rate limited - retrying in {FormatRetryDelay(TimeSpan.FromSeconds(delay))}";
                    break;
                }

                case SpotifyPollState.QuotaExceeded:
                {
                    var delay = ScheduleSpotifyFailure(
                        result.Error,
                        result.RetryAfterSeconds,
                        useExponentialBackoff: false,
                        isQuotaExceeded: true);
                    lastState = $"Spotify Development Mode quota exceeded - retrying in {FormatRetryDelay(TimeSpan.FromSeconds(delay))}";
                    break;
                }

                case SpotifyPollState.TransientError:
                {
                    var delay = ScheduleSpotifyFailure(result.Error, result.RetryAfterSeconds, useExponentialBackoff: true);
                    lastState = $"Temporary Spotify error - retrying in {delay}s";
                    break;
                }

                case SpotifyPollState.Error:
                {
                    // Unknown/non-transient HTTP failures still back off so a broken
                    // endpoint or scope cannot flood Spotify or the Dalamud log. The
                    // last known-good Honorific title is deliberately retained.
                    var delay = ScheduleSpotifyFailure(result.Error, 0, useExponentialBackoff: true);
                    lastState = $"Spotify API error - retrying in {delay}s";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            // Plugin is unloading.
        }
        catch (Exception ex)
        {
            var delay = ScheduleSpotifyFailure(ex.Message, 0, useExponentialBackoff: true);
            lastState = $"Unexpected Spotify error - retrying in {delay}s";
            Log.Error(ex, "Unexpected Spotify polling error");
        }
        finally
        {
            Volatile.Write(ref pollInProgress, 0);
        }
    }

    private void MarkSpotifyPollHealthy()
    {
        var recoveredFailures = Interlocked.Exchange(ref consecutivePollFailures, 0);
        Interlocked.Exchange(ref lastSuccessfulPollUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        ClearSpotifyCooldown();
        lastLoggedSpotifyError = null;

        if (recoveredFailures > 0)
            Log.Information($"Spotify polling recovered after {recoveredFailures} consecutive failure(s).");
    }

    private void ResetFailureCounter()
    {
        Interlocked.Exchange(ref consecutivePollFailures, 0);
        lastLoggedSpotifyError = null;
    }

    private int ScheduleSpotifyFailure(
        string? error,
        int minimumDelaySeconds,
        bool useExponentialBackoff,
        bool isQuotaExceeded = false)
    {
        var failureCount = Interlocked.Increment(ref consecutivePollFailures);
        var delay = Math.Max(0, minimumDelaySeconds);

        if (isQuotaExceeded)
        {
            // Spotify's Development Mode quota is separate from the rolling
            // rate-limit window. If Spotify provides Retry-After, never retry
            // earlier than requested. If it does not, back off aggressively:
            // 1h, 2h, 4h, 8h, 12h, 12h...
            var shift = Math.Min(Math.Max(0, failureCount - 1), 4);
            var quotaFallback = Math.Min(
                QuotaFallbackMaxSeconds,
                QuotaFallbackBaseSeconds * (1 << shift));
            delay = Math.Max(delay, quotaFallback);
        }
        else if (useExponentialBackoff)
        {
            // 5, 10, 20, 40, 80, 120, 120... seconds.
            var shift = Math.Min(Math.Max(0, failureCount - 1), 5);
            var exponential = Math.Min(FailureBackoffMaxSeconds, FailureBackoffBaseSeconds * (1 << shift));
            delay = Math.Max(delay, exponential);
        }
        else
        {
            // Ordinary Spotify rate limits are based on a rolling 30-second
            // request window. Retry-After is authoritative when supplied.
            delay = Math.Max(delay, RateLimitFallbackSeconds);
        }

        delay = Math.Max(1, delay);
        lastError = string.IsNullOrWhiteSpace(error) ? "Unknown Spotify error." : error;
        var retryDelay = TimeSpan.FromSeconds(delay);
        ScheduleNextPoll(retryDelay);

        if (isQuotaExceeded || !useExponentialBackoff)
            SetSpotifyCooldown(retryDelay);

        // Avoid one warning every retry forever. Log the first failure, any changed
        // error, and periodic reminders during a long outage.
        if (failureCount == 1 || failureCount % 5 == 0 || !string.Equals(lastLoggedSpotifyError, lastError, StringComparison.Ordinal))
        {
            Log.Warning($"Spotify poll failure #{failureCount}; retrying in {FormatRetryDelay(retryDelay)}: {lastError}");
            lastLoggedSpotifyError = lastError;
        }

        return delay;
    }

    private void ScheduleNextPoll(TimeSpan delay)
    {
        var when = DateTimeOffset.UtcNow.Add(delay).UtcDateTime.Ticks;
        Interlocked.Exchange(ref nextPollUtcTicks, when);
    }

    private void SchedulePollNow()
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var cooldownTicks = Interlocked.Read(ref spotifyCooldownUntilUtcTicks);
        Interlocked.Exchange(ref nextPollUtcTicks, Math.Max(nowTicks, cooldownTicks));
    }

    private void SetSpotifyCooldown(TimeSpan delay)
    {
        var untilTicks = DateTimeOffset.UtcNow.Add(delay).UtcDateTime.Ticks;
        Interlocked.Exchange(ref spotifyCooldownUntilUtcTicks, untilTicks);
    }

    private void ClearSpotifyCooldown() =>
        Interlocked.Exchange(ref spotifyCooldownUntilUtcTicks, 0);

    private bool TryGetSpotifyCooldownRemaining(out TimeSpan remaining)
    {
        var cooldownTicks = Interlocked.Read(ref spotifyCooldownUntilUtcTicks);
        if (cooldownTicks <= 0)
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        var until = new DateTimeOffset(new DateTime(cooldownTicks, DateTimeKind.Utc));
        remaining = until - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ClearSpotifyCooldown();
            remaining = TimeSpan.Zero;
            return false;
        }

        return true;
    }

    private string BuildSpotifyFriendlyStatus()
    {
        if (!config.Enabled)
            return "Disabled";
        if (IsAuthenticating)
            return "Connecting...";
        if (!IsAuthenticated)
            return "Needs connection";
        if (Volatile.Read(ref consecutivePollFailures) > 0)
            return "Temporarily unavailable";
        if (lastState.Contains("re-authentication required", StringComparison.OrdinalIgnoreCase))
            return "Needs attention";
        return "Connected";
    }

    private string BuildReliabilityText()
    {
        if (!config.Enabled)
            return "Polling disabled";

        var now = DateTimeOffset.UtcNow;
        var failures = Volatile.Read(ref consecutivePollFailures);
        var lastSuccessTicks = Interlocked.Read(ref lastSuccessfulPollUtcTicks);
        var nextTicks = Interlocked.Read(ref nextPollUtcTicks);

        string successText;
        if (lastSuccessTicks <= 0)
        {
            successText = "no successful poll yet";
        }
        else
        {
            var successAt = new DateTimeOffset(new DateTime(lastSuccessTicks, DateTimeKind.Utc));
            successText = $"last success {FormatAge(now - successAt)} ago";
        }

        var retryDelay = new DateTimeOffset(new DateTime(nextTicks, DateTimeKind.Utc)) - now;
        if (retryDelay < TimeSpan.Zero)
            retryDelay = TimeSpan.Zero;

        if (failures > 0)
            return $"Recovering: {failures} consecutive failure(s), retry in {FormatRetryDelay(retryDelay)}, {successText}";

        if (Volatile.Read(ref pollInProgress) != 0)
            return $"Healthy: polling now, {successText}";

        return $"Healthy: {successText}";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalSeconds < 60)
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24)
            return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private static string FormatRetryDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        if (delay.TotalDays >= 1)
            return $"{(int)delay.TotalDays}d {delay.Hours}h";
        if (delay.TotalHours >= 1)
            return $"{(int)delay.TotalHours}h {delay.Minutes}m";
        if (delay.TotalMinutes >= 1)
            return $"{(int)delay.TotalMinutes}m {delay.Seconds}s";
        return $"{Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds))}s";
    }

    private static bool UsesListeningSessionVariable(string? format) =>
        !string.IsNullOrWhiteSpace(format) &&
        format.Contains(
            "{session}",
            StringComparison.OrdinalIgnoreCase);

    private static bool UsesLocallyRefreshingTitleVariable(string? format) =>
        TitleTemplateFormatter.UsesProgressVariable(format) ||
        UsesListeningSessionVariable(format);

    private void MarkListeningSessionPlaying()
    {
        if (!config.Enabled)
            return;

        var nowTicks =
            DateTimeOffset.UtcNow.UtcDateTime.Ticks;

        Interlocked.CompareExchange(
            ref listeningSessionStartedUtcTicks,
            nowTicks,
            0);
    }

    private void ResetListeningSession() =>
        Interlocked.Exchange(
            ref listeningSessionStartedUtcTicks,
            0);

    private long GetListeningSessionMinutes()
    {
        var startedTicks =
            Interlocked.Read(
                ref listeningSessionStartedUtcTicks);

        if (startedTicks <= 0)
            return 0;

        var elapsedTicks = Math.Max(
            0L,
            DateTimeOffset.UtcNow.UtcDateTime.Ticks -
            startedTicks);

        return elapsedTicks / TimeSpan.TicksPerMinute;
    }

    private string BuildListeningSessionText()
    {
        var totalMinutes =
            GetListeningSessionMinutes();

        if (totalMinutes < 60)
            return $"listening {totalMinutes}m";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        return $"listening {hours}h {minutes}m";
    }

    private string ExpandHiddenFormatterTokens(string text)
    {
        return (text ?? string.Empty)
            .Replace(
                "{version}",
                $"SpotifyTrackHonorific v{DisplayVersion}",
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{sth}",
                "SpotifyTrackHonorific",
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{session}",
                BuildListeningSessionText(),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{womm}",
                "works on my machine",
                StringComparison.OrdinalIgnoreCase);
    }

    private SpotifyTrackInfo? GetCurrentRenderTrack()
    {
        var track = lastTrack;
        if (track == null || lastTrackPaused || lastTrackObservedUtcTicks <= 0)
            return track;

        var observedAt = new DateTimeOffset(new DateTime(lastTrackObservedUtcTicks, DateTimeKind.Utc));
        var elapsedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - observedAt).TotalMilliseconds);
        var maxProgress = track.DurationMs > 0 ? track.DurationMs : int.MaxValue;
        var progress = Math.Clamp((long)track.ProgressMs + elapsedMs, 0L, (long)maxProgress);

        return track with { ProgressMs = (int)progress };
    }

    private void RefreshCachedProgressTitle()
    {
        var track = lastTrack;
        if (track == null ||
            lastTrackPaused ||
            !UsesLocallyRefreshingTitleVariable(config.TitleFormat) ||
            !IsTrackAllowed(track))
            return;

        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        if (nowTicks < Interlocked.Read(ref nextLocalRenderUtcTicks))
            return;

        Interlocked.Exchange(
            ref nextLocalRenderUtcTicks,
            DateTimeOffset.UtcNow.Add(LocalRenderRefreshInterval).UtcDateTime.Ticks);

        if (GetContentFilterMatch(track) != null && config.ContentFilterAction != 0)
            return;

        var renderTrack = GetCurrentRenderTrack();
        if (renderTrack == null)
            return;

        var fingerprint = BuildRenderFingerprint(renderTrack, paused: false);
        if (!string.Equals(appliedFingerprint, fingerprint, StringComparison.Ordinal))
            ApplyHonorificTitle(BuildConfiguredTitle(renderTrack, paused: false), fingerprint);
    }

    private bool IsTrackAllowed(SpotifyTrackInfo track) =>
        track.IsLocal ? config.ShowLocalTracks : config.ShowNormalTracks;

    private ContentFilterMatch? GetContentFilterMatch(SpotifyTrackInfo track)
    {
        if (!config.EnableContentFilter)
            return null;

        return ContentFilterMatcher.MatchTrack(
            track,
            config.ContentFilterEntries,
            config.UseBuiltInContentFilterList,
            config.DisabledBuiltInContentFilterEntries,
            config.SmartContentFilterMatching);
    }

    private string BuildConfiguredTitle(SpotifyTrackInfo track, bool paused)
    {
        var renderTrack = GetContentFilteredTrack(track);
        var formatted = TitleTemplateFormatter.Expand(
            config.TitleFormat,
            renderTrack,
            paused,
            config.StripBracketedTrackParts,
            config.CachedHonorificTitle);

        formatted = ExpandHiddenFormatterTokens(formatted);

        return HonorificBridge.FitTitle(
            formatted,
            config.SmartFitLongTitles);
    }

    private SpotifyTrackInfo GetContentFilteredTrack(SpotifyTrackInfo track)
    {
        if (!config.EnableContentFilter || config.ContentFilterAction != 0)
            return track;

        var replacement = string.IsNullOrWhiteSpace(config.ContentFilterFallback)
            ? Configuration.DefaultContentFilterFallback
            : config.ContentFilterFallback.Trim();

        return ContentFilterMatcher.CensorTrack(
            track,
            config.ContentFilterEntries,
            config.UseBuiltInContentFilterList,
            config.DisabledBuiltInContentFilterEntries,
            config.SmartContentFilterMatching,
            replacement).Track;
    }

    private SpotifyTrackInfo BuildPreviewTrack() => GetCurrentRenderTrack() ?? new SpotifyTrackInfo(
        "A Very Long Example Track Title (Remastered 2026)",
        new[] { "Artist", "Featured Artist" },
        "Album",
        243000,
        83000,
        false,
        "preview");

    private string BuildPreviewExpandedTitle()
    {
        var track = GetContentFilteredTrack(BuildPreviewTrack());
        var formatted = TitleTemplateFormatter.Expand(
            config.TitleFormat,
            track,
            lastTrack != null && lastTrackPaused,
            config.StripBracketedTrackParts,
            config.CachedHonorificTitle);

        return ExpandHiddenFormatterTokens(formatted);
    }

    private string BuildPreviewTitle() =>
        HonorificBridge.FitTitle(BuildPreviewExpandedTitle(), config.SmartFitLongTitles);

    private string BuildRenderFingerprint(SpotifyTrackInfo track, bool paused)
    {
        var progressPart = TitleTemplateFormatter.UsesProgressVariable(config.TitleFormat)
            ? $"|progress:{track.ProgressMs / 1000}"
            : string.Empty;

        var listeningSessionPart =
            UsesListeningSessionVariable(config.TitleFormat)
                ? $"|listeningSessionMinute:{GetListeningSessionMinutes()}"
                : string.Empty;

        var honorificCachePart = config.TitleFormat.Contains("{honorific}", StringComparison.OrdinalIgnoreCase)
            ? $"|cachedHonorific:{config.CachedHonorificTitle}"
            : string.Empty;

        var matchedHonorificStylePart =
            config.MatchCharacterSelectHonorificStyle
                ? $"|matchCharacterSelectStyle:true|cachedHonorificStyle:{BuildCachedHonorificStyleFingerprint()}"
                : "|matchCharacterSelectStyle:false";

        var contentFilterPart =
            $"|contentFilter:{config.EnableContentFilter}" +
            $"|contentFilterSmart:{config.SmartContentFilterMatching}" +
            $"|contentFilterAction:{config.ContentFilterAction}" +
            $"|contentFilterReplacement:{config.ContentFilterFallback}" +
            $"|contentFilterBuiltIn:{config.UseBuiltInContentFilterList}" +
            $"|contentFilterBuiltInDisabled:{config.DisabledBuiltInContentFilterEntries}" +
            $"|contentFilterEntries:{config.ContentFilterEntries}";

        var stylePart = config.UseTitleColor
            ? $"|color:{config.TitleColor.X:F4},{config.TitleColor.Y:F4},{config.TitleColor.Z:F4}|glowEnabled:{config.UseTitleGlow}|glow:{config.TitleGlowColor.X:F4},{config.TitleGlowColor.Y:F4},{config.TitleGlowColor.Z:F4}"
            : "|color:default|glowEnabled:false";

        var supporterStylePart =
            $"|supporterConfirmed:{config.HonorificSupporterConfirmed}" +
            $"|supporterGradient:{config.UseSupporterGradient}" +
            $"|customGradient:{config.UseCustomDualGradient}" +
            $"|gradientSet:{config.GradientColourSet}" +
            $"|gradientAnimationStyle:{config.GradientAnimationStyle}" +
            $"|gradientA:{config.GradientColorA.X:F4},{config.GradientColorA.Y:F4},{config.GradientColorA.Z:F4}" +
            $"|gradientB:{config.GradientColorB.X:F4},{config.GradientColorB.Y:F4},{config.GradientColorB.Z:F4}" +
            $"|gradientC:{config.GradientColorC.X:F4},{config.GradientColorC.Y:F4},{config.GradientColorC.Z:F4}";

        return $"{track.Fingerprint}|prefix:{config.IsPrefix}|paused:{paused}|strip:{config.StripBracketedTrackParts}|smartfit:{config.SmartFitLongTitles}|format:{config.TitleFormat}{honorificCachePart}{stylePart}{supporterStylePart}{matchedHonorificStylePart}{contentFilterPart}{progressPart}{listeningSessionPart}";
    }

    private void TryAutoCaptureOriginalHonorificTitle()
    {
        if (characterSelectRecaptureActive)
            return;
        // While STH is disabled and no STH title is still applied, keep the
        // cached Honorific title synchronized. This lets users change their
        // normal Honorific title while STH is off without needing a manual
        // clear/cache cycle. As soon as STH is enabled, the cached value freezes
        // again and becomes the stable {honorific} source for Spotify output.
        var syncWhileDisabled = !config.Enabled && !hasAppliedTitle;

        if (!syncWhileDisabled)
        {
            if (originalHonorificCaptureFinished)
                return;

            if (!string.IsNullOrWhiteSpace(config.CachedHonorificTitle))
            {
                originalHonorificCaptureFinished = true;
                return;
            }
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        if (nowTicks < Interlocked.Read(ref nextOriginalHonorificCaptureAttemptUtcTicks))
            return;

        Interlocked.Exchange(
            ref nextOriginalHonorificCaptureAttemptUtcTicks,
            DateTimeOffset.UtcNow.AddSeconds(1).UtcDateTime.Ticks);

        TryCaptureOriginalHonorificTitleBeforeFirstWrite(syncWhileDisabled);
    }

    private void TryCaptureOriginalHonorificTitleBeforeFirstWrite(bool refreshExistingCache = false)
    {
        if (!refreshExistingCache)
        {
            if (originalHonorificCaptureFinished)
                return;

            if (!string.IsNullOrWhiteSpace(config.CachedHonorificTitle))
            {
                originalHonorificCaptureFinished = true;
                return;
            }
        }

        try
        {
            if (honorific.TryGetCurrentTitleState(out var currentState))
            {
                var currentTitle = currentState.Title;

                var changed = !string.Equals(
                    config.CachedHonorificTitle,
                    currentTitle,
                    StringComparison.Ordinal);

                changed |= CacheHonorificStyle(currentState);

                config.CachedHonorificTitle = currentTitle;
                originalHonorificCaptureFinished = true;

                if (changed)
                {
                    SaveConfig();
                    appliedFingerprint = null;

                    Log.Information(
                        refreshExistingCache
                            ? "Synchronized cached Honorific title/style while STH is disabled."
                            : "Cached the existing Honorific title/style before STH's first title write.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read the existing Honorific title before STH's first write: {ex.Message}");
        }
    }
    private void OnHonorificLocalTitleChanged(string title)
    {
        if (suppressHonorificTitleChanged)
            return;

        title = (title ?? string.Empty).Trim();

        // During a CS+ hand-off this event is only a wake-up signal. The
        // framework-thread recapture performs its own authoritative IPC read.
        if (characterSelectRecaptureActive)
        {
            Interlocked.Exchange(
                ref pendingCharacterSelectCaptureUtcTicks,
                DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            return;
        }

        // During zoning Honorific may briefly announce its underlying title or an
        // empty state while the local player object is being replaced. Do not treat
        // that transition as a PatMeHonorific temporary override.
        if (territoryRebindActive)
        {
            appliedFingerprint = null;
            hasAppliedTitle = false;
            lastAppliedTitle = null;

            Interlocked.Exchange(
                ref pendingTerritoryRebindUtcTicks,
                DateTimeOffset.UtcNow
                    .Add(TerritoryRebindRetryInterval)
                    .UtcDateTime.Ticks);

            return;
        }

        if (!config.EnablePatMeHonorificSupport || !config.Enabled)
        {
            patMeHonorificOverrideActive = false;
            return;
        }

        // Honorific 1.7.5.1+ rate-limits LocalCharacterTitleChanged.
        // The announced value can therefore describe an older title. Query
        // Honorific again and make compatibility decisions from current state.
        var announcedTitle = title;

        try
        {
            title = honorific.TryGetCurrentTitle(out var currentTitle)
                ? currentTitle.Trim()
                : string.Empty;

            if (!string.Equals(
                announcedTitle,
                title,
                StringComparison.Ordinal))
            {
                Log.Debug(
                    $"Honorific delayed title notification ignored as authoritative state: " +
                    $"announced='{announcedTitle}', current='{title}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(
                $"Could not verify Honorific title-change notification: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            // This includes our own cycle stage. A delayed notification for
            // {honorific} must not be mistaken for a PatMe override.
            if (!string.IsNullOrWhiteSpace(lastAppliedTitle) &&
                string.Equals(
                    title,
                    lastAppliedTitle,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (patMeHonorificOverrideActive)
            {
                if (!string.IsNullOrWhiteSpace(config.CachedHonorificTitle) &&
                    string.Equals(
                        title,
                        config.CachedHonorificTitle,
                        StringComparison.Ordinal))
                {
                    patMeHonorificOverrideActive = false;
                    appliedFingerprint = null;
                    lastAppliedTitle = null;

                    Log.Information(
                        "PatMeHonorific compatibility: regular Honorific title returned; restoring Spotify title.");

                    RestoreCurrentSpotifyTitle();
                }

                return;
            }

            if (!hasAppliedTitle)
                return;

            Log.Information(
                $"PatMeHonorific compatibility: yielding to temporary Honorific title '{title}'.");

            patMeHonorificOverrideActive = true;
            hasAppliedTitle = false;
            appliedFingerprint = null;
            lastAppliedTitle = null;
            return;
        }

        if (!patMeHonorificOverrideActive)
            return;

        patMeHonorificOverrideActive = false;
        appliedFingerprint = null;
        lastAppliedTitle = null;

        Log.Information(
            "PatMeHonorific compatibility: temporary Honorific title cleared; restoring Spotify title.");

        RestoreCurrentSpotifyTitle();
    }

    private bool TryGetRetainedSpotifyTitle(
        out string title,
        out string fingerprint)
    {
        title = retainedSpotifyTitle ?? string.Empty;
        fingerprint = retainedSpotifyFingerprint ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (string.IsNullOrWhiteSpace(fingerprint))
            fingerprint = $"retained:{title}";

        return true;
    }

    private bool TryRestoreRetainedSpotifyTitle()
    {
        if (!TryGetRetainedSpotifyTitle(
            out var title,
            out var fingerprint))
        {
            return false;
        }

        ApplyHonorificTitle(
            title,
            fingerprint,
            preserveProvidedTitle: true);

        return hasAppliedTitle &&
               string.Equals(
                   lastAppliedTitle,
                   title,
                   StringComparison.Ordinal);
    }

    private void ClearRetainedSpotifyTitle()
    {
        retainedSpotifyTitle = null;
        retainedSpotifyFingerprint = null;
    }

    private void ApplyHonorificTitle(
        string title,
        string fingerprint,
        bool preserveProvidedTitle = false,
        bool rememberForRecovery = true)
    {
        if (characterSelectRecaptureActive)
        {
            appliedFingerprint = null;
            return;
        }

        if (config.EnablePatMeHonorificSupport && patMeHonorificOverrideActive)
        {
            appliedFingerprint = null;
            return;
        }

        if (ShouldAutoHideForCombat())
        {
            combatAutoHideActive = true;

            if (hasAppliedTitle)
                TryClearHonorific(preserveRetainedTitle: true);

            appliedFingerprint = null;
            return;
        }

        try
        {
            TryCaptureOriginalHonorificTitleBeforeFirstWrite();

            if (!preserveProvidedTitle &&
                !string.IsNullOrWhiteSpace(config.CachedHonorificTitle) &&
                lastTrack != null &&
                config.TitleFormat.Contains("{honorific}", StringComparison.OrdinalIgnoreCase))
            {
                var refreshedTrack = GetCurrentRenderTrack();
                if (refreshedTrack != null)
                {
                    title = BuildConfiguredTitle(refreshedTrack, lastTrackPaused);
                    fingerprint = BuildRenderFingerprint(refreshedTrack, lastTrackPaused);
                }
            }

            Vector3? color = null;
            Vector3? glow = null;
            Vector3? color3 = null;
            int? gradientColourSet = null;
            int? gradientAnimationStyle = null;

            if (config.MatchCharacterSelectHonorificStyle &&
                config.CachedHonorificStyleCaptured)
            {
                // Character Select+ ultimately applies its style through Honorific.
                // Reuse Honorific's final TitleData instead of depending on CS+'s
                // internal save format or trying to duplicate its style logic.
                color = config.CachedHonorificColor;
                glow = config.CachedHonorificGlow;
                color3 = config.CachedHonorificColor3;
                gradientColourSet =
                    config.CachedHonorificGradientColourSet;
                gradientAnimationStyle =
                    config.CachedHonorificGradientAnimationStyle;
            }
            else
            {
                var supporterGradient =
                    config.HonorificSupporterConfirmed &&
                    config.UseSupporterGradient;

                color =
                    config.UseTitleColor
                        ? config.TitleColor
                        : (Vector3?)null;

                if (supporterGradient)
                {
                    // Stored numerically so we can pass Honorific's enum through IPC
                    // without taking a compile-time Honorific assembly dependency.
                    gradientAnimationStyle =
                        Math.Max(0, config.GradientAnimationStyle);

                    if (config.UseCustomDualGradient)
                    {
                        // Honorific's custom GradientColourSet = -1 form uses THREE
                        // colours: Color + Glow + Color3.
                        gradientColourSet = -1;
                        color = config.GradientColorA;
                        glow = config.GradientColorB;
                        color3 = config.GradientColorC;
                    }
                    else
                    {
                        gradientColourSet =
                            Math.Max(0, config.GradientColourSet);
                    }
                }
                else if (config.UseTitleColor &&
                         config.UseTitleGlow)
                {
                    glow = config.TitleGlowColor;
                }
            }

            if (hasAppliedTitle &&
                !string.IsNullOrWhiteSpace(lastAppliedTitle) &&
                string.Equals(
                    title,
                    lastAppliedTitle,
                    StringComparison.Ordinal))
            {
                appliedFingerprint = fingerprint;
                originalHonorificCaptureFinished = true;

                if (rememberForRecovery)
                {
                    retainedSpotifyTitle = title;
                    retainedSpotifyFingerprint = fingerprint;
                }

                return;
            }

            suppressHonorificTitleChanged = true;
            try
            {
                honorific.Set(
                    title,
                    config.IsPrefix,
                    color,
                    glow,
                    gradientColourSet,
                    gradientAnimationStyle,
                    color3);
            }
            finally
            {
                suppressHonorificTitleChanged = false;
            }
            appliedFingerprint = fingerprint;
            hasAppliedTitle = true;
            lastAppliedTitle = title;

            if (rememberForRecovery)
            {
                retainedSpotifyTitle = title;
                retainedSpotifyFingerprint = fingerprint;
            }

            originalHonorificCaptureFinished = true;
            honorificErrorShown = false;
            Log.Information($"Applied Spotify title: {title}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Honorific.SetCharacterTitle IPC failed");
            appliedFingerprint = null;
            hasAppliedTitle = false;
            lastAppliedTitle = null;
            if (!honorificErrorShown)
            {
                ChatGui.PrintError("SpotifyTrackHonorific could not reach Honorific. Make sure Honorific is installed and enabled.", "SpotifyTrackHonorific");
                honorificErrorShown = true;
            }
        }
    }

    private bool TryClearHonorific() =>
        TryClearHonorific(preserveRetainedTitle: false);

    private bool TryClearHonorific(bool preserveRetainedTitle)
    {
        if (!hasAppliedTitle)
        {
            if (!preserveRetainedTitle)
                ClearRetainedSpotifyTitle();

            return true;
        }

        var cleared = false;

        try
        {
            suppressHonorificTitleChanged = true;
            try
            {
                honorific.Clear();
                cleared = true;
            }
            finally
            {
                suppressHonorificTitleChanged = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Honorific.ClearCharacterTitle IPC failed");
        }
        finally
        {
            hasAppliedTitle = false;
            lastAppliedTitle = null;
            appliedFingerprint = null;

            if (!preserveRetainedTitle)
                ClearRetainedSpotifyTitle();
        }

        return cleared;
    }

    private void OnCommand(string command, string args)
    {
        var split = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = split.Length == 0 ? "config" : split[0].ToLowerInvariant();
        var argument = split.Length > 1 ? split[1].Trim() : string.Empty;

        switch (sub)
        {
            case "config":
                OpenConfigUi();
                break;

            case "auth":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    ChatGui.PrintError("Usage: /sth auth YOUR_SPOTIFY_CLIENT_ID", "SpotifyTrackHonorific");
                    return;
                }
                StartAuthentication(argument);
                break;

            case "status":
                PrintStatus();
                break;

            case "now":
                PrintNow();
                break;

            case "dash":
                PrintDash();
                break;

            case "retry":
                RetrySpotifyNow();
                ChatGui.Print("Spotify retry scheduled immediately.");
                break;

            case "enable":
                config.Enabled = true;
                SettingsChanged();
                ChatGui.Print("SpotifyTrackHonorific enabled.");
                break;

            case "disable":
                config.Enabled = false;
                SettingsChanged();
                ChatGui.Print("SpotifyTrackHonorific disabled.");
                break;

            case "clear":
                TryClearHonorific();
                ChatGui.Print("SpotifyTrackHonorific title cleared.");
                break;

            case "ipc-test":
                ApplyHonorificTitle(
                    "♪ Spotify IPC test",
                    "manual-ipc-test",
                    preserveProvidedTitle: true,
                    rememberForRecovery: false);
                ChatGui.Print("Sent an Honorific IPC test title. Use /sth clear afterward.");
                break;

            case "help":
            default:
                PrintHelp();
                break;
        }
    }

    private async Task AuthenticateAsync(string clientId)
    {
        try
        {
            ChatGui.Print($"Spotify authentication starting. Redirect URI must be registered as: {spotify.RedirectUriText}");
            ChatGui.Print("Opening Spotify in your browser...");

            await spotify.AuthenticateAsync(
                clientId,
                OpenBrowser,
                lifetimeCts.Token).ConfigureAwait(false);

            config.Enabled = true;
            config.OnboardingCompleted = true;
            SaveConfig();
            ResetFailureCounter();
            ClearSpotifyCooldown();
            lastError = null;
            lastState = "Authenticated - waiting for Spotify";
            SchedulePollNow();
            await Framework.RunOnFrameworkThread(() =>
            {
                configWindow.SyncClientId();
                ChatGui.Print("Spotify authentication succeeded. SpotifyTrackHonorific is enabled.");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lifetimeCts.IsCancellationRequested)
        {
            await Framework.RunOnFrameworkThread(() => ChatGui.PrintError("Spotify authentication timed out. Authenticate again from /sth or the settings window.", "SpotifyTrackHonorific")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spotify authentication failed");
            await Framework.RunOnFrameworkThread(() => ChatGui.PrintError($"Spotify authentication failed: {ex.Message}", "SpotifyTrackHonorific")).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref authenticationInProgress, 0);
        }
    }

    private static void OpenBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private void PrintStatus()
    {
        var auth = spotify.HasRefreshToken ? "authenticated" : "not authenticated";
        var enabled = config.Enabled ? "enabled" : "disabled";
        ChatGui.Print($"SpotifyTrackHonorific v{DisplayVersion}: {enabled}, {auth}. State: {lastState}.");
        ChatGui.Print($"Reliability: {BuildReliabilityText()}.");
        ChatGui.Print(
            $"Character Select+: profile='{lastCharacterSelectProfile ?? "(unknown)"}', " +
            $"signal='{characterSelectLastSignal}', events={characterSelectEventCount}, " +
            $"pollChanges={characterSelectPollChangeCount}, sessionChanges={characterSelectSessionChangeCount}, " +
            $"recapture={characterSelectRecaptureActive}, " +
            $"ipcError='{characterSelectLastIpcError ?? "none"}'.");
        if (!string.IsNullOrWhiteSpace(lastError))
            ChatGui.PrintError($"Last error: {lastError}", "SpotifyTrackHonorific");
    }

    private void PrintNow()
    {
        var renderTrack = GetCurrentRenderTrack();
        if (renderTrack == null)
        {
            ChatGui.Print($"No active track cached. State: {lastState}.");
            return;
        }

        ChatGui.Print($"Spotify: {renderTrack.ArtistText} - {renderTrack.Name} | Album: {renderTrack.Album} | Local: {renderTrack.IsLocal}");
        ChatGui.Print($"Honorific title: {BuildConfiguredTitle(renderTrack, lastTrackPaused)} | {(config.IsPrefix ? "Prefix" : "Suffix")}");
    }

    private void PrintDash()
    {
        ChatGui.Print("You found Dash's drawer.");
        ChatGui.Print(
            $"{{version}}  -> SpotifyTrackHonorific v{DisplayVersion}");
        ChatGui.Print(
            "{sth}      -> SpotifyTrackHonorific");
        ChatGui.Print(
            $"{{session}}  -> {BuildListeningSessionText()}");
        ChatGui.Print(
            "{womm}     -> works on my machine");
    }

    private static void PrintHelp()
    {
        ChatGui.Print("SpotifyTrackHonorific commands:");
        ChatGui.Print("/sth                    - open settings");
        ChatGui.Print("/sth auth <client-id>   - authenticate with Spotify using PKCE");
        ChatGui.Print("/sth status             - show auth/poll state");
        ChatGui.Print("/sth now                - show the detected current track/title");
        ChatGui.Print("/sth retry              - reset backoff and retry Spotify now");
        ChatGui.Print("/sth ipc-test           - test Honorific without Spotify");
        ChatGui.Print("/sth clear              - clear this plugin's title");
        ChatGui.Print("/sth enable|disable     - toggle Spotify polling");
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);
}

