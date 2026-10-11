using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Session;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class SessionTests
{
    private sealed class Assembly(List<string> log, string? fail) : ISessionMode, ISessionWorldSource
    {
        private ActorRuntime actor = null!;
        private void Step(string name)
        {
            log.Add(name);
            if (name == fail) throw new InvalidOperationException("injected assembly failure");
        }
        public void Configure(SessionContext session)
        {
            session.Cancellation.Register(() => log.Add("cancel"));
            session.OnDetaching(() => log.Add("detach"));
            session.OnUnsubscribing(() => log.Add("unsubscribe"));
            session.OnStopping(() => log.Add("stop"));
            Step("configure");
        }
        public void Build(SessionContext session)
        {
            var content = new Node3D();
            content.TreeExiting += () => log.Add("content freed");
            session.World.Root.AddChild(content);
            Step("content");
        }
        public void CreateActors(SessionContext session)
        {
            var character = new PlayerCharacter { World = session.World };
            character.TreeExiting += () => log.Add("actor freed");
            actor = session.AddActor(character);
            Step("actors");
        }
        public void CreateViews(SessionContext session)
        {
            var view = session.AddLocalView(actor, session.World.ViewRoot);
            view.TreeExiting += () => log.Add("view freed");
            Step("views");
        }
        public void RegisterReplication(SessionContext session) => Step("replication");
        public void AttachInbox(SessionContext session) => Step("inbox");
        public void Ready(SessionContext session) => Step("ready");
    }

    public static void Register(TestRunner runner)
    {
        var stages = new[] { "configure", "content", "actors", "views", "replication", "inbox", "ready" };
        foreach (var failure in stages)
        {
            runner.Run($"session/failure at {failure} uses retirement", () =>
            {
                var log = new List<string>();
                var assembly = new Assembly(log, failure);
                SessionContext session = null!;
                System.Threading.CancellationToken token = default;
                Throws<InvalidOperationException>(() => SessionFactory.Build(runner, runner.World.Tuning, assembly, assembly,
                    built => { session = built; token = built.Cancellation; }, present: false));
                Assert(!session.IsAlive && !session.World.IsAlive && token.IsCancellationRequested, "failed assembly stayed live");
                Assert(session.Actors.Count == 0 && session.LocalViews.Count == 0, "failed assembly retained actors/views");
                Assert(!GodotObject.IsInstanceValid(session.World.Root), "failed assembly retained content");
                Assert(log.Where(name => new[] { "cancel", "detach", "unsubscribe", "stop" }.Contains(name))
                    .SequenceEqual(new[] { "cancel", "detach", "unsubscribe", "stop" }), "adapter retirement order changed");
                session.Dispose();
            });
        }

        runner.Run("session/assembly and reverse node retirement", () =>
        {
            var log = new List<string>();
            var assembly = new Assembly(log, null);
            using var session = SessionFactory.Build(runner, runner.World.Tuning, assembly, assembly, _ => { }, present: false);
            Assert(log.SequenceEqual(stages), "factory stage order changed");
            session.OnStopping(() => throw new InvalidOperationException("bad adapter"));
            Throws<AggregateException>(session.Dispose);
            Assert(log.Skip(stages.Length).SequenceEqual(new[] { "cancel", "detach", "unsubscribe", "stop", "view freed", "actor freed", "content freed" }),
                "cleanup stopped on an adapter error or freed simulation before views");
            Assert(session.Actors.Count == 0 && !GodotObject.IsInstanceValid(session.World.Root), "adapter failure leaked the world");
        });
        runner.Run("input/assigned devices isolate strengths and edges", () =>
        {
            InputSetup.Register();
            var enabled = true;
            var keyboard = new DeviceInput(runner.World.Tuning, () => enabled, InputDeviceAssignment.KeyboardAndMouse);
            var pad = new DeviceInput(runner.World.Tuning, () => enabled, InputDeviceAssignment.Gamepad(7));
            void Send(InputEvent e) { keyboard.Accumulate(e); pad.Accumulate(e); e.Dispose(); }
            Send(new InputEventKey { PhysicalKeycode = Key.W, Pressed = true });
            Assert(keyboard.Read().Move.Y == 1 && pad.Read().Move == Vector2.Zero, "keyboard movement crossed assignments");
            Send(new InputEventJoypadMotion { Device = 8, Axis = JoyAxis.LeftY, AxisValue = -1 });
            Assert(pad.Read().Move == Vector2.Zero, "another pad drove the assigned pad");
            Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.LeftX, AxisValue = 1 });
            Assert(pad.Read().Move.X == 1 && keyboard.Read().Move.X == 0, "pad movement crossed assignments");
            Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.LeftX, AxisValue = -1 });
            Assert(pad.Read().Move.X == -1, "axis reversal retained the opposite direction");
            Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.TriggerRight, AxisValue = 1 });
            Assert(pad.Read().FirePressed && !keyboard.Read().FirePressed, "trigger crossed assignments or lost its edge");
            Send(new InputEventJoypadMotion { Device = 7, Axis = JoyAxis.TriggerRight, AxisValue = 1 });
            Assert(!pad.Read().FirePressed && pad.Read().FireHeld, "repeated axis event became another press");
            Send(new InputEventMouseMotion { ScreenRelative = new Vector2(4, -3) });
            Assert(keyboard.Read().Look == new Vector2(4, -3) && pad.Read().Look == Vector2.Zero, "mouse crossed assignments");
            enabled = false;
            Send(new InputEventJoypadButton { Device = 7, ButtonIndex = JoyButton.A, Pressed = true });
            enabled = true;
            pad.Reset();
            Assert(!pad.Read().JumpPressed, "disabled press survived resume");
            pad.Disconnect(7);
            Assert(pad.Read().Move == Vector2.Zero && !pad.Read().FireHeld, "disconnect retained held actions");
            Send(new InputEventKey { PhysicalKeycode = Key.F, Pressed = true });
            Send(new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1, Pressed = true });
            keyboard.Read();
            Send(new InputEventKey { PhysicalKeycode = Key.F, Pressed = false });
            Send(new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1, Pressed = true });
            Assert(!keyboard.Read().MeleePressed, "releasing one binding canceled another held binding");
        });
    }
}
