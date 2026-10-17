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

internal sealed class MovementScenario(INetScenarioHost host) : NetScenario(host)
{
    private readonly HashSet<string> meleeCaptured = new();
    private float duelTime = -1;
    private ScriptedInput duelInput = new();

    protected override void Assembled(Match match)
    {
        duelTime = -1; duelInput = new ScriptedInput();
        movementCamera = null;
        remoteFeet = null; remoteGrip = null; remoteMotion = null;
        sawRemoteReload = false;
        sawRemoteCreep = sawRemoteSprint = sawRemoteAir = sawRemoteRestoredAim = movementProved = false;
        capturedCreep = capturedSprint = capturedLanding = capturedRelease = false;
        remoteGait = PlayerGait.Default; remoteGaitTime = 0;
        meleeCaptured.Clear();
    }

    protected override void Run(Match match, float dt) => Movement(match, dt);

    private Camera3D? movementCamera;
    private GripProbe? remoteGrip;
    private LocomotionProbe? remoteFeet;
    private MotionProbe? remoteMotion;
    private bool sawRemoteReload;
    private bool sawRemoteCreep;
    private bool sawRemoteSprint;
    private bool sawRemoteAir;
    private bool sawRemoteRestoredAim;
    private bool movementProved;
    private PlayerGait remoteGait;
    private float remoteGaitTime;
    private bool capturedCreep;
    private bool capturedSprint;
    private bool capturedLanding;
    private bool capturedRelease;

    /// <summary>Two-player gait/stance proof on the unobstructed void floor, seen through real snapshots.</summary>
    private void Movement(Match match, float dt)
    {
        if (!match.IsPlaying) return;
        if (duelTime < 0)
        {
            duelTime = 0;
            match.Player.InputOverride = duelInput;
            match.Player.Teleport(new Vector3(role == "host" ? 20 : 0, 0.05f, -200), 0);
            if (role != "host")
            {
                duelInput.Hold(0.5f, 8.5f, new PlayerIntent { Move = new Vector2(0, 1) });
                duelInput.Hold(1.5f, 8.5f, new PlayerIntent { Sprint = true });
                duelInput.Hold(1.5f, 3.0f, new PlayerIntent { Aim = true });
                duelInput.Hold(4.5f, 7.5f, new PlayerIntent { Aim = true });
                duelInput.Jump(5.0f);
                duelInput.Hold(8.6f, 9.3f, new PlayerIntent { FireHeld = true });
                duelInput.Hold(9.5f, 11.2f, new PlayerIntent { Aim = true });
                duelInput.Hold(10, 10.7f, new PlayerIntent { FireHeld = true });
                duelInput.Hold(11.4f, 13.8f, new PlayerIntent { Move = Vector2.Down * 0.3f });
                duelInput.Reload(11.6f);
            }
            else
            {
                if (DevArgs.Get("view") != "player")
                {
                    movementCamera = new Camera3D { Fov = 40, Current = true };
                    match.AddChild(movementCamera);
                }
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        if (role != "host") return;
        var other = match.RemoteBodies.FirstOrDefault();
        if (other == null) return;
        if (remoteGrip == null && (DevArgs.Get("grip-metrics") != null || DevArgs.Get("assert-grip") != null))
        {
            remoteGrip = GripProbe.Attach(other);
        }
        // The spawn-to-void relocation also replaces the aim target and body yaw. Measure the movement
        // sequence after that controlled staging has settled; the matrix measures ordinary corrections.
        if (remoteFeet == null && duelTime >= 1 && (DevArgs.Get("feet-metrics") != null || DevArgs.Get("assert-feet") != null))
            remoteFeet = LocomotionProbe.Attach(other);
        if (remoteMotion == null && duelTime >= 1 && DevArgs.Get("assert-motion") != null)
            remoteMotion = MotionProbe.Attach(other);
        sawRemoteReload |= other.ResolvedState.Weapon == WeaponAction.Reload;
        if (remoteFeet != null) remoteFeet.Label = other.ResolvedState.Gait.ToString();
        var focus = other.GlobalPosition + Vector3.Up;
        var offset = new Vector3(4, 0.5f, -1.5f);
        var view = DevArgs.Get("view");
        if (view is "rifleleft" or "rifleright")
        {
            var rifle = other.GetNode<Rifle>("Rifle");
            focus = rifle.GlobalTransform * new Vector3(0, 0, -0.12f);
            offset = new Basis(Vector3.Up, other.FacingYaw) * new Vector3(view == "rifleleft" ? -0.8f : 0.8f, 0.2f, -0.45f);
        }
        if (DevArgs.Get("view") == "player")
        {
            // Review through the observer's actual local view, close enough to judge both wrists.
            match.Player.Teleport(other.GlobalPosition + new Vector3(2, .05f, -3), 0);
            AimAt(match.Player, focus);
        }
        else
        {
            movementCamera!.LookAtFromPosition(focus + offset, focus);
        }

        var remoteRig = other.FindChildren("RifleRig", owned: false).OfType<RifleRig>().Single();
        if (remoteRig.Reload > .4f && remoteRig.Reload < .7f && meleeCaptured.Add("remote_10_reload"))
            CaptureRemoteFrame("remote_10_reload");
        var state = other.ResolvedState;
        var speed = new Vector2(other.Velocity.X, other.Velocity.Z).Length();
        remoteGaitTime = remoteGait == state.Gait ? remoteGaitTime + dt : 0;
        remoteGait = state.Gait;
        if (!sawRemoteCreep && state.Gait == PlayerGait.Creep && state.EffectiveAim && Math.Abs(speed - match.World.Tuning.CreepSpeed) < 0.15f)
        {
            sawRemoteCreep = true;
            CaptureRemoteFrame("remote_00_creep");
            Log($"remote creep: {state.Support}/{state.Weapon} speed={speed:0.00}");
        }
        if (!sawRemoteSprint && state.Gait == PlayerGait.Sprint && speed > match.World.Tuning.SprintSpeed - 0.15f)
        {
            sawRemoteSprint = true;
            CaptureRemoteFrame("remote_01_sprint");
            Log($"remote sprint: {state.Support}/{state.Weapon} speed={speed:0.00}");
        }
        if (!sawRemoteAir && sawRemoteCreep && sawRemoteSprint &&
            state.Support == PlayerSupport.Airborne && !state.EffectiveAim && other.AimAmount == 0)
        {
            sawRemoteAir = true;
            CaptureRemoteFrame("remote_02_air");
            Log($"remote air: {state.Gait}/{state.Weapon}");
        }
        if (sawRemoteAir && state.Grounded && state.EffectiveAim && state.Gait == PlayerGait.Creep)
        {
            sawRemoteRestoredAim = true;
        }
        // Each client can start its script on a different frame. Label screenshots from observed remote states,
        // after the gait/rig blends settle, rather than the observer's script clock.
        if (remoteGaitTime > 0.6f && state.Gait == PlayerGait.Creep && other.AimAmount > 0.99f)
        {
            if (!capturedCreep)
            {
                capturedCreep = true;
                CaptureRemoteFrame("remote_03_creep_settled");
            }
            if (sawRemoteAir && !capturedLanding)
            {
                capturedLanding = true;
                CaptureRemoteFrame("remote_06_landed_creep");
            }
        }
        if (remoteGaitTime > 0.6f && state.Gait == PlayerGait.Sprint && state.Grounded)
        {
            if (!capturedSprint)
            {
                capturedSprint = true;
                CaptureRemoteFrame("remote_04_sprint_settled");
            }
            if (capturedLanding && !capturedRelease)
            {
                capturedRelease = true;
                CaptureRemoteFrame("remote_07_release_sprint");
            }
        }
        if (!movementProved && duelTime >= 14.5f)
        {
            movementProved = true;
            remoteGrip?.Finish();
            remoteFeet?.Finish();
            remoteMotion?.Finish();
            if (DevArgs.Get("assert-motion") != null && (!sawRemoteReload || remoteMotion?.RecoilFrames < 10))
            {
                GD.PushError("[net movement] FAIL missing remote reload or recoil");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            if (!sawRemoteCreep || !sawRemoteSprint || !sawRemoteAir || !sawRemoteRestoredAim ||
                !capturedCreep || !capturedSprint || !capturedLanding || !capturedRelease)
            {
                GD.PushError("[net movement] FAIL missing remote gait/support");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            Log("[net movement] PASS remote creep, sprint, air aim suppression, restored landing aim, bursts and walking reload");
        }
    }

    private async void CaptureRemoteFrame(string label)
    {
        if (DisplayServer.GetName() == "headless") return;
        // Physics runs before animation/render. Read the frame after drawing the state that named it.
        var captured = Epoch;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsCurrent(captured) && IsInsideTree()) director.Screenshot(label);
    }

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

    public override bool CheckCompletion(NetScenarioCompletion requirement)
    {
        if (requirement == NetScenarioCompletion.Movement && role == "host" && !movementProved)
        {
            Fail("[net movement] FAIL match ended before movement proof completed");
            return false;
        }
        return true;
    }
}
