using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class MeleeTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("melee/resolver owns every gait, reload, aim, fire and jump", () =>
        {
            foreach (var intent in new[] { new StateIntent(), new StateIntent(MoveY: 1, Sprint: true),
                         new StateIntent(MoveY: 1, AimHeld: true) })
            {
                foreach (var reload in new[] { false, true })
                {
                    var r = new PlayerStateResolver();
                    var state = r.Resolve(intent with { MeleePressed = true, Trigger = true, JumpPressed = true },
                        new StatePhysics(PlayerLifecycle.Active, true, reload, ShotAvailable: true), Settings, 0.01f);
                    TestRunner.Assert(state.MeleeStarted && state.MeleePhase == MeleePhase.Windup && state.Weapon == WeaponAction.Melee &&
                        state.Gait == PlayerGait.Default && state.TargetSpeed == 2.6f && !state.Jump && !state.Fire && !state.EffectiveAim &&
                        state.MoveX == 0 && state.MoveY == 1, "strike failed to take ownership");
                    state = r.Resolve(intent, Ground, Settings, 0.25f);
                    TestRunner.Assert(state.MeleePhase == MeleePhase.Active && !state.MeleeStarted, "missing active transition");
                    state = r.Resolve(intent, Ground, Settings, 0.16f);
                    TestRunner.Assert(state.MeleePhase == MeleePhase.Recovery && state.TargetSpeed == 0 && state.MoveY == 0, "missing recovery");
                    state = r.Resolve(intent with { MeleePressed = true }, Ground, Settings, 0.4f);
                    TestRunner.Assert(state.Weapon != WeaponAction.Melee && !r.CanMelee, "recovery failed to release or cooldown bypassed");
                    state = r.Resolve(intent with { MeleePressed = true }, Ground, Settings, 0.7f);
                    TestRunner.Assert(state.MeleeStarted, "cooldown never released");
                }
            }
        });
        runner.Run("melee/air rejects presses and interrupts contact without resetting cooldown", () =>
        {
            var r = new PlayerStateResolver();
            var intent = new StateIntent(MeleePressed: true);
            TestRunner.Assert(r.Resolve(intent, Ground with { Grounded = false }, Settings, .01f).Weapon != WeaponAction.Melee, "air strike accepted");
            TestRunner.Assert(r.Resolve(new StateIntent(), Ground, Settings, .01f).Weapon != WeaponAction.Melee, "air press buffered into landing");
            r.Resolve(intent, Ground, Settings, .01f);
            TestRunner.Assert(r.Resolve(intent, Ground with { Grounded = false }, Settings, .01f).Weapon != WeaponAction.Melee && !r.CanMelee, "air did not cancel");
            r.Interrupt();
            TestRunner.Assert(!r.CanMelee, "throw reset cooldown");
        });
        runner.Run("melee/arc bounds reach, height, angle and rear contacts", () =>
        {
            TestRunner.Assert(MeleeContact.InArc(Vector3.Forward * 1.6f, Vector3.Forward, 1.65f, 50), "forward excluded");
            TestRunner.Assert(MeleeContact.InArc(Vector3.Forward.Rotated(Vector3.Up, Mathf.DegToRad(49)) * 1.5f,
                Vector3.Forward, 1.65f, 50), "inside angle excluded");
            foreach (var offset in new[] { Vector3.Forward * 1.66f, Vector3.Back, Vector3.Up * 2,
                         Vector3.Forward.Rotated(Vector3.Up, Mathf.DegToRad(51)), Vector3.Zero })
            {
                TestRunner.Assert(!MeleeContact.InArc(offset, Vector3.Forward, 1.65f, 50), $"invalid arc accepted {offset}");
            }

            TestRunner.Assert(MeleeContact.InArc(Vector3.Right, Vector3.Right, 1.65f, 50), "rotated arc failed");
        });
        runner.Run("melee/one contact per victim per swing", () =>
        {
            var contact = new MeleeContact();
            contact.Begin();
            TestRunner.Assert(contact.TryHit(1) && !contact.TryHit(1) && contact.TryHit(2), "duplicate hit or second victim lost");
            contact.Begin();
            TestRunner.Assert(contact.TryHit(1), "next swing lost contact");
        });
        runner.Run("melee/snapshots advance within a swing without blending a new swing or release", () =>
        {
            var a = new PlayerNetState { Weapon = WeaponAction.Melee, Melee = .3f };
            var b = a; b.Melee = .7f;
            TestRunner.Assert(Mathf.IsEqualApprox(PlayerNetState.Lerp(a, b, .75f).Melee, .6f), "swing progress not blended");
            b.Melee = .1f;
            TestRunner.Assert(PlayerNetState.Lerp(a, b, .75f).Melee == .1f, "new swing rewound through old progress");
            b.Weapon = WeaponAction.Carry; b.Melee = 0;
            var release = PlayerNetState.Lerp(a, b, .75f);
            TestRunner.Assert(release.Weapon == WeaponAction.Carry && release.Melee == 0, "release retained contact pose");
        });
        runner.Run("melee/server bounds swing identity, cooldown, window and contact rate", () =>
        {
            using var t = new Tuning { MeleeMaxContacts = 2 };
            var v = new MeleeValidation();
            var start = new MeleeStartMessage(1, Vector3.Forward);
            var hit = new MeleeContactMessage(1, 3, -1);
            TestRunner.Assert(!v.Contact(hit, 0, t), "contact without start");
            TestRunner.Assert(v.Start(start, 0, t) && !v.Start(start with { Swing = 2 }, 0.1, t), "cooldown bypassed");
            TestRunner.Assert(v.Contact(hit, .3, t) && !v.Contact(hit, .3, t), "duplicate accepted");
            TestRunner.Assert(v.Contact(hit with { Victim = 4 }, .3, t) && !v.Contact(hit with { Victim = 5 }, .3, t), "rate bound failed");
            TestRunner.Assert(v.Start(start with { Swing = 2 }, 2, t), "next start refused");
            TestRunner.Assert(!v.Contact(hit, 2.3, t) && !v.Contact(hit with { Swing = 2 }, 3, t), "stale or late contact accepted");
            TestRunner.Assert(!v.Start(start, 4, t), "old swing reused");
        });
        runner.Run("melee/wire rejects malformed direction and ambiguous contacts", () =>
        {
            foreach (var direction in new[] { Vector3.Up, Vector3.Zero, Vector3.Forward * 2, new Vector3(float.NaN, 0, 0) })
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(
                    new NetMessage(Msg.MeleeStart, 1, 0, new MeleeStartMessage(1, direction), 2, 1, 1), false), false));
            }

            foreach (var (victim, prop) in new[] { (0, -1), (3, 1), (-1, -1), (3, -2) })
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(
                    new NetMessage(Msg.MeleeContact, 1, 0, new MeleeContactMessage(1, victim, prop), 2, 1, 1), false), false));
            }
        });
        runner.Run("melee/server production relay enforces phase, membership, target and cooldown", () =>
        {
            using var f = new ServerFixture();
            byte[] Start(uint swing) => MessageCodec.Encode(new NetMessage(Msg.MeleeStart, f.Id, 0,
                new MeleeStartMessage(swing, Vector3.Forward)), false);
            byte[] Hit(int victim, uint swing = 1) => MessageCodec.Encode(new NetMessage(Msg.MeleeContact, f.Id, 0,
                new MeleeContactMessage(swing, victim, -1)), false);
            f.Reject(Hit(f.ActorId(3)));
            f.Reject(Start(1), peer: 999);
            f.Receive(Start(1));
            TestRunner.Assert(f.Packets.Count == 2 && f.Packets.Any(p => p.Peer == 3 && p.Message.Sender == 2), "start not relayed");
            f.Reject(Start(2));
            f.Reject(Hit(999)); f.Reject(Hit(f.ActorId(2))); f.Reject(Hit(3, 2));
            f.Receive(Hit(f.ActorId(3)));
            TestRunner.Assert(f.Packets.Count == 2 && f.Packets.All(p => p.Message.Payload is MeleeContactMessage), "hit not relayed");
            f.Reject(Hit(f.ActorId(3)));
            using var loading = new ServerFixture(playing: false);
            loading.Reject(Start(1));
            loading.Reject(Hit(f.ActorId(3)));
            loading.Receive(new NetWriter(Msg.Loaded, loading.Id));
            loading.Receive(new NetWriter(Msg.Loaded, loading.Id), peer: 3);
            loading.Advance();
            loading.Reject(Start(1)); // Countdown.
            loading.Advance();
            loading.Clock.Seconds += 20;
            loading.Reject(Start(1)); // Expired play, before the next phase tick.
            loading.Advance();
            loading.Reject(Start(1)); // Results.
            loading.Reject(Hit(f.ActorId(3)));
        });
    }

    private static StatePhysics Ground => new(PlayerLifecycle.Active, true);
    private static StateTuning Settings => new(6, 9.5f, 1.6f, .12f, .12f, 1.3f, .3f, .24f, .16f, .4f, .65f, 2.6f);
}
