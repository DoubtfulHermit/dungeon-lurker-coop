#!/usr/bin/env python3
"""Generate the Dungeon Lurker game bible from the Unity runtime dump."""

from __future__ import annotations

import argparse
import base64
import html
import json
import mimetypes
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent
DEFAULT_INPUT = ROOT / "dump" / "bible.json"
DEFAULT_OUTPUT = ROOT / "index.html"
DEFAULT_STANDALONE = ROOT / "DungeonLurkerBible.html"


def esc(value: Any) -> str:
    return html.escape(str(value), quote=True)


def num(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def human(value: Any) -> str:
    value = str(value or "")
    value = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", value)
    value = value.replace("Spd", "Speed").replace("Dmg", "Damage").replace("Amt", "Amount")
    return re.sub(r"\s+", " ", value).strip()


def display_name(name: str) -> str:
    name = re.sub(r"^(ITEM|ENEMY)\s+", "", name, flags=re.I)
    name = re.sub(r"\(Clone\)$", "", name, flags=re.I)
    name = re.sub(
        r"\s+(BOON MASTER|SPELL SLOT|TONIC MASTER|BLADE MASTER|CHARM MASTER)$",
        "",
        name,
        flags=re.I,
    )
    return name.strip()


class Bible:
    def __init__(self, data: dict[str, Any]):
        self.data = data
        self.objects: dict[str, dict[str, Any]] = data["objects"]

    def rows(self, type_name: str) -> list[tuple[str, dict[str, Any]]]:
        return [(key, value) for key, value in self.objects.items() if key.startswith(type_name + "#")]

    def resolve(self, value: Any) -> dict[str, Any] | None:
        if isinstance(value, dict) and "$ref" in value:
            return self.objects.get(value["$ref"])
        if isinstance(value, str) and "#" in value:
            return self.objects.get(value)
        return value if isinstance(value, dict) else None

    @staticmethod
    def ref_name(value: Any) -> str:
        if isinstance(value, dict):
            # Object keys are implementation details, not useful names for readers.
            return str(value.get("$name") or value.get("$gameObject") or "")
        return str(value or "")

    @staticmethod
    def icon(obj: dict[str, Any], label: str) -> str:
        icon = obj.get("icon")
        if isinstance(icon, dict) and icon.get("$file"):
            src = icon["$file"].lstrip("/")
            return (
                f'<img class="icon" src="{esc(src)}" alt="{esc(label)}" '
                'loading="lazy" onerror="this.remove()">'
            )
        return ""

    def attack_line(self, attack: dict[str, Any] | None) -> str:
        if not attack:
            return "Attack details unavailable"
        bits = [f"{num(attack.get('damage', 0))} damage"]
        if attack.get("magicCost"):
            bits.append(f"{num(attack['magicCost'])} magic")
        if attack.get("poiseMult", 1) != 1:
            bits.append(f"×{num(attack['poiseMult'])} poise")
        if attack.get("stunTime"):
            bits.append(f"{num(attack['stunTime'])}s stun")
        if attack.get("knockback", "(0, 0, 0)") != "(0, 0, 0)":
            bits.append(f"knockback {attack['knockback']}")
        if attack.get("hitFrequency"):
            bits.append(f"{num(attack['hitFrequency'])}s hit interval")
        return " · ".join(bits)

    def trigger_line(self, mod: dict[str, Any]) -> str:
        trigger = str(mod.get("trigger", "Passive"))
        pieces: list[str] = []
        if trigger == "Passive":
            pass
        elif trigger == "Attack":
            attack_trigger = human(mod.get("attackTrigger", "Constant")).lower()
            target_names = [human(x).lower() for x in re.split(r",\s*", str(mod.get("targetAttack", "0"))) if x != "0"]
            if target_names and all(x.startswith("spell") for x in target_names):
                target = "casting a spell"
            elif len(target_names) > 3 and all("spell" not in x for x in target_names):
                target = "weapon attacks"
            else:
                target = ", ".join(target_names) if target_names else "an attack"
            pieces.append(f"after {target}" if attack_trigger == "on end" else f"on {target}")
        elif trigger in ("Health", "Magic"):
            op = "≥" if mod.get("greaterThan", True) else "≤"
            pieces.append(f"when {trigger.lower()} {op} {num(mod.get('triggerAmount', 0))}")
        elif trigger == "State":
            conditions = [x for x in (str(mod.get("actState", "0")), str(mod.get("posState", "0"))) if x != "0"]
            pieces.append("while " + (" / ".join(conditions) if conditions else "state condition is met"))
        elif trigger == "Overkill":
            pieces.append(f"on overkill ≥ {num(mod.get('triggerAmount', 0))}")
        else:
            pieces.append("on " + human(trigger).lower())
        if mod.get("useTimer"):
            pieces.append(f"every {num(mod.get('frequency', 1))}s")
        if mod.get("useProbability"):
            pieces.append(f"chance range {mod.get('triggerChance', '(0, 1)')}")
        if mod.get("triggerOnce"):
            pieces.append("once")
        return ", ".join(pieces)

    def attack_summary(self, mods: list[dict[str, Any]]) -> str:
        damages = []
        for mod in mods:
            attack = self.resolve(mod.get("attackData"))
            if attack and float(attack.get("damage", 0) or 0) != 0:
                damages.append(float(attack["damage"]))
        amount = ""
        if damages:
            low, high = min(damages), max(damages)
            amount = f" (~{num(low)} dmg)" if low == high else f" (~{num(low)}–{num(high)} dmg)"
        trigger = self.trigger_line(mods[0]) if len(mods) == 1 else ""
        return f"adds extra attacks{amount}" + (f" {trigger}" if trigger else "")

    def mod_line(self, mod_ref: Any, depth: int = 0, seen: set[str] | None = None) -> str:
        if mod_ref is None:
            return "Empty modification slot in source data"
        mod = self.resolve(mod_ref)
        if not mod:
            return "Unresolved modification"
        seen = set() if seen is None else seen
        identity = self.ref_name(mod_ref) or str(id(mod))
        if identity in seen or depth > 4:
            return "nested effect (cycle)"
        seen.add(identity)
        kind = str(mod.get("type", "Attribute"))
        value = num(mod.get("value", 0))
        if kind == "Attribute":
            attribute = human(mod.get("attribute", "Unknown"))
            if float(mod.get("value", 0) or 0) == 0 and str(mod.get("variableMult", "None")) == "None":
                return ""
            if mod.get("multiplicative"):
                effect = f"×{float(mod.get('value', 0) or 0) / 100:g} {attribute.lower()}"
            else:
                sign = "+" if float(mod.get("value", 0) or 0) >= 0 else ""
                effect = f"{sign}{value} {attribute.lower()}"
            variable = str(mod.get("variableMult", "None"))
            if variable != "None":
                effect += f" × current {human(variable)} (offset {num(mod.get('multOffset', 0))})"
        elif kind == "DamageOverTime":
            if float(mod.get("value", 0) or 0) == 0:
                return ""
            effect = (
                f"{value} damage every {num(mod.get('tickRate', 0.1))}s for "
                f"{num(mod.get('duration', 1))}s, up to {num(mod.get('stackLimit', 1))} stacks"
            )
        elif kind == "Buff":
            nested_mods = [self.resolve(x) for x in mod.get("modList", [])]
            nested_mods = [x for x in nested_mods if x]
            attacks = [x for x in nested_mods if x.get("type") == "Attack"]
            others = [self.mod_line(x, depth + 1, seen.copy()).split(" — ", 1)[0] for x in nested_mods if x.get("type") != "Attack"]
            others = [x for x in others if x]
            inner = self.attack_summary(attacks) if attacks else "; ".join(others[:2])
            effect = inner or "buff"
            duration = float(mod.get("duration", 1) or 0)
            if duration > 0:
                effect += f" for {num(duration)}s"
            elif duration < 0 or mod.get("persistant"):
                effect += " permanently"
            if mod.get("stackLimit", 1) != 1:
                effect += f", up to {num(mod['stackLimit'])} stacks"
        elif kind == "Attack":
            effect = self.attack_summary([mod])
        elif kind == "Health":
            if float(mod.get("value", 0) or 0) == 0:
                return ""
            effect = f"restore {value} health" if float(mod.get("value", 0) or 0) >= 0 else f"lose {abs(float(mod.get('value', 0))):g} health"
        elif kind == "Magic":
            if float(mod.get("value", 0) or 0) == 0:
                return ""
            effect = f"restore {value} magic" if float(mod.get("value", 0) or 0) >= 0 else f"spend {abs(float(mod.get('value', 0))):g} magic"
        elif kind == "Invulnerability":
            effect = "become invulnerable"
            if mod.get("duration"):
                effect += f" for {num(mod['duration'])}s"
        elif kind in ("Knockback", "KnockbackMult"):
            effect = f"{human(kind)} {mod.get('knockback', value)}"
        elif kind == "Custom":
            effect = f"{human(mod.get('customEffect', 'custom effect'))} {value}"
        elif kind in ("Prefab", "Projectile"):
            effect = "spawns a projectile" if kind == "Projectile" else "spawns an effect"
        else:
            effect = f"{human(kind)} {value}"
        trigger = self.trigger_line(mod)
        return effect + (" — " + trigger if trigger else "")

    def effects(self, boon_ref: Any) -> list[str]:
        boon = self.resolve(boon_ref)
        return self.effect_summaries(boon.get("mods", [])) if boon else []

    def effect_summaries(self, mods: list[Any]) -> list[str]:
        resolved = [self.resolve(x) for x in mods]
        resolved = [x for x in resolved if x]
        attacks = [x for x in resolved if x.get("type") == "Attack"]
        lines = [self.mod_line(x) for x in resolved if x.get("type") != "Attack"]
        lines = [x for x in lines if x]
        if attacks:
            lines.append(self.attack_summary(attacks))
        # Two concise additions are enough; the authored description does the explaining.
        return lines[:2]

    def effects_html(self, effects: list[str]) -> str:
        if not effects:
            return ""
        return '<ul class="effects">' + "".join(f"<li>{esc(line)}</li>" for line in effects) + "</ul>"

    def card(self, obj: dict[str, Any], body: str, tags: list[str] | None = None, effects: list[str] | None = None) -> str:
        name = display_name(obj.get("$name", "Unnamed"))
        desc = obj.get("description") or "No description available."
        search = " ".join([name, desc] + (tags or []) + (effects or []))
        tag_html = "".join(f'<span class="tag">{esc(tag)}</span>' for tag in (tags or []) if tag)
        return f'''<article class="card searchable" data-search="{esc(search.lower())}">
          <div class="card-head">{self.icon(obj, name)}<h3>{esc(name)}</h3></div>
          <p class="description">{esc(desc)}</p><div class="tags">{tag_html}</div>{body}
        </article>'''


def page(data: dict[str, Any]) -> tuple[str, dict[str, int]]:
    b = Bible(data)

    attached: set[str] = set()
    for type_name, field in (("Blade", "bladeBoon"), ("Charm", "charmBoon"), ("TonicData", "tonicBoon"), ("Curse", "curseBoon")):
        for _, obj in b.rows(type_name):
            value = obj.get(field)
            if isinstance(value, dict) and value.get("$ref"):
                attached.add(value["$ref"])

    # Blank "New" assets are editor leftovers, not usable fan-facing boons.
    boons = [
        (key, obj) for key, obj in b.rows("Boon")
        if key not in attached
        and not (not obj.get("description") and not obj.get("saveKey") and obj.get("$name", "").lower().startswith("new"))
    ]
    boon_cards = []
    for _, obj in boons:
        effects = b.effect_summaries(obj.get("mods", []))
        flags = [label for field, label in (("startingBoon", "starting"), ("counterBoon", "counter"), ("permanentBoon", "permanent")) if obj.get(field)]
        tags = [obj.get("rarity", "Common"), obj.get("build", "None"), obj.get("aspect", "None")] + flags
        boon_cards.append(b.card(obj, b.effects_html(effects), tags, effects))

    attack_rows = b.rows("AttackData")
    def spell_attack(spell: dict[str, Any]) -> dict[str, Any] | None:
        core = re.sub(r"\b(SPELL|SLOT|DATA)\b", " ", spell.get("$name", ""), flags=re.I)
        words = [x.lower() for x in re.findall(r"[A-Za-z0-9]+", core) if len(x) > 1]
        candidates = []
        for _, attack in attack_rows:
            name = attack.get("$name", "").lower()
            if words and all(word in name for word in words):
                score = ("spell" in name) * 3 + (attack.get("magicCost", 0) > 0) * 2 - ("impact" in name)
                candidates.append((score, attack))
        return max(candidates, key=lambda x: x[0])[1] if candidates else None

    spell_cards = []
    matched_spells = 0
    for _, obj in b.rows("Spell"):
        attack = spell_attack(obj)
        matched_spells += bool(attack)
        body = '<div class="facts"><b>Attack</b><span>' + esc(b.attack_line(attack)) + "</span>"
        body += f'<b>Save key</b><span>{esc(obj.get("saveKey") or "—")}</span>'
        body += f'<b>Spell color</b><span>{esc(obj.get("spellColor", "—"))}</span>'
        body += f'<b>Upgrade</b><span>{esc(b.ref_name(obj.get("upgradedSpellData")) or "—")}</span>'
        body += f'<b>Degrade</b><span>{esc(b.ref_name(obj.get("degradedSpellData")) or "—")}</span></div>'
        tags = [f"cost {num(attack.get('magicCost', 0))}" if attack else "attack unresolved"]
        spell_cards.append(b.card(obj, body, tags, [b.attack_line(attack)]))

    def equipment_cards(type_name: str, boon_field: str) -> list[str]:
        cards = []
        for _, obj in b.rows(type_name):
            effects = b.effects(obj.get(boon_field))
            boon = b.resolve(obj.get(boon_field))
            attached_name = display_name(boon.get("$name", "Unknown") if boon else "Unknown")
            body = f'<div class="facts"><b>Save key</b><span>{esc(obj.get("saveKey") or "—")}</span><b>Bonus</b><span>{esc(attached_name)}</span></div>{b.effects_html(effects)}'
            cards.append(b.card(obj, body, ["unique" if obj.get("unique") else "stackable"], effects))
        return cards

    blade_cards = equipment_cards("Blade", "bladeBoon")
    charm_cards = equipment_cards("Charm", "charmBoon")

    tonic_cards = []
    for _, obj in b.rows("TonicData"):
        effects = b.effects(obj.get("tonicBoon"))
        notable = []
        if obj.get("healthTonic"):
            notable.append(f"health +{num(obj.get('healthGain', 0))}")
        if obj.get("magicTonic"):
            notable.append(f"magic +{num(obj.get('magicGain', 0))}")
        if obj.get("floorLifetime"):
            notable.append(f"{num(obj['floorLifetime'])} floors")
        body = f'<div class="facts"><b>Save key</b><span>{esc(obj.get("saveKey") or "—")}</span></div>{b.effects_html(effects)}'
        tonic_cards.append(b.card(obj, body, notable + (["ephemeral"] if obj.get("ephemeral") else []), effects))

    item_cards = []
    for _, obj in b.rows("ItemData"):
        tags = ["unique" if obj.get("unique") else "stackable"]
        if obj.get("ephemeral"):
            tags.append("ephemeral")
        if obj.get("showOnCurrencyBar"):
            tags.append("currency bar")
        body = f'<div class="facts"><b>Save key</b><span>{esc(obj.get("saveKey") or "—")}</span></div>'
        item_cards.append(b.card(obj, body, tags))

    curse_cards = []
    for _, obj in b.rows("Curse"):
        curse = b.resolve(obj.get("curseBoon"))
        reward = b.resolve(obj.get("rewardBoon"))
        effects = b.effects(obj.get("curseBoon"))
        body = f'<div class="facts"><b>Duration</b><span>{num(obj.get("floorCount", 0))} floor(s)</span>'
        body += f'<b>Curse</b><span>{esc(curse.get("$name", "—") if curse else "—")}</span>'
        body += f'<b>Reward</b><span>{esc(reward.get("$name", "—") if reward else "—")}</span></div>{b.effects_html(effects)}'
        shell = dict(obj)
        shell.setdefault("description", curse.get("description", "") if curse else "")
        shell.setdefault("icon", curse.get("icon") if curse else None)
        curse_cards.append(b.card(shell, body, [f"{obj.get('floorCount', 0)} floors"], effects))

    # Merge presets and live instances into one compact table. Duplicate scene spawns are counted.
    live_by_setting: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for scene, enemies in data.get("sceneEnemies", {}).items():
        for enemy in enemies:
            row = dict(enemy)
            row["scene"] = scene
            live_by_setting[str(enemy.get("actorSettings", "unresolved"))].append(row)
    preset_rows = {key: obj for key, obj in b.rows("ActorSettingPreset")}
    setting_keys = list(preset_rows) + [key for key in live_by_setting if key not in preset_rows]
    enemy_html = []
    for key in setting_keys:
        preset = preset_rows.get(key)
        live = live_by_setting.get(key, [])
        grouped: dict[tuple[Any, ...], list[dict[str, Any]]] = defaultdict(list)
        for enemy in live:
            sig = (enemy.get("enemyName"), enemy.get("maxHealth"), enemy.get("behaviour"), enemy.get("targetingRange"), enemy.get("soulTokens"))
            grouped[sig].append(enemy)
        if grouped:
            live_lines = []
            for sig, group in grouped.items():
                scenes = sorted({x["scene"] for x in group})
                live_lines.append(f"{sig[0]} — {sig[2]} (×{len(group)} in {', '.join(scenes)})")
            names = "<br>".join(esc(x) for x in live_lines)
            health = " / ".join(sorted({num(sig[1]) for sig in grouped}))
            ranges = " / ".join(sorted({num(sig[3]) for sig in grouped}))
            souls = " / ".join(sorted({num(sig[4]) for sig in grouped}))
        else:
            names = '<span class="muted">No live instance captured</span>'
            health = ranges = "—"
            souls = num(preset.get("baseSoulTokenValue", "—")) if preset else "—"
        preset_name = display_name(preset.get("$name", "Enemy settings")) if preset else "Unknown enemy settings"
        preset_facts = "—" if not preset else f"guard {num(preset['damageGuard'])} · poise guard {num(preset['poiseGuard'])} · despawn {num(preset['despawnTime'])}s"
        search = f"{preset_name} {names} {health} {ranges} {souls} {preset_facts}".lower()
        enemy_html.append(f'<tr class="searchable" data-search="{esc(search)}"><td><b>{esc(preset_name)}</b></td><td>{names}</td><td data-sort="{esc(health)}">{esc(health)}</td><td>{esc(ranges)}</td><td>{esc(souls)}</td><td>{esc(preset_facts)}</td></tr>')

    shop_cards = []
    for _, shop in b.rows("ShopStock"):
        lines = []
        search_bits = [shop.get("$name", "")]
        for entry in shop.get("shopStock", []):
            item_name = display_name(b.ref_name(entry.get("data")))
            costs = [f"{num(x.get('currencyAmount', 0))} {display_name(b.ref_name(x.get('currencyItem')))}" for x in entry.get("requiredCurrencies", [])]
            requirements = []
            requirements += ["item: " + display_name(b.ref_name(x)) for x in entry.get("requiredItems", [])]
            requirements += ["variable: " + str(x) for x in entry.get("requiredVariables", []) if x]
            requirements += [f"{x.get('type', 'Stat')} {x.get('flag', '')} ≥ {num(x.get('amount', 0))}" for x in entry.get("requiredStats", [])]
            detail = " · ".join(costs or ["free"])
            if requirements:
                detail += " · requires " + ", ".join(requirements)
            lines.append(f"<li><b>{esc(item_name)}</b><span>{esc(detail)}</span></li>")
            search_bits.extend([item_name, detail])
        shop_cards.append(f'<article class="shop-card searchable" data-search="{esc(" ".join(search_bits).lower())}"><h3>{esc(shop.get("$name", "Shop"))}</h3><ol>{"".join(lines) or "<li>Empty stock</li>"}</ol></article>')
    loot_cards = []
    for _, pool in b.rows("ItemLootPool"):
        entries = []
        for item in pool.get("lootPool", []):
            entries.append(f'<li><b>{esc(display_name(b.ref_name(item.get("item"))))}</b><span>amount {num(item.get("amount", 0))} · weight {num(item.get("weight", 0))}</span></li>')
        search = pool.get("$name", "") + " " + " ".join(re.sub("<[^>]+>", " ", x) for x in entries)
        loot_cards.append(f'<article class="shop-card searchable" data-search="{esc(search.lower())}"><h3>{esc(pool.get("$name", "Loot pool"))}</h3><ol>{"".join(entries)}</ol></article>')

    counts = {
        "boons": len(boons), "boons_total": len(b.rows("Boon")), "spells": len(spell_cards),
        "blades": len(blade_cards), "charms": len(charm_cards), "tonics": len(tonic_cards),
        "items": len(item_cards), "curses": len(curse_cards), "enemy_rows": len(enemy_html),
        "presets": len(preset_rows), "shops": len(shop_cards), "loot_pools": len(loot_cards),
        "matched_spells": matched_spells,
    }

    sections = [
        ("boons", f"Boons <em>{counts['boons']} featured · {counts['boons_total']} total</em>", "Player-facing blessings. Equipment, tonic, and curse helper boons are folded into their parent entries.", "".join(boon_cards), "card-grid"),
        ("spells", f"Spells <em>{counts['spells']}</em>", "Magic costs and notable combat properties for each spell.", "".join(spell_cards), "card-grid wide"),
        ("blades", f"Blades <em>{counts['blades']}</em>", "Weapons and the bonuses that make each one distinct.", "".join(blade_cards), "card-grid"),
        ("charms", f"Charms <em>{counts['charms']}</em>", "Charms with their attached passive effects.", "".join(charm_cards), "card-grid"),
        ("tonics", f"Tonics <em>{counts['tonics']}</em>", "Temporary boosts, restoration values, and their most important effects.", "".join(tonic_cards), "card-grid"),
        ("items", f"Items <em>{counts['items']}</em>", "Currencies, quest objects, consumables, and other inventory finds.", "".join(item_cards), "card-grid compact"),
        ("curses", f"Curses <em>{counts['curses']}</em>", "Floor duration, penalty effects, and promised rewards.", "".join(curse_cards), "card-grid wide"),
    ]
    section_html = "".join(f'<section id="{sid}" data-section><header><h2>{title}</h2><p>{desc}</p></header><div class="{cls}">{content}</div><p class="empty-section" hidden>No matches in this section.</p></section>' for sid, title, desc, content, cls in sections)
    section_html += f'''<section id="enemies" data-section><header><h2>Enemies <em>{counts['enemy_rows']} rows · {counts['presets']} presets</em></h2><p>Enemy encounters are grouped by combat profile, with repeat appearances counted together.</p></header>
      <div class="table-wrap"><table class="sortable"><thead><tr><th>Preset</th><th>Live enemy / behaviour</th><th>Health</th><th>Range</th><th>Souls</th><th>Preset combat facts</th></tr></thead><tbody>{''.join(enemy_html)}</tbody></table></div><p class="empty-section" hidden>No matching enemies.</p></section>'''
    section_html += f'''<section id="shops" data-section><header><h2>Shops &amp; Loot <em>{counts['shops']} shops · {counts['loot_pools']} pools</em></h2><p>Stock costs, unlock requirements, loot amounts, and drop weights.</p></header><div class="shop-grid">{''.join(shop_cards)}{''.join(loot_cards)}</div><p class="empty-section" hidden>No matching stock or loot.</p></section>'''

    nav = "".join(f'<a href="#{sid}">{label}</a>' for sid, label in (("boons", "Boons"), ("spells", "Spells"), ("blades", "Blades"), ("charms", "Charms"), ("tonics", "Tonics"), ("items", "Items"), ("curses", "Curses"), ("enemies", "Enemies"), ("shops", "Shops/Loot")))
    css = r'''
:root{--bg:#0b0a0c;--panel:#151216;--panel2:#1d181b;--ink:#e8ddc5;--muted:#9d9383;--accent:#b6403b;--accent2:#dd6a52;--line:#3a2e31;--gold:#c5a467;color-scheme:dark}*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:radial-gradient(circle at 50% -20%,#332128 0,#100d10 36rem,var(--bg) 70rem);color:var(--ink);font:15px/1.52 Georgia,"Times New Roman",serif}body:before{content:"";position:fixed;inset:0;pointer-events:none;opacity:.035;background-image:repeating-linear-gradient(0deg,transparent 0 3px,#fff 4px)}a{color:inherit}.hero{padding:68px max(28px,calc((100vw - 1480px)/2));border-bottom:1px solid var(--line);text-align:center}.kicker{color:var(--accent2);font:700 12px/1 sans-serif;letter-spacing:.28em;text-transform:uppercase}.hero h1{font-size:clamp(42px,6vw,82px);line-height:.95;margin:18px 0 15px;text-transform:uppercase;letter-spacing:.04em;text-shadow:0 3px 0 #000}.hero p{max-width:720px;margin:auto;color:var(--muted);font-size:18px}.controls{position:sticky;top:0;z-index:20;background:#0c0a0dec;border-bottom:1px solid var(--line);backdrop-filter:blur(12px);padding:11px max(22px,calc((100vw - 1480px)/2))}.control-row{display:flex;align-items:center;gap:13px}.search{width:min(460px,40vw);border:1px solid #594044;background:#171216;color:var(--ink);padding:12px 15px;font:600 14px Georgia,serif;outline:none}.search:focus{border-color:var(--accent2);box-shadow:0 0 0 2px #b6403b33}.result-count{font:12px/1 sans-serif;color:var(--muted);white-space:nowrap}.nav{display:flex;gap:5px;margin-left:auto;overflow:auto}.nav a{padding:9px 10px;text-decoration:none;color:#c8bcaa;font:700 11px/1 sans-serif;text-transform:uppercase;letter-spacing:.08em;border:1px solid transparent;white-space:nowrap}.nav a:hover{border-color:var(--line);color:#fff}main{max-width:1480px;margin:auto;padding:0 28px 100px}section{scroll-margin-top:76px;padding:60px 0 20px;border-bottom:1px solid #2b2326}section>header{display:flex;align-items:end;justify-content:space-between;gap:28px;margin-bottom:24px}h2{font-size:32px;line-height:1;margin:0;text-transform:uppercase;letter-spacing:.05em}h2:before{content:"✦";color:var(--accent);font-size:.55em;margin-right:12px;vertical-align:.25em}h2 em{display:inline-block;color:var(--gold);font:700 11px/1 sans-serif;letter-spacing:.08em;font-style:normal;margin-left:8px}section>header p{max-width:540px;color:var(--muted);margin:0;text-align:right}.card-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:13px}.card-grid.wide{grid-template-columns:repeat(2,minmax(0,1fr))}.card-grid.compact{grid-template-columns:repeat(4,minmax(0,1fr))}.card{position:relative;min-width:0;background:linear-gradient(145deg,var(--panel2),var(--panel));border:1px solid var(--line);padding:17px;box-shadow:0 8px 22px #0003}.card:before{content:"";position:absolute;inset:5px;border:1px solid #ffffff08;pointer-events:none}.card:hover{border-color:#6a4144;transform:translateY(-1px)}.card-head{display:flex;gap:14px;align-items:center;min-height:60px}.icon{width:48px;height:48px;object-fit:contain;image-rendering:pixelated;flex:0 0 auto;filter:drop-shadow(0 3px 1px #0008)}.card h3,.shop-card h3{margin:0;color:#f5e8cd;font-size:18px;line-height:1.15}.description{color:#c9bdab;margin:13px 0 10px;min-height:2.6em}.tags{display:flex;flex-wrap:wrap;gap:5px;margin-bottom:11px}.tag{border:1px solid #53363a;color:#d6a38c;padding:3px 6px;font:700 9px/1 sans-serif;text-transform:uppercase;letter-spacing:.08em}.effects{margin:10px 0 0;padding:10px 0 0 17px;border-top:1px solid #33282b}.effects li{padding:3px 0;color:#e1d5c1}.effects li::marker{color:var(--accent2)}.linked{color:var(--gold);font-size:12px;border-top:1px solid #33282b;padding-top:10px}.facts{display:grid;grid-template-columns:max-content 1fr;gap:5px 10px;border-top:1px solid #33282b;padding-top:10px}.facts b{color:var(--gold);font-size:12px}.facts span{color:#d0c4b1}.muted{color:var(--muted)}.table-wrap{overflow:auto;border:1px solid var(--line)}table{border-collapse:collapse;width:100%;background:#141114}th{position:sticky;top:57px;background:#241a1e;color:var(--gold);text-align:left;padding:12px;font:700 10px/1 sans-serif;text-transform:uppercase;letter-spacing:.08em;cursor:pointer}td{border-top:1px solid #30262a;padding:12px;vertical-align:top}tbody tr:nth-child(even){background:#191519}tbody tr:hover{background:#21191c}.shop-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:13px}.shop-card{background:var(--panel);border:1px solid var(--line);padding:18px}.shop-card h3{border-bottom:1px solid #3b2c30;padding-bottom:11px}.shop-card ol{padding-left:22px;margin:10px 0 0}.shop-card li{padding:7px 0;border-bottom:1px dotted #342a2d}.shop-card li::marker{color:var(--accent)}.shop-card li span{display:block;color:var(--muted);font-size:12px}.empty-section{padding:30px;text-align:center;border:1px dashed var(--line);color:var(--muted)}footer{padding:30px;text-align:center;color:#706860;font-size:12px}.is-hidden{display:none!important}.no-results{display:none;text-align:center;padding:80px 20px;color:var(--muted);font-size:20px}.no-results.show{display:block}@media(max-width:1100px){.card-grid,.card-grid.compact{grid-template-columns:repeat(2,minmax(0,1fr))}.shop-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.nav{display:none}}@media(max-width:700px){.hero{padding:45px 20px}.controls{padding:9px 15px}.search{width:100%}.result-count{display:none}main{padding:0 15px 70px}.card-grid,.card-grid.wide,.card-grid.compact,.shop-grid{grid-template-columns:1fr}section>header{display:block}section>header p{text-align:left;margin-top:10px}.card{padding:15px}h2{font-size:25px}}
'''
    js = r'''
const search=document.querySelector('#search'), count=document.querySelector('#result-count');
const items=[...document.querySelectorAll('.searchable')];
function filter(){const terms=search.value.toLowerCase().trim().split(/\s+/).filter(Boolean);let visible=0;items.forEach(el=>{const ok=terms.every(t=>el.dataset.search.includes(t));el.classList.toggle('is-hidden',!ok);visible+=ok});document.querySelectorAll('[data-section]').forEach(section=>{const has=[...section.querySelectorAll('.searchable')].some(x=>!x.classList.contains('is-hidden'));section.querySelector('.empty-section')?.toggleAttribute('hidden',has)});count.textContent=terms.length?`${visible} of ${items.length} entries`:`${items.length} searchable entries`;document.querySelector('.no-results').classList.toggle('show',visible===0)}
search.addEventListener('input',filter);document.addEventListener('keydown',e=>{if(e.key==='/'&&document.activeElement!==search){e.preventDefault();search.focus()}});filter();
document.querySelectorAll('table.sortable th').forEach((th,i)=>th.addEventListener('click',()=>{const table=th.closest('table'),body=table.tBodies[0],rows=[...body.rows],asc=th.dataset.dir!=='asc';table.querySelectorAll('th').forEach(x=>delete x.dataset.dir);th.dataset.dir=asc?'asc':'desc';rows.sort((a,b)=>{const av=(a.cells[i].dataset.sort||a.cells[i].innerText).trim(),bv=(b.cells[i].dataset.sort||b.cells[i].innerText).trim(),an=Number(av),bn=Number(bv),cmp=Number.isNaN(an)||Number.isNaN(bn)?av.localeCompare(bv):an-bn;return asc?cmp:-cmp});rows.forEach(r=>body.appendChild(r))}));
'''
    generated = __import__("datetime").datetime.now().astimezone().strftime("%Y-%m-%d %H:%M %Z")
    document = f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Dungeon Lurker — Game Bible</title><style>{css}</style></head><body>
<header class="hero"><div class="kicker">The Unofficial Compendium</div><h1>Dungeon Lurker</h1><p>A fan-made field guide to the demo's blessings, blades, spells, charms, tonics, curses, creatures, shops, and loot.</p></header>
<div class="controls"><div class="control-row"><input id="search" class="search" type="search" placeholder="Search names, descriptions, effects…" aria-label="Search game bible" autocomplete="off"><span id="result-count" class="result-count"></span><nav class="nav">{nav}</nav></div></div>
<main>{section_html}<div class="no-results">Nothing in the dungeon matches that search.</div></main><footer>Dungeon Lurker Compendium — demo version data · Updated {esc(generated)}</footer><script>{js}</script></body></html>'''
    return document, counts


def inline_icons(document: str) -> str:
    """Return a portable copy with every local icon embedded in the page."""
    def replace(match: re.Match[str]) -> str:
        relative = match.group(1)
        icon_path = ROOT / "dump" / relative
        if not icon_path.is_file():
            return match.group(0)
        mime = mimetypes.guess_type(icon_path.name)[0] or "application/octet-stream"
        encoded = base64.b64encode(icon_path.read_bytes()).decode("ascii")
        return f'src="data:{mime};base64,{encoded}"'

    return re.sub(r'src="(icons/[^"<>]+)"', replace, document)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--standalone", type=Path, default=DEFAULT_STANDALONE)
    args = parser.parse_args()
    with args.input.open(encoding="utf-8") as handle:
        data = json.load(handle)
    document, counts = page(data)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(document, encoding="utf-8")
    args.standalone.parent.mkdir(parents=True, exist_ok=True)
    args.standalone.write_text(inline_icons(document), encoding="utf-8")
    print(
        f"Generated {args.output} and {args.standalone}: {counts['boons']} featured boons ({counts['boons_total']} total), "
        f"{counts['spells']} spells, {counts['blades']} blades, {counts['charms']} charms, "
        f"{counts['tonics']} tonics, {counts['items']} items, {counts['curses']} curses, "
        f"{counts['enemy_rows']} enemy rows, {counts['shops']} shops, and {counts['loot_pools']} loot pools."
    )


if __name__ == "__main__":
    main()
