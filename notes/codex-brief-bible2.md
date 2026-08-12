# Task: Polish pass on the game bible (bible/generate.py → regenerate bible/index.html)

The current page reads like a datamine dump. Make it read like a fan-made game wiki. Work on `bible/generate.py`, regenerate `bible/index.html`, verify with python that the HTML parses and the embedded JS is syntactically fine. Do not touch anything outside `bible/`.

User feedback (from a phone screenshot of the live page):

1. **Kill the internal-asset-name subtitle.** Cards currently show e.g. "Empowered Strikes" with a monospace subtitle "Empowered Strikes BOON MASTER" — remove that subtitle line entirely. Display name only.
2. **Kill all "raw" annotations.** No "(raw 200/100)", no "(raw 80/100)". Just say "×2 crit chance" / "×0.8 spell damage".
3. **Kill the giant effect walls.** The Modification decoder currently dumps entire nested attack chains — e.g. Empowered Strikes renders 30+ lines of "perform attack — damage 10 · magic cost 0 · poise ×1 · stun 0s · knockback (0.5, 1, 0) · hit frequency 0 · blockable, parryable, dodgeable, hits stunned — constant of Light1; applies to self; ..." repeated per attack variant. Replace with a COMPACT summary policy:
   - Max ~2 short effect lines per card. The in-game `description` field is the primary text; effect lines only ADD the key numbers (multipliers, flat amounts, durations, stack counts, trigger conditions).
   - When a mod performs attacks: summarize as one clause, e.g. "adds projectile attacks (~10–15 dmg)" — never enumerate per-variant stats, never print hit-flag lists (blockable/parryable/dodgeable/hits stunned), never print zero-valued fields (magic cost 0, stun 0s, hit frequency 0, poise ×1).
   - General rule: omit any field at its neutral/default value.
4. **No empty icon boxes.** If an entry has no icon, render the card without the icon slot (no white placeholder square).
5. **Wording sweep:** nothing on the page may say "dump", "dumped", "datamine", "extracted", "assets", "$name", or show object ids like "Boon#12". Header/footer should read like a fan wiki (e.g. "Dungeon Lurker Compendium — demo version data"). Keep the search box.
6. Keep everything else: sections, counts, icons, search, dark theme, sortable-ish tables for enemies/shops.

After regenerating `bible/index.html`, ALSO regenerate the self-contained single-file version at `bible/DungeonLurkerBible.html` by inlining the icons as base64 data URIs (same as before — a `src="icons/..."` → data URI pass; icons live in `bible/dump/icons/`).

Print a short summary of what changed when done.
