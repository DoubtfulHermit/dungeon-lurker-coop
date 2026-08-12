# Dungeon Lurker Couch Co-op Mod

Local 2-player co-op for the **Dungeon Lurker Demo** (13AM Games), built with
BepInEx 5 + Harmony. Player 2 is a full clone of the knight — targeted by
enemies, hurt by pits and traps, healed by boons — with its own gamepad, its
own health bar, and a revive mechanic.

**Online co-op for free:** because this is local co-op in a Steam game, it
works over the internet via **Steam Remote Play Together** with zero netcode.

## Features

- **P2 spawns automatically** in every scene (town + dungeon floors) when a
  gamepad is present. P1 plays keyboard & mouse (or the first gamepad if you
  have several — P2 always takes the *last* connected pad).
- **Independent input**: P2 gets its own clone of the game's input actions
  bound exclusively to its gamepad; that pad is removed from P1's input so
  they never fight over a device. Menus stay driven by P1.
- **Camera** follows both players (the game's own Cinemachine target group).
- **Second HUD bar** under P1's with live health/magic for P2.
- **Co-op death rules**: shared respawns work per player as in vanilla; when
  respawns run out a downed player waits instead of ending the run — a living
  ally standing next to them for ~3 s revives them at half health. The run
  only ends when *everyone* is down.
- **Shared build**: boons, spells, traits and blade/charm apply to both
  players (chests, boon goddess, wizard equip, old-knight respec, floor
  events).
- **Fixed single-player assumptions**: interaction prompts, pit falls, feign-
  death enemies and player-seeking traps all operate on the player who
  actually did the thing / is actually nearest, not always P1.
- P2 gets a light green tint (via the game's palette system) — toggleable.

## Install

The mod lives in the game directory (`steamapps/common/Dungeon Lurker Demo`):

```
winhttp.dll + doorstop_config.ini      <- BepInEx loader (5.4.23.3 win x64)
BepInEx/core/...                       <- BepInEx runtime
BepInEx/plugins/DungeonLurkerCoop.dll  <- this mod
```

All of that is already in place on this machine. For a fresh install:
unzip [BepInEx 5.4.23.3 win x64](https://github.com/BepInEx/BepInEx/releases)
into the game dir, build this project (below), done.

### Steam launch options (required on Linux/Proton)

BepInEx hooks the game through a fake `winhttp.dll`, which Wine ignores
unless told otherwise. Set in Steam → game Properties → Launch Options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

On Windows nothing special is needed.

### Verify it works

`BepInEx/LogOutput.log` in the game dir should contain
`Loading [DungeonLurkerCoop ...]` after a launch. With a second controller
connected you'll see `Co-op: spawned P2 ...` once you're in the town.

## Config

`BepInEx/config/com.doubtfulhermit.dungeonlurker.coop.cfg`:

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | true | Master switch. |
| `SpawnWithoutGamepad` | false | Spawn P2 even with no pad (testing). |
| `TintP2` | true | Green palette tint on P2. |
| `AutoDrive` / `AutoStart` / `DeathTest` / `ScreenshotInterval` | off | Autonomous test harness (leave off for play). |

## Development

```
cd src/DungeonLurkerCoop
dotnet build          # builds AND deploys the DLL into BepInEx/plugins
./run-game.sh         # launch the game under GE-Proton outside Steam
```

- Targets `net472`; references the game's `Managed/` assemblies directly,
  with `Assembly-CSharp`, `Unity.InputSystem` and `Unity.Cinemachine`
  **publicized** at build time (BepInEx.AssemblyPublicizer.MSBuild), so
  private game members are called directly — no reflection.
- `reference/decompiled/` holds an ILSpy decompile of the game (the demo
  ships PDBs, so it's fully symbolized). Not committed. Regenerate:
  `ilspycmd -p -o reference/decompiled <game>/DungeonLurker_Data/Managed/Assembly-CSharp.dll`
- `notes/seams-report.md` is the co-op seam analysis (where every
  single-player assumption lives, with citations).

### Architecture

| File | Role |
| --- | --- |
| `CoopManager.cs` | Per-scene orchestration: waits for the scene-baked P1, clones it as P2, strips hazard components, wires input/camera/HUD, runs the revive loop and menu-block mirroring. |
| `CoopPlayers.cs` | Tiny registry (P1/P2/All + helpers) every patch codes against. |
| `P2InputBridge.cs` | Clones the game's `InputActionAsset`, restricts it to P2's gamepad, forwards actions to P2's own `PlayerInput` component by reference (the game's relay only ever feeds the P1 singleton). |
| `Patches/CorePatches.cs` | Keeps `Player.instance` = P1 when the clone initializes; lets the clone's `PlayerInput` arm itself; co-op death rules in `Player.UpdateDeath`; `Trigger`/`Pitbox` de-singletoned. |
| `Patches/CameraPatches.cs` | Lookahead + frustum math use the players' midpoint/aggregate velocity. |
| `Patches/SharedRewardsPatches.cs` | Boon mirror (`ModController.AddBoon` P1→P2, suppressed during per-player setup so nothing double-applies), trait/spell sync, cinematic input block mirror, nearest-player targeting for feign-death enemies and traps. |
| `TestHarness.cs` | Config-gated autonomous test driver (menu → town → dungeon, wander/seek/revive AI, scripted kill/revive/game-over verification, periodic screenshots). Drives the game through its own methods only. |

### Verified in-game (autonomous runs, 2026-08-11)

- P2 spawn + independent movement/attacks in town and dungeon floors
- Camera target group tracking both players
- Dual HUD with correct per-player values (100/100 vs 50/100 post-revive)
- Downed → ally-proximity revive at half HP (scripted + organic)
- All-players-down → game over → run recap (scripted + organic)
- Pit falls hurt/reset the player who fell (P2 repeatedly, P1 untouched)
- Interaction prompts from either player's position
- Menu-open blocks P2 gameplay input (block mirror)

### Known limitations / untested

- **Real-gamepad pairing is untested** — no controller was active during the
  autonomous runs (`Gamepad.all` was empty; the harness drove inputs
  in-engine). First manual session should confirm pad pairing + the
  P1-device-restriction behave; the code paths are conventional InputSystem.
- Enemy-vs-P2 combat targeting is by faction list (`LevelManager.FindActors`)
  and should just work, but no enemy engagement happened in the auto-runs.
- If a player is **gibbed** (overkill), the game destroys the body outright
  (vanilla behavior); co-op then degrades gracefully to single-player until
  the next scene.
- Floor-end HP/magic snapshot (`DungeonMaster.EndLevel`) records P1 only;
  both players restore from it next floor (they share the pool by design).
- The demo is Mono; if the full game ships IL2CPP this needs BepInEx 6 +
  Il2CppInterop and a port pass.
```
