# Porting Playbook — Dungeon Lurker Co-op

Everything needed to rebuild/port this mod when the **full game** ships,
written assuming the worst: no PDBs, changed internals, possibly IL2CPP.
Captured 2026-08-11 against the demo (Unity 6000.0.25f1, Mono, appid 5010970).

**The demo itself is the ultimate reference** — a pristine pre-mod copy is
archived at `~/Games/archive/DungeonLurkerDemo-pristine-2026-08-11` (btrfs
reflink), including `Managed/Assembly-CSharp.dll` + its PDB. The full
decompile lives in `reference/decompiled/` (regenerate:
`ilspycmd -p -o reference/decompiled .../Assembly-CSharp.dll`).

## Step 0 — triage the full game build (15 minutes)

1. **Mono or IL2CPP?** Mono ⇒ `<Game>_Data/Managed/Assembly-CSharp.dll`
   exists. IL2CPP ⇒ `GameAssembly.dll` + `il2cpp_data/`. IL2CPP means:
   BepInEx 6 (IL2CPP build) + Il2CppInterop, decompile via Cpp2IL/Il2CppDumper
   using `global-metadata.dat`. All patch concepts survive; syntax changes
   (interop proxies instead of direct types). Budget a day, not an hour.
2. **Symbols:** even WITHOUT PDBs, a Mono `Assembly-CSharp.dll` keeps all
   type/method/field names (PDBs only added sequence points). Only
   deliberate obfuscation would hide names — check with ilspycmd. If
   obfuscated, diff structurally against the demo decompile (method shapes,
   string literals like "Duplicate PlayerInput", scene names).
3. **Diff the seams:** run the fingerprint check below; anything that moved
   gets re-read before the patch is re-enabled.

## Dependency fingerprint — every game symbol the mod touches

Harmony patch targets (method missing ⇒ that feature breaks, rest still loads):

| Type.Method | Why patched |
| --- | --- |
| `Player.Initialize` (protected override) | keep `Player.instance` = P1 when clone awakes |
| `Player.UpdateDeath` (protected override) | co-op death rules |
| `PlayerInput.Init` (private, called from Start) | clone's input must arm past singleton guard |
| `Trigger.OnTriggerEnter/OnTriggerExit/Cleanup` | accept any co-op player |
| `Pitbox.Hit(Hurtbox)` / `Pitbox.ResetPlayer` | pit hurts the faller, not P1 |
| `CameraManager.Update` / `CalculateFrustrumExtents` | lookahead + frustum use midpoint |
| `Unity.Cinemachine.LookAhead.PostPipelineStageCallback` | aggregate velocity |
| `LevelManager.SetupPlayer(Player)` (private static) | baseline-mod trim + mirror suppression |
| `ModController.AddBoon(Boon)` | P1→P2 boon mirror |
| `DungeonMaster.ClearPlayerMods` / `ApplyTraits(Actor)` | trait respec both players |
| `MenuWizardEquipWindow.CloseEquipWindow(bool)` | spell equip both players |
| `Cinematic.PauseActors` (private) | mirror `pauseInput` block to all players |
| `PlayDead.Update` (private) | wake on nearest player |
| `ProjectileSpawner.FireProjectile` (private) | target nearest player |

Non-patch API surface used directly (compiled against, publicized):
`Player.instance/GetInstance/GetPlayerInput/pitFallEffect`,
`Actor.CheckActState/GetActState/GetPosState/CanAct/GetVel/GetHurtbox/GetModCon/AddSpell/RemoveSpell/Stun/deathTimer/despawnTime/hurtbox/indexColor`,
`Hurtbox.health/MaxHealth/Heal/Damage/HealthRatio/ForceHealth/Kill/overkillHealth`,
`DamageEvent(int, float, float, bool, bool, Actor, ?, Vector3, float, bool, float, float)` ctor,
`PlayerInput.instance/initialized/blockInput/blockTimer/OnMove/OnSprint/OnJump/OnLightAtk/OnHeavyAtk/OnInteract/OnGuard/OnDodge/CheckBlocked/SetBlockTimer/RegisterPlayer`,
`InputRelay.activeInput/instance/GetCurrentActionMap/SwitchActionMap`,
`LevelManager.RegisterActor/FindActors/GetActorList/instance`,
`DungeonMaster.instance/CheckRespawns/UseRespawn/SaveGhostData/AddBoon/activeBoons/activeSpells`,
`CameraManager.instance/cineCam/targetGroup/composer/main/LockCamera/UpdateCameraPlanes/GetCameraPlane/frustrumBound*`,
`HUDController.TryGetInstance/playerHUD/ShowInteractionPrompt`, `PlayerHUD.Initialize(Actor)/UpdateLivesTracker`,
`MenuController.ActivateMenu<T>/GetActiveMenu`, `MenuObjectGameOver`,
`IndexColor.colorArray` (public Color[64], runtime-mutable, 8 rows × 8 shades),
`Tools.Sign/QuickFlatDistSqr/Vec2ToXZ/DrawCircle/SolidGradient`,
`Bot`, `Boon.mods`, `ModController.mods` (public serialized List<Modification>),
`LevelTransitioner.Activate` + `Listener` base (test harness),
`MenuObjectDungeonLurkerSplash.Progress/MoveToTown` (test harness).

## Architecture facts that made co-op cheap (re-verify each on the full game)

1. **Enemies target via faction list**, not the player singleton:
   `Bot.FindTarget` → `LevelManager.FindActors(list, team, active, range, pos)`.
   A registered second Player is engaged with zero AI changes.
2. **Camera follows a CinemachineTargetGroup** (vcam Follow == the group's
   transform). Co-op camera = `targetGroup.AddMember(p2.transform, 1f, 1f)`.
   Code also handles Follow==P1-transform by swapping in a midpoint target.
3. **Player is scene-baked** (one per playable scene, no factory/prefab
   instantiation in code). P2 = `Object.Instantiate(p1.gameObject)`; the
   clone's Awake chain runs synchronously inside Instantiate.
4. **Run state lives outside the Player** (`DungeonMaster`: boons, spells,
   HP snapshot between floors; static `InventoryManager`), and
   `LevelManager.RegisterActor` → `SetupPlayer` applies it to EVERY
   registering Player — the clone gets the whole build automatically.
5. **Input flow:** persistent `InputRelay` GameObject owns THE
   `UnityEngine.InputSystem.PlayerInput`; its SendMessage callbacks forward
   to the static custom `PlayerInput.instance` (P1 only, by design).
   Action names: `Move Sprint SprintPress Jump LightAtk HeavyAtk Interact
   Guard Dodge` in map **"Player"**; UI map **"UI"**. Menu-open = relay
   switches to UI map (`MenuHandler.SetActiveMenuObject`).

## The traps (each cost real debugging — do not rediscover)

1. **Clone overwrites `Player.instance`.** `Player.Initialize` runs inside
   `Instantiate`; postfix restores the saved P1 reference (flag
   `CoopManager.IsCloning` around the Instantiate call — safe because
   Awake is synchronous).
2. **Clone's `PlayerInput` never arms.** Its `Init()` runs from `Start` —
   ONE FRAME AFTER `IsCloning` cleared — hits the `instance != null` guard,
   leaves `initialized=false`, and then `Update()` never ticks
   `blockTimer` down ⇒ any cinematic block freezes P2 FOREVER. Fix: set
   `initialized=true` + `blockTimer=0` directly at spawn AND patch `Init`
   with a registry check (not just the cloning flag).
3. **`ModController.mods` is serialized** ⇒ the clone copies P1's runtime
   mods, then `SetupPlayer` re-applies run state ⇒ double-stacked boons.
   Fix: record P1's pre-setup mod count (prefab baseline), trim the clone's
   list back to it in a `SetupPlayer` prefix. Also suppress the boon mirror
   during `SetupPlayer`/`ApplyTraits` (both are per-player already).
4. **Unity InputSystem auto-switch re-grabs the pad for P1.** Setting
   `asset.devices` on the relay is NOT enough. Must set
   `neverAutoSwitchControlSchemes = true` and
   `SwitchCurrentControlScheme(<kb scheme>, Keyboard.current, Mouse.current)`.
   **Scheme names in the demo asset: `Keyboard&Mouse` and `Gamepad` — NO
   SPACES** (the game's own `OnControlsChanged` switch uses "Keyboard & Mouse"
   with spaces and therefore never matches — dev bug, don't copy it).
   Discover scheme names at runtime; never hardcode.
5. **Overkill gibs destroy the Player object** (vanilla): `Hurtbox.Kill`
   with `health <= -overkillHealth` (default 50) ⇒ `Overkill` ⇒ object gone.
   Prune destroyed players from the registry; degrade to single-player.
6. **P2 visual identity:** actors render via `IndexColor` palette shader —
   `material.color` does NOTHING. Flat-color overrides look holographic.
   Correct approach: hue-rotate `IndexColor.colorArray` in place (skip
   s < 0.08 to keep metals/greys; HSVToRGB with hdr:true), then the shader
   picks it up automatically. `SamplePalette()` (which would reset it from
   the texture) is editor-ContextMenu-only — runtime mutation is safe.
7. **Between-floor boon events (Riddler/Priest/Devourer/Gambler) only touch
   shared `DungeonMaster` state** — players receive those at next
   `SetupPlayer`. Do NOT mirror there or you double-apply. Only
   `LootContainer.GrantLoot`, BoonGoddess choose, and debug tools apply
   live to P1's ModController — the `ModController.AddBoon` postfix mirror
   covers all of them uniformly.

## Design decisions (so future-you doesn't re-litigate)

- **P1 stays the game-facing singleton** everywhere; P2 is patched in at
  specific seams. Inverting this (registry everywhere) touches 24 files.
- **Death:** shared respawn pool per vanilla (each downed player consumes
  one). Respawns exhausted ⇒ downed player parks (`deathTimer = -inf`),
  ally within 2.5u accumulates 3s (progress decays at half speed when
  away) ⇒ revive = `Stun(2f)` + heal to 50% + `deathTimer=0` (mirrors the
  vanilla respawn sequence, at half HP). All down ⇒ vanilla game-over path
  once (`SaveGhostData` + `MenuObjectGameOver`).
- **Floor-end HP snapshot stays P1-only** (both restore from it next floor
  — shared-pool feel, and avoids patching `DungeonMaster.EndLevel`).
- **Menus belong to P1** (relay pinned to keyboard scheme; P2's pad is
  removed from the relay). P2's gameplay input is force-blocked
  (`blockInput`) whenever the relay's action map != "Player".
- **P2 HUD**: clone `HUDController.playerHUD`, mirror to top-right
  (anchor/pivot x flipped, `anchoredPosition.x` negated), `Initialize(p2)`.
  `PlayerHUD.Initialize(Actor)` is already actor-generic.

## Ops / testing knowledge

- **Launch outside Steam** (GE-Proton): `run-game.sh`. Direct `proton run`
  needs the **absolute** exe path (relative ⇒ silent exit, error 2 in
  `WINEDEBUG=+loaddll` only) and `XAUTHORITY=/run/user/1000/xauth_*` for
  Xwayland. Via Steam: launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- **BepInEx "Unable to start Unity log writer"** is cosmetic BUT means
  in-game exceptions do NOT reach `BepInEx/LogOutput.log` — read
  `pfx/drive_c/users/steamuser/AppData/LocalLow/13AM Games/Dungeon Lurker/Player.log`
  whenever something silently half-works (that's how the scheme-name crash
  and the frozen-P2 bug were found).
- **BepInEx config files are CRLF** and get rewritten/reordered — edit with
  `\r`-tolerant sed, verify the value landed in the right `[Section]`.
- **`pkill -f DungeonLurker.exe` kills your own launch chain** when chained
  in the same command (matches the wrapper's cmdline) — use `pkill -x`.
- **TestHarness** (all config-gated, in-engine only): AutoStart drives
  splash → `MoveToTown()` (fresh save goes straight to Floor 1A; with
  `Z1F1_SEEN` set it goes to town, then `LevelTransitioner.Activate()`
  enters the dungeon and calls `StartRun`). AutoDrive: wander/seek-enemy/
  stand-by-downed-ally AI via direct calls on each player's `PlayerInput`.
  DeathTest: scripted kill→revive→all-down verification. Screenshots via
  `ScreenCapture.CaptureScreenshot` to a `Z:/...` path.
- **Kill players in tests with EXACT current-health damage** — 10× max HP
  trips the gib path and destroys the body (see trap 5).
- Demo saves: `pfx/.../LocalLow/13AM Games/Dungeon Lurker/DL_DEMO_SAVE0.json`
  + `GLOBAL_DEMO_DL_SAVE.json` (delete both for a fresh intro run).

## Verified vs. still open (as of 2026-08-11)

Verified live: P2 spawn (town+dungeon), real-pad pairing (8BitDo → "Xbox
Controller"), independent inputs after relay pinning, dual HUD (top-right),
hue-shift recolor, pit per-player damage, ally revive (scripted + organic),
all-down game over → run recap, menu input blocking, camera target group.

Still unverified: live enemy-vs-P2 combat (faction logic makes it
near-certain), mid-run floor transitions with P2, boon-goddess menu choose
with P2 present (mirror patch is code-reviewed only), two-gamepad setup
(P1 pad + P2 pad path), Steam-launched session with Steam Input in the
middle, Remote Play Together end-to-end.
