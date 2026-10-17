using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

using ProcessModeEnum = Godot.Node.ProcessModeEnum;

namespace Propane.Dev.NetScenarios;

internal sealed class MeleeScenario(INetScenarioHost host) : NetScenario(host)
{
    private float duelTime = -1;
    private ScriptedInput duelInput = new();
    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;
    private Camera3D? movementCamera;
    private GripProbe? remoteGrip;
    private MotionProbe? remoteMotion;

    protected override void Assembled(Match match)
    {
        duelTime = -1; duelInput = new ScriptedInput();
        meleeThrows = meleeGetups = 0;
        meleeFirstDown = meleeSecondDown = false;
        meleeCaptured.Clear();
        movementCamera = null; remoteGrip = null; remoteMotion = null;
    }

    protected override void Run(Match match, float dt) => Melee(match, dt);

    private int meleeThrows, meleeGetups;
    private bool meleeFirstDown, meleeSecondDown;
    private readonly HashSet<string> meleeCaptured = new();

    private void Melee(Match match, float dt)
    {
        if (!match.IsPlaying) return;
        var victim = role == "host";
        var player = match.Player;
        var other = match.RemoteBodies.FirstOrDefault();
        if (other == null) return;
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
            var captured = Epoch;
            void Thrown(Vector3 position, Vector3 direction, Vector3 impulse) { if (IsCurrent(captured)) meleeThrows++; }
            void GotUp(Vector3 position, float yaw) { if (IsCurrent(captured)) meleeGetups++; }
            player.Ragdolled += Thrown;
            player.GotUp += GotUp;
            DetachOnRetirement(() => player.Ragdolled -= Thrown);
            DetachOnRetirement(() => player.GotUp -= GotUp);
            if (!victim) { duelInput.Melee(2); duelInput.Melee(12); }
            if (victim && DevArgs.Get("view") == "side")
            {
                movementCamera = new Camera3D { Fov = 40, Current = true };
                match.AddChild(movementCamera);
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var attacker = victim ? other : player;
        var target = victim ? player : other;
        var origin = new Vector3(match.World.Tuning.MpMapSizeBase + match.World.Tuning.MpMapSizePerPlayer * match.RemoteBodies.Count() + 30, 0.05f, 0);
        if (Once(.25f, dt) || Once(10.5f, dt))
        {
            player.Teleport(origin + (victim ? Vector3.Forward * 1.8f : Vector3.Zero), victim ? Mathf.Pi : 0);
            player.Aim.SetOrientation(victim ? 180 : 0, -6);
            if (remoteMotion != null) remoteMotion.SkipNextPose = true;
        }
        if (duelTime > 1 && remoteGrip == null && DevArgs.Get("assert-grip") != null) remoteGrip = GripProbe.Attach(attacker);
        if (duelTime > 1 && remoteMotion == null && DevArgs.Get("assert-motion") != null) remoteMotion = MotionProbe.Attach(attacker);
        if (movementCamera != null)
        {
            var focus = attacker.GlobalPosition + Vector3.Up;
            movementCamera.LookAtFromPosition(focus + new Vector3(4, .6f, -1.2f), focus);
        }
        if (!victim && DevArgs.Get("view") == "player" && target.IsRagdolled)
        {
            AimAt(player, target.GlobalPosition + Vector3.Up);
            var label = target.ResolvedState.Lifecycle == PlayerLifecycle.GettingUp ? "remote_getup" : "remote_down";
            if (meleeCaptured.Add(label)) CaptureMelee(label);
            var targetRig = target.FindChildren("RifleRig", owned: false).OfType<RifleRig>().Single();
            if (targetRig.Weight > .5f && targetRig.Weight < .9f && meleeCaptured.Add("remote_getup_handoff"))
                CaptureMelee("remote_getup_handoff");
        }
        if (duelTime < 8) meleeFirstDown |= target.IsRagdolled;
        if (duelTime > 12) meleeSecondDown |= target.IsRagdolled;
        if (attacker.ResolvedState.Weapon == WeaponAction.Melee)
        {
            var swing = duelTime < 8 ? 1 : 2;
            var frame = (int)(attacker.MeleeProgress * 12);
            var label = $"melee_{role}_{swing}_{frame:00}";
            if (meleeCaptured.Add(label)) CaptureMelee(label);
        }
        if (Once(4, dt))
        {
            ScenarioAssert(meleeFirstDown && match.Scores[target.ActorId] == 0 && match.LiveTanks.Count(IsDrop) == 5,
                "melee: first knockdown must spill exactly five tanks from a five-tank bank");
            if (victim) ScenarioAssert(meleeThrows == 1, "melee: repeated active contacts threw victim twice");
        }
        if (Once(10, dt))
        {
            ScenarioAssert(!target.IsRagdolled && meleeFirstDown, "melee: first victim never got up");
            if (victim) ScenarioAssert(meleeGetups == 1, "melee: missing first get-up");
        }
        if (Once(14, dt))
        {
            ScenarioAssert(meleeSecondDown && match.Scores[target.ActorId] == 0 && match.LiveTanks.Count(IsDrop) == 5,
                "melee: zero-bank victim must ragdoll without another drop");
            if (victim) ScenarioAssert(meleeThrows == 2, "melee: missing zero-bank throw");
        }
        if (Once(22, dt))
        {
            ScenarioAssert(!target.IsRagdolled && meleeSecondDown, "melee: zero-bank victim never got up");
            if (victim) ScenarioAssert(meleeGetups == 2, "melee: missing zero-bank get-up");
            remoteGrip?.Finish(); remoteMotion?.Finish();
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) Log("melee: assertions passed; five tanks spilled, zero-bank stun, both get-ups");
        }
    }

    private async void CaptureMelee(string label)
    {
        if (DisplayServer.GetName() == "headless" || DevArgs.Get("capture-dir") == null) return;
        var captured = Epoch;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsCurrent(captured) && IsInsideTree()) director.ScreenshotAsync(label);
    }

}
