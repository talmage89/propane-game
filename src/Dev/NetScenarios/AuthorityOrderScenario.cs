using System;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.NetScenarios;

/// <summary>Native authority refusals and controlled cross-lane overtaking, through the real client inbox.</summary>
internal sealed class AuthorityOrderScenario(INetScenarioHost host) : NetScenario(host)
{
    private readonly bool future = DevArgs.Get("scenario") == "future-epoch";
    private int stage;
    private float time;
    private Action? fuse;
    private bool refused;
    private bool overtook;
    private bool released;
    private NetMessage? held;

    protected override void Preparing(Match match)
    {
        stage = 0;
        time = 0;
        fuse = null;
        held = null;
        refused = overtook = released = false;
        DetachOnRetirement(() => { fuse = null; held = null; });
        var captured = Epoch;
        void Resolved(AuthorityMessage result)
        {
            if (!IsCurrent(captured)) return;
            if (result.Peer == net.MyId && result.Reason == ClaimRefusal.StaleEpoch) refused = true;
        }
        match.AuthorityResolved += Resolved;
        DetachOnRetirement(() => match.AuthorityResolved -= Resolved);
    }

    protected override void Assembled(Match match)
    {
        match.Player.InputOverride = new ScriptedInput();
        if (future && role != "host")
        {
            // Reorder only the first real epoch-2 grant behind a real publication on another lane. No synthetic
            // authority or motion is introduced: both packets came through ENet and the production decoder.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var inbox = (MatchInbox)typeof(NetClient).GetField("inbox", flags)!.GetValue(net)!;
            var receiverField = typeof(MatchInbox).GetField("receiver", flags)!;
            var previous = (Action<NetMessage>)receiverField.GetValue(inbox)!;
            var captured = Epoch;
            void Receive(NetMessage message)
            {
                if (!IsCurrent(captured)) return;
                if (held == null && !released && message.Payload is AuthorityMessage { Reason: ClaimRefusal.None } result &&
                    result.Bodies.Any(b => b.Body.Id == BodyIds.MapTankId(0) && b.Body.Epoch == 2))
                {
                    held = message;
                    return;
                }
                previous(message);
                if (held != null && match.Bodies.Get(BodyIds.MapTankId(0)) is { Epoch: 1, Future: not null } entry)
                {
                    var pending = held;
                    held = null;
                    overtook = true;
                    released = true;
                    var sequence = entry.Future!.Value.Body.Sequence;
                    previous(pending);
                    ScenarioAssert(entry.Epoch == 2 && entry.LastSequence >= sequence && entry.Future == null,
                        "future-epoch: grant lost the overtaking publication");
                    Log($"future-epoch: native publication {sequence} preceded grant; retained sequence {entry.LastSequence}");
                }
            }
            Action<NetMessage> receiver = Receive;
            receiverField.SetValue(inbox, receiver);
            DetachOnRetirement(() =>
            {
                if (ReferenceEquals(receiverField.GetValue(inbox), receiver)) receiverField.SetValue(inbox, previous);
                held = null;
            });
        }
        if (!future && role == "host")
        {
            var previous = match.AuthoritySendOverride;
            match.AuthoritySendOverride = bytes =>
            {
                var message = MessageCodec.Decode(bytes, false);
                if (message.Payload is ExplodeMessage explosion && explosion.Id == BodyIds.MapTankId(0))
                {
                    ScenarioAssert(explosion.Epoch == 2, "root-refusal: root did not use its accepted puncture epoch");
                    bytes = MessageCodec.Encode(message with { Payload = explosion with { Epoch = 1 } }, false);
                }
                net.Send(bytes);
            };
            DetachOnRetirement(() => match.AuthoritySendOverride = previous);
        }
    }

    protected override void Run(Match match, float dt)
    {
        if (!match.IsPlaying || scenarioPassed || scenarioFailed) return;
        time += dt;
        var id = BodyIds.MapTankId(0);
        var other = BodyIds.MapTankId(1);
        if (stage == 0 && time >= 2)
        {
            stage = 1;
            if (role == "host")
            {
                var tank = match.LiveTanks.Single(t => t.NetId == id);
                if (future)
                {
                    tank.Sleeping = false;
                    ScenarioAssert(match.Claim(new[] { tank }, atomic: true) != 0, "future-epoch: no native claim");
                }
                else
                {
                    foreach (var target in match.LiveTanks.Where(t => t.NetId == id || t.NetId == other).ToArray())
                    {
                        BodySync.Snap(target, new Transform3D(Basis.Identity, new Vector3(target.NetId == id ? 0 : 20, 60, 0)), Vector3.Zero, Vector3.Zero);
                        target.TakeShot(target.ToGlobal(PropaneTank.LocalCenter + Vector3.Right * .15f), Vector3.Right, Vector3.Left, match.Player.ActorId);
                    }
                }
            }
        }
        if (!future && role == "host" && stage == 1 && match.Bodies.Get(id) is { Epoch: 2, ClaimPending: false } &&
            match.Bodies.Get(other) is { Epoch: 2, ClaimPending: false })
        {
            stage = 2;
            var root = match.LiveTanks.Single(t => t.NetId == id);
            var child = match.LiveTanks.Single(t => t.NetId == other);
            child.FuseScheduleOverride = (_, callback) => fuse = callback;
            root.Detonate(match.Player.ActorId);
            child.ChainHit(Vector3.Zero, 0, root.BlastRequest, match.Player.ActorId);
            match.OnLocalBlast(root.GlobalPosition, new[] { child }, root.BlastRequest);
            ScenarioAssert(fuse != null, "root-refusal: owned vent did not schedule a dependent fuse");
        }
        if (time < 8) return;
        if (future)
        {
            ScenarioAssert(match.Bodies.Get(id) is { Epoch: 2, ClaimPending: false }, "future-epoch: authority never settled");
            if (role != "host") ScenarioAssert(overtook && released && held == null, "future-epoch: publication never overtook the held native grant");
        }
        else
        {
            var root = match.LiveTanks.SingleOrDefault(t => t.NetId == id);
            var child = match.LiveTanks.SingleOrDefault(t => t.NetId == other);
            ScenarioAssert(root?.State == PropaneTank.TankState.Venting && child?.State == PropaneTank.TankState.Venting,
                "root-refusal: canonical vents were not restored");
            if (role == "host")
            {
                ScenarioAssert(refused && fuse != null && child!.ChainCause == 0, "root-refusal: refusal failed to cancel the dependent fuse");
                fuse?.Invoke();
                ScenarioAssert(child!.State == PropaneTank.TankState.Venting, "root-refusal: retired fuse detonated its canonical vent");
            }
            ScenarioAssert(match.Bodies.Get(id) is { ClaimPending: false }, "root-refusal: reservation survived refusal");
        }
        scenarioPassed = !scenarioFailed;
        if (match.LiveTanks.SingleOrDefault(t => t.NetId == id) is { } subject)
        {
            var camera = new Camera3D { Current = true, Fov = 55 };
            match.AddChild(camera);
            camera.GlobalPosition = subject.GlobalPosition + new Vector3(2, 1.4f, 3);
            camera.LookAt(subject.GlobalTransform * PropaneTank.LocalCenter, Vector3.Up);
        }
        CaptureResolved($"{net.PlayerName}_{(future ? "future_epoch" : "root_refused")}");
        Log($"{(future ? "future-epoch" : "root-refusal")}: assertions passed");
    }

    private async void CaptureResolved(string label)
    {
        if (DisplayServer.GetName() == "headless" || DevArgs.Get("capture-dir") == null) return;
        // The scenario changes cameras during physics; the framebuffer still holds the preceding view.
        var captured = Epoch;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsCurrent(captured) && IsInsideTree()) director.ScreenshotAsync(label);
    }

}
