using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Player;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Remote presentation sanity: shot echoes, displayed reloads, strikes and the get-up handoff arrive in any
/// order with snapshots. Both wrists stay on their grips (within 2 cm) in the actual final bones, and nothing
/// goes non-finite. Shots never depend on this pose, so the barrel is not fitted to each echo.</summary>
public partial class RifleReachTests : Node3D
{
    private WorldContext world = null!;
    private PlayerCharacter remote = null!;
    private Skeleton3D skeleton = null!;
    private RifleRig rig = null!;
    private string stage = "warmup";
    private Exception? failure;
    private int echoes;
    private int samples;
    private float maxResidual;

    public override void _ExitTree() => world.Dispose();

    public override async void _Ready()
    {
        DevArgs.MarkReady();
        DevArgs.Setup();
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        world.SetContentRoot(this);
        remote = new PlayerCharacter { World = world, IsRemote = true };
        AddChild(remote);
        remote.SetPhysicsProcess(false); // Explicitly order snapshot application and reliable echoes below.
        skeleton = remote.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("hand_l") >= 0);
        rig = skeleton.GetNode<RifleRig>("RifleRig");
        skeleton.SkeletonUpdated += Sample;
        try
        {
            Apply(Vector3.Zero, 0);
            await Frames(30).ConfigureAwait(true);
            for (var turn = 0; turn < 8; turn++)
            {
                stage = $"turn {turn}: echo then snapshot relocation";
                var origin = new Vector3(turn * 2, 0, 0);
                Apply(origin, turn * Mathf.Pi / 4);
                await Frames(10).ConfigureAwait(true);
                // A catch-up frame can deliver more than one reliable echo before a single render.
                Echo(origin + new Vector3(-4, 1, -20));
                Echo(origin + new Vector3(-3, 1, -20));
                Apply(origin + new Vector3(1, 0.2f, 0), (turn + 1) * Mathf.Pi / 4);
                await Frames(2).ConfigureAwait(true);

                stage = $"turn {turn}: echo during displayed reload";
                Apply(origin, turn * Mathf.Pi / 4, .65f);
                await Frames(20).ConfigureAwait(true);
                Echo(origin + new Vector3(3, 1, -20));
                await Frames(2).ConfigureAwait(true);

                stage = $"turn {turn}: echo during displayed melee after late reload";
                Apply(origin, turn * Mathf.Pi / 4, .65f);
                await Frames(60).ConfigureAwait(true);
                Apply(origin, turn * Mathf.Pi / 4, melee: .4f);
                Echo(origin + new Vector3(3, 1, -20));
                await Frames(4).ConfigureAwait(true);
                Assert(rig.Melee, "an echo suppressed the displayed melee");

                stage = $"turn {turn}: completed reload before presentation";
                Apply(origin, turn * Mathf.Pi / 4);
                Echo(origin + new Vector3(0, 2, -20));
                await Frames(2).ConfigureAwait(true);
            }
            Assert(samples >= 100 && echoes == 40, $"missing final-pose coverage: {samples} samples, {echoes}/40 echoes");
            for (var aimed = 0; aimed < 2; aimed++)
            {
                for (var yaw = 0; yaw < 8; yaw++)
                {
                    // Match-sized coordinates also exercise float precision in the short muzzle ray.
                    var origin = new Vector3((yaw - 4) * 40, 0, (aimed - .5f) * 150);
                    Apply(origin, yaw * Mathf.Pi / 4, aim: aimed);
                    await Frames(60).ConfigureAwait(true);
                    foreach (var offset in new[] { new Vector3(.8f, 0, 0), new Vector3(0, 0, -.8f),
                        new Vector3(.04f, .25f, -.88f), new Vector3(0, 0, 1), new Vector3(0, 0, -2) })
                    {
                        stage = $"near echo aim={aimed} yaw={yaw} offset={offset}";
                        Echo(origin + Vector3.Up * 1.4f + offset);
                        await Frames(2).ConfigureAwait(true);
                    }
                }
            }
            stage = "delayed echoes during get-up handoff";
            var floor = new StaticBody3D { CollisionLayer = Layers.Floor, Position = new Vector3(0, -.5f, 0) };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(100, 1, 100) } });
            AddChild(floor);
            remote.RemoteRagdoll(remote.GlobalPosition, Vector3.Up, Vector3.Zero);
            remote.SetRemoteState(new PlayerNetState { State = PlayerNetState.Mode.GettingUp });
            await Frames(20).ConfigureAwait(true);
            remote.RemoteGetUp(Vector3.Zero, 0);
            var handoffShots = 0;
            for (var frame = 0; frame < 180; frame++)
            {
                remote._PhysicsProcess(1.0 / 60);
                if (rig.Weight > .001f && rig.Weight < .999f && frame % 3 == 0)
                {
                    handoffShots++;
                    Echo(new Vector3(2, 1, -20));
                }
                await Frames(1).ConfigureAwait(true);
            }
            Assert(handoffShots >= 3, $"missing get-up echoes: {handoffShots}");
            GD.Print($"[rifle reach] PASS samples={samples} echoes={echoes} wrist={maxResidual:F6}m");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[rifle reach] FAIL {stage}: {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void Apply(Vector3 position, float yaw, float reload = 0, float aim = 0, float melee = 0)
    {
        remote.SetRemoteState(new PlayerNetState
        {
            Position = position,
            Yaw = yaw,
            AimTarget = position + new Vector3(0, 1.4f, -20),
            Reload = reload,
            Ready = 1,
            Aim = aim,
            Melee = melee,
            Grounded = true,
            Support = PlayerSupport.Grounded,
            Weapon = melee > 0 ? WeaponAction.Melee : reload > 0 ? WeaponAction.Reload : aim > 0 ? WeaponAction.Aim : WeaponAction.Ready,
        });
        remote._PhysicsProcess(1.0 / Engine.PhysicsTicksPerSecond);
    }

    private void Echo(Vector3 target)
    {
        echoes++;
        remote.RemoteShot(target);
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (failure != null) throw failure;
    }

    private void Sample()
    {
        try
        {
            if (samples++ < 2) return;
            if (rig.Weight <= .001f) return;
            foreach (var side in new[] { "r", "l" })
            {
                var wrist = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone($"hand_{side}")).Origin;
                var target = side == "r" ? rig.RightTarget.Origin : rig.LeftTarget.Origin;
                var residual = wrist.DistanceTo(target);
                maxResidual = Mathf.Max(maxResidual, residual);
                Assert(float.IsFinite(residual) && residual <= .02f, $"{stage}: {side} wrist residual {residual:F6}m");
            }
            var rifle = rig.Rifle!;
            Assert(float.IsFinite(rifle.MuzzlePosition.LengthSquared()), $"{stage}: non-finite rifle");
        }
        catch (Exception error) { failure ??= error; }
    }
}
