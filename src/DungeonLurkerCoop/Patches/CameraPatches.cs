using HarmonyLib;
using Unity.Cinemachine;
using UnityEngine;

namespace DungeonLurkerCoop.Patches;

/// Camera lookahead should react to the players' aggregate motion, and the
/// frustum ground plane should sit at the players' midpoint height.
[HarmonyPatch(typeof(CameraManager), "Update")]
internal static class CameraManagerUpdatePatch
{
    private static bool Prefix(CameraManager __instance)
    {
        if (!CoopPlayers.CoopActive) return true;

        float num = 0f;
        num = Tools.Sign(CameraManager.main.transform.position.x - __instance.lastCamX);
        __instance.lastCamX = CameraManager.main.transform.position.x;

        Vector3 vel = CoopPlayers.AggregateVelocity();
        if (CoopPlayers.AliveCount() == 0 || num != Tools.Sign(vel.x))
            num = 0f;

        float target = 0f - num;
        __instance.currLookahead = Mathf.MoveTowards(
            __instance.currLookahead, target,
            ((num == 0f) ? __instance.lookaheadReturnSpeed : __instance.lookaheadSpeed) * Time.deltaTime);
        __instance.composer.Composition.ScreenPosition.x =
            __instance.lookaheadCurve.Evaluate(Mathf.Abs(__instance.currLookahead))
            * Mathf.Sign(__instance.currLookahead) * __instance.lookaheadDistance;
        return false;
    }
}

[HarmonyPatch(typeof(CameraManager), nameof(CameraManager.CalculateFrustrumExtents))]
internal static class FrustrumExtentsPatch
{
    private static bool Prefix()
    {
        if (!CoopPlayers.CoopActive) return true;
        if (CameraManager.main == null) return false;

        CameraManager.UpdateCameraPlanes();
        Vector3 mid = CoopPlayers.Midpoint();
        Plane p = new(Vector3.up, (mid.y - 1f) * Vector3.up);
        CameraManager.frustrumBoundOrigin = Vector3.Project(CameraManager.main.transform.position, Vector3.right);
        Tools.PlaneIntersect(CameraManager.GetCameraPlane(0), p, out var lDir, out var lPoint);
        Tools.PlaneIntersect(CameraManager.GetCameraPlane(3), p, out _, out var lPoint2);
        Vector3 vector = lPoint2;
        vector.x = lPoint.x + lDir.x * ((lPoint2.z - lPoint.z) / lDir.z);
        Tools.PlaneIntersect(CameraManager.GetCameraPlane(2), p, out lDir, out lPoint);
        CameraManager.frustrumBoundExtents.x = Mathf.Abs(CameraManager.frustrumBoundOrigin.x - vector.x);
        CameraManager.frustrumBoundOrigin.y = vector.y;
        CameraManager.frustrumBoundExtents.z = Mathf.Abs(lPoint.z - lPoint2.z) * 0.5f;
        CameraManager.frustrumBoundOrigin.z = lPoint.z + CameraManager.frustrumBoundExtents.z;
        CameraManager.frustrumBoundOrigin -= CameraManager.main.transform.position;
        return false;
    }
}

/// Cinemachine LookAhead extension: use aggregate velocity instead of P1's.
[HarmonyPatch(typeof(LookAhead), "PostPipelineStageCallback")]
internal static class LookAheadPatch
{
    private static bool Prefix(LookAhead __instance, CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
    {
        if (!CoopPlayers.CoopActive) return true;
        if (CoopPlayers.P1 == null || stage != CinemachineCore.Stage.Body) return false;

        Vector2 target = (Vector2)(Vector3)CoopPlayers.AggregateVelocity().normalized * __instance.maxLookAhead;
        __instance.currOffset = Vector2.MoveTowards(__instance.currOffset, target, __instance.lookAheadSpeed * Time.deltaTime);
        state.PositionCorrection += Tools.Vec2ToXZ(__instance.currOffset);
        return false;
    }
}
