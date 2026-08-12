using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DungeonLurkerCoop.Patches;

internal static class SharedRewards
{
    /// True while LevelManager.SetupPlayer runs. Setup applies run state to
    /// EACH player individually, so the boon/trait mirrors must stay quiet.
    internal static bool InPlayerSetup;

    /// Number of mods on the scene-baked P1's ModController before SetupPlayer
    /// added run state — i.e. the prefab baseline the P2 clone should reset to.
    internal static int BaselineModCount = -1;

    public static void SyncSpellToOtherPlayers(Spell spell, int slot)
    {
        var singleton = Player.GetInstance();
        foreach (var player in CoopPlayers.All)
        {
            if (player == null || player == singleton) continue;
            if (spell != null)
                player.AddSpell(spell.GetAttack(), slot);
            else
                player.RemoveSpell(slot);
        }
    }

    public static void BlockAllPlayerInputs(float duration)
    {
        foreach (var player in CoopPlayers.All)
        {
            var input = player != null ? player.GetPlayerInput() : null;
            if (input != null) input.SetBlockTimer(duration);
        }
    }
}

/// Two jobs around per-player setup:
/// 1. Record P1's pre-setup mod count (prefab baseline).
/// 2. The P2 clone copies P1's serialized mods list (baseline + run boons) and
///    SetupPlayer would then add run state AGAIN — trim the clone back to the
///    baseline first so it ends up with exactly one copy of everything.
[HarmonyPatch(typeof(LevelManager), "SetupPlayer")]
internal static class SetupPlayerPatch
{
    private static void Prefix(Player newActor)
    {
        SharedRewards.InPlayerSetup = true;

        var modCon = newActor != null ? newActor.GetModCon() : null;
        if (modCon == null || modCon.mods == null) return;

        if (CoopManager.IsCloning)
        {
            int baseline = SharedRewards.BaselineModCount;
            if (baseline >= 0 && modCon.mods.Count > baseline)
            {
                Plugin.Log.LogInfo($"P2 clone: trimming copied mods {modCon.mods.Count} -> baseline {baseline} before setup.");
                modCon.mods.RemoveRange(baseline, modCon.mods.Count - baseline);
            }
        }
        else
        {
            SharedRewards.BaselineModCount = modCon.mods.Count;
        }
    }

    private static void Finalizer()
    {
        SharedRewards.InPlayerSetup = false;
    }
}

/// Whenever the game hands P1 a boon outside per-player setup (boon goddess
/// choice, loot chests, debug tools, future content), hand it to P2 as well.
[HarmonyPatch(typeof(ModController), nameof(ModController.AddBoon))]
internal static class BoonMirrorPatch
{
    private static bool mirroring;

    private static void Postfix(ModController __instance, Boon newBoon)
    {
        if (mirroring || SharedRewards.InPlayerSetup || !CoopPlayers.CoopActive || newBoon == null)
            return;
        if (__instance.actor is not Player p || p != CoopPlayers.P1)
            return;
        var p2Con = CoopPlayers.P2 != null ? CoopPlayers.P2.GetModCon() : null;
        if (p2Con == null) return;

        mirroring = true;
        try { p2Con.AddBoon(newBoon); }
        finally { mirroring = false; }
    }
}

/// Old Knight trait reset: vanilla clears and reapplies only P1.
[HarmonyPatch(typeof(DungeonMaster), nameof(DungeonMaster.ClearPlayerMods))]
internal static class ClearPlayerModsPatch
{
    private static void Postfix()
    {
        if (!CoopPlayers.CoopActive || CoopPlayers.P2 == null) return;
        var modCon = CoopPlayers.P2.GetModCon();
        if (modCon == null) return;
        new List<Modification>(modCon.mods).ForEach(m => modCon.RemoveMod(m));
    }
}

[HarmonyPatch(typeof(DungeonMaster), nameof(DungeonMaster.ApplyTraits))]
internal static class ApplyTraitsMirrorPatch
{
    private static bool mirroring;

    private static void Postfix(DungeonMaster __instance, Actor actor)
    {
        if (mirroring || SharedRewards.InPlayerSetup || !CoopPlayers.CoopActive)
            return;
        if (actor is not Player p || p != CoopPlayers.P1 || CoopPlayers.P2 == null)
            return;

        mirroring = true;
        try { __instance.ApplyTraits(CoopPlayers.P2); }
        finally { mirroring = false; }
    }
}

/// Wizard spell equip: vanilla copies chosen spells onto P1 only.
[HarmonyPatch(typeof(MenuWizardEquipWindow), nameof(MenuWizardEquipWindow.CloseEquipWindow))]
internal static class MenuWizardEquipWindowClosePatch
{
    private static void Postfix(MenuWizardEquipWindow __instance, bool updateSpells)
    {
        if (!CoopPlayers.CoopActive || !updateSpells || !__instance.lootPicker)
            return;

        for (var i = 0; i < __instance.tempSpellSlots.Length; i++)
            SharedRewards.SyncSpellToOtherPlayers(__instance.tempSpellSlots[i], i + 1);
    }
}

/// Cinematics: the pauseInput fallback blocks only P1's input; mirror to all.
[HarmonyPatch(typeof(Cinematic), "PauseActors")]
internal static class CinematicPauseActorsPatch
{
    private static void Postfix(Cinematic __instance)
    {
        if (!CoopPlayers.CoopActive || __instance.pauseTime || __instance.pauseGameplay || !__instance.pauseInput || Player.GetInstance() == null)
            return;

        var input = Player.GetInstance().GetPlayerInput();
        if (input != null)
            SharedRewards.BlockAllPlayerInputs(input.blockTimer);
    }
}

/// Feign-death enemies: wake on the nearest player, not only P1.
[HarmonyPatch(typeof(PlayDead), "Update")]
internal static class PlayDeadUpdatePatch
{
    private static bool Prefix(PlayDead __instance)
    {
        if (!CoopPlayers.CoopActive)
            return true;

        if (!__instance.active || !__instance.holdingAnim)
            return false;

        __instance.actor.PauseAnim(pause: true);
        var player = CoopPlayers.Nearest(__instance.transform.position);
        if (!(__instance.wakeOnPlayerProximity < 0f) && player != null)
        {
            Tools.DrawCircle(__instance.transform.position, __instance.wakeOnPlayerProximity, 1f, Vector3.up, Vector3.forward, Color.red);
            if (Mathf.Abs(player.transform.position.y - __instance.transform.position.y) < 3f &&
                Tools.QuickFlatDistSqr(player.transform.position, __instance.transform.position) < __instance.wakeOnPlayerProximity * __instance.wakeOnPlayerProximity)
            {
                __instance.UniqueTrigger();
            }
        }

        return false;
    }
}

/// Traps/turrets: aim at the nearest player (also fixes a vanilla NRE when
/// firing with no player present).
[HarmonyPatch(typeof(ProjectileSpawner), "FireProjectile")]
internal static class ProjectileSpawnerFireProjectilePatch
{
    private static bool Prefix(ProjectileSpawner __instance)
    {
        if (!CoopPlayers.CoopActive)
            return true;

        if (__instance.attackData == null || __instance.projectile == null)
            return false;

        var position = __instance.transform.position;
        position += __instance.transform.InverseTransformDirection(__instance.fireOffset);
        var target = __instance.targetPlayer ? CoopPlayers.Nearest(__instance.transform.position) : null;
        Object.Instantiate(__instance.projectile, position, Quaternion.identity)
            .FireProjectile(__instance.transform.forward, target != null ? target.transform : null, null, __instance.attackData);

        if (__instance.repeatFireRate > 0f)
            __instance.repeatTimer = __instance.repeatFireRate;

        return false;
    }
}
