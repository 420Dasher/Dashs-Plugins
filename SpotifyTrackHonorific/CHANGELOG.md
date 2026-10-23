# Changelog
## 1.0.19 DEV1 - Theme and Character Style Matching

- STH's custom header accent now follows the active Dalamud theme automatically while the rest of the interface continues using native Dalamud styling.
- Adds optional Character Select+ Honorific style matching.
- Captures the underlying Honorific text color, glow, third color, gradient preset, and animation after Character Select+ hand-offs.
- Detects style-only Character Select+ changes even when two profiles use the same Honorific title text.
- Manual STH appearance settings remain the fallback when matching is disabled or no current-character style has been captured.
- Character-local Honorific style data stays outside saved title profiles and portable settings.
## 1.0.18 - Retained Title Recovery

- Keeps the previous safe Spotify title recoverable when the content filter is set to Keep previous title.
- Restores that retained title after teleporting, zoning, combat auto-hide, and Character Select+ hand-offs.
- Keeps the last Spotify title recoverable while playback is stopped when Hide title while playback is paused or stopped is disabled.
- Separates current Honorific IPC ownership from the last genuine Spotify title that is safe to restore.
- Permanent clears, disabled tracks, content-filter Clear title behavior, logout, and Spotify disconnect still discard retained title state.
- Manual Honorific / IPC test titles are excluded from retained-title recovery.
## 1.0.17 - Zone Transition Honorific Rebind

- Detects territory changes and invalidates STH's stale Honorific ownership state.
- Waits for the replacement local-player object and re-sends the current Spotify/cycle title.
- Verifies that Honorific actually accepted the IPC title before considering the rebind complete.
- Retries the rebind for up to 10 seconds if the player object is not ready yet.
- Prevents Honorific's temporary zoning notifications from being mistaken for PatMeHonorific overrides.
- Keeps v1.0.16's duplicate-write suppression during normal playback.
## 1.0.16 - Profile Persistence and Honorific Compatibility

- Saved profile identity now persists across plugin reloads instead of relying only on an exact settings comparison at startup.
- Loading, saving, updating, renaming, and deleting saved profiles now keep the active-profile indicator synchronized correctly.
- Changing settings captured by the active profile still changes the indicator to `Custom`, and that state persists across reloads.
- Configuration schema advances from v13 to v14 for the persisted active saved-profile identity.
- Updated Honorific compatibility for Honorific 1.7.5.1 and newer delayed/rate-limited local-title change announcements.
- STH now verifies Honorific's current title before treating a delayed title-change announcement as an external temporary override.
- Reduced redundant Honorific IPC writes while cycle formatting remains on the same visible stage.
- Verified `{honorific}` cycle stages continue rotating correctly with PatMeHonorific compatibility enabled.
- Spotify polling, authentication, content-filter matching, Character Select+ compatibility, and portable-settings format are otherwise unchanged.
## 1.0.15 - UI Refresh

- Refreshed the settings window with a cleaner native Dalamud-style layout and more consistent spacing.
- Added a compact status header for title updates, Spotify, Honorific, and the active profile.
- Reworked the Dashboard around title updates, playback behavior, quick profiles, connection tools, and compatibility options.
- Moved detailed saved-profile management behind the Dashboard quick-profile section.
- Simplified the Title tab so title formatting and live preview are the primary focus.
- Reworked the format builder with separate controls for inserting variables into the final Custom format and into cycle stages.
- Improved the cycle builder with clearer stage instructions, stage-variable buttons, a cycle preview, and one-click insertion of the finished cycle into Custom format.
- Reorganized the Filter tab around filtering status, rule sources, custom rules, match behavior, and rule testing.
- Moved custom-rule cleanup, clearing, and bulk editing into a collapsed management section.
- Kept advanced Honorific, formatting-reference, built-in-rule customization, and testing controls collapsed until needed.
- No Spotify polling, authentication, Honorific IPC, Character Select+ compatibility, content-filter matching logic, saved-profile format, portable-settings format, or configuration-schema behavior was changed.
## 1.0.14 - Character Select+ Compatibility

- Added automatic Character Select+ profile-switch detection for the normal Character Grid workflow.
- STH now temporarily relinquishes its Honorific IPC title during a Character Select+ switch so the newly forced underlying Honorific title can be captured correctly.
- Added a session-file fallback for Character Select+ workflows where its current-character IPC remains empty and its character-changed IPC event is not emitted.
- Character switches now refresh the cached {honorific} title automatically without requiring STH to be toggled off and on.
- Kept the existing Character Select+ IPC event and polling paths as compatibility fallbacks for workflows that do provide them.
- Reduced session-file overhead by only reading it when its write timestamp changes.
- Improved /sth status Character Select+ diagnostics with separate session-change tracking while ignoring empty IPC polling results once the session fallback is active.
- No Spotify polling, authentication, content filtering, saved-profile format, portable-settings format, or configuration-schema behavior was changed.

## 1.0.11 - Better Profile Management

- Expanded saved-profile management with Update selected, Rename selected, Duplicate, Move up, and Move down actions.
- Update selected overwrites the selected profile from the current settings while preserving its name.
- Rename selected rejects duplicate profile names case-insensitively and keeps the existing 48-character name limit.
- Duplicate clones the selected profile, inserts the copy directly after it, and generates a unique Copy / Copy 2 style name.
- Reordering saved profiles also reorders the Home-tab Quick profile buttons because both use the same persisted profile list.
- Existing Load profile, Save current, Delete profile, five-profile limit, profile persistence, and portable-profile serialization remain compatible.
- Configuration schema remains v12; no migration is required.
- Spotify polling/recovery, Honorific cached-title synchronization, content-filter matching, and portable-settings format are unchanged.
## 1.0.10 - Quick Profiles and Honorific Cycle Titles

- Added Quick profiles to the Home tab with one-click saved-profile switching and a Current profile indicator that reports `Custom` when captured profile settings differ.
- Added the `{honorific}` formatting variable, usable anywhere a normal title variable is accepted, including inside `{cycle:...}` stages.
- Added a local cached Honorific-title value with manual Cache current Honorific title and Clear cached title controls.
- Added automatic Honorific-title synchronization while STH is disabled so changing the normal Honorific title updates the cached `{honorific}` value without requiring a manual cache action.
- The cached Honorific title freezes while STH is enabled, keeping the `{honorific}` cycle stage stable while Spotify output owns the visible title.
- Added self-title protection so STH refuses to cache its own active Spotify title as the original Honorific title.
- Cached Honorific text persists locally across plugin reloads but is intentionally excluded from saved profiles and portable settings.
- Configuration schema advances from v11 to v12 for the local cached Honorific title. Portable-settings schema remains unchanged.
- Spotify polling/recovery and content-filter matching are unchanged.
## 1.0.9 - Title Format Builder

- Added a Title-tab format builder with clickable insert buttons for the formatter's existing Spotify variables.
- Variable buttons are sourced from `TitleTemplateFormatter.SupportedVariables` so the UI stays aligned with the formatter's supported token list.
- Added one-click Copy format and Reset format to default actions.
- Added a cycle builder with configurable seconds per stage, editable pipe-separated stages, generated-token preview, and one-click append.
- Added non-destructive warnings for obvious malformed cycle syntax, including missing closing braces, invalid/non-positive seconds, missing stages, and unsupported nested cycle blocks.
- Existing live preview updates immediately after builder changes and continues to use the normal formatting/filter/smart-fit path.
- No configuration-schema migration is required.
- `TitleTemplateFormatter.cs`, `ContentFilterMatcher.cs`, Spotify polling/recovery, Honorific rendering, profiles, and portable-settings format are unchanged.

## 1.0.8 - Blacklist UX

- Reworked custom blacklist management with quick-add controls for all-fields, artist, track, and album scoped rules.
- Added case-insensitive duplicate prevention when quick-adding custom rules.
- Added search/filtering for saved custom blacklist entries and per-entry Remove buttons.
- Added `Clean + sort` to trim entries, remove case-insensitive duplicates, and alphabetize the custom list.
- Added a confirmed `Clear custom entries` action while retaining the raw multiline editor for bulk paste/edit workflows.
- Improved the matcher test panel with All fields / Artist only / Track only / Album only testing so scoped rules can be validated without misleading cross-field matches.
- Added clearer warnings when a custom scoped term overlaps an active built-in triggerword, since built-in triggerwords remain intentionally all-fields.
- Fixed a development build compile issue by importing LINQ for the built-in overlap check.
- Repaired documentation-only UTF-8 mojibake in the v1.0.7 changelog entry.
- ContentFilterMatcher.cs, matching behavior, Spotify polling/recovery, Honorific rendering, and configuration schema remain unchanged.
## 1.0.7 - Copy Diagnostics

- Added a one-click `Copy diagnostics` action to the Advanced tab for easier bug reports and support.
- Diagnostics include plugin version, enable/auth state, Spotify state, polling reliability, Honorific detection, cached playback state, combat visibility settings, formatting/filter flags, and whether an error is currently present.
- Diagnostic output deliberately excludes Spotify Client ID, OAuth tokens, track names, and artist names.
- Fixed a development-only UTF-8 encoding regression discovered during v1.0.7 testing; the final release preserves the intended `♪` and `» ... «` UI/template symbols.
- No Spotify polling, quota/backoff, content-filter matching, Honorific title rendering, authentication, or configuration-schema behavior was changed.
## 1.0.6 - Combat Auto-Hide

- Added one opt-in setting: `Hide Spotify title during combat`.
- Entering combat clears only STH's Honorific title while Spotify polling continues normally.
- Leaving combat restores the latest cached title immediately instead of waiting for the next Spotify API poll.
- The setting is global rather than profile-specific.
- Portable settings advance to format v2 and include the combat auto-hide preference.
- Existing v1.0.5 portable settings remain import-compatible and leave the new preference unchanged.
- Configuration schema advances from v10 to v11 with combat auto-hide disabled on migration.

## 1.0.5 - Profiles, Portable Settings and Resume Detection

- Added up to five named profiles that capture title, playback, appearance and content-filter settings.
- Saving an existing profile name overwrites it; profiles can be loaded or deleted directly from the Title tab.
- Enhanced the live preview to show current/example source, prefix/suffix position, exact Honorific output, and pre-fit text when smart fitting changes it.
- Added portable JSON settings export/import through the clipboard for backup or transfer between installations.
- Portable exports include current display/filter settings and saved profiles while deliberately excluding Spotify Client ID, refresh token, onboarding/auth state, global enable state, and Honorific supporter-entitlement confirmation.
- Configuration schema advances to v10; existing v1.0.4 settings migrate in place and preserve the existing Spotify connection.
- Paused playback now remains on the normal ~15-second polling cadence so playback resume is detected without a manual retry.
- Truly idle/not-playing playback remains on the ~60-second cadence.
- Existing v1.0.4 content filtering, rate-limit handling and Spotify cooldown behavior are preserved.

## 1.0.4 - Content Filter and Built-In Triggerwords

- Added an optional content filter with custom blacklist entries and smart variation matching.
- Added field-level censoring so matching artist, track, or album metadata is replaced without interrupting `{cycle:...}` title rotation.
- Added optional `artist:`, `track:`, and `album:` scopes for custom blacklist entries.
- Added an optional built-in list of 32 conservative high-sensitivity trigger terms, kept separate from the user's custom blacklist.
- Added a master built-in-list toggle, per-term controls, and Restore built-in defaults.
- Smart matching handles case, punctuation, spacing, common leetspeak forms such as `$uicide`, and conservative typo matching for longer terms.
- Added short-term boundary protection to avoid obvious false positives such as `grape` matching `rape`.
- Default replacement text is `Triggerword censored` and remains user-editable.
- Existing Spotify polling, quota recovery, styling, and title formatting behavior from v1.0.1 is preserved.

## 1.0.3-dev - Field-Level Content Censoring Test Build

- Changed the default blacklist behavior from replacing the entire Honorific title to censoring only the matching Spotify metadata field.
- Artist matches replace only the matching artist name; other credited artists remain visible.
- Track matches replace only `{track}` and album matches replace only `{album}`.
- `{cycle:...}` formatting, elapsed/remaining variables, wrappers, and unaffected metadata continue updating normally while censorship is active.
- The replacement text remains user-editable and defaults to `Triggerword censored`.
- Clear-title and keep-previous-title modes remain available as explicit alternatives.

## 1.0.2-dev - Content Filter Test Build

- Added an optional blacklist/content filter in a dedicated Filter tab.
- Added one-entry-per-line rules with optional `artist:`, `track:`, and `album:` scopes.
- Added Smart Variation Matching for case/spacing/punctuation, common leetspeak substitutions, and conservative typo matching on longer entries.
- Added three match actions: fallback title, clear title, or keep the previous title.
- Default fallback title is `Triggerword censored`.
- Added a built-in matcher test field (pre-filled with `$uicideboy$`).
- Corrected the Advanced tab polling description to the v1.0.1 15s/60s intervals.

## 1.0.1

- Fixed Spotify `Retry-After` values being incorrectly capped at 3600 seconds.
- Added explicit detection of Development Mode `QUOTA_EXCEEDED` 429 responses.
- Added conservative quota cooldown fallback: 1h, 2h, 4h, 8h, then 12h when Spotify provides no retry time.
- Manual retry and settings changes no longer bypass an active Spotify rate/quota cooldown.
- Reduced Web API polling from ~3s playing / ~8s idle to ~15s playing / ~60s idle to lower long-running Development Mode quota usage.
- Progress and `{cycle:...}` templates now advance locally between API polls, preserving smooth title rotation without spending extra Spotify quota.
- Reliability status now renders long cooldowns in readable minute/hour/day form.
- Keeps the last valid Honorific title during temporary Spotify failures as before.

## 1.0.0

- First stable release of SpotifyTrackHonorific.
- Promoted directly from the fully tested v0.0.14 release candidate.
- Standalone Spotify Web API -> Honorific integration with regular and local tracks.
- Configurable templates, rotating titles, cleanup and 32-character smart fitting.
- Honorific color/glow and trust-gated supporter gradient/animation support.
- Rate-limit-aware Spotify reliability and recovery handling.
- Release-oriented Home / Title / Appearance / Advanced settings UI.
- No runtime behavior changes from the passed v0.0.14 RC.

## 0.0.14 - Release Candidate

- Feature freeze for final v1.0 regression testing.
- Release-facing metadata and documentation cleanup.
- Added repeatable release-package helper and RC checklist.
- Centralized the displayed plugin version to avoid mismatched UI/chat version strings.
- No Spotify, formatting, Honorific, supporter-style, configuration-schema, or polling behavior changes intended.

## 0.0.13

- Reworked settings into Home / Title / Appearance / Advanced.
- Added first-run onboarding, clearer status wording, title presets, improved preview, and safer maintenance actions.

## 0.0.12

- Added Spotify reliability/backoff handling, rate-limit awareness, recovery status, manual retry, and concurrency guards.

## 0.0.11

- Added named Honorific gradient-preset and animation-style dropdowns using Honorific's loaded metadata.
- Corrected custom gradients to use all three Honorific color slots.

## 0.0.10

- Added trust-gated Honorific supporter gradient/animation controls.

## 0.0.9

- Added Honorific title color and glow controls.

## 0.0.8

- Improved smart-fit punctuation/separator cleanup.

## 0.0.7

- Added bracketed track-name cleanup and smart-fit title shortening.

## 0.0.6

- Added working `{cycle:SECONDS|...}` formatting.

## 0.0.4

- Added extended Spotify template variables including album, duration, progress, local-file and pause state.

## 0.0.3

- Added the first configuration UI and live title-format updates.

## 0.0.2

- First stable standalone Spotify -> Honorific core with regular tracks, local files, authentication persistence, and no DAH/Discord dependency.
