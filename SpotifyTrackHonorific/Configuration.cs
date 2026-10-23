using Dalamud.Configuration;
using SpotifyTrackHonorific.Profiles;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpotifyTrackHonorific;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const string DefaultTitleFormat = "♪ {artist} - {track}";
    public const string DefaultContentFilterFallback = "Triggerword censored";
    public const int MaxTitleProfiles = 5;

    public int Version { get; set; } = 15;

    public bool Enabled { get; set; } = true;
    public bool ShowNormalTracks { get; set; } = true;
    public bool ShowLocalTracks { get; set; } = true;
    public bool ClearOnPause { get; set; } = true;

    // v11 optional combat auto-hide. Off by default so updating from v1.0.5
    // preserves existing title behavior until the user explicitly enables it.
    public bool AutoHideInCombat { get; set; } = false;

    // v13 optional interoperability with temporary Honorific-title writers such
    // as PatMeHonorific. Off by default to preserve existing STH ownership rules.
    public bool EnablePatMeHonorificSupport { get; set; } = false;

    public bool IsPrefix { get; set; } = false;
    public string TitleFormat { get; set; } = DefaultTitleFormat;

    // v12 local-only snapshot of the Honorific title that existed before STH
    // replaced it. This is intentionally not part of saved profiles or portable
    // settings because it belongs to the current character/Honorific context.
    public string CachedHonorificTitle { get; set; } = string.Empty;

    // v15 optional Character Select+ / underlying Honorific style matching.
    // Manual STH appearance remains the fallback when this is disabled or no
    // underlying style has been captured for the current character yet.
    public bool MatchCharacterSelectHonorificStyle { get; set; } = false;

    // Character-local style snapshot paired with CachedHonorificTitle.
    // These fields deliberately stay outside saved profiles and portable settings.
    public bool CachedHonorificStyleCaptured { get; set; } = false;
    public Vector3? CachedHonorificColor { get; set; }
    public Vector3? CachedHonorificGlow { get; set; }
    public Vector3? CachedHonorificColor3 { get; set; }
    public int? CachedHonorificGradientColourSet { get; set; }
    public int? CachedHonorificGradientAnimationStyle { get; set; }

    // v3 title-cleanup/presentation options.
    public bool StripBracketedTrackParts { get; set; } = false;
    public bool SmartFitLongTitles { get; set; } = true;

    // v4 Honorific appearance options.
    public bool UseTitleColor { get; set; } = false;
    public Vector3 TitleColor { get; set; } = Vector3.One;
    public bool UseTitleGlow { get; set; } = false;
    public Vector3 TitleGlowColor { get; set; } = new Vector3(0.35f, 0.70f, 1.00f);

    // v5/v6 Honorific supporter gradient/animation options. Honorific itself uses a
    // trust-based supporter toggle; this confirmation deliberately does the same.
    public bool HonorificSupporterConfirmed { get; set; } = false;
    public bool UseSupporterGradient { get; set; } = false;

    // Kept under its old serialized name so v0.0.10 configs migrate cleanly. In v6
    // this selects Honorific's custom three-colour gradient payload (-1) vs a preset.
    public bool UseCustomDualGradient { get; set; } = true;
    public int GradientColourSet { get; set; } = 0;

    // v5 compatibility value. v6 uses GradientAnimationStyle so Honorific's enum can
    // be represented directly in a dropdown instead of as a boolean.
    public bool AnimateGradient { get; set; } = true;
    public int GradientAnimationStyle { get; set; } = 1;

    public Vector3 GradientColorA { get; set; } = new Vector3(0.35f, 0.70f, 1.00f);
    public Vector3 GradientColorB { get; set; } = new Vector3(1.00f, 0.35f, 0.75f);
    public Vector3 GradientColorC { get; set; } = new Vector3(0.35f, 0.70f, 1.00f);

    public string SpotifyClientId { get; set; } = string.Empty;
    public string SpotifyRefreshToken { get; set; } = string.Empty;
    public DateTime SpotifyAuthorizedAtUtc { get; set; } = DateTime.MinValue;

    // v7 release-UI onboarding state. Existing authenticated users migrate as completed.
    public bool OnboardingCompleted { get; set; } = false;

    // v8 content filter. Entries are one per line, with optional artist:/track:/album: prefixes.
    // Action: 0 = censor only matching metadata fields, 1 = clear title, 2 = keep previous title.
    public bool EnableContentFilter { get; set; } = false;
    public bool SmartContentFilterMatching { get; set; } = true;
    public string ContentFilterEntries { get; set; } = string.Empty;
    public int ContentFilterAction { get; set; } = 0;
    public string ContentFilterFallback { get; set; } = DefaultContentFilterFallback;

    // v9 optional built-in triggerword preset. The master preset is maintained by the
    // plugin; user exclusions are stored separately so updates never overwrite the
    // user's custom blacklist or their per-entry choices.
    public bool UseBuiltInContentFilterList { get; set; } = true;
    public string DisabledBuiltInContentFilterEntries { get; set; } = string.Empty;

    // v10 saved display profiles. Profiles intentionally exclude Spotify credentials,
    // onboarding state, the global enabled toggle, and supporter entitlement confirmation.
    public List<TitleProfile> TitleProfiles { get; set; } = new();

    // v14 remembers which saved profile currently owns the loaded display settings.
    // Empty means the current captured settings are Custom.
    public string ActiveTitleProfileName { get; set; } = string.Empty;

    public bool EnsureDefaults()
    {
        var changed = false;

        if (Version < 3)
        {
            StripBracketedTrackParts = false;
            SmartFitLongTitles = true;
            Version = 3;
            changed = true;
        }

        if (Version < 4)
        {
            UseTitleColor = false;
            TitleColor = Vector3.One;
            UseTitleGlow = false;
            TitleGlowColor = new Vector3(0.35f, 0.70f, 1.00f);
            Version = 4;
            changed = true;
        }

        if (Version < 5)
        {
            HonorificSupporterConfirmed = false;
            UseSupporterGradient = false;
            UseCustomDualGradient = true;
            GradientColourSet = 0;
            AnimateGradient = true;
            GradientColorA = new Vector3(0.35f, 0.70f, 1.00f);
            GradientColorB = new Vector3(1.00f, 0.35f, 0.75f);
            Version = 5;
            changed = true;
        }

        if (Version < 6)
        {
            // Carry the v0.0.10 animation checkbox into the enum-backed selector.
            GradientAnimationStyle = AnimateGradient ? 1 : 0;

            // v0.0.10 only supplied two of Honorific's three custom-gradient colour
            // slots. Loop the first colour back into C for a pleasant A -> B -> A
            // default while preserving both colours the user already chose.
            GradientColorC = GradientColorA;
            Version = 6;
            changed = true;
        }

        if (Version < 7)
        {
            // Do not show first-run onboarding to users who already completed Spotify
            // setup in an earlier build. New installs stay false until auth succeeds.
            OnboardingCompleted =
                !string.IsNullOrWhiteSpace(SpotifyClientId) &&
                !string.IsNullOrWhiteSpace(SpotifyRefreshToken);
            Version = 7;
            changed = true;
        }

        if (Version < 8)
        {
            EnableContentFilter = false;
            SmartContentFilterMatching = true;
            ContentFilterEntries = string.Empty;
            ContentFilterAction = 0;
            ContentFilterFallback = DefaultContentFilterFallback;
            Version = 8;
            changed = true;
        }

        if (Version < 9)
        {
            // The content filter itself remains opt-in. Once enabled, the conservative
            // built-in preset is available by default and can be disabled globally or
            // entry-by-entry without touching custom user rules.
            UseBuiltInContentFilterList = true;
            DisabledBuiltInContentFilterEntries = string.Empty;
            Version = 9;
            changed = true;
        }

        if (Version < 10)
        {
            // Existing v1.0.4 users simply start with no saved profiles. All current
            // title, appearance, filter and Spotify connection settings stay untouched.
            TitleProfiles = new List<TitleProfile>();
            Version = 10;
            changed = true;
        }

        if (Version < 11)
        {
            // Combat auto-hide is deliberately opt-in on migration.
            AutoHideInCombat = false;
            Version = 11;
            changed = true;
        }

        if (Version < 12)
        {
            // Existing users start with no cached pre-STH Honorific title. v1.0.10
            // can capture one automatically before its first write, or the user can
            // cache one manually from the Title tab.
            CachedHonorificTitle = string.Empty;
            Version = 12;
            changed = true;
        }

        if (Version < 13)
        {
            // PatMeHonorific interoperability is deliberately opt-in.
            EnablePatMeHonorificSupport = false;
            Version = 13;
            changed = true;
        }

        if (Version < 14)
        {
            // Older builds inferred the active profile only from an exact settings
            // comparison. Start empty and let Plugin reconcile it from saved profiles.
            ActiveTitleProfileName = string.Empty;
            Version = 14;
            changed = true;
        }

        if (Version < 15)
        {
            // Preserve existing STH appearance for upgraded users until they
            // explicitly opt into Character Select+ style matching.
            MatchCharacterSelectHonorificStyle = false;
            CachedHonorificStyleCaptured = false;
            CachedHonorificColor = null;
            CachedHonorificGlow = null;
            CachedHonorificColor3 = null;
            CachedHonorificGradientColourSet = null;
            CachedHonorificGradientAnimationStyle = null;
            Version = 15;
            changed = true;
        }

        // Honorific only applies ordinary glow when a main title colour is present.
        if (UseTitleGlow && !UseTitleColor)
        {
            UseTitleGlow = false;
            changed = true;
        }

        // Never emit supporter-only gradient fields unless the user has explicitly
        // confirmed their entitlement. Turning confirmation off is an immediate gate.
        if (!HonorificSupporterConfirmed && UseSupporterGradient)
        {
            UseSupporterGradient = false;
            changed = true;
        }

        // Gradient styling owns Honorific's Glow field while active, so ordinary glow
        // is disabled to keep the payload unambiguous.
        if (UseSupporterGradient && UseTitleGlow)
        {
            UseTitleGlow = false;
            changed = true;
        }

        if (GradientColourSet < 0)
        {
            GradientColourSet = 0;
            changed = true;
        }

        if (GradientAnimationStyle < 0)
        {
            GradientAnimationStyle = 0;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(TitleFormat))
        {
            TitleFormat = DefaultTitleFormat;
            changed = true;
        }

        if (ContentFilterAction < 0 || ContentFilterAction > 2)
        {
            ContentFilterAction = 0;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(ContentFilterFallback))
        {
            ContentFilterFallback = DefaultContentFilterFallback;
            changed = true;
        }

        SpotifyClientId ??= string.Empty;
        SpotifyRefreshToken ??= string.Empty;
        TitleFormat ??= DefaultTitleFormat;
        CachedHonorificTitle ??= string.Empty;
        ContentFilterEntries ??= string.Empty;
        ContentFilterFallback ??= DefaultContentFilterFallback;
        DisabledBuiltInContentFilterEntries ??= string.Empty;
        TitleProfiles ??= new List<TitleProfile>();
        ActiveTitleProfileName ??= string.Empty;

        if (TitleProfiles.Count > MaxTitleProfiles)
        {
            TitleProfiles.RemoveRange(MaxTitleProfiles, TitleProfiles.Count - MaxTitleProfiles);
            changed = true;
        }

        var usedProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < TitleProfiles.Count; i++)
        {
            var profile = TitleProfiles[i];
            if (profile == null)
            {
                profile = new TitleProfile { Name = $"Profile {i + 1}" };
                TitleProfiles[i] = profile;
                changed = true;
            }

            if (profile.EnsureDefaults(i))
                changed = true;

            var baseName = profile.Name;
            var uniqueName = baseName;
            var suffix = 2;
            while (!usedProfileNames.Add(uniqueName))
                uniqueName = $"{baseName} {suffix++}";

            if (!string.Equals(uniqueName, profile.Name, StringComparison.Ordinal))
            {
                profile.Name = uniqueName;
                changed = true;
            }
        }

        return changed;
    }
}
