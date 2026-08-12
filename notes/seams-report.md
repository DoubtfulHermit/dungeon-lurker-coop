# Dungeon Lurker local co-op seam report

Scope: decompiled C# under `reference/decompiled/`. I found 23 source files that call `Player.GetInstance()` plus `Player.cs` itself defining the singleton. I did not find a source method that instantiates a `Player`; all direct gameplay actor instantiation paths found in the provided sources are for bots/projectiles/effects/menus/cinematics, while the player lifecycle starts from `PhysObject.Awake` on an already-loaded scene object.

## 1. Player lifecycle

### Scene object vs instantiated object

- `Player` is initialized by Unity lifecycle, not by a visible factory in the provided source. `PhysObject.Awake` calls virtual `Initialize`; `Player.Initialize` runs from that path, assigns `Player.instance = this`, calls `Actor.Initialize`, gets the same GameObject's custom `PlayerInput`, and registers the player with that input component. Citations: `reference/decompiled/PhysObject.cs` `PhysObject.Awake` / `PhysObject.Initialize`; `reference/decompiled/Player.cs` `Player.Initialize`.
- `Actor.Initialize` is the shared actor bootstrap. It initializes `ActionController`, `AttackController`, `ModController`, render/anim references, physics masks, animation overrides, then calls `LevelManager.RegisterActor(this)`. Citation: `reference/decompiled/Actor.cs` `Actor.Initialize`.
- `LevelManager.RegisterActor` adds every actor to the per-level `actors` list; for `Player`, it calls `SetupPlayer`. Citation: `reference/decompiled/LevelManager.cs` `LevelManager.RegisterActor`.
- `LevelManager.SetupPlayer` applies shared run traits, active boons, active spells, charm/blade boons, and if already in-dungeon after floor 1 restores HP/magic/status from `DungeonMaster`. Citation: `reference/decompiled/LevelManager.cs` `LevelManager.SetupPlayer`.
- I found no `DontDestroyOnLoad` call on `Player` or `Actor`. The objects explicitly made persistent are `DungeonMaster`, `GameManager`, `InputRelay`, `RenderCanvas`, `SoundManager`, `MenuHandler`'s parent, etc. Citations: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.Initialize`; `reference/decompiled/GameManager.cs` `GameManager.Initialize`; `reference/decompiled/InputRelay.cs` `InputRelay.Initialize`; `reference/decompiled/MenuHandler.cs` `MenuHandler.Awake`.
- Level transitions load whole scenes. `GameManager.CreateTransition` instantiates a `LevelTransition` prefab or directly calls `SceneManager.LoadScene` if skipping; `LevelTransition.UpdateState` marks only the transition object `DontDestroyOnLoad`, loads `LoadingScene`, then asynchronously loads the target scene and destroys the transition object during cleanup. Citations: `reference/decompiled/GameManager.cs` `GameManager.CreateTransition`; `reference/decompiled/LevelTransition.cs` `LevelTransition.UpdateState`.

Conclusion: the player is scene-baked in normal play, destroyed with the level scene, and re-created from the next scene's baked `Player` object. Persistent run state lives outside the player.

### Shared state vs per-Player-object state

- Per-object state: `Hurtbox.health`, `Actor.magic`, actor action/position state, `ModController` contents on that object, spells copied onto that actor's `AttackController`, current input direction/held flags on custom `PlayerInput`. Citations: `reference/decompiled/Hurtbox.cs` `Hurtbox.Damage` / `Hurtbox.Heal`; `reference/decompiled/Actor.cs` `Actor.SetMagic` / `Actor.AddMagic` / `Actor.AddSpell`; `reference/decompiled/PlayerInput.cs` `PlayerInput.OnMove` / `PlayerInput.OnGuard`.
- Persistent run state: `DungeonMaster.activeBoons`, `activeSpells`, `activeCurses`, `activeTonics`, `activeCharm`, `activeBlade`, `floorsCleared`, `playerHealth`, `playerMaxHealth`, `playerMagic`, respawn count, saved statuses, death/ghost data. Citation: `reference/decompiled/DungeonMaster.cs` fields and `DungeonMaster.Initialize`.
- `DungeonMaster.EndLevel` snapshots only `Player.GetInstance()` HP, max HP, magic, and statuses. In co-op this is P1-only unless patched. Citation: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.EndLevel` / `DungeonMaster.SavePlayerStatuses`.
- `LevelManager.SetupPlayer` restores HP/magic/status from `DungeonMaster` only after floor 1. `LoadPlayerStatuses` clears `playerStatuses` after loading them into the first actor passed, so a later P2 clone will not receive persistent statuses unless patched. Citations: `reference/decompiled/LevelManager.cs` `LevelManager.SetupPlayer`; `reference/decompiled/DungeonMaster.cs` `DungeonMaster.LoadPlayerStatuses`.
- Inventory and stats are global/static. `InventoryManager.items` is static and `AddItem` applies global gold/soul modifiers through `CustomEffect.CheckEffect`; `StatTracker` stores global/dungeon stats in static dictionaries. Citations: `reference/decompiled/InventoryManager.cs` `InventoryManager.AddItem`; `reference/decompiled/StatTracker.cs` `StatTracker.UpdateStat`.

### Clone duplicates that must be controlled

- Critical: cloning the `Player` and letting `Awake` run will set `Player.instance` to the clone in `Player.Initialize`. That redirects every `Player.GetInstance()` user to P2. Citation: `reference/decompiled/Player.cs` `Player.Initialize` / `Player.GetInstance`.
- Critical: the custom `PlayerInput` is also a singleton. `PlayerInput.Init` logs "Duplicate PlayerInput" and returns if `PlayerInput.instance` is already set; the clone's custom input component will not set `initialized = true`, so its `Update` guard/held-input logic will not run unless patched. Citation: `reference/decompiled/PlayerInput.cs` `PlayerInput.Init` / `PlayerInput.Update`.
- Intentional but must be understood: `Actor.Initialize` registers the clone in `LevelManager.actors`, and bots can target all hostile actors returned by `LevelManager.FindActors`. This is desirable for P2 as a target, but only if the clone remains on player team/layer. Citations: `reference/decompiled/Actor.cs` `Actor.Initialize`; `reference/decompiled/LevelManager.cs` `LevelManager.RegisterActor` / `LevelManager.FindActors`; `reference/decompiled/Bot.cs` `Bot.FindTarget`.
- HUD and spell UI bind to the singleton player by default, not every player. `HUDController.Start` calls `playerHUD.Initialize(Player.GetInstance())`; `SpellHUD.Update` reads `Player.GetInstance().GetMagic()`. Citations: `reference/decompiled/HUDController.cs` `HUDController.Start`; `reference/decompiled/SpellHUD.cs` `SpellHUD.Update`.
- Camera serialized references are not automatically expanded. The source exposes `CameraManager.cineCam` and `targetGroup`, but no source method sets the main virtual camera's `Follow`/`LookAt`. Speculation: scene serialization provides the gameplay follow target; this must be verified at runtime or in scene data. Citation for absence-in-code context: `reference/decompiled/CameraManager.cs` `CameraManager.Initialize` / fields; `reference/decompiled/Cinematic.cs` `Cinematic.SetTargetGroup` only handles cinematic-local target groups.
- Global menu/input state blocks only the game's persistent `InputRelay` action map. Opening menus switches `InputRelay.activeInput` to `UI`; a mod-owned P2 input source will keep sending gameplay commands unless the mod mirrors block state. Citations: `reference/decompiled/MenuHandler.cs` `MenuHandler.SetActiveMenuObject`; `reference/decompiled/InputRelay.cs` `InputRelay.SwitchActionMap`; `reference/decompiled/MenuController.cs` `MenuController.ReleaseControls`.

## 2. Input path

### Exact hardware-to-move flow

1. The persistent `InputRelay` GameObject owns a Unity Input System `UnityEngine.InputSystem.PlayerInput` component. `InputRelay.Initialize` sets static `InputRelay.activeInput = GetComponent<UnityEngine.InputSystem.PlayerInput>()`, enables it, switches to `UI`, and marks the relay `DontDestroyOnLoad`. Citation: `reference/decompiled/InputRelay.cs` `InputRelay.Initialize`.
2. On level setup, `LevelManager.Initialize` switches that one Unity `PlayerInput` to the `"Player"` action map. Citation: `reference/decompiled/LevelManager.cs` `LevelManager.Initialize`.
3. Unity Input System message callbacks on `InputRelay` forward hardware events to the static custom `PlayerInput.instance`. `OnMove` calls `PlayerInput.instance.OnMove(value.Get<Vector2>())`; sprint/jump/light/heavy/interact/guard/dodge do the same. Citation: `reference/decompiled/InputRelay.cs` `InputRelay.OnMove` / `OnSprint` / `OnJump` / `OnLightAtk` / `OnHeavyAtk` / `OnInteract` / `OnGuard` / `OnDodge`.
4. The custom `PlayerInput` stores `currDir`, `guardHeld`, `heavyHeld`, and calls actions on its registered `Player`. Citation: `reference/decompiled/PlayerInput.cs` `PlayerInput.OnMove` / `OnLightAtk` / `OnHeavyAtk` / `OnInteract` / `OnJump` / `OnDodge`.
5. `Player.FixedUpdate` polls `playerIn.GetCurrDir().normalized` each physics tick and passes it to `Actor.Move`. Citation: `reference/decompiled/Player.cs` `Player.FixedUpdate`; `reference/decompiled/Actor.cs` `Actor.Move`.

The custom `PlayerInput` does not read `InputRelay.activeInput` directly and does not poll action assets. It is driven by `InputRelay` callbacks through `PlayerInput.instance`. Citation: `reference/decompiled/PlayerInput.cs` full class; `reference/decompiled/InputRelay.cs` input callbacks.

### Clean P2 input seam

- Do not try to make the clone's stock custom `PlayerInput` receive events through `InputRelay`; `InputRelay` is a singleton and forwards only to `PlayerInput.instance`. Citation: `reference/decompiled/InputRelay.cs` input callbacks; `reference/decompiled/PlayerInput.cs` `PlayerInput.instance`.
- Recommended mod seam: create a mod-owned Unity Input System input source for P2 with its own actions asset instance and pair it to a chosen `Gamepad` via `InputUser.PerformPairingWithDevice` / a separate `UnityEngine.InputSystem.PlayerInput`. Forward its action callbacks directly to the clone's custom `PlayerInput` component by object reference, not through `PlayerInput.instance`. This is a recommendation based on the cited forwarding design, not a game-source claim.
- Patch/initialize the clone's custom `PlayerInput` so it is usable despite the singleton guard: either bypass `PlayerInput.Init` singleton logic for marked P2 inputs or set its private `initialized` and `player` fields via Harmony/reflection after `Player.Initialize` calls `RegisterPlayer`. Citation: `reference/decompiled/PlayerInput.cs` `PlayerInput.Init` / `RegisterPlayer` / `Update`.
- Block P2 input whenever gameplay is blocked. The original system blocks gameplay by action-map switching (`InputRelay.SwitchActionMap`) and by `PlayerInput.SetBlockTimer` during cinematics; both only affect the singleton paths unless mirrored. Citations: `reference/decompiled/InputRelay.cs` `InputRelay.SwitchActionMap`; `reference/decompiled/Cinematic.cs` `Cinematic.PauseActors`.

Leak points:

- UI navigation, back/exit/page/more-info, pause, manual, meta toggles all come from the same persistent `InputRelay` Unity `PlayerInput`, so whichever device is paired to it can drive UI. Citation: `reference/decompiled/InputRelay.cs` `OnOpenPause` / `OnMenuBack` / `OnMenuExit` / `OnMenuInfo` / `OnMenuPageForward` / `OnMenuPageBackward` / `OnMenuNavigate`.
- Control-scheme display is global: `InputRelay.controlScheme` is set by `InputRelay.OnControlsChanged`, and `LocalizationManager` / `MenuControlContainer` read `InputRelay.activeInput`. Citations: `reference/decompiled/InputRelay.cs` `InputRelay.OnControlsChanged`; `reference/decompiled/LocalizationManager.cs` `LocalizationManager.GetInputStringFromAction`; `reference/decompiled/MenuControlContainer.cs` `MenuControlContainer.Initialize`.
- Spell guard UI is global. Custom `PlayerInput.OnGuard` calls `HUDController.TryGetInstance()?.spellHUD.UpdateGuard(guardHeld)` for whichever player receives that call. Citation: `reference/decompiled/PlayerInput.cs` `PlayerInput.OnGuard`; `reference/decompiled/SpellHUD.cs` `SpellHUD.UpdateGuard`.

## 3. Camera

### What sets follow/look

- I found no source method that assigns `CameraManager.cineCam.Follow` or `.LookAt`. `CameraManager` stores serialized Cinemachine references and initializes `Camera.main`, but does not set Follow/LookAt in code. Citation: `reference/decompiled/CameraManager.cs` `CameraManager.Initialize`.
- `Cinematic` sets up its own `CinemachineTargetGroup` for cinematic playback by creating temporary transforms at actor positions and assigning them to `targetGroup.Targets`; this is separate from the main gameplay camera. Citation: `reference/decompiled/Cinematic.cs` `Cinematic.SetTargetGroup`.

### `CameraManager` singleton-player uses

- `CameraManager.Update`: compares camera horizontal movement direction against `Player.GetInstance().GetVel().x` to drive lookahead composition. Needs midpoint or aggregate living-player velocity logic; otherwise P2 movement will not affect lookahead. Citation: `reference/decompiled/CameraManager.cs` `CameraManager.Update`.
- `CameraManager.CalculateFrustrumExtents`: uses the singleton player's Y position to define the horizontal plane for frustum-bound calculations. Needs midpoint/follow-target Y or a null-guarded selected living-player Y. Citation: `reference/decompiled/CameraManager.cs` `CameraManager.CalculateFrustrumExtents`.
- `CameraManager.OnDrawGizmos`: editor-only gizmo plane uses singleton player Y. Nice-to-have only. Citation: `reference/decompiled/CameraManager.cs` `CameraManager.OnDrawGizmos`.
- Utility methods `OnCamera`, `InLeftBound`, `InRightBound`, `InFrontBound`, `InBackBound`, `ClampBounds` users do not call `Player.GetInstance()` directly; they operate from `CameraManager.main` and stored frustum bounds. Citations: `reference/decompiled/CameraManager.cs` `CameraManager.OnCamera` / `InLeftBound` / `InRightBound` / `InFrontBound` / `InBackBound`; `reference/decompiled/PhysObject.cs` `PhysObject.ClampBounds`.

### Other camera-adjacent player refs

- `Unity.Cinemachine.LookAhead.PostPipelineStageCallback` uses `Player.GetInstance().GetVel().normalized` and applies position correction. Needs aggregate velocity if this extension is active on the gameplay vcam; otherwise it will bias only P1. Citation: `reference/decompiled/Unity.Cinemachine/LookAhead.cs` `LookAhead.PostPipelineStageCallback`.
- `PixelController.Update` writes shader global `PlayerPos` from `Player.GetInstance().transform.position`. Nice-to-have midpoint or P1-only, depending on shader effect; gameplay movement does not depend on it. Citation: `reference/decompiled/PixelController.cs` `PixelController.Update`.

Best patch point: create one mod-owned "CoopCameraTarget" transform updated each `LateUpdate` to the midpoint of living/non-downed players. At runtime, assign the gameplay `CameraManager.cineCam` follow/look target or the serialized target group member to that transform after `CameraManager.Initialize` / after level load. Patch `CameraManager.Update`, `CameraManager.CalculateFrustrumExtents`, and `LookAhead.PostPipelineStageCallback` to use the same aggregate target/velocity. The exact assignment mechanism must be verified at runtime because Follow/LookAt is serialized, not assigned in source. Citations for needed methods: `reference/decompiled/CameraManager.cs` `CameraManager.Initialize` / `Update` / `CalculateFrustrumExtents`; `reference/decompiled/Unity.Cinemachine/LookAhead.cs` `LookAhead.PostPipelineStageCallback`.

## 4. Death, respawn, game-over, revive

### Current path

- Damage flows through `Hurtbox.Damage`; when health reaches zero from positive it calls `Hurtbox.Kill`, which calls `phys.Die(damageEvent?.actor, gibbed)`. Citations: `reference/decompiled/Hurtbox.cs` `Hurtbox.Damage` / `Hurtbox.Kill`.
- `Player.Die` calls `Actor.Die` then writes death cause into `DungeonMaster` as either the attacking bot's `enemyName` or `"The Dungeon"`. Citations: `reference/decompiled/Player.cs` `Player.Die`; `reference/decompiled/DungeonMaster.cs` `DungeonMaster.SetDeathData`.
- `Actor.Die` calls action/attack `Die` hooks, sets `ActState.Downed`, plays death/air stun animation path, and triggers death mods. Non-player actors also award soul tokens/stats. Citation: `reference/decompiled/Actor.cs` `Actor.Die`.
- `Player.UpdateDeath` runs while `ActState.Downed`. After `despawnTime`, it checks `DungeonMaster.CheckRespawns()`. If true, it consumes one respawn, stuns, heals to max, resets the death timer, and updates the HUD lives tracker. If false, it saves ghost data, opens `MenuObjectGameOver`, and parks the death timer at negative infinity. Citations: `reference/decompiled/Player.cs` `Player.UpdateDeath`; `reference/decompiled/DungeonMaster.cs` `DungeonMaster.CheckRespawns` / `UseRespawn`.
- `MenuObjectGameOver.Activate` starts fade/dialogue and later `LeaveDungeon` calls `DungeonMaster.EndRun`, deactivates, and opens run recap. Citation: `reference/decompiled/MenuObjectGameOver.cs` `MenuObjectGameOver.Activate` / `LeaveDungeon`.

### Respawns

- Respawn count is shared, not per player: `DungeonMaster.CheckRespawns` compares `respawnsUsed` to `CustomEffect.CheckEffect(NumRespawns)`, and `UseRespawn` increments the shared counter. Citation: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.CheckRespawns` / `UseRespawn`.
- `CustomEffect.CheckEffect` reads only `Player.GetInstance().GetModCon()`, so respawn-granting effects on P2 would not count unless patched. Citation: `reference/decompiled/CustomEffect.cs` `CustomEffect.CheckEffect`.

### Resurrect action

- `CheckResurrect` is Behavior Designer enemy AI: it gets a `Bot`, finds a child `Resurrect`, and succeeds only when that enemy has idle `PlayDead` actors available and has not hit max resurrects. Citation: `reference/decompiled/CheckResurrect.cs` `CheckResurrect.OnAwake` / `OnUpdate`.
- `Resurrect` finds `PlayDead` components under the bot's encounter, marks some as ready, then calls their `UniqueTrigger`; it tracks active resurrected actors and kills them when the resurrecting actor dies. Citation: `reference/decompiled/Resurrect.cs` `Resurrect.FindIdle` / `UniqueTrigger` / `ResurrectActors` / `Die`.
- `PlayDead` can wake on singleton player proximity, not on any player. Citation: `reference/decompiled/PlayDead.cs` `PlayDead.Update`.

Conclusion: reuse `Resurrect` concepts/effects if useful, but do not treat it as an ally-revive system. It is enemy/encounter `PlayDead` logic.

### Concrete both-players-down patch

- Patch `Player.UpdateDeath` with a prefix/transpiler or replace after-death decision: when a player death timer expires and at least one other co-op player is alive/not `ActState.Downed`, do not call `SaveGhostData` or `MenuObjectGameOver`. Leave that player downed, optionally keep `deathTimer` below the threshold or parked. Citation for original behavior: `reference/decompiled/Player.cs` `Player.UpdateDeath`.
- When all co-op players are downed and no shared respawn remains, allow exactly one player to execute the original failure path (`DungeonMaster.SaveGhostData`; `MenuController.ActivateMenu<MenuObjectGameOver>()`) and park all other downed timers. Citations: `reference/decompiled/Player.cs` `Player.UpdateDeath`; `reference/decompiled/DungeonMaster.cs` `DungeonMaster.SaveGhostData`.
- If a shared respawn is available, decide mod policy explicitly: current single-player code would let whichever downed player's timer reaches threshold consume the shared respawn and heal only that player. For "all down only" semantics, defer shared respawn consumption until all living players are down, then revive either all players or one selected player. This is a mod design recommendation based on `DungeonMaster.CheckRespawns` being shared. Citation: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.CheckRespawns` / `UseRespawn`.

## 5. `Player.GetInstance()` / `Player.instance` inventory

Classification key: `[SAFE for P1-as-instance]`, `[NEEDS patch - gameplay-breaking for P2]`, `[NEEDS patch - nice-to-have]`.

- `Player.cs` `Player.Initialize` / `GetInstance`: `[NEEDS patch - gameplay-breaking for P2]`. Clone overwrites `Player.instance`; all singleton-player systems retarget to P2. Keep P1 as canonical singleton or replace singleton APIs with coop registry. Citation: `reference/decompiled/Player.cs` `Player.Initialize` / `GetInstance`.
- `Breakable.cs` `Breakable.Damage`: `[NEEDS patch - nice-to-have]`. Loot effect owner/facing actor is always singleton player, even when P2 breaks the object. Actual loot goes through shared `LootContainer.GrantLoot`, so gameplay reward still happens. Citation: `reference/decompiled/Breakable.cs` `Breakable.Damage`.
- `CameraManager.cs` `Update` / `CalculateFrustrumExtents` / `OnDrawGizmos`: `[NEEDS patch - gameplay-breaking for P2]` for `Update` and frustum extents, `[NEEDS patch - nice-to-have]` for gizmos. Citation: `reference/decompiled/CameraManager.cs` those methods.
- `Cinematic.cs` `PauseActors`: `[NEEDS patch - nice-to-have/gameplay-breaking by cinematic]`. If a cinematic actor list includes P2, P2 is blocked through direct actor cast; but the `pauseInput` fallback blocks only `Player.GetInstance()`. Citation: `reference/decompiled/Cinematic.cs` `Cinematic.PauseActors`.
- `CinematicListener.cs` `Activate`: `[SAFE for P1-as-instance]` for authored single-player cinematics; `[NEEDS patch - nice-to-have]` if cinematics should include both players. Citation: `reference/decompiled/CinematicListener.cs` `CinematicListener.Activate`.
- `CustomEffect.cs` `CheckEffect`: `[NEEDS patch - gameplay-breaking for P2/shared run]`. Global item/respawn/economy effects check only singleton player's mods. Citation: `reference/decompiled/CustomEffect.cs` `CustomEffect.CheckEffect`.
- `DebugManager.cs` `ApplyPlayerModes`: `[SAFE for P1-as-instance]` unless debug tools must affect P2; then nice-to-have. Citation: `reference/decompiled/DebugManager.cs` `DebugManager.ApplyPlayerModes`.
- `DungeonMaster.cs` `EndLevel` / `SavePlayerStatuses` / `ClearPlayerMods` / `GiveAllBoons`: `[NEEDS patch - gameplay-breaking for P2]` for `EndLevel` and statuses because inter-floor HP/magic/status are singleton-only; `ClearPlayerMods` affects trait reset only P1; context-menu helpers are lower priority. Citations: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.EndLevel` / `SavePlayerStatuses` / `ClearPlayerMods` / `GiveAllBoons`.
- `Encounter.cs` `ActivateEncounter`: `[SAFE for P1-as-instance]` for cinematic slot 0 unless the cinematic must represent P2; encounter spawn/door completion itself is not singleton-player based. Citation: `reference/decompiled/Encounter.cs` `Encounter.ActivateEncounter` / `HandleEncounterCompletion`.
- `HUDController.cs` `Start`: `[NEEDS patch - gameplay-breaking for P2 visibility]`. Only one `PlayerHUD` binds to singleton player. Citation: `reference/decompiled/HUDController.cs` `HUDController.Start`.
- `LootContainer.cs` `Activate` / `GrantLoot`: `[NEEDS patch - gameplay-breaking for P2]`. Particle attractor targets singleton player; boon rewards are added only to singleton player's `ModController` after shared `DungeonMaster.AddBoon`. Citation: `reference/decompiled/LootContainer.cs` `LootContainer.Activate` / `GrantLoot`.
- `MenuObjectBoonGoddess.cs` `Activate` choose action: `[NEEDS patch - gameplay-breaking for P2]`. Chosen boon is applied only to singleton player's `ModController`, though run state is shared. Citation: `reference/decompiled/MenuObjectBoonGoddess.cs` `MenuObjectBoonGoddess.Activate`.
- `MenuObjectDebugTools.cs` debug apply/boon/kill player paths: `[SAFE for P1-as-instance]` for debug-only unless coop debug support matters. Citation: `reference/decompiled/MenuObjectDebugTools.cs` relevant switch/apply blocks.
- `MenuObjectMerchantEvent.cs` `Activate`: `[NEEDS patch - nice-to-have]`. Merchant preview health bar uses singleton player's saved/run HP ratio. Citation: `reference/decompiled/MenuObjectMerchantEvent.cs` `MenuObjectMerchantEvent.Activate`.
- `MenuObjectOldKnight.cs` `Deactivate`: `[NEEDS patch - gameplay-breaking after trait changes]`. Clears/reapplies trait mods only on singleton player and reinitializes only singleton HUD. Citation: `reference/decompiled/MenuObjectOldKnight.cs` `MenuObjectOldKnight.Deactivate`.
- `MenuObjectStatus.cs` status menu initialization: `[NEEDS patch - nice-to-have]`. Status menu shows singleton player only. Citation: `reference/decompiled/MenuObjectStatus.cs` `MenuObjectStatus.Activate`.
- `MenuWizardEquipWindow.cs` `CloseEquipWindow`: `[NEEDS patch - gameplay-breaking for P2 spells]`. Active spells are shared in `DungeonMaster`, but copied onto only singleton player's `AttackController`. Citation: `reference/decompiled/MenuWizardEquipWindow.cs` `MenuWizardEquipWindow.CloseEquipWindow`.
- `Pitbox.cs` `Hit` / `ResetPlayer`: `[NEEDS patch - gameplay-breaking for P2]`. Any player entering starts a single shared reset timer; pit effect uses singleton player's effect; reset/damage/death timer apply only to singleton player, not the actual target. Citation: `reference/decompiled/Pitbox.cs` `Pitbox.Hit` / `ResetPlayer`.
- `PixelController.cs` `Update`: `[NEEDS patch - nice-to-have]`. Shader global `PlayerPos` tracks singleton player only. Citation: `reference/decompiled/PixelController.cs` `PixelController.Update`.
- `PlayDead.cs` `Update`: `[NEEDS patch - gameplay-breaking for enemy wake behavior around P2]`. Wake-on-player-proximity ignores P2. Citation: `reference/decompiled/PlayDead.cs` `PlayDead.Update`.
- `ProjectileSpawner.cs` `FireProjectile`: `[NEEDS patch - gameplay-breaking for traps/turrets that target players]`. `targetPlayer` projectiles target singleton player's transform only. Citation: `reference/decompiled/ProjectileSpawner.cs` `ProjectileSpawner.FireProjectile`.
- `SpellHUD.cs` `Update`: `[NEEDS patch - gameplay-breaking for P2 HUD accuracy]`. Spell charge visual uses singleton player's magic only. Citation: `reference/decompiled/SpellHUD.cs` `SpellHUD.Update`.
- `Trigger.cs` `OnTriggerEnter` / `OnTriggerExit` / `Cleanup`: `[NEEDS patch - gameplay-breaking for P2]`. Non-all-actor triggers reject any collider not equal to `Player.GetInstance().gameObject`; interaction triggers set/clear interactable only on singleton player and show a single prompt. Citation: `reference/decompiled/Trigger.cs` `Trigger.OnTriggerEnter` / `OnTriggerExit` / `Cleanup`.
- `Unity.Cinemachine/LookAhead.cs` `PostPipelineStageCallback`: `[NEEDS patch - nice-to-have or gameplay depending camera feel]`. Uses singleton player velocity for Cinemachine correction. Citation: `reference/decompiled/Unity.Cinemachine/LookAhead.cs` `LookAhead.PostPipelineStageCallback`.

## 6. HUD

- `HUDController` is singleton-ish and owns one serialized `PlayerHUD`; it initializes that HUD 0.05 seconds after `Start` with `Player.GetInstance()`, then initializes currency/mod/spell HUD pools. Citation: `reference/decompiled/HUDController.cs` `HUDController.Awake` / `Start`.
- `PlayerHUD.Initialize(Actor)` is already generic enough to bind to any `Actor`: it stores the actor, finds child `HealthBar`, calls `healthBar.Initialize(actor.GetHurtbox())`, initializes `Magicbar`, and updates shared lives display. Citation: `reference/decompiled/PlayerHUD.cs` `PlayerHUD.Initialize`.
- `PlayerHUD.Update` pulls health and magic from its attached actor every frame, so a duplicated `PlayerHUD` prefab/subtree should work for P2 if initialized with the P2 actor. Citation: `reference/decompiled/PlayerHUD.cs` `PlayerHUD.Update`.
- `HealthBar.Initialize(Hurtbox)` binds directly to a hurtbox and clones its own material, so duplicated health bars can be independent. Citation: `reference/decompiled/HealthBar.cs` `HealthBar.Initialize` / `UpdateInfo`.
- `Magicbar.Initialize` sizes pips from shared player trait data, not the actor, while `PlayerHUD.Update` passes actor magic to `Magicbar.UpdateInfo`. This is acceptable if both players share traits. Citation: `reference/decompiled/Magicbar.cs` `Magicbar.Initialize` / `UpdateInfo`.
- Minimal viable P2 health display: after P2 clone initialization, instantiate or duplicate the existing `HUDController.playerHUD` GameObject under the same UI parent, reposition/anchor it away from P1, call `PlayerHUD.Initialize(p2)`, and optionally disable or hide duplicated lives/spell widgets if the shared lives/magic UI becomes confusing. This recommendation is based on the generic `PlayerHUD.Initialize(Actor)` and `HealthBar.Initialize(Hurtbox)` methods cited above.

## 7. Clone-based approach hazards

Severity scale: Critical blocks basic co-op; High causes major wrong gameplay; Medium causes wrong feedback or edge-case gameplay; Low is debug/cosmetic.

- Critical: `Player.instance` overwrite on clone. Every singleton-player call can flip from P1 to P2 depending on clone initialization order. Citation: `reference/decompiled/Player.cs` `Player.Initialize`.
- Critical: custom `PlayerInput.instance` singleton prevents clone input component from initializing normally; `InputRelay` forwards only to that static. Citations: `reference/decompiled/PlayerInput.cs` `PlayerInput.Init`; `reference/decompiled/InputRelay.cs` input callbacks.
- Critical: `LevelManager.SetupPlayer` applies shared state to every registered `Player`, but only first status load consumes `DungeonMaster.playerStatuses`. P2 may miss persistent statuses unless explicitly cloned/shared. Citations: `reference/decompiled/LevelManager.cs` `LevelManager.SetupPlayer`; `reference/decompiled/DungeonMaster.cs` `DungeonMaster.LoadPlayerStatuses`.
- High: shared run HP/magic/status snapshot is singleton-only at floor end. Citation: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.EndLevel`.
- High: death/game-over is per-player timer but global failure UI; first downed player whose timer expires without respawns ends the run. Citation: `reference/decompiled/Player.cs` `Player.UpdateDeath`.
- High: respawn count/effect lookup is shared and singleton-mod based. Citations: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.CheckRespawns`; `reference/decompiled/CustomEffect.cs` `CustomEffect.CheckEffect`.
- High: interaction triggers exclude P2 by GameObject equality against singleton player. Citation: `reference/decompiled/Trigger.cs` `Trigger.OnTriggerEnter` / `OnTriggerExit`.
- High: pitfall reset/damage always applies to singleton player, regardless of which player fell. Citation: `reference/decompiled/Pitbox.cs` `Pitbox.Hit` / `ResetPlayer`.
- High: loot/boon/spell application updates shared run state but copies actor-local mods/spells only to singleton player. Citations: `reference/decompiled/LootContainer.cs` `LootContainer.GrantLoot`; `reference/decompiled/MenuObjectBoonGoddess.cs` `MenuObjectBoonGoddess.Activate`; `reference/decompiled/MenuWizardEquipWindow.cs` `MenuWizardEquipWindow.CloseEquipWindow`; `reference/decompiled/MenuObjectOldKnight.cs` `MenuObjectOldKnight.Deactivate`.
- High: camera follow is serialized/unknown and source lookahead/frustum logic uses singleton player. Citations: `reference/decompiled/CameraManager.cs` `CameraManager.Update` / `CalculateFrustrumExtents`; `reference/decompiled/Unity.Cinemachine/LookAhead.cs` `LookAhead.PostPipelineStageCallback`.
- Medium: P2 gameplay input will continue during menus/cinematics unless the mod mirrors `InputRelay.SwitchActionMap` and `PlayerInput.SetBlockTimer` into P2's input path. Citations: `reference/decompiled/MenuHandler.cs` `MenuHandler.SetActiveMenuObject`; `reference/decompiled/Cinematic.cs` `Cinematic.PauseActors`.
- Medium: UI navigation and prompt display are global. P2 interactions would open the same menu stack and prompt widget, and the original UI input source remains `InputRelay.activeInput`. Citations: `reference/decompiled/HUDController.cs` `HUDController.ShowInteractionPrompt`; `reference/decompiled/InputRelay.cs` menu callbacks.
- Medium: trap/turret projectiles with `targetPlayer` target singleton player only. Citation: `reference/decompiled/ProjectileSpawner.cs` `ProjectileSpawner.FireProjectile`.
- Medium: `PlayDead` wake-on-proximity enemies ignore P2. Citation: `reference/decompiled/PlayDead.cs` `PlayDead.Update`.
- Medium: HUD spell/magic visuals and guard highlight are singleton/global. Citations: `reference/decompiled/SpellHUD.cs` `SpellHUD.Update`; `reference/decompiled/PlayerInput.cs` `PlayerInput.OnGuard`.
- Medium: `CustomEffect.CheckEffect` makes global economy/effect calculations depend on P1 mods only; this affects `InventoryManager.AddItem` gold/soul modifiers and respawn counts. Citations: `reference/decompiled/CustomEffect.cs` `CustomEffect.CheckEffect`; `reference/decompiled/InventoryManager.cs` `InventoryManager.AddItem`.
- Medium: cinematic actor slot 0 passes singleton player; P2 will not be bound into player-authored cinematic slots unless the mod expands actor lists. Citations: `reference/decompiled/Encounter.cs` `Encounter.ActivateEncounter`; `reference/decompiled/CinematicListener.cs` `CinematicListener.Activate`; `reference/decompiled/Cinematic.cs` `Cinematic.AssignTracks`.
- Medium: save writes are global and tied to singleton death/run state. The co-op game-over patch must ensure only the all-down failure path calls ghost/run save logic. Citations: `reference/decompiled/DungeonMaster.cs` `DungeonMaster.SaveGhostData` / `EndRun` / `SaveRunRecaps`; `reference/decompiled/SaveManager.cs` `SaveManager.Save`.
- Low/Medium: stats are global, not per-player. P2 kills will still count through enemy death, but there is no per-player attribution and economy modifiers can still be P1-only through `CustomEffect`. Citations: `reference/decompiled/Bot.cs` `Bot.Die`; `reference/decompiled/StatTracker.cs` `StatTracker.UpdateStat`; `reference/decompiled/CustomEffect.cs` `CustomEffect.CheckEffect`.
- Medium: timers are global. Player/pit/menu camera-unlock and action-map-release timers can overlap when both players trigger similar events, so P2-specific timers should avoid reusing singleton-player callbacks. Citations: `reference/decompiled/TimerManager.cs` `TimerManager.CreateNewTimer`; `reference/decompiled/Player.cs` `Player.ResetToLastGround`; `reference/decompiled/Pitbox.cs` `Pitbox.Update`; `reference/decompiled/MenuController.cs` `MenuController.ReleaseControls`.
- Low/Medium: duplicated animation events are mostly instance-local. `Actor.FireProjectile`, `Lunge`, `StartRecovery`, and `PlayFootstep` delegate to the actor's own controllers/audio source; double firing only occurs when both players perform animations, which is expected. Citation: `reference/decompiled/Actor.cs` `Actor.FireProjectile` / `Lunge` / `StartRecovery` / `PlayFootstep`.
- Low: footstep audio sources are pulled from shared `SoundManager` but parented to each actor, so cloning will create additional footstep sources rather than a singleton conflict. Citations: `reference/decompiled/Actor.cs` `Actor.PlayFootstep` / `ParentFootsteps`; `reference/decompiled/SoundManager.cs` `SoundManager.GetAudioSource`.
- Low: debug/player-mode tools affect only singleton player. Citation: `reference/decompiled/DebugManager.cs` `DebugManager.ApplyPlayerModes`; `reference/decompiled/MenuObjectDebugTools.cs` debug apply/kill paths.
- Low: `PixelController` shader `PlayerPos` tracks singleton player only. Citation: `reference/decompiled/PixelController.cs` `PixelController.Update`.

## Recommended patch order

1. Add a mod-side `CoopPlayers` registry and mark P1/P2 explicitly. Prevent clone `Player.Initialize` from overwriting `Player.instance`, or restore P1 immediately after cloning. Citation: `reference/decompiled/Player.cs` `Player.Initialize`.
2. Make P2 custom `PlayerInput` instance usable without the static singleton, and add a separate paired Unity Input System source that forwards to it by reference. Citations: `reference/decompiled/PlayerInput.cs` `PlayerInput.Init`; `reference/decompiled/InputRelay.cs` input callbacks.
3. Patch death/game-over first: defer game-over until all registered coop players are downed. Citation: `reference/decompiled/Player.cs` `Player.UpdateDeath`.
4. Patch triggers and pitboxes to operate on the actual `Player` component from the collider, not `Player.GetInstance()`. Citations: `reference/decompiled/Trigger.cs` `Trigger.OnTriggerEnter`; `reference/decompiled/Pitbox.cs` `Pitbox.Hit` / `ResetPlayer`.
5. Add midpoint camera target and aggregate camera patches. Citations: `reference/decompiled/CameraManager.cs` `CameraManager.Update` / `CalculateFrustrumExtents`; `reference/decompiled/Unity.Cinemachine/LookAhead.cs` `LookAhead.PostPipelineStageCallback`.
6. Duplicate/bind P2 HUD health at minimum. Citations: `reference/decompiled/HUDController.cs` `HUDController.Start`; `reference/decompiled/PlayerHUD.cs` `PlayerHUD.Initialize`.
7. Patch shared reward/spell/trait application so actor-local mods/spells are applied to all active coop players. Citations: `reference/decompiled/LootContainer.cs` `LootContainer.GrantLoot`; `reference/decompiled/MenuWizardEquipWindow.cs` `MenuWizardEquipWindow.CloseEquipWindow`; `reference/decompiled/MenuObjectOldKnight.cs` `MenuObjectOldKnight.Deactivate`.

## SharedRewardsPatches decisions

- `LootContainer.Activate`: full replacement only in co-op, because the particle attractor is created and assigned locally in this method. The copy preserves item prompts, menu routing, shared loot selection, trigger state, and single effect creation, but targets the attractor at `CoopPlayers.Nearest`.
- `LootContainer.GrantLoot`: postfix, because vanilla already adds shared `DungeonMaster` boon state and P1's actor-local boon mods once. The postfix adds only boon mods to players other than `Player.GetInstance()`.
- `MenuObjectBoonGoddess.Activate`: co-op-only replacement of the short activate/confirm setup, because the chosen-boon application lives inside a generated delegate. The replacement keeps `DungeonMaster.AddBoon` once and applies the selected boon to every co-op player's `ModController`.
- `MenuWizardEquipWindow.CloseEquipWindow`: postfix, because vanilla already updates shared `DungeonMaster.activeSpells` and P1's `AttackController`. The postfix mirrors the same spell add/remove calls only to other players for loot-picker spell equips.
- `MenuObjectOldKnight.Deactivate`: co-op-only replacement, because the trait clear/reapply work runs inside the fade-out callback. The replacement preserves HUD currency update, fade timing, menu close/release, P1 HUD reinit, villager update, and save, but clears and reapplies player mods/traits for all co-op players.
- `Cinematic.PauseActors`: postfix, because vanilla actor/time/gameplay pause behavior should stay intact. The postfix mirrors the `pauseInput` fallback block duration to every co-op player's `PlayerInput`.
- `PlayDead.Update`: co-op-only full replacement of the small update method. It preserves animation holding and wake radius checks, but evaluates the nearest co-op player instead of the singleton.
- `ProjectileSpawner.FireProjectile`: co-op-only full replacement of the small fire method. It preserves null checks, spawn position calculation, projectile firing, and repeat timer behavior, but uses the nearest co-op player as the target when `targetPlayer` is enabled.
