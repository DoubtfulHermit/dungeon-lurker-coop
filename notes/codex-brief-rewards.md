# Task: Write Patches/SharedRewardsPatches.cs for the Dungeon Lurker co-op mod

You are writing ONE C# file for a BepInEx 5 + Harmony couch co-op mod: `src/DungeonLurkerCoop/Patches/SharedRewardsPatches.cs`. The decompiled game source is in `reference/decompiled/` — read every method you patch before writing the patch. The build already works: run `cd src/DungeonLurkerCoop && dotnet build` and fix all errors until it compiles clean. Do not touch any other file except creating your one new file. Do not create a git commit.

## Project conventions (must match exactly)
- Namespace: `DungeonLurkerCoop.Patches`. File-scoped namespace, C# latest.
- Harmony attribute classes: `[HarmonyPatch(typeof(X), nameof(X.Method))]` or `[HarmonyPatch(typeof(X), "MethodName")]` for private methods, static patch classes, applied via `harmony.PatchAll()` already called in Plugin.cs.
- Assembly-CSharp and Unity.InputSystem and Unity.Cinemachine are PUBLICIZED at compile time — you can access private/protected members of game classes directly (no reflection needed). If the compiler complains about a member, check the decompiled source for its real name.
- The co-op registry API you code against (already exists, `DungeonLurkerCoop.CoopPlayers`):
  - `CoopPlayers.CoopActive` (bool), `CoopPlayers.P1`, `CoopPlayers.P2` (Player)
  - `CoopPlayers.All` (List<Player>), `CoopPlayers.Alive()` (IEnumerable<Player>)
  - `CoopPlayers.IsCoopPlayer(Player)`, `CoopPlayers.IsP2(Player)`
  - `CoopPlayers.Nearest(Vector3)` (nearest living player, may return downed/null fallback)
- Every patch MUST no-op (fall through to vanilla, `return true` from prefixes) when `!CoopPlayers.CoopActive`.
- Log via `DungeonLurkerCoop.Plugin.Log.LogInfo/LogWarning` sparingly (only on unexpected states).

## Patches to write

1. **LootContainer.GrantLoot** (`reference/decompiled/LootContainer.cs`): vanilla applies granted boon mods only to `Player.GetInstance()`'s ModController (and aims the particle attractor at the singleton). Patch so boon/mod application happens to ALL `CoopPlayers.All` (skip null/destroyed), and the particle attractor targets the nearest player to the container instead. Preserve single-application of shared run state (`DungeonMaster.AddBoon` etc. must still run ONCE — do not double-add shared state).
2. **MenuObjectBoonGoddess** choose path (`reference/decompiled/MenuObjectBoonGoddess.cs`): chosen boon's mod goes only to singleton player. Apply to all coop players; shared `DungeonMaster` state still once.
3. **MenuWizardEquipWindow.CloseEquipWindow** (`reference/decompiled/MenuWizardEquipWindow.cs`): spells copied only to singleton player's AttackController. Copy to all coop players.
4. **MenuObjectOldKnight.Deactivate** (`reference/decompiled/MenuObjectOldKnight.cs`): trait mods cleared/reapplied only on singleton player. Do it for all coop players. (HUD reinit can stay P1-only.)
5. **Cinematic.PauseActors** (`reference/decompiled/Cinematic.cs`): the `pauseInput` fallback blocks only `Player.GetInstance()`'s input. Mirror the same block (same duration/semantics) onto every coop player's `PlayerInput` component (each Player has its own via `GetPlayerInput()`).
6. **PlayDead.Update** (`reference/decompiled/PlayDead.cs`): wake-on-proximity checks distance to singleton player only. Use distance to the NEAREST coop player.
7. **ProjectileSpawner.FireProjectile** (`reference/decompiled/ProjectileSpawner.cs`): `targetPlayer` mode aims at singleton player transform. Aim at nearest coop player to the spawner.

## Technique guidance
- Prefer a full-replacement prefix (`return false`) copying the vanilla method body with the minimal change, ONLY for short methods (PlayDead.Update, ProjectileSpawner.FireProjectile, Pitbox-style small ones). For longer methods (GrantLoot, menu Deactivate/Activate paths) prefer a targeted approach: e.g. postfix that applies the missing mods to the OTHER players (P2 etc.) after vanilla applied them to P1 — that avoids copying long bodies. Think per-patch about which is least fragile, and double-check you do not double-apply anything to P1 or to shared DungeonMaster state.
- For "apply mod to other players too" postfixes you must replicate exactly what vanilla did to `Player.GetInstance()` for each other player — read the ModController/AttackController API in `reference/decompiled/ModController.cs`, `reference/decompiled/Actor.cs` (AddSpell etc.) to use the right calls.
- Beware: menu classes reference `Player.GetInstance()` — in co-op that is P1. "Other players" = `CoopPlayers.All` minus `Player.GetInstance()`.
- Compile until clean: `cd src/DungeonLurkerCoop && dotnet build` must end with 0 errors. Warnings acceptable.

Deliverable: the compiling file at `src/DungeonLurkerCoop/Patches/SharedRewardsPatches.cs`. Also append a short section `## SharedRewardsPatches decisions` to `notes/seams-report.md` describing per-patch what technique you chose and any vanilla behavior you preserved deliberately.
