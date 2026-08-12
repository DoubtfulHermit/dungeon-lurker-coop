using System.Collections;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using UnityEngine.SceneManagement;

namespace DungeonLurkerCoop;

/// Orchestrates co-op per scene: spawns the P2 clone once P1 exists, wires its
/// input, camera target and HUD, runs the revive loop and menu-block mirroring.
public class CoopManager : MonoBehaviour
{
    public static CoopManager Instance;

    /// True while Object.Instantiate of the P1 GameObject is running, so
    /// Harmony patches can tell the clone's Awake chain apart from a
    /// scene-baked player. Instantiate is synchronous, so this is safe.
    public static bool IsCloning;

    private const float ReviveRadius = 2.5f;
    private const float ReviveTime = 3f;

    private readonly Dictionary<Player, float> awaitingRevive = new();

    private P2InputBridge p2Bridge;
    private Transform camTarget;
    private PlayerHUD p2Hud;
    private Coroutine spawnRoutine;

    public static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("DLCoopManager");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CoopManager>();
    }

    private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        awaitingRevive.Clear();
        p2Hud = null;
        camTarget = null;
        if (spawnRoutine != null) StopCoroutine(spawnRoutine);
        spawnRoutine = StartCoroutine(TrySpawnP2());
    }

    public static void MarkAwaitingRevive(Player p)
    {
        if (Instance != null && !Instance.awaitingRevive.ContainsKey(p))
        {
            Instance.awaitingRevive[p] = 0f;
            Plugin.Log.LogInfo($"{p.name} is down and awaiting revive.");
        }
    }

    private IEnumerator TrySpawnP2()
    {
        if (!Plugin.CoopEnabled.Value) yield break;

        // Wait for a scene-baked player to appear and initialize (if any).
        float deadline = Time.unscaledTime + 5f;
        while (Time.unscaledTime < deadline && (CoopPlayers.P1 == null || InputRelay.activeInput == null))
            yield return null;
        if (CoopPlayers.P1 == null || CoopPlayers.P1.gameObject.scene != SceneManager.GetActiveScene())
            yield break;

        Gamepad pad = PickP2Gamepad();
        if (pad == null && !Plugin.SpawnWithoutGamepad.Value)
        {
            UnpinRelay();
            Plugin.Log.LogInfo("Co-op: no gamepad yet — waiting for one to connect (hot-plug).");
            while (CoopPlayers.P1 != null && CoopPlayers.P2 == null)
            {
                pad = PickP2Gamepad();
                if (pad != null)
                {
                    Plugin.Log.LogInfo($"Co-op: gamepad '{pad.displayName}' connected, spawning P2.");
                    break;
                }
                yield return new WaitForSecondsRealtime(2f);
            }
            if (pad == null) yield break;
        }

        SpawnP2(pad);
    }

    /// P2 takes the last gamepad; P1's relay keeps everything else. With a
    /// single gamepad P1 is expected to play keyboard+mouse.
    private static Gamepad PickP2Gamepad()
    {
        var pads = Gamepad.all;
        if (pads.Count == 0) return null;
        return pads[pads.Count - 1];
    }

    private void SpawnP2(Gamepad pad)
    {
        Player p1 = CoopPlayers.P1;
        Vector3 spawnPos = p1.transform.position + p1.transform.right * -1.2f + Vector3.up * 0.1f;

        IsCloning = true;
        GameObject clone;
        try
        {
            clone = Instantiate(p1.gameObject, spawnPos, p1.transform.rotation);
        }
        finally
        {
            IsCloning = false;
        }
        clone.name = "Player2";

        Player p2 = clone.GetComponent<Player>();
        if (p2 == null || CoopPlayers.P2 != p2)
        {
            Plugin.Log.LogError("P2 clone did not register correctly; destroying clone.");
            Destroy(clone);
            CoopPlayers.P2 = null;
            return;
        }

        StripSingletonHazards(clone);
        TintRenderers(clone);

        // Input: dedicated bridge bound to P2's own custom PlayerInput component.
        // Mark it initialized ourselves — its Init() would hit the singleton
        // guard and never arm, leaving block timers stuck forever.
        var p2Input = p2.GetComponent<PlayerInput>();
        p2Input.initialized = true;
        p2Input.blockTimer = 0f;
        p2Bridge = clone.AddComponent<P2InputBridge>();
        p2Bridge.Bind(p2Input, pad);
        if (pad != null) RestrictRelayFrom(pad);

        StartCoroutine(SetupCameraAndHud());

        Plugin.Log.LogInfo($"Co-op: spawned P2 at {spawnPos}, device: {(pad != null ? pad.displayName : "none (debug)")}.");
    }

    /// The clone must never carry a second camera, audio listener or vcam.
    private static void StripSingletonHazards(GameObject clone)
    {
        foreach (var cam in clone.GetComponentsInChildren<Camera>(true))
        {
            Plugin.Log.LogWarning($"Destroying cloned Camera on {cam.gameObject.name}");
            Destroy(cam.gameObject);
        }
        foreach (var listener in clone.GetComponentsInChildren<AudioListener>(true))
            Destroy(listener);
        foreach (var vcam in clone.GetComponentsInChildren<CinemachineCamera>(true))
        {
            Plugin.Log.LogWarning($"Destroying cloned CinemachineCamera on {vcam.gameObject.name}");
            Destroy(vcam.gameObject);
        }
    }

    /// The game renders actors through an 8x8 palette-index shader. A flat
    /// color overlay reads as a hologram, so instead rotate the hue of the
    /// clone's palette: every color keeps its saturation/brightness (shading
    /// intact) but lands on a different hue — a classic "player 2" recolor.
    /// Greys/metals (low saturation) are left untouched.
    private static void TintRenderers(GameObject clone)
    {
        if (!Plugin.TintP2.Value) return;
        float shift = Plugin.P2HueShift.Value / 360f;
        int palettes = 0;
        foreach (var ic in clone.GetComponentsInChildren<IndexColor>(true))
        {
            var arr = ic.colorArray;
            if (arr == null || arr.Length < 64) continue;
            for (int i = 0; i < arr.Length; i++)
            {
                Color.RGBToHSV(arr[i], out float h, out float s, out float v);
                if (s < 0.08f) continue;
                Color c = Color.HSVToRGB(Mathf.Repeat(h + shift, 1f), s, v, hdr: true);
                c.a = arr[i].a;
                arr[i] = c;
            }
            palettes++;
        }
        if (palettes > 0)
            Plugin.Log.LogInfo($"P2 palette: hue-shifted {palettes} palette(s) by {Plugin.P2HueShift.Value} degrees.");
        else
            Plugin.Log.LogWarning("P2 palette: no IndexColor palettes found on clone.");
    }

    /// Pin the game's own input relay away from P2's pad. Restricting the
    /// action asset's devices is not enough: the relay's PlayerInput auto-
    /// switches control schemes on any device activity and re-grabs the pad,
    /// so the pad ends up driving BOTH characters. Kill auto-switch and pin
    /// P1 to keyboard+mouse (or the first pad when there are two).
    private static void RestrictRelayFrom(Gamepad pad)
    {
        var relay = InputRelay.activeInput;
        if (relay == null) return;
        try
        {
            // Scheme names vary per asset — discover them instead of guessing.
            string kbScheme = null, padScheme = null, allNames = "";
            foreach (var sch in relay.actions.controlSchemes)
            {
                string n = sch.name.ToLowerInvariant();
                allNames += sch.name + " | ";
                if (kbScheme == null && (n.Contains("key") || n.Contains("mouse"))) kbScheme = sch.name;
                if (padScheme == null && (n.Contains("pad") || n.Contains("controller") || n.Contains("joy"))) padScheme = sch.name;
            }
            Plugin.Log.LogInfo($"Relay control schemes: {allNames}");

            relay.neverAutoSwitchControlSchemes = true;
            var pads = Gamepad.all;
            if (pads.Count >= 2 && padScheme != null)
                relay.SwitchCurrentControlScheme(padScheme, pads[0]);
            else if (kbScheme != null)
                relay.SwitchCurrentControlScheme(kbScheme, Keyboard.current, Mouse.current);
            else
            {
                relay.user.UnpairDevice(pad);
                InputUser.PerformPairingWithDevice(Keyboard.current, relay.user);
                InputUser.PerformPairingWithDevice(Mouse.current, relay.user);
            }
            Plugin.Log.LogInfo($"Pinned P1 to '{relay.currentControlScheme}'; P2 owns '{pad.displayName}'.");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogWarning($"Could not pin P1 input away from P2's pad: {e.Message}. The pad may drive both players.");
        }
    }

    private static void UnpinRelay()
    {
        var relay = InputRelay.activeInput;
        if (relay != null) relay.neverAutoSwitchControlSchemes = false;
    }

    private IEnumerator SetupCameraAndHud()
    {
        // Give CameraManager/HUDController their own Start/Initialize time.
        yield return new WaitForSeconds(0.3f);
        if (!CoopPlayers.CoopActive) yield break;

        SetupCamera();
        SetupHud();
    }

    private void SetupCamera()
    {
        var cm = CameraManager.instance;
        if (cm == null || cm.cineCam == null)
        {
            Plugin.Log.LogWarning("Co-op camera: no CameraManager/cineCam found.");
            return;
        }

        Transform follow = cm.cineCam.Follow;
        Plugin.Log.LogInfo($"Co-op camera: vcam Follow is '{(follow != null ? follow.name : "null")}', targetGroup is '{(cm.targetGroup != null ? cm.targetGroup.name : "null")}'.");

        if (cm.targetGroup != null && follow == cm.targetGroup.transform)
        {
            // Native path: the camera already follows a target group.
            cm.targetGroup.AddMember(CoopPlayers.P2.transform, 1f, 1f);
            Plugin.Log.LogInfo("Co-op camera: added P2 to existing CinemachineTargetGroup.");
            return;
        }

        // Follow points at P1 (or something else): swap in a midpoint target.
        camTarget = new GameObject("CoopCamTarget").transform;
        camTarget.position = CoopPlayers.Midpoint();
        cm.cineCam.Follow = camTarget;
        if (cm.cineCam.LookAt == follow) cm.cineCam.LookAt = camTarget;
        Plugin.Log.LogInfo("Co-op camera: swapped vcam Follow to midpoint target.");
    }

    private void SetupHud()
    {
        var hud = HUDController.TryGetInstance();
        if (hud == null || hud.playerHUD == null)
        {
            Plugin.Log.LogWarning("Co-op HUD: no HUDController found.");
            return;
        }
        var clone = Instantiate(hud.playerHUD.gameObject, hud.playerHUD.transform.parent);
        clone.name = "PlayerHUD_P2";
        p2Hud = clone.GetComponent<PlayerHUD>();

        // Mirror P1's HUD to the top-right corner at the same height.
        var src = hud.playerHUD.GetComponent<RectTransform>();
        var dst = clone.GetComponent<RectTransform>();
        if (Mathf.Approximately(src.anchorMin.x, src.anchorMax.x))
        {
            dst.anchorMin = new Vector2(1f, src.anchorMin.y);
            dst.anchorMax = new Vector2(1f, src.anchorMax.y);
            dst.pivot = new Vector2(1f - src.pivot.x, src.pivot.y);
            dst.anchoredPosition = new Vector2(-src.anchoredPosition.x, src.anchoredPosition.y);
        }
        else
        {
            // Stretch-anchored fallback: stack below P1 as before.
            dst.anchoredPosition = src.anchoredPosition + new Vector2(0f, -(src.rect.height * src.localScale.y + 6f));
        }

        p2Hud.Initialize(CoopPlayers.P2);
        Plugin.Log.LogInfo($"Co-op HUD: P2 health bar created at {dst.anchoredPosition}.");
    }

    private void Update()
    {
        PruneDestroyed();
        if (!CoopPlayers.CoopActive) return;

        MirrorMenuBlock();
        UpdateRevives();
    }

    /// A gibbed (overkilled) player is destroyed outright; drop dead references
    /// so the rest of the mod cleanly reverts to single-player behavior.
    private void PruneDestroyed()
    {
        if (CoopPlayers.P2 == null && !ReferenceEquals(CoopPlayers.P2, null))
        {
            Plugin.Log.LogWarning("P2 was destroyed (gibbed?); dropping from registry.");
            CoopPlayers.P2 = null;
            UnpinRelay();
        }
        if (CoopPlayers.P1 == null && !ReferenceEquals(CoopPlayers.P1, null))
            CoopPlayers.P1 = null;
        CoopPlayers.All.RemoveAll(p => p == null);
    }

    private void LateUpdate()
    {
        if (camTarget != null && CoopPlayers.CoopActive)
            camTarget.position = CoopPlayers.Midpoint();
    }

    /// The game blocks P1 gameplay input by switching the relay to the UI
    /// action map; mirror that onto P2's own input component.
    private void MirrorMenuBlock()
    {
        var p2In = CoopPlayers.P2 != null ? CoopPlayers.P2.GetPlayerInput() : null;
        if (p2In == null) return;
        bool gameplay = InputRelay.activeInput != null && InputRelay.GetCurrentActionMap() == "Player";
        p2In.blockInput = !gameplay;
    }

    private void UpdateRevives()
    {
        if (awaitingRevive.Count == 0) return;

        var keys = new List<Player>(awaitingRevive.Keys);
        foreach (var downed in keys)
        {
            if (downed == null) { awaitingRevive.Remove(downed); continue; }

            bool allyNear = false;
            foreach (var ally in CoopPlayers.Alive())
            {
                if (ally == downed) continue;
                if ((ally.transform.position - downed.transform.position).sqrMagnitude <= ReviveRadius * ReviveRadius)
                {
                    allyNear = true;
                    break;
                }
            }

            float t = awaitingRevive[downed];
            t = allyNear ? t + Time.deltaTime : Mathf.Max(0f, t - Time.deltaTime * 0.5f);
            awaitingRevive[downed] = t;

            if (t >= ReviveTime)
            {
                awaitingRevive.Remove(downed);
                Revive(downed);
            }
        }
    }

    /// Mirrors the game's own respawn sequence (Player.UpdateDeath), at half HP.
    private static void Revive(Player p)
    {
        p.Stun(2f);
        p.GetHurtbox().Heal(Mathf.Max(1, p.GetHurtbox().MaxHealth() / 2));
        p.deathTimer = 0f;
        Plugin.Log.LogInfo($"Revived {p.name} at half health.");
    }
}
