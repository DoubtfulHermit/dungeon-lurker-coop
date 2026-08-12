#!/usr/bin/env python3
"""Generate the Dungeon Lurker game bible from the Unity runtime dump."""

from __future__ import annotations

import argparse
import html
import json
import re
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
    words = {
        "Spd": "Speed", "Dmg": "Damage", "Amt": "Amount",
        "Def": "Defense", "Gen": "Generation", "Mult": "Multiplier",
    }
    value = " ".join(words.get(word, word) for word in value.split())
    return re.sub(r"\s+", " ", value).strip()


def percent(value: Any) -> str:
    """Format a data ratio as a reader-facing percentage."""
    amount = float(value) * 100
    # Runtime thresholds sometimes include a tiny epsilon (for example .501).
    # Tooltips should show the intended whole percentage, not that telemetry.
    if abs(amount - round(amount)) <= 0.1000001:
        amount = float(round(amount))
    return f"{num(amount)}%"


def scrub_vectors(line: str) -> str:
    """Never expose Unity Vector2/Vector3 values in reader-facing copy."""
    number = r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)"
    vector = rf"\(\s*{number}\s*,\s*{number}(?:\s*,\s*{number})?\s*\)"
    line = re.sub(rf"\s*{vector}", "", line)
    return re.sub(r"\s+([,;])", r"\1", re.sub(r"\s{2,}", " ", line)).strip(" ,;—")


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
    def glyph() -> str:
        return (
            '<svg class="glyph" viewBox="0 0 32 32" aria-hidden="true">'
            '<path d="M16 3 27 10v12l-11 7L5 22V10Z"/>'
            '<path d="m16 8 6 8-6 8-6-8Z"/>'
            '</svg>'
        )

    def attack_line(self, attack: dict[str, Any] | None) -> str:
        if not attack:
            return "Attack details unavailable"
        bits = [f"{num(attack.get('damage', 0))} damage"]
        if attack.get("magicCost"):
            bits.append(f"{num(attack['magicCost'])} magic")
        if attack.get("poiseMult", 1) != 1:
            change = (float(attack["poiseMult"]) - 1) * 100
            quality = "more" if change > 0 else "less"
            bits.append(f"{num(abs(change))}% {quality} poise damage")
        if attack.get("stunTime"):
            bits.append(f"{num(attack['stunTime'])}s stun")
        if attack.get("knockback", "(0, 0, 0)") != "(0, 0, 0)":
            bits.append("knockback")
        if attack.get("hitFrequency"):
            bits.append(f"hits every {num(attack['hitFrequency'])}s")
        return " · ".join(bits)

    @staticmethod
    def attack_target(value: Any) -> str:
        names = [x.strip() for x in str(value or "0").split(",") if x.strip() not in ("0", "-1")]
        if not names:
            return "any attack"
        if all(x.startswith("Spell") for x in names):
            return "casting a spell"
        groups = []
        for name in names:
            if name.startswith("HeavyHold"):
                group = "held heavy attacks"
            elif name.startswith("Jump"):
                group = "jump attacks"
            elif name.startswith("Sprint"):
                group = "sprint attacks"
            elif name.startswith("Heavy"):
                group = "heavy attacks"
            elif name.startswith("Light"):
                group = "light attacks"
            elif name.startswith("Spell"):
                group = "spells"
            else:
                group = human(re.sub(r"\d+$", "", name)).lower()
            if group not in groups:
                groups.append(group)
        if len(groups) > 3:
            return "weapon attacks"
        return ", ".join(groups[:-1]) + (" and " if len(groups) > 1 else "") + groups[-1]

    @staticmethod
    def state_condition(mod: dict[str, Any]) -> str:
        groups = []
        for field in ("actState", "posState"):
            states = [human(x).lower() for x in str(mod.get(field, "0")).split(",") if x.strip() not in ("0", "-1")]
            if states:
                groups.append(" or ".join(states))
        return " and ".join(groups) or "the condition is met"

    @staticmethod
    def chance_text(value: Any) -> str:
        """Probability curves are implementation detail; scalar chances are useful."""
        if isinstance(value, (list, tuple)) or re.fullmatch(
            r"\s*\([^)]*,[^)]*\)\s*", str(value or "")
        ):
            return ""
        try:
            chance = float(value)
        except (TypeError, ValueError):
            return ""
        if chance < 0:
            return ""
        chance = chance * 100 if chance <= 1 else chance
        return f"{num(chance)}% chance"

    def trigger_line(self, mod: dict[str, Any]) -> str:
        trigger = str(mod.get("trigger", "Passive"))
        pieces: list[str] = []
        if trigger == "Passive":
            pass
        elif trigger == "Attack":
            attack_trigger = human(mod.get("attackTrigger", "Constant")).lower()
            target = self.attack_target(mod.get("targetAttack", "0"))
            pieces.append(f"after {target}" if attack_trigger == "on end" else f"on {target}")
        elif trigger in ("Health", "Magic"):
            amount = float(mod.get("triggerAmount", 0) or 0)
            threshold = percent(amount) if abs(amount) <= 1 else num(amount)
            relation = "above" if mod.get("greaterThan", True) else "below"
            pieces.append(f"when {trigger.lower()} is {relation} {threshold}")
        elif trigger == "State":
            pieces.append("while " + self.state_condition(mod))
        elif trigger == "Overkill":
            pieces.append("on overkill")
        else:
            pieces.append("on " + human(trigger).lower())
        if mod.get("useTimer"):
            pieces.append(f"every {num(mod.get('frequency', 1))}s")
        if mod.get("useProbability"):
            chance = self.chance_text(mod.get("triggerChance"))
            if chance:
                pieces.append(chance)
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
            amount = f" ({num(low)} damage)" if low == high else f" ({num(low)}–{num(high)} damage)"
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
            attribute = human(mod.get("attribute", "Unknown")).lower()
            if float(mod.get("value", 0) or 0) == 0 and str(mod.get("variableMult", "None")) == "None":
                return ""
            variable = str(mod.get("variableMult", "None"))
            if variable != "None":
                effect = f"gain {attribute} equal to {num(abs(float(mod.get('value', 0) or 0)))}% of bonus {human(variable).lower()}"
            elif attribute in ("max health", "max magic"):
                action = "gain" if float(mod.get("value", 0) or 0) >= 0 else "lose"
                effect = f"{action} {num(abs(float(mod.get('value', 0) or 0)))} {attribute}"
            else:
                raw_change = float(mod.get("value", 0) or 0)
                change = raw_change - 100 if mod.get("multiplicative") else raw_change
                action = "increase" if change >= 0 else "reduce"
                effect = f"{action} {attribute} by {num(abs(change))}%"
        elif kind == "DamageOverTime":
            if float(mod.get("value", 0) or 0) == 0:
                return ""
            effect = (
                f"{value} damage every {num(mod.get('tickRate', 0.1))}s for "
                f"{num(mod.get('duration', 1))}s"
            )
            if mod.get("stackLimit", 1) != 1:
                effect += ", stacking"
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
                effect += ", stacking"
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
            effect = "knockback"
        elif kind == "Custom":
            custom = str(mod.get("customEffect", "custom effect"))
            amount = float(mod.get("value", 0) or 0)
            if custom == "PercentGold":
                effect = f"{num(abs(amount))}% more gold"
            elif custom == "PercentSouls":
                effect = f"{num(abs(amount))}% more souls"
            elif custom == "SpellCost":
                effect = f"spells cost {num(abs(amount))} less magic" if amount < 0 else "spells cost more magic"
            elif custom == "DamageReflect":
                effect = "reflect damage"
            elif custom == "NumRespawns":
                effect = "gain an extra respawn"
            elif custom == "PriestBoonCount":
                effect = "empower priest boons"
            else:
                effect = human(custom).lower()
        elif kind in ("Prefab", "Projectile"):
            effect = "spawns a projectile" if kind == "Projectile" else "spawns an effect"
        else:
            effect = human(kind).lower()
        trigger = self.trigger_line(mod)
        return scrub_vectors(effect + (" — " + trigger if trigger else ""))

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
        return '<ul class="effects">' + "".join(f"<li>{esc(scrub_vectors(line))}</li>" for line in effects) + "</ul>"

    def card(self, obj: dict[str, Any], body: str, tags: list[str] | None = None, effects: list[str] | None = None) -> str:
        name = display_name(obj.get("$name", "Unnamed"))
        desc = obj.get("description") or "No description available."
        search = " ".join([name, desc] + (tags or []) + (effects or []))
        tag_html = "".join(f'<span class="tag">{esc(tag)}</span>' for tag in (tags or []) if tag)
        return f'''<article class="card searchable" data-search="{esc(search.lower())}">
          <div class="card-head">{self.glyph()}<h3>{esc(name)}</h3></div>
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
        and not (not obj.get("description") and obj.get("$name", "").lower().startswith("new"))
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
        body = '<div class="facts"><b>Attack</b><span>' + esc(b.attack_line(attack)) + "</span></div>"
        tags = [f"cost {num(attack.get('magicCost', 0))}" if attack else "attack unresolved"]
        spell_cards.append(b.card(obj, body, tags, [b.attack_line(attack)]))

    def equipment_cards(type_name: str, boon_field: str) -> list[str]:
        cards = []
        for _, obj in b.rows(type_name):
            effects = b.effects(obj.get(boon_field))
            boon = b.resolve(obj.get(boon_field))
            attached_name = display_name(boon.get("$name", "Unknown") if boon else "Unknown")
            body = f'<div class="facts"><b>Bonus</b><span>{esc(attached_name)}</span></div>{b.effects_html(effects)}'
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
        body = b.effects_html(effects)
        tonic_cards.append(b.card(obj, body, notable + (["ephemeral"] if obj.get("ephemeral") else []), effects))

    item_cards = []
    for _, obj in b.rows("ItemData"):
        tags = ["unique" if obj.get("unique") else "stackable"]
        if obj.get("ephemeral"):
            tags.append("ephemeral")
        if obj.get("showOnCurrencyBar"):
            tags.append("currency bar")
        item_cards.append(b.card(obj, "", tags))

    shop_cards = []
    for _, shop in b.rows("ShopStock"):
        lines = []
        search_bits = [shop.get("$name", "")]
        for entry in shop.get("shopStock", []):
            item_name = display_name(b.ref_name(entry.get("data")))
            costs = [f"{num(x.get('currencyAmount', 0))} {display_name(b.ref_name(x.get('currencyItem')))}" for x in entry.get("requiredCurrencies", [])]
            detail = " · ".join(costs or ["free"])
            lines.append(f"<li><b>{esc(item_name)}</b><span>{esc(detail)}</span></li>")
            search_bits.extend([item_name, detail])
        shop_cards.append(f'<article class="shop-card searchable" data-search="{esc(" ".join(search_bits).lower())}"><h3>{esc(shop.get("$name", "Shop"))}</h3><ol>{"".join(lines) or "<li>Empty stock</li>"}</ol></article>')
    loot_cards = []
    for _, pool in b.rows("ItemLootPool"):
        entries = []
        for item in pool.get("lootPool", []):
            entries.append(f'<li><b>{esc(display_name(b.ref_name(item.get("item"))))}</b><span>amount {num(item.get("amount", 0))}</span></li>')
        search = pool.get("$name", "") + " " + " ".join(re.sub("<[^>]+>", " ", x) for x in entries)
        loot_cards.append(f'<article class="shop-card searchable" data-search="{esc(search.lower())}"><h3>{esc(pool.get("$name", "Loot pool"))}</h3><ol>{"".join(entries)}</ol></article>')

    counts = {
        "boons": len(boons), "boons_total": len(b.rows("Boon")), "spells": len(spell_cards),
        "blades": len(blade_cards), "charms": len(charm_cards), "tonics": len(tonic_cards),
        "items": len(item_cards), "shops": len(shop_cards), "loot_pools": len(loot_cards),
        "matched_spells": matched_spells,
    }

    sections = [
        ("boons", f"Boons <em>{counts['boons']}</em>", "", "".join(boon_cards), "card-grid"),
        ("spells", f"Spells <em>{counts['spells']}</em>", "Magic costs and notable combat properties for each spell.", "".join(spell_cards), "card-grid wide"),
        ("blades", f"Blades <em>{counts['blades']}</em>", "Weapons and the bonuses that make each one distinct.", "".join(blade_cards), "card-grid"),
        ("charms", f"Charms <em>{counts['charms']}</em>", "Charms with their attached passive effects.", "".join(charm_cards), "card-grid"),
        ("tonics", f"Tonics <em>{counts['tonics']}</em>", "Temporary boosts, restoration values, and their most important effects.", "".join(tonic_cards), "card-grid"),
        ("items", f"Items <em>{counts['items']}</em>", "Currencies, quest objects, consumables, and other inventory finds.", "".join(item_cards), "card-grid compact"),
    ]
    section_html = "".join(f'<section id="{sid}" data-section><header><h2>{title}</h2>{('<p>' + desc + '</p>') if desc else ''}</header><div class="{cls}">{content}</div><p class="empty-section" hidden>No matches in this section.</p></section>' for sid, title, desc, content, cls in sections)
    section_html += f'''<section id="shops" data-section><header><h2>Shops &amp; Loot <em>{counts['shops']} shops · {counts['loot_pools']} pools</em></h2><p>Shop stock and currency costs, plus loot pool amounts.</p></header><div class="shop-grid">{''.join(shop_cards)}{''.join(loot_cards)}</div><p class="empty-section" hidden>No matching stock or loot.</p></section>'''

    nav = "".join(f'<a href="#{sid}">{label}</a>' for sid, label in (("boons", "Boons"), ("spells", "Spells"), ("blades", "Blades"), ("charms", "Charms"), ("tonics", "Tonics"), ("items", "Items"), ("shops", "Shops/Loot")))
    css = r'''
:root{--bg:#0b0a0c;--panel:#151216;--panel2:#1d181b;--ink:#e8ddc5;--muted:#9d9383;--accent:#b6403b;--accent2:#dd6a52;--line:#3a2e31;--gold:#c5a467;color-scheme:dark}*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:radial-gradient(circle at 50% -20%,#332128 0,#100d10 36rem,var(--bg) 70rem);color:var(--ink);font:15px/1.52 Georgia,"Times New Roman",serif}body:before{content:"";position:fixed;inset:0;pointer-events:none;opacity:.035;background-image:repeating-linear-gradient(0deg,transparent 0 3px,#fff 4px)}a{color:inherit}.hero{padding:68px max(28px,calc((100vw - 1480px)/2));border-bottom:1px solid var(--line);text-align:center}.kicker{color:var(--accent2);font:700 12px/1 sans-serif;letter-spacing:.28em;text-transform:uppercase}.hero h1{font-size:clamp(42px,6vw,82px);line-height:.95;margin:18px 0 15px;text-transform:uppercase;letter-spacing:.04em;text-shadow:0 3px 0 #000}.hero p{max-width:720px;margin:auto;color:var(--muted);font-size:18px}.controls{position:sticky;top:0;z-index:20;background:#0c0a0dec;border-bottom:1px solid var(--line);backdrop-filter:blur(12px);padding:11px max(22px,calc((100vw - 1480px)/2))}.control-row{display:flex;align-items:center;gap:13px}.search{width:min(460px,40vw);border:1px solid #594044;background:#171216;color:var(--ink);padding:12px 15px;font:600 14px Georgia,serif;outline:none}.search:focus{border-color:var(--accent2);box-shadow:0 0 0 2px #b6403b33}.result-count{font:12px/1 sans-serif;color:var(--muted);white-space:nowrap}.nav{display:flex;gap:5px;margin-left:auto;overflow:auto}.nav a{padding:9px 10px;text-decoration:none;color:#c8bcaa;font:700 11px/1 sans-serif;text-transform:uppercase;letter-spacing:.08em;border:1px solid transparent;white-space:nowrap}.nav a:hover{border-color:var(--line);color:#fff}main{max-width:1480px;margin:auto;padding:0 28px 100px}section{scroll-margin-top:76px;padding:60px 0 20px;border-bottom:1px solid #2b2326}section>header{display:flex;align-items:end;justify-content:space-between;gap:28px;margin-bottom:24px}h2{font-size:32px;line-height:1;margin:0;text-transform:uppercase;letter-spacing:.05em}h2:before{content:"✦";color:var(--accent);font-size:.55em;margin-right:12px;vertical-align:.25em}h2 em{display:inline-block;color:var(--gold);font:700 11px/1 sans-serif;letter-spacing:.08em;font-style:normal;margin-left:8px}section>header p{max-width:540px;color:var(--muted);margin:0;text-align:right}.card-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:13px}.card-grid.wide{grid-template-columns:repeat(2,minmax(0,1fr))}.card-grid.compact{grid-template-columns:repeat(4,minmax(0,1fr))}.card{position:relative;min-width:0;background:linear-gradient(145deg,var(--panel2),var(--panel));border:1px solid var(--line);padding:17px;box-shadow:0 8px 22px #0003}.card:before{content:"";position:absolute;inset:5px;border:1px solid #ffffff08;pointer-events:none}.card:hover{border-color:#6a4144;transform:translateY(-1px)}.card-head{display:flex;gap:12px;align-items:center;min-height:48px}.glyph{width:32px;height:32px;flex:0 0 auto;fill:#211a1d;stroke:#8f7e70;stroke-width:1.4;filter:drop-shadow(0 2px 1px #0008)}.glyph path+path{fill:none;stroke:#b6403b}.card h3,.shop-card h3{margin:0;color:#f5e8cd;font-size:18px;line-height:1.15}.description{color:#c9bdab;margin:13px 0 10px;min-height:2.6em}.tags{display:flex;flex-wrap:wrap;gap:5px;margin-bottom:11px}.tag{border:1px solid #53363a;color:#d6a38c;padding:3px 6px;font:700 9px/1 sans-serif;text-transform:uppercase;letter-spacing:.08em}.effects{margin:10px 0 0;padding:10px 0 0 17px;border-top:1px solid #33282b}.effects li{padding:3px 0;color:#e1d5c1}.effects li::marker{color:var(--accent2)}.linked{color:var(--gold);font-size:12px;border-top:1px solid #33282b;padding-top:10px}.facts{display:grid;grid-template-columns:max-content 1fr;gap:5px 10px;border-top:1px solid #33282b;padding-top:10px}.facts b{color:var(--gold);font-size:12px}.facts span{color:#d0c4b1}.shop-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:13px}.shop-card{background:var(--panel);border:1px solid var(--line);padding:18px}.shop-card h3{border-bottom:1px solid #3b2c30;padding-bottom:11px}.shop-card ol{padding-left:22px;margin:10px 0 0}.shop-card li{padding:7px 0;border-bottom:1px dotted #342a2d}.shop-card li::marker{color:var(--accent)}.shop-card li span{display:block;color:var(--muted);font-size:12px}.empty-section{padding:30px;text-align:center;border:1px dashed var(--line);color:var(--muted)}footer{padding:30px;text-align:center;color:#706860;font-size:12px}.is-hidden{display:none!important}.no-results{display:none;text-align:center;padding:80px 20px;color:var(--muted);font-size:20px}.no-results.show{display:block}@media(max-width:1100px){.card-grid,.card-grid.compact{grid-template-columns:repeat(2,minmax(0,1fr))}.shop-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.nav{display:none}}@media(max-width:700px){.hero{padding:45px 20px}.controls{padding:9px 15px}.search{width:100%}.result-count{display:none}main{padding:0 15px 70px}.card-grid,.card-grid.wide,.card-grid.compact,.shop-grid{grid-template-columns:1fr}section>header{display:block}section>header p{text-align:left;margin-top:10px}.card{padding:15px}h2{font-size:25px}}
'''
    js = r'''
const search=document.querySelector('#search'), count=document.querySelector('#result-count');
const items=[...document.querySelectorAll('.searchable')];
function filter(){const terms=search.value.toLowerCase().trim().split(/\s+/).filter(Boolean);let visible=0;items.forEach(el=>{const ok=terms.every(t=>el.dataset.search.includes(t));el.classList.toggle('is-hidden',!ok);visible+=ok});document.querySelectorAll('[data-section]').forEach(section=>{const has=[...section.querySelectorAll('.searchable')].some(x=>!x.classList.contains('is-hidden'));section.querySelector('.empty-section')?.toggleAttribute('hidden',has)});count.textContent=terms.length?`${visible} of ${items.length} entries`:`${items.length} searchable entries`;document.querySelector('.no-results').classList.toggle('show',visible===0)}
search.addEventListener('input',filter);document.addEventListener('keydown',e=>{if(e.key==='/'&&document.activeElement!==search){e.preventDefault();search.focus()}});filter();
'''
    generated = __import__("datetime").datetime.now().astimezone().strftime("%Y-%m-%d %H:%M %Z")
    document = f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Dungeon Lurker — Game Bible</title><style>{css}</style></head><body>
<header class="hero"><div class="kicker">The Unofficial Compendium</div><h1>Dungeon Lurker</h1><p>A fan-made field guide to the demo's blessings, blades, spells, charms, tonics, items, shops, and loot.</p></header>
<div class="controls"><div class="control-row"><input id="search" class="search" type="search" placeholder="Search names, descriptions, effects…" aria-label="Search game bible" autocomplete="off"><span id="result-count" class="result-count"></span><nav class="nav">{nav}</nav></div></div>
<main>{section_html}<div class="no-results">Nothing in the dungeon matches that search.</div></main><footer>Dungeon Lurker Compendium — demo version data · Updated {esc(generated)}</footer><script>{js}</script></body></html>'''
    return document, counts


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
    args.standalone.write_text(document, encoding="utf-8")
    print(
        f"Generated {args.output} and {args.standalone}: {counts['boons']} featured boons ({counts['boons_total']} total), "
        f"{counts['spells']} spells, {counts['blades']} blades, {counts['charms']} charms, "
        f"{counts['tonics']} tonics, {counts['items']} items, {counts['shops']} shops, "
        f"and {counts['loot_pools']} loot pools."
    )


if __name__ == "__main__":
    main()
