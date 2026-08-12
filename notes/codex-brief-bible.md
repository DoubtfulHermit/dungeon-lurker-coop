# Task: Generate the Dungeon Lurker game bible (single HTML page)

Input: `bible/dump/bible.json` (runtime dump of every game data object) and `bible/dump/icons/*.png` (154 sprite exports). Decompiled game source in `reference/decompiled/` if you need to understand semantics of a field (e.g. read `Modification.cs`, `Boon.cs`, `Spell.cs`, `AttackData.cs`, `TonicData.cs`, `ActorSettingPreset.cs`, `Hurtbox.cs`).

Deliverable: `bible/index.html` — a polished, self-contained page (may reference `dump/icons/` by relative path; embed the data as inline JSON or pre-rendered HTML, your choice). Plus a small generator script `bible/generate.py` that produced it (so it can be re-run after a fuller dump). Python 3 stdlib only.

## Data shape
`bible.json` = `{"objects": {"<Type>#<n>": {...fields...}}, "sceneEnemies": {"<scene>": [...]}}`.
Object references look like `{"$ref": "Modification#59", "$name": "..."}` — resolve them.
Sprites look like `{"$sprite": "name", "$file": "icons/name.png"}`.

## Required sections (tabs or anchored sections, with a global search box)
1. **Boons** (105): icon, asset name, description, rarity, build, aspect, starting/counter/permanent flags, and — THE IMPORTANT PART — resolve each boon's `mods` ($ref → Modification) into a human-readable effect line using the Modification fields (attribute type + amount + trigger + conditions). Read `Modification.cs` to map enums correctly (attribute types like MoveSpd/Damage etc., triggers like Passive/OnHit/Death, trigger amounts, durations). Show raw numbers.
2. **Spells** (21): icon, name, description, magic cost fields, and resolve the spell's attack ($ref AttackData) → damage, knockback, hit properties.
3. **Blades** (19) and **Charms** (11): their fields + their attached boon's resolved effects.
4. **Tonics** (20) and **Items** (53): icon, name, description, notable fields.
5. **Curses** (3): fields + resolved mods if any.
6. **Enemies**: merge `ActorSettingPreset` objects (7) with `sceneEnemies` live data (name, maxHealth, behaviour, targetingRange, soulTokens). One table.
7. **Shops/Loot**: `ShopStock` (7) contents; any `ItemLootPool`-ish objects if present.

## Style
Dark theme fitting a gothic pixel dungeon crawler (near-black background, parchment-ish text, one accent color). Icons rendered with `image-rendering: pixelated` at 2-3x their native size. Tables: sortable is nice-to-have, filter-as-you-type search across name+description+effects is REQUIRED (small vanilla JS). No external assets/CDN — everything local/inline. Readable on a 1600px screen.

## Quality bar
- Do NOT show internal plumbing objects (ActionAnim, Effect, DebugFlagBundle, PixelPost, CRTPost, SceneData, ExternalBehaviorTree, AnimationPlayableAsset, PhysSettingPreset) as sections — they're only for reference resolution.
- Where description is empty and the object looks internal (e.g. "* BLADE BOON" helper boons), fold it into its parent (blade/charm) rather than listing standalone. Use judgment: the page is for a PLAYER/fan, not a datamine dump — but keep exact numbers visible.
- Verify the final HTML opens without JS errors: you can sanity-check by parsing your own JSON embed with python.
- Sections get item counts in their headers.

When done, print a one-paragraph summary of what's in the bible (counts per section, anything surprising in the data).
