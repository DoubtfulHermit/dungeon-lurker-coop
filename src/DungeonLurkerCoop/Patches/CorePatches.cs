using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DungeonLurkerCoop.Patches;

/// Keeps P1 as the game's Player.instance and registers players with the mod.
[HarmonyPatch(typeof(Player), "Initialize")]
internal static class PlayerInitializePatch
{
    private static Player instanceBefore;

    private static void Prefix()
    {
        instanceBefore = Player.instance;
    }

    private static void Postfix(Player __instance)
    {
        if (CoopManager.IsCloning)
        {
            // The clone's Awake chain grabbed the singleton: give it back.
            Player.instance = instanceBefore;
            CoopPlayers.RegisterP2(__instance);
        }
        else if (Player.instance == __instance)
        {
            // Scene-baked player = fresh level: start a clean registry.
            CoopPlayers.Clear();
            CoopPlayers.RegisterP1(__instance);
        }
    }
}

/// The custom PlayerInput refuses to initialize when the singleton is taken.
/// Let the P2 clone's component initialize privately without becoming it.
/// (Init runs from Start, a frame after cloning, so check the registry.)
[HarmonyPatch(typeof(PlayerInput), "Init")]
internal static class PlayerInputInitPatch
{
    private static bool Prefix(PlayerInput __instance)
    {
        bool isP2 = CoopManager.IsCloning ||
                    (CoopPlayers.P2 != null && __instance.GetComponent<Player>() == CoopPlayers.P2);
        if (!isP2) return true;
        __instance.initialized = true;
        return false;
    }
}

/// Co-op death rules: per-player shared respawns as vanilla, but when respawns
/// are gone a downed player waits for an ally revive instead of ending the
/// run. Game over only fires once every player is down.
[HarmonyPatch(typeof(Player), "UpdateDeath")]
internal static class PlayerUpdateDeathPatch
{
    private static bool Prefix(Player __instance)
    {
        if (!CoopPlayers.CoopActive) return true;
        if (!__instance.CheckActState(Actor.ActState.Downed)) return false;

        __instance.deathTimer += Time.deltaTime;
        if (__instance.deathTimer < __instance.despawnTime) return false;

        if (DungeonMaster.instance.CheckRespawns())
        {
            DungeonMaster.instance.UseRespawn();
            __instance.Stun(2f);
            __instance.hurtbox.Heal(__instance.hurtbox.MaxHealth());
            __instance.deathTimer = 0f;
            HUDController.TryGetInstance()?.playerHUD.UpdateLivesTracker();
        }
        else if (CoopPlayers.AliveCount() > 0)
        {
            __instance.deathTimer = float.NegativeInfinity;
            CoopManager.MarkAwaitingRevive(__instance);
        }
        else
        {
            DungeonMaster.instance.SaveGhostData();
            MenuController.ActivateMenu<MenuObjectGameOver>();
            __instance.deathTimer = float.NegativeInfinity;
        }
        return false;
    }
}

/// Triggers: accept any co-op player, not just the singleton.
[HarmonyPatch(typeof(Trigger), "OnTriggerEnter")]
internal static class TriggerEnterPatch
{
    private static bool Prefix(Trigger __instance, Collider other)
    {
        if (!CoopPlayers.CoopActive) return true;

        Player p = other.GetComponentInParent<Player>();
        bool isPlayerRoot = p != null && other.gameObject == p.gameObject;
        if (!__instance.triggeredByAllActors && !isPlayerRoot) return false;

        switch (__instance.type)
        {
            case Trigger.TriggerType.TriggerZone:
                if (!__instance.disableToggle)
                {
                    if (__instance.triggerOnce) __instance.disableToggle = true;
                    __instance.ActivateListeners();
                }
                break;
            case Trigger.TriggerType.TriggerStayZone:
                if (__instance.stayCount == 0) __instance.ActivateListeners();
                __instance.stayCount++;
                break;
            case Trigger.TriggerType.PlayerInteract:
                if (isPlayerRoot)
                {
                    p.SetInteractable(__instance);
                    if (!__instance.disableToggle)
                        HUDController.TryGetInstance()?.ShowInteractionPrompt(show: true, __instance);
                }
                break;
        }
        return false;
    }
}

[HarmonyPatch(typeof(Trigger), "OnTriggerExit")]
internal static class TriggerExitPatch
{
    private static bool Prefix(Trigger __instance, Collider other)
    {
        if (!CoopPlayers.CoopActive) return true;

        Player p = other.GetComponentInParent<Player>();
        bool isPlayerRoot = p != null && other.gameObject == p.gameObject;
        if (!__instance.triggeredByAllActors && !isPlayerRoot) return false;

        if (__instance.type == Trigger.TriggerType.TriggerStayZone)
        {
            __instance.stayCount--;
            if (__instance.stayCount == 0) __instance.ActivateListeners(inverted: true);
        }
        else if (__instance.type == Trigger.TriggerType.PlayerInteract && isPlayerRoot)
        {
            p.SetInteractable(null);
            // Only hide the prompt if no other player is still on this trigger.
            bool othersUsing = false;
            foreach (var q in CoopPlayers.All)
                if (q != null && q != p && q.GetInteractable() == __instance)
                    othersUsing = true;
            if (!othersUsing)
                HUDController.TryGetInstance()?.ShowInteractionPrompt(show: false, __instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(Trigger), "Cleanup")]
internal static class TriggerCleanupPatch
{
    private static bool Prefix(Trigger __instance)
    {
        if (!CoopPlayers.CoopActive) return true;
        if (__instance.type != Trigger.TriggerType.PlayerInteract) return false;

        foreach (var p in CoopPlayers.All)
        {
            if (p != null && p.GetInteractable() == __instance)
            {
                p.SetInteractable(null);
                HUDController.TryGetInstance()?.ShowInteractionPrompt(show: false, __instance);
            }
        }
        return false;
    }
}

/// Pits: reset/damage the player who actually fell, not the singleton.
[HarmonyPatch(typeof(Pitbox), nameof(Pitbox.Hit))]
internal static class PitboxHitPatch
{
    internal static readonly Dictionary<Pitbox, Player> FallenBy = new();

    private static bool Prefix(Pitbox __instance, Hurtbox target)
    {
        if (!CoopPlayers.CoopActive) return true;
        if (!__instance.initialized) __instance.Initialize();

        if (target.GetActor() is Player fallen)
        {
            if (__instance.resetPlayerTimer > 0f) return false;

            FallenBy[__instance] = fallen;
            CameraManager.LockCamera(newLock: true);
            EffectTrigger.CreateEffect(fallen.pitFallEffect);
            if (target.HealthRatio() > __instance.pitDamagePercent || DungeonMaster.instance.CheckRespawns())
            {
                __instance.resetPlayerTimer = __instance.resetTime;
                return false;
            }
            __instance.resetPlayerTimer = float.PositiveInfinity;
        }

        target.overkillHealth = Mathf.FloorToInt(float.PositiveInfinity);
        target.ForceHealth(0);
        target.Kill();
        if (target.GetActor() != null && target.GetActor() is Bot bot)
            bot.ForceDeathUpdate();
        return false;
    }
}

[HarmonyPatch(typeof(Pitbox), "ResetPlayer")]
internal static class PitboxResetPatch
{
    private static bool Prefix(Pitbox __instance)
    {
        if (!CoopPlayers.CoopActive) return true;

        if (!PitboxHitPatch.FallenBy.TryGetValue(__instance, out var fallen) || fallen == null)
            fallen = Player.GetInstance();
        PitboxHitPatch.FallenBy.Remove(__instance);
        if (fallen == null) return false;

        fallen.ResetToLastGround();
        fallen.SetPosState(Actor.PosState.Knockdown);
        fallen.AnimPlay("DownedBack");
        fallen.GetHurtbox().Damage(new DamageEvent(
            Mathf.FloorToInt(fallen.GetHurtbox().MaxHealth() * __instance.pitDamagePercent),
            1f, 1f, newBypass: true, newBlock: false, null, null, Vector3.zero, 0f,
            newLaunch: false, 0f, 1f));
        if (fallen.CheckActState(Actor.ActState.Downed))
            fallen.ForceDeathTimer();
        __instance.fadeStarted = false;
        return false;
    }
}
