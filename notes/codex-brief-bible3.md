# Task: Trim pass on the compendium (bible/generate.py → regenerate both HTML files)

Work only in `bible/`. Edit `generate.py`, regenerate `bible/index.html` AND the standalone `bible/DungeonLurkerBible.html`. Verify HTML parses and JS passes `node --check`.

Changes (user feedback):

1. **Remove the Enemies section entirely.**
2. **Remove the Curses section entirely.**
3. **Shops:** remove any "requires <variable>" facts and remove weight values. A shop entry shows just: item name + its token/currency cost. Nothing else.
4. **Remove ALL icons.** Do not reference `dump/icons/` or embed any PNG. In place of every icon, render one small inline SVG placeholder (a simple neutral diamond/shield glyph matching the dark theme, ~32px, same for all entries). The standalone file therefore needs NO base64 image embedding anymore — both files become identical in content; still write both paths.
5. **Remove saveKey** from every card/fact list — never display it.
6. **Remove these fields wherever shown:** spell color (any color swatch/hex on spells), "upgrade"/"degrade" facts (upgrade paths / degrade references on items/spells/boons).
7. Everything else stays: sections (Boons, Spells, Blades, Charms, Tonics, Items, Shops), counts in headers, search, compact effect lines, dark theme.

Print a one-line summary per change when done.
