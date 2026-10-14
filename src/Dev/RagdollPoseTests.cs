using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>The first physical-bone transforms must not depend on a distant actor's render sample rate.</summary>
public partial class RagdollPoseTests : Node3D
{
    private WorldContext world = null!;
    private PlayerCharacter full = null!, distant = null!;
    private PlayerCharacter hitFull = null!, hitDistant = null!;
    private readonly List<(string Label, PlayerCharacter Full, PlayerCharacter Distant, WeaponAction Action)> weaponPairs = [];
    private Camera3D observer = null!;
    private int ticks;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        InputSetup.Register();
        using var tuning = new Tuning { ActorPoseFarRate = 1, ActorPoseHiddenRate = 1 };
        world = new WorldContext(this, tuning, present: true);
        world.SetContentRoot(world.Root);
        world.Spawn.InWorld(new VoidEnvironment());
        PlayerCharacter Actor(int id, Vector3 position, WeaponAction action = WeaponAction.Carry)
        {
            var input = new ScriptedInput();
            input.Hold(0, 3, new PlayerIntent { Move = Vector2.Up, Aim = action == WeaponAction.Aim });
            if (action == WeaponAction.Reload) input.Reload(.8f);
            if (action == WeaponAction.Melee) input.Melee(1);
            return world.Spawn.InWorld(new PlayerCharacter
            {
                World = world,
                ActorId = id,
                Position = position,
                InputOverride = input,
                Ammo = action == WeaponAction.Reload ? new AmmoState { Tuning = world.Tuning, Magazine = 5, Reserve = 90 } : null,
            });
        }
        full = Actor(1, Vector3.Right * 20);
        distant = Actor(8, Vector3.Zero);
        LocalView.Attach(full, this).Hud.ShowHud(false);
        hitFull = Actor(2, Vector3.Right * 60);
        hitDistant = Actor(9, Vector3.Right * 40);
        LocalView.Attach(hitFull, this).Hud.ShowHud(false);
        foreach (var (label, action) in new[] { ("aiming", WeaponAction.Aim), ("reload", WeaponAction.Reload), ("melee", WeaponAction.Melee) })
        {
            var i = weaponPairs.Count;
            var control = Actor(3 + i, Vector3.Right * (100 + i * 40), action);
            var held = Actor(10 + i, Vector3.Right * (80 + i * 40), action);
            LocalView.Attach(control, this).Hud.ShowHud(false);
            weaponPairs.Add((label, control, held, action));
        }
        observer = world.AddView(this, new Camera3D { Current = true });
        observer.LookAtFromPosition(new Vector3(0, 3, 140), Vector3.Up);
        ProcessPhysicsPriority = int.MinValue;
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            observer.MakeCurrent();
            ((ScriptedInput)full.InputOverride!).Advance((float)delta);
            ((ScriptedInput)distant.InputOverride!).Advance((float)delta);
            ((ScriptedInput)hitFull.InputOverride!).Advance((float)delta);
            ((ScriptedInput)hitDistant.InputOverride!).Advance((float)delta);
            foreach (var pair in weaponPairs)
            {
                ((ScriptedInput)pair.Full.InputOverride!).Advance((float)delta);
                ((ScriptedInput)pair.Distant.InputOverride!).Advance((float)delta);
            }
            ticks++;
            if (ticks == 139 && (Tree(distant).CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual ||
                Tree(hitDistant).CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual ||
                weaponPairs.Any(pair => Tree(pair.Distant).CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual)))
            {
                throw new InvalidOperationException("fixture must hold every distant animation tree before the hit");
            }

            if (ticks == 140)
            {
                var distance = (world.Tuning.RagdollRadius + world.Tuning.StaggerRadius) / 2;
                var blast = new BlastInfo(Vector3.Zero, Vector3.Forward, distance, .5f);
                hitFull.ReceiveBlast(blast);
                hitDistant.ReceiveBlast(blast);
            }
            if (ticks != 150) return;
            if (!Tree(hitFull).Get("parameters/hit/active").AsBool() || !Tree(hitDistant).Get("parameters/hit/active").AsBool())
                throw new InvalidOperationException("both actors must play the hit before the physical throw");
            Compare(full, distant, "walking");
            Compare(hitFull, hitDistant, "hit reaction");
            foreach (var pair in weaponPairs)
            {
                if (pair.Full.ResolvedState.Weapon != pair.Action || pair.Distant.ResolvedState.Weapon != pair.Action)
                    throw new InvalidOperationException($"both actors must be {pair.Label} before the physical throw");
                Compare(pair.Full, pair.Distant, pair.Label);
            }
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[ragdoll pose] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private static AnimationTree Tree(PlayerCharacter actor) => actor.FindChildren("*", "AnimationTree", owned: false)
        .OfType<AnimationTree>().Single();

    private static void Compare(PlayerCharacter control, PlayerCharacter held, string label)
    {
        control.Collapse();
        held.Collapse();
        var a = control.FindChildren("*", "PhysicalBoneSimulator3D", owned: false).OfType<Ragdoll>().Single();
        var b = held.FindChildren("*", "PhysicalBoneSimulator3D", owned: false).OfType<Ragdoll>().Single();
        if (a.Bones.Count == 0 || a.Bones.Count != b.Bones.Count)
            throw new InvalidOperationException("actors must expose the same nonempty physical-bone rig");
        var offset = control.GlobalPosition - held.GlobalPosition;
        var maximum = 0f;
        var rotation = 0f;
        for (var i = 0; i < a.Bones.Count; i++)
        {
            maximum = Math.Max(maximum, (a.Bones[i].GlobalPosition - offset).DistanceTo(b.Bones[i].GlobalPosition));
            rotation = Math.Max(rotation, Mathf.RadToDeg(a.Bones[i].GlobalBasis.GetRotationQuaternion()
                .AngleTo(b.Bones[i].GlobalBasis.GetRotationQuaternion())));
        }
        if (!float.IsFinite(maximum) || !float.IsFinite(rotation) || maximum > .001f || rotation > .1f)
            throw new InvalidOperationException($"{label}: render cadence changed initial physical-bone transforms by {maximum:F6}m/{rotation:F4}deg");
        GD.Print($"[ragdoll pose] PASS {label} full vs 1-Hz initial bone error={maximum:F6}m/{rotation:F4}deg");
    }

    public override void _ExitTree() => world.Dispose();
}
