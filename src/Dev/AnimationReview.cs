using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.World;

namespace Propane.Dev;

/// <summary>Repeatable visual review through the real local controller; never supplies animation poses.</summary>
public partial class AnimationReview : Node3D
{
    private readonly ScriptedInput input = new();
    private readonly List<(string Name, float Start, float End)> cases = new();
    private PlayerCharacter player = null!;
    private Camera3D? observer;
    private Label caption = null!;
    private string current = "";
    private int burstShots;
    private bool jumped;
    private bool reloaded;
    private bool struck;
#if !PROPANE_REVIEW_RELEASE
    private WorldContext world = null!;
    public override void _ExitTree() => world?.Dispose();
#endif

    public override void _Ready()
    {
        try { Setup(); }
        catch (Exception error) { GD.PushError($"[animation-review] FAIL {error}"); Quit(GetTree(), 1); }
    }

    private void Setup()
    {
        ProcessPhysicsPriority = -10;
        InputSetup.Register();
        var dynamic = new Node3D { Name = "Dynamic" };
        AddChild(dynamic);
#if PROPANE_REVIEW_RELEASE
        // The release's 3.6 m/s aim speed is normalized only in this review fixture to today's 1.6 m/s rule.
        Tuning.UseForMatch(new Tuning { AimMoveSpeed = 1.6f });
        var effects = new Node3D { Name = "Effects" };
        AddChild(effects);
        Spawn.SetEffectsRoot(effects);
        Spawn.SetWorldRoot(dynamic);
        player = new PlayerCharacter { Name = "Player" };
        var sensitivity = Tuning.Current.MouseSensitivity;
#else
        using var defaults = new Tuning();
        world = new WorldContext(this, defaults, present: true);
        world.SetContentRoot(dynamic);
        player = new PlayerCharacter { World = world, Name = "Player" };
        var sensitivity = world.Tuning.MouseSensitivity;
#endif
        var environment = new VoidEnvironment();
        AddChild(environment);
        AddChild(player);
#if !PROPANE_REVIEW_RELEASE
        LocalView.Attach(player, this).Hud.ShowHud(false);
#endif
        player.InputOverride = input;
        player.Fired += _ => { if (CaseAt(input.Now).Name == "burst") burstShots++; };
#if !PROPANE_REVIEW_RELEASE
        player.MeleeStarted += (_, _) => struck = true;
#endif
        environment.Follow = player;
#if PROPANE_REVIEW_RELEASE
        player.Ammo = new AmmoState { Magazine = 30, Reserve = 90 };
#else
        player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 30, Reserve = 90 };
#endif
        var view = DevArgs.Get("view") ?? "side";
        if (view != "player")
        {
            if (view != "side") throw new ArgumentException("view must be side or player");
            observer = new Camera3D { Current = true, Fov = 40 };
            AddChild(observer);
        }
        var canvas = new CanvasLayer();
        AddChild(canvas);
        caption = new Label { Position = new Vector2(16, 16) };
        caption.AddThemeFontSizeOverride("font_size", 22);
        canvas.AddChild(caption);
        var director = new CaptureDirector();
        var cursor = 0f;
        var filter = DevArgs.Get("cases")?.Split(',');
        void Case(string name, Action<float> schedule, float length = 3)
        {
            if (filter != null && !filter.Contains(name)) return;
            var start = cursor;
            cases.Add((name, start, start + length));
            director.At(start, () => player.Teleport(Vector3.Zero, 0));
            schedule(start);
            // A dense sequence also serves as a deterministic 12 fps review film; --record remains available.
            for (var i = 1; i < length * 12; i++) director.ShotAt(start + i / 12f, $"{name}_{i:000}");
            cursor += length;
        }
        Case("idle", _ => { });
        var directions = new[] { Vector2.Down, new Vector2(1, 1).Normalized(), Vector2.Right,
            new Vector2(1, -1).Normalized(), Vector2.Up, new Vector2(-1, -1).Normalized(), Vector2.Left,
            new Vector2(-1, 1).Normalized() };
        foreach (var gait in new[] { "walk", "jog", "creep" })
        {
            for (var i = 0; i < directions.Length; i++)
            {
                var move = directions[i] * (gait == "walk" ? 0.25f : 1);
                Case($"{gait}_{i * 45:000}", t => input.Hold(t + 0.25f, t + 2.65f,
                    new PlayerIntent { Move = move, Aim = gait == "creep" }));
            }
        }

        Case("sprint", t => input.Hold(t + 0.25f, t + 2.65f, new PlayerIntent { Move = Vector2.Down, Sprint = true }));
        Case("start_stop", t => input.Hold(t + 0.6f, t + 1.9f, new PlayerIntent { Move = Vector2.Down }));
        Case("pivot", t =>
        {
            input.Hold(t + 0.2f, t + 1.5f, new PlayerIntent { Move = Vector2.Down, Aim = true });
            input.Hold(t + 1.5f, t + 2.8f, new PlayerIntent { Move = Vector2.Up, Aim = true });
        });
        Case("turn", t =>
        {
            input.Hold(t, t + 3, new PlayerIntent { Aim = true });
            input.Look(t + 0.5f, t + 1.5f, new Vector2(Mathf.Pi / sensitivity, 0));
            input.Look(t + 1.6f, t + 2.6f, new Vector2(-Mathf.Pi / sensitivity, 0));
        });
        Case("jump_land", t =>
        {
            input.Hold(t + 0.1f, t + 2.6f, new PlayerIntent { Move = Vector2.Down, Aim = true });
            input.Jump(t + 0.5f);
        });
        Case("burst", t =>
        {
            input.Hold(t, t + 3, new PlayerIntent { Aim = true });
            input.Hold(t + 0.5f, t + 1.3f, new PlayerIntent { FireHeld = true });
        });
        Case("reload", t =>
        {
            director.At(t, () => player.Ammo!.Magazine = 20);
            input.Reload(t + 0.25f);
        });
        Case("melee", t =>
        {
#if !PROPANE_REVIEW_RELEASE
            input.Melee(t + 0.6f);
#endif
        });
        Case("aim_idle", t => input.Hold(t, t + 3, new PlayerIntent { Aim = true }));
        if (cases.Count == 0) throw new ArgumentException("no matching cases");
        director.At(cursor - 0.1f, () =>
        {
            bool Included(string name) => cases.Any(c => c.Name == name);
            var failed = (Included("burst") && burstShots < 2) || (Included("jump_land") && !jumped) ||
                (Included("reload") && !reloaded);
#if !PROPANE_REVIEW_RELEASE
            failed |= Included("melee") && !struck;
#endif
            if (failed) { GD.PushError("[animation-review] FAIL controller action coverage"); Quit(GetTree(), 1); }
            else
            {
                GD.Print($"[animation-review] controller coverage: burst={burstShots}, jump={jumped}, reload={reloaded}, melee={struck}");
            }
        });
        director.QuitAfter = cursor;
        AddChild(director);
        var path = DevArgs.Get("metrics");
        if (path != null)
        {
            var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("foot_l") >= 0);
            skeleton.AddChild(new AnimationReviewProbe { Player = player, Clock = input, CaseAt = CaseAt, Output = path });
        }
        var manifest = DevArgs.Get("manifest");
        if (manifest != null)
        {
            File.WriteAllText(manifest, JsonSerializer.Serialize(cases.Select(c => new
            {
                name = c.Name,
                start = c.Start,
                end = c.End,
#if PROPANE_REVIEW_RELEASE
            available = c.Name != "melee",
#else
                available = true,
#endif
            })));
        }
    }

    internal static void Quit(SceneTree tree, int code)
    {
#if PROPANE_REVIEW_RELEASE
        tree.Quit(code);
#else
        ShutdownGuard.Quit(tree, code);
#endif
    }

    private (string Name, float LocalTime) CaseAt(float time)
    {
        var episode = cases.LastOrDefault(c => time >= c.Start);
        return (episode.Name ?? "startup", time - episode.Start);
    }

    public override void _PhysicsProcess(double delta)
    {
        input.Advance((float)delta);
        var episode = CaseAt(input.Now).Name;
        if (episode == "jump_land" && player.Velocity.Y > 0.5f) jumped = true;
        if (episode == "reload" && player.Ammo?.Reloading == true) reloaded = true;
    }

    public override void _Process(double delta)
    {
        var episode = CaseAt(input.Now).Name;
        if (current != episode)
        {
            current = episode;
            caption.Text = $"{DevArgs.Get("label") ?? "working tree"} / {DevArgs.Get("view") ?? "side"} / {episode}";
#if PROPANE_REVIEW_RELEASE
            if (episode == "melee") caption.Text += " (not in v0.2.0)";
#endif
            GD.Print($"[animation-review] {episode} wall={Time.GetTicksMsec()}");
        }
        var focus = player.GlobalPosition + Vector3.Up;
        observer?.LookAtFromPosition(focus + new Vector3(3.4f, 0.3f, 0.4f), focus);
    }
}
