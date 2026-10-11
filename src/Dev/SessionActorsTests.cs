using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>Two assigned views share physics; a third actor navigates, fights and recovers without any camera.</summary>
public partial class SessionActorsTests : Node
{
    private SessionContext session = null!;
    private readonly PlayerCharacter[] actors = new PlayerCharacter[3];
    private readonly LocalView[] views = new LocalView[2];
    private readonly DeviceInput[] devices = new DeviceInput[2];
    private readonly RigidBody3D[] props = new RigidBody3D[3];
    private readonly int[] shots = new int[3];
    private readonly int[] contacts = new int[3];
    private readonly Vector3[] directions = new Vector3[2];
    private int falls, recoveries;
    private PropaneTank tank = null!;
    private Brain brain = null!;

    private sealed class Source(SessionActorsTests test) : ISessionWorldSource
    {
        public void Build(SessionContext session)
        {
            session.World.Root.AddChild(new VoidEnvironment());
            session.World.Random.Seed = 23;
            session.World.SetContentRoot(session.World.Root);
            var origins = new[] { new Vector3(-6, .5f, -1.2f), new Vector3(4.8f, .5f, 0), new Vector3(20, .5f, 3.8f) };
            for (var i = 0; i < 3; i++)
            {
                var prop = test.props[i] = new RigidBody3D { Position = origins[i] + Vector3.Up * 100, CollisionLayer = Layers.Props, CollisionMask = Layers.World | Layers.Floor, Mass = 1 };
                prop.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * .5f } });
                prop.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * .5f } });
                session.World.Root.AddChild(prop);
            }
            test.tank = PropaneTank.Create(session.World);
            session.World.Root.AddChild(test.tank);
            test.tank.Position = new Vector3(20, 0, -5);
        }
    }
    private sealed class Mode(SessionActorsTests test) : ISessionMode
    {
        public void CreateActors(SessionContext session)
        {
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                var actor = test.actors[i] = new PlayerCharacter { World = session.World, Name = $"Actor{i}", Ammo = AmmoState.Starting(session.World.Tuning) };
                session.AddActor(actor);
                actor.Teleport(new Vector3(i == 0 ? -6 : i == 1 ? 6 : 20, .05f, i == 2 ? 5 : 0), i == 1 ? Mathf.Pi / 2 : 0);
                actor.Fired += report => { test.shots[index]++; if (index < 2) test.directions[index] = (report.End - report.Muzzle).Normalized(); };
                actor.MeleeHit += hit => { if (hit.Collider == test.props[index]) test.contacts[index]++; };
            }
            test.brain = new Brain(test.actors[2], test.tank);
            test.actors[2].InputOverride = test.brain;
            test.actors[2].Ragdolled += (_, _, _) => test.falls++;
            test.actors[2].GotUp += (_, _) => test.recoveries++;
        }
        public void CreateViews(SessionContext session)
        {
            var row = new HBoxContainer();
            session.AddView(test, row);
            for (var i = 0; i < 2; i++)
            {
                var panel = new SubViewportContainer { CustomMinimumSize = new Vector2(480, 270) };
                row.AddChild(panel);
                var viewport = new SubViewport { World3D = session.World.Physics, Size = new Vector2I(480, 270) };
                panel.AddChild(viewport);
                test.devices[i] = new DeviceInput(session.World.Tuning, () => true,
                    i == 0 ? InputDeviceAssignment.KeyboardAndMouse : InputDeviceAssignment.Gamepad(7));
                test.views[i] = session.AddLocalView(test.actors[i].Runtime, viewport, test.devices[i]);
                test.views[i].Hud.ShowTankCount(false);
                test.views[i].Hud.SetCrosshair(4, true, 0);
            }
            // The viewports themselves are presentation resources, retired after their local views.
            session.OnStopping(() => row.Visible = false);
        }
    }
    private sealed class Brain(PlayerCharacter actor, PropaneTank target) : IPlayerInputSource
    {
        public bool Enabled;
        private bool melee;
        private float wait;
        public PlayerIntent Read()
        {
            if (!Enabled || actor.IsRagdolled) return default;
            if (!melee) { melee = true; wait = 1.2f; return new PlayerIntent { MeleePressed = true }; }
            wait -= 1f / Engine.PhysicsTicksPerSecond;
            if (wait > 0 || !GodotObject.IsInstanceValid(target) || target.State == PropaneTank.TankState.Exploded) return default;
            var point = target.GlobalTransform * PropaneTank.LocalCenter;
            var to = point - actor.Aim.EyeOrigin;
            actor.Aim.SetOrientation(Mathf.RadToDeg(Mathf.Atan2(-to.X, -to.Z)), Mathf.RadToDeg(Mathf.Asin(to.Normalized().Y)));
            var distance = new Vector2(point.X - actor.GlobalPosition.X, point.Z - actor.GlobalPosition.Z).Length();
            return new PlayerIntent { Move = distance > 3 ? Vector2.Down : Vector2.Zero, Aim = true, FirePressed = distance <= 3 };
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        try
        {
            using var tuning = Tuning.SavedTemplate.Copy();
            tuning.FullAuto = false;
            tuning.AimSpreadDegrees = tuning.MoveSpreadDegrees = tuning.ShotBloomDegrees = 0;
            session = SessionFactory.Build(this, tuning, new Source(this), new Mode(this), context => session = context, present: false);
            await Run().ConfigureAwait(true);
            GD.Print("[session actors] PASS two views/devices share one simulation; own shots/melee; survivor continuity; camera-free navigation/shoot/melee/ragdoll/get-up");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error) { GD.PushError($"[session actors] FAIL {error}"); ShutdownGuard.Quit(GetTree(), 1); }
    }
    public override void _ExitTree() => session?.Dispose();
    private async Task Ticks(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private void Send(InputEvent e)
    {
        // Both views see the same application event. Each assignment must filter it itself.
        foreach (var view in views) if (GodotObject.IsInstanceValid(view)) view._Input(e);
        e.Dispose();
    }
    private async Task Run()
    {
        await Ticks(6).ConfigureAwait(true);
        Require(views[0].CameraRig.GetWorld3D() == session.World.Physics && views[1].CameraRig.GetWorld3D() == session.World.Physics,
            "views did not share the simulation World3D");
        Send(new InputEventKey { PhysicalKeycode = Key.W, Pressed = true });
        Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.LeftY, AxisValue = -1 });
        await Ticks(30).ConfigureAwait(true);
        Require(actors[0].GlobalPosition.Z < -1 && actors[1].GlobalPosition.X < 5, "assigned actors did not move in their own yaw");
        for (var i = 0; i < 2; i++)
        {
            Require(views[i].CameraRig.Camera.GlobalPosition.DistanceTo(actors[i].Aim.EyeOrigin) < .00001f,
            "view did not follow its actor's aim");
        }

        Send(new InputEventKey { PhysicalKeycode = Key.W, Pressed = false });
        Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.LeftY, AxisValue = 0 });
        actors[0].Teleport(new Vector3(-6, .05f, 0), 0);
        actors[1].Teleport(new Vector3(6, .05f, 0), Mathf.Pi / 2);
        await Ticks(3).ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        await Ticks(12).ConfigureAwait(true);
        Require(shots[0] == 1 && shots[1] == 1 && directions[0].Dot(Vector3.Forward) > .9f && directions[1].Dot(Vector3.Left) > .9f,
            $"shots did not use independent actor aim: shots={shots[0]}/{shots[1]} directions={directions[0]}/{directions[1]}");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        props[0].GlobalPosition = new Vector3(-6, .25f, -1.2f);
        props[1].GlobalPosition = new Vector3(4.8f, .25f, 0);
        foreach (var prop in props) { prop.LinearVelocity = Vector3.Zero; prop.AngularVelocity = Vector3.Zero; }
        Send(new InputEventKey { PhysicalKeycode = Key.F, Pressed = true });
        Send(new InputEventJoypadButton { Device = 7, ButtonIndex = JoyButton.RightStick, Pressed = true });
        await Ticks(Engine.PhysicsTicksPerSecond).ConfigureAwait(true);
        Require(contacts[0] == 1 && contacts[1] == 1, "melee did not contact each actor's own forward target once");
        var director = new CaptureDirector(); AddChild(director);
        director.Screenshot("two_views");
        var first = actors[0];
        session.CloseView(views[0]);
        Require(GodotObject.IsInstanceValid(first) && first.Runtime.IntentSource == null && session.LocalViews.Count == 1,
            "closing a view retired its actor or kept the device");
        var before = actors[1].GlobalPosition;
        Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.LeftY, AxisValue = -1 });
        await Ticks(30).ConfigureAwait(true);
        Require(actors[1].GlobalPosition.DistanceTo(before) > 1, $"closing one view stopped the survivor: before={before} after={actors[1].GlobalPosition} velocity={actors[1].Velocity} resolved={actors[1].ResolvedState} intent={actors[1].Runtime.Intent.Move} source={actors[1].Runtime.IntentSource}");
        Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.LeftY, AxisValue = 0 });
        Require(actors[2].View == null && actors[2].FindChildren("*", "Camera3D", owned: false).Count == 0, "bot has a camera");
        props[2].GlobalPosition = new Vector3(20, .25f, 3.8f);
        props[2].LinearVelocity = Vector3.Zero;
        brain.Enabled = true;
        var botStart = actors[2].GlobalPosition;
        var moved = 0f;
        for (var tick = 0; tick < Engine.PhysicsTicksPerSecond * 20 && (recoveries == 0 || actors[2].IsRagdolled); tick++)
        {
            await Ticks(1).ConfigureAwait(true);
            moved = Mathf.Max(moved, actors[2].GlobalPosition.DistanceTo(botStart));
        }
        Require(moved > 2 && shots[2] >= 2 && contacts[2] == 1 && falls > 0 && recoveries > 0 && !actors[2].IsRagdolled,
            $"camera-free bot incomplete: moved={moved} shots={shots[2]} melee={contacts[2]} falls={falls} getups={recoveries}");
        director.Screenshot("survivor");
    }
}
