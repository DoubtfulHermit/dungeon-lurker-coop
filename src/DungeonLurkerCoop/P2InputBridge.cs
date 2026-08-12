using UnityEngine;
using UnityEngine.InputSystem;

namespace DungeonLurkerCoop;

/// Feeds a second player's custom PlayerInput component from a cloned copy of
/// the game's own InputActionAsset, restricted to one gamepad. Mirrors the
/// forwarding logic of InputRelay's SendMessage callbacks, but by reference to
/// the P2 component instead of the PlayerInput.instance singleton.
public class P2InputBridge : MonoBehaviour
{
    private PlayerInput target;
    private InputActionAsset asset;
    private InputActionMap playerMap;

    public void Bind(PlayerInput p2Input, Gamepad pad)
    {
        target = p2Input;

        var source = InputRelay.activeInput.actions;
        asset = Instantiate(source);
        asset.name = source.name + "_P2";
        asset.devices = pad != null ? new InputDevice[] { pad } : new InputDevice[0];

        playerMap = asset.FindActionMap("Player");
        if (playerMap == null)
        {
            Plugin.Log.LogError("P2InputBridge: no 'Player' action map found in cloned asset!");
            return;
        }

        Hook("Move", OnMove, alsoCanceled: true);
        Hook("Sprint", OnSprint);
        Hook("SprintPress", OnSprintPress);
        Hook("Jump", OnJump);
        Hook("LightAtk", OnLightAtk);
        Hook("HeavyAtk", OnHeavyAtk, alsoCanceled: true);
        Hook("Interact", OnInteract);
        Hook("Guard", OnGuard, alsoCanceled: true);
        Hook("Dodge", OnDodge, alsoCanceled: true);

        playerMap.Enable();
        Plugin.Log.LogInfo($"P2InputBridge bound to '{(pad != null ? pad.displayName : "no device")}' with actions: {ActionNames()}");
    }

    private string ActionNames()
    {
        if (playerMap == null) return "-";
        var names = new System.Text.StringBuilder();
        foreach (var a in playerMap.actions) names.Append(a.name).Append(' ');
        return names.ToString();
    }

    private void Hook(string actionName, System.Action<InputAction.CallbackContext> cb, bool alsoCanceled = false)
    {
        var action = playerMap.FindAction(actionName);
        if (action == null)
        {
            Plugin.Log.LogWarning($"P2InputBridge: action '{actionName}' not found in Player map.");
            return;
        }
        action.performed += cb;
        if (alsoCanceled) action.canceled += cb;
    }

    private void OnDestroy()
    {
        if (asset != null)
        {
            asset.Disable();
            Destroy(asset);
        }
    }

    // --- forwarding (mirrors InputRelay semantics) ---

    private void OnMove(InputAction.CallbackContext ctx) =>
        target.OnMove(ctx.canceled ? Vector2.zero : ctx.ReadValue<Vector2>());

    private void OnSprint(InputAction.CallbackContext ctx)
    {
        float v = ctx.ReadValue<float>();
        if (v != 0f) target.OnSprint(Tools.Sign(v));
    }

    private void OnSprintPress(InputAction.CallbackContext ctx)
    {
        float v = ctx.ReadValue<float>();
        if (v != 0f) target.OnSprint(Tools.Sign(v), buttonPress: true);
    }

    private void OnJump(InputAction.CallbackContext ctx)
    {
        if (ctx.ReadValue<float>() != 0f) target.OnJump();
    }

    private void OnLightAtk(InputAction.CallbackContext ctx)
    {
        if (ctx.ReadValue<float>() != 0f) target.OnLightAtk();
    }

    private void OnHeavyAtk(InputAction.CallbackContext ctx) =>
        target.OnHeavyAtk(!ctx.canceled && ctx.ReadValue<float>() == 1f);

    private void OnInteract(InputAction.CallbackContext ctx)
    {
        if (ctx.ReadValue<float>() != 0f) target.OnInteract();
    }

    private void OnGuard(InputAction.CallbackContext ctx) =>
        target.OnGuard(!ctx.canceled && ctx.ReadValue<float>() == 1f);

    private void OnDodge(InputAction.CallbackContext ctx)
    {
        if (!ctx.canceled && ctx.ReadValue<float>() != 0f) target.OnDodge();
    }
}
