using System.Collections.Generic;
using UnityEngine;

namespace DungeonLurkerCoop;

/// Registry of active co-op players. P1 is the scene-baked player (kept as
/// Player.instance); P2 is the mod-spawned clone. Scene loads wipe both.
public static class CoopPlayers
{
    public static Player P1;
    public static Player P2;

    public static readonly List<Player> All = new();

    public static bool CoopActive => P1 != null && P2 != null;

    public static void RegisterP1(Player p)
    {
        P1 = p;
        if (!All.Contains(p)) All.Add(p);
    }

    public static void RegisterP2(Player p)
    {
        P2 = p;
        if (!All.Contains(p)) All.Add(p);
    }

    public static void Clear()
    {
        P1 = null;
        P2 = null;
        All.Clear();
    }

    public static bool IsP2(Player p) => p != null && p == P2;

    public static bool IsCoopPlayer(Player p) => p != null && (p == P1 || p == P2);

    /// Players that exist and are not in the Downed act state.
    public static IEnumerable<Player> Alive()
    {
        foreach (var p in All)
            if (p != null && !p.CheckActState(Actor.ActState.Downed))
                yield return p;
    }

    public static int AliveCount()
    {
        int n = 0;
        foreach (var _ in Alive()) n++;
        return n;
    }

    /// Nearest living player to a position; falls back to any player, then null.
    public static Player Nearest(Vector3 pos)
    {
        Player best = null;
        float bestSqr = float.PositiveInfinity;
        foreach (var p in Alive())
        {
            float d = (p.transform.position - pos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = p; }
        }
        if (best != null) return best;
        foreach (var p in All)
            if (p != null) return p;
        return null;
    }

    /// Midpoint of living players (falls back to all registered, then P1).
    public static Vector3 Midpoint()
    {
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var p in Alive()) { sum += p.transform.position; n++; }
        if (n == 0)
            foreach (var p in All)
                if (p != null) { sum += p.transform.position; n++; }
        return n > 0 ? sum / n : Vector3.zero;
    }

    /// Average velocity of living players.
    public static Vector3 AggregateVelocity()
    {
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var p in Alive()) { sum += p.GetVel(); n++; }
        return n > 0 ? sum / n : Vector3.zero;
    }
}
