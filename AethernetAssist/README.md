# AethernetAssist

Dalamud API 15 / .NET 10

Current stable: v3.0.0.0
Testing release candidate: v3.0.9.0

## Features

- City Aethernet navigation and attunement checks
- Housing ward and residential Aethernet travel
- Supported housing gate navigation
- Cosmic Exploration planet and local Aethernet travel
- Overworld Aetheryte and supported map navigation
- European Light and Chaos World Visit
- Automatic inheritance of the active Dalamud theme

## DC Travel

Available at eligible main-city Aetherytes in
Limsa Lominsa, New Gridania and Ul'dah.

All 16 public Light and Chaos worlds appear in
two grouped columns.

Selecting a destination initiates the native
World Visit workflow. The plugin:

1. Opens the native World Visit menu.
2. Selects Light or Chaos when necessary.
3. Validates the displayed eight-world list.
4. Selects the exact requested world.
5. Automatically confirms only the exact native
   destination-specific confirmation prompt.
6. Monitors the native queue and arrival.

Unknown prompts and disabled confirmation buttons
are not accepted automatically.

FFXIV controls accepted and queued transfers.
Stopping plugin monitoring cannot cancel a
World Visit that FFXIV has already accepted.

## Appearance

AethernetAssist inherits the client's active
Dalamud/ImGui colors and styling dynamically.
There is no separate theme selector or saved
theme palette.

## Commands

- /anet - Open the navigation window
- /anet housing - Open Housing
- /anet settings - Open settings
- /anet debug - Toggle diagnostics
- /anet cancel - Stop supported automation
- /anet sinus - Cosmic travel to Sinus Ardorum
- /anet phaenna - Cosmic travel to Phaenna
- /anet oizys - Cosmic travel to Oizys
- /anet auxesia - Cosmic travel to Auxesia

## Testing and release policy

Version 3.0.9.0 is a testing release candidate.
Stable remains v3.0.0.0 until regression tests
and explicit stable promotion.

Historical development records remain available
in KNOWN_ISSUES.md and Git history.