using System;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Paired full-auto magazines using the production brain's aim controller and actual player hit capsule.</summary>
public partial class BotRecoilProbe : Node
{
    public PlayerCharacter Player { get; init; } = null!;
    private readonly Bot bot = new();
    private readonly SimpleBot simple = new();
    private int magazines;
    private PlayerCharacter target = null!;
    private float time;
    private int shots, hits, magazine = -1;
    private bool finished;
    private float distance, skill;

    public override void _Ready()
    {
        ProcessPhysicsPriority = -20;
        magazines = Math.Clamp((int)DevArgs.GetFloat("magazines", 10), 1, 10);
        distance = DevArgs.GetFloat("distance", 20);
        skill = DevArgs.GetFloat("skill", 1);
        bot.Skill = skill;
        target = new PlayerCharacter { World = Player.World, Name = "Target", IsRemote = true, Position = new Vector3(0, 0, -distance) };
        Player.World.Root.AddChild(target);
        Player.Fired += Shot;
    }

    private void Shot(ShotReport shot)
    {
        shots++;
        if (PlayerCharacter.Owning(shot.Collider) == target) hits++;
    }

    public override void _PhysicsProcess(double delta)
    {
        time += (float)delta;
        if (time >= magazines * 4 + 1)
        {
            if (!finished)
            {
                finished = true;
                Player.Fired -= Shot;
                var failed = shots != magazines * 30 || hits < magazines * 6;
                GD.Print(FormattableString.Invariant($"[bot-recoil] {(failed ? "FAIL" : "PASS")} skill={skill} distance={distance} shots={shots} hits={hits} rate={(float)hits / Math.Max(1, shots):F6}"));
                if (failed) { GD.PushError("[bot-recoil] FAIL magazine coverage or accuracy"); ShutdownGuard.Quit(GetTree(), 1); }
            }
            return;
        }
        var nextMagazine = (int)(time / 4);
        if (nextMagazine < magazines && nextMagazine != magazine)
        {
            magazine = nextMagazine;
            Player.Ammo!.Magazine = 30;
        }
        // The same skill-dependent tracking wobble used against players, at a settled tracking weight.
        var clock = time + 1;
        var error = Mathf.Lerp(Player.World.Tuning.BotAimErrorEasy, Player.World.Tuning.BotAimErrorHard, skill) * 0.55f;
        var wobble = new Vector3(Mathf.Sin(clock * 1.7f + 0.3f), 0.6f * Mathf.Sin(clock * 2.3f + 1.1f), Mathf.Cos(clock * 1.9f + 2.2f)) * error;
        if (DevArgs.Get("brain") == "simple")
            simple.Face(Player, target.GlobalPosition + Vector3.Up * 1.15f, (float)delta);
        else
            bot.Face(Player, target.GlobalPosition + Vector3.Up * 1.15f + wobble, (float)delta);
    }
}
