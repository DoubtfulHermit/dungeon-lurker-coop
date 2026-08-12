using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DungeonLurkerCoop;

/// Debug-only autonomous driver. Progresses the splash menu into a fresh run,
/// then drives both players with in-engine input calls so co-op behavior can
/// be verified from logs and screenshots without physical controllers.
/// Everything goes through the game's own public methods — no OS-level input.
public class TestHarness : MonoBehaviour
{
    private float heartbeatTimer;
    private float screenshotTimer;
    private float driveTimer;
    private int shotIndex;
    private bool menuDriveStarted;

    public static void Bootstrap()
    {
        if (!Plugin.DebugAutoStart.Value && !Plugin.DebugAutoDrive.Value && !Plugin.DebugDeathTest.Value && Plugin.DebugScreenshotInterval.Value <= 0f)
            return;
        var go = new GameObject("DLCoopTestHarness");
        DontDestroyOnLoad(go);
        go.AddComponent<TestHarness>();
        Plugin.Log.LogInfo("TestHarness active.");
    }

    private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        menuDriveStarted = false;
        dungeonEntered = false;
        townTimer = 0f;
        if (Plugin.DebugAutoStart.Value && scene.name == "DungeonLurkerMainMenu")
            StartCoroutine(DriveMainMenu());
    }

    private IEnumerator DriveMainMenu()
    {
        if (menuDriveStarted) yield break;
        menuDriveStarted = true;

        yield return new WaitForSecondsRealtime(2f);
        var splash = FindFirstObjectByType<MenuObjectDungeonLurkerSplash>(FindObjectsInactive.Include);
        if (splash == null)
        {
            Plugin.Log.LogWarning("TestHarness: no splash menu found.");
            yield break;
        }

        for (int i = 0; i < 3; i++)
        {
            Plugin.Log.LogInfo($"TestHarness: splash Progress() #{i + 1}");
            splash.Progress();
            yield return new WaitForSecondsRealtime(1.2f);
        }

        Plugin.Log.LogInfo("TestHarness: calling MoveToTown() to start a run.");
        splash.MoveToTown();
    }

    private float levelTime;
    private int deathTestStage;
    private float townTimer;
    private bool dungeonEntered;

    private void Update()
    {
        Heartbeat();
        Screenshots();
        if (Plugin.DebugAutoDrive.Value) DrivePlayers();
        if (Plugin.DebugDeathTest.Value) DeathTest();
        if (Plugin.DebugAutoStart.Value) AutoEnterDungeon();
    }

    /// From the town hub, start a dungeon run via the game's own door logic.
    private void AutoEnterDungeon()
    {
        if (dungeonEntered || !SceneManager.GetActiveScene().name.Contains("Town")) { townTimer = 0f; return; }
        if (!CoopPlayers.CoopActive) return;
        townTimer += Time.deltaTime;
        if (townTimer < 25f) return;

        var door = FindFirstObjectByType<LevelTransitioner>(FindObjectsInactive.Include);
        if (door == null)
        {
            Plugin.Log.LogWarning("TestHarness: no LevelTransitioner found in town.");
            dungeonEntered = true;
            return;
        }
        Plugin.Log.LogInfo($"TestHarness: entering dungeon via '{door.gameObject.name}'.");
        dungeonEntered = true;
        door.Activate();
    }

    /// Scripted verification of the co-op death rules.
    private void DeathTest()
    {
        if (!CoopPlayers.CoopActive) { levelTime = 0f; deathTestStage = 0; return; }
        levelTime += Time.deltaTime;

        switch (deathTestStage)
        {
            case 0 when levelTime > 40f:
                deathTestStage = 1;
                Plugin.Log.LogInfo("[TEST] Killing P2 outright.");
                KillPlayer(CoopPlayers.P2);
                break;
            case 1 when levelTime > 55f:
                deathTestStage = 2;
                Plugin.Log.LogInfo($"[TEST] P2 state after kill: act={CoopPlayers.P2.GetActState()} hp={CoopPlayers.P2.GetHurtbox().health} respawnsLeft={DungeonMaster.instance.CheckRespawns()}");
                break;
            case 2 when levelTime > 60f:
                deathTestStage = 3;
                Plugin.Log.LogInfo("[TEST] Teleporting P1 next to P2 for revive.");
                CoopPlayers.P1.transform.position = CoopPlayers.P2.transform.position + Vector3.right * 0.8f;
                break;
            case 3 when levelTime > 75f:
                deathTestStage = 4;
                Plugin.Log.LogInfo($"[TEST] Post-revive-window: P2 act={CoopPlayers.P2.GetActState()} hp={CoopPlayers.P2.GetHurtbox().health}/{CoopPlayers.P2.GetHurtbox().MaxHealth()}");
                break;
            case 4 when levelTime > 85f:
                deathTestStage = 5;
                Plugin.Log.LogInfo("[TEST] Killing BOTH players — expecting game over.");
                KillPlayer(CoopPlayers.P1);
                KillPlayer(CoopPlayers.P2);
                break;
            case 5 when levelTime > 105f:
                deathTestStage = 6;
                Plugin.Log.LogInfo($"[TEST] Final: activeMenu={(MenuController.GetActiveMenu() != null ? MenuController.GetActiveMenu().GetType().Name : "none")} P1={CoopPlayers.P1?.GetActState().ToString() ?? "gone"} P2={CoopPlayers.P2?.GetActState().ToString() ?? "gone"}");
                break;
        }
    }

    private static void KillPlayer(Player p)
    {
        if (p == null) return;
        var hb = p.GetHurtbox();
        // Exactly lethal — more would trip the overkill/gib path and destroy the body.
        hb.Damage(new DamageEvent(hb.health, 1f, 1f, newBypass: true, newBlock: false,
            null, null, Vector3.zero, 0f, newLaunch: false, 0f, 1f));
    }

    private void Heartbeat()
    {
        heartbeatTimer += Time.unscaledDeltaTime;
        if (heartbeatTimer < 5f) return;
        heartbeatTimer = 0f;

        var scene = SceneManager.GetActiveScene().name;
        string p1 = Describe(CoopPlayers.P1, "P1");
        string p2 = Describe(CoopPlayers.P2, "P2");
        int actors = LevelManager.GetActorList() != null ? LevelManager.GetActorList().Count : -1;
        Plugin.Log.LogInfo($"[HB] scene={scene} actors={actors} {p1} {p2} map={(InputRelay.activeInput != null ? InputRelay.GetCurrentActionMap() : "-")}");
    }

    private static string Describe(Player p, string tag)
    {
        if (p == null) return $"{tag}=null";
        var hb = p.GetHurtbox();
        var pin = p.GetPlayerInput();
        return $"{tag}[hp={(hb != null ? hb.health : -1)}/{(hb != null ? hb.MaxHealth() : -1)} pos={p.transform.position:F1}" +
               $" act={p.GetActState()} posSt={p.GetPosState()} canAct={p.CanAct()} cine={CinematicController.CheckBusy(p)}" +
               $" holdVel={p.HoldVelActive()} dir={(pin != null ? pin.GetCurrDir() : Vector2.zero):F1} blocked={(pin != null && pin.CheckBlocked())}]";
    }

    private void Screenshots()
    {
        float interval = Plugin.DebugScreenshotInterval.Value;
        if (interval <= 0f) return;
        screenshotTimer += Time.unscaledDeltaTime;
        if (screenshotTimer < interval) return;
        screenshotTimer = 0f;

        string dir = Plugin.DebugScreenshotDir.Value;
        if (string.IsNullOrEmpty(dir)) return;
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"shot_{shotIndex++:D4}.png");
        ScreenCapture.CaptureScreenshot(path);
    }

    /// Simple wander + attack pattern, applied to both players' own inputs.
    private void DrivePlayers()
    {
        driveTimer += Time.deltaTime;

        Drive(CoopPlayers.P1 != null ? CoopPlayers.P1.GetPlayerInput() : null, 0f);
        Drive(CoopPlayers.P2 != null ? CoopPlayers.P2.GetPlayerInput() : null, Mathf.PI);
    }

    private void Drive(PlayerInput input, float phase)
    {
        if (input == null) return;

        // Priority: stand by a downed ally to revive them; then seek the
        // nearest live enemy so encounters trigger; else wander.
        Vector2 dir;
        var self = input.GetComponent<Player>();
        Player downedAlly = null;
        if (self != null && !self.CheckActState(Actor.ActState.Downed))
            foreach (var q in CoopPlayers.All)
                if (q != null && q != self && q.CheckActState(Actor.ActState.Downed))
                    downedAlly = q;
        if (downedAlly != null)
        {
            var delta = downedAlly.transform.position - self.transform.position;
            var flat = new Vector2(delta.x, delta.z);
            input.OnMove(flat.magnitude > 1.2f ? flat.normalized : Vector2.zero);
            return;
        }
        Bot enemy = NearestBot(self);
        if (self != null && enemy != null)
        {
            var delta = enemy.transform.position - self.transform.position;
            var flat = new Vector2(delta.x, delta.z);
            dir = flat.magnitude > 1.2f ? flat.normalized : Vector2.zero;
        }
        else
        {
            float t = driveTimer * 0.7f + phase;
            dir = new Vector2(Mathf.Sin(t), Mathf.Cos(t * 0.6f) * 0.5f);
            dir = dir.magnitude > 0.3f ? dir.normalized : Vector2.zero;
        }
        input.OnMove(dir);

        // Attack roughly every 2.5s, offset per player.
        if (Mathf.Repeat(driveTimer + phase, 2.5f) < Time.deltaTime)
            input.OnLightAtk();
    }

    private static Bot NearestBot(Player self)
    {
        if (self == null || LevelManager.GetActorList() == null) return null;
        Bot best = null;
        float bestSqr = 40f * 40f;
        foreach (var a in LevelManager.GetActorList())
        {
            if (a is not Bot b || b == null || b.CheckActState(Actor.ActState.Downed)) continue;
            float d = (b.transform.position - self.transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = b; }
        }
        return best;
    }
}
