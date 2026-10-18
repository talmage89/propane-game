using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Bots;

/// <summary>
/// One Banking brain's cover, combat and sight memory. Timers advance at the original physics call sites; no
/// native resources are owned.
/// </summary>
internal sealed class BankingCombat
{
    private const float PlayerRange = 38f;
    private float strafeTimer;
    private Vector3? cover;
    private PlayerCharacter? fightTarget;
    private float fightTime;
    private float burstClock;
    private float coverTimer;
    private float strafeSide = 1f;
    private Node3D? sighted;
    private bool sightedVisible;
    private float sightTimer;
    private PlayerCharacter? coverObserver;
    private PlayerCharacter? shooterSeen;
    private PlayerCharacter? watcher;
    private bool retiredWatcher;
    private float watchTimer;
    private bool shooterVisible;
    private float shooterTimer;

    public readonly record struct Attack(Vector3? Travel = null, bool Sprint = false, bool Aim = false,
        Vector2 Strafe = default, bool Fire = false);

    public void Begin(float dt) { sightTimer -= dt; watchTimer -= dt; shooterTimer -= dt; }
    public void ForgetCover() => cover = null;

    public void ForgetActor(PlayerCharacter actor)
    {
        if (ReferenceEquals(fightTarget, actor)) fightTarget = null;
        if (ReferenceEquals(sighted, actor)) sighted = null;
        if (ReferenceEquals(coverObserver, actor)) coverObserver = null;
        if (ReferenceEquals(shooterSeen, actor)) shooterSeen = null;
        if (ReferenceEquals(watcher, actor)) { watcher = null; retiredWatcher = true; }
    }

    public Vector3? UnderFireCover(IBotWorld match, PlayerCharacter player, Vector3 here, Tuning tuning, float dt)
    {
        var shooter = Body(match, match.LastHitBy);
        if (shooter != null && match.Scores.GetValueOrDefault(shooter.ActorId) < match.MyScore)
        {
            if (shooter != shooterSeen || shooterTimer <= 0)
            {
                shooterSeen = shooter;
                shooterTimer = tuning.BotSightInterval;
                shooterVisible = CanSee(match, shooter, here + Vector3.Up * 1.2f);
            }
            if (shooterVisible) return Cover(match, player, shooter, tuning, dt);
        }
        return null;
    }

    public Vector3? ProtectionCover(IBotWorld match, PlayerCharacter player, Vector3 here, Tuning tuning, float dt)
    {
        if (watchTimer <= 0 || retiredWatcher || watcher != null && (!GodotObject.IsInstanceValid(watcher) || watcher.IsRagdolled))
        {
            retiredWatcher = false;
            watchTimer = tuning.BotWatchInterval;
            watcher = Watcher(match, here);
        }
        return watcher == null ? null : Cover(match, player, watcher, tuning, dt);
    }

    public int WatcherId => watcher!.ActorId;

    /// <summary>Holding a bank worth keeping: leading with a few, or holding several late in the match.</summary>
    public static bool Protecting(IBotWorld match, bool pointsDriven)
    {
        var mine = match.MyScore;
        if (mine < 4) return false;
        // A trailing Hard bot must keep gaining/denying points even late; a leader still protects its bank.
        if (!pointsDriven && match.TimeLeft < 40f) return true;
        var bodies = match.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            if (match.Scores.GetValueOrDefault(bodies[i].ActorId) >= mine) return false;
        }
        return true;
    }

    /// <summary>The nearest upright player within 45 m holding no more than this bot that can see it, or null.</summary>
    private static PlayerCharacter? Watcher(IBotWorld match, Vector3 here)
    {
        var bodies = match.RemoteBodies;
        var tried = -1f;
        while (true)
        {
            PlayerCharacter? next = null;
            var nextDistance = 45f;
            for (var i = 0; i < bodies.Count; i++)
            {
                var e = bodies[i];
                var distance = e.GlobalPosition.DistanceTo(here);
                if (distance > tried && distance < nextDistance && !e.IsRagdolled && match.Scores.GetValueOrDefault(e.ActorId) <= match.MyScore)
                {
                    next = e;
                    nextDistance = distance;
                }
            }
            if (next == null || CanSee(match, next, here + Vector3.Up * 1.2f)) return next;
            tried = nextDistance;
        }
    }

    /// <summary>The nearest upright opponent within melee reach whose eye sees this bot's chest, or null.</summary>
    public static PlayerCharacter? Closest(IBotWorld match, Vector3 here, float reach, float height)
    {
        var bodies = match.RemoteBodies;
        var tried = -1f;
        while (true)
        {
            PlayerCharacter? next = null;
            var nextDistance = float.MaxValue;
            for (var i = 0; i < bodies.Count; i++)
            {
                var e = bodies[i];
                if (e.IsRagdolled) continue;
                var at = e.GlobalPosition;
                var distance = at.DistanceSquaredTo(here);
                if (distance > tried && distance < nextDistance && at.DistanceTo(here) <= reach)
                {
                    next = e;
                    nextDistance = distance;
                }
            }
            if (next == null || CanSee(match, next, here + Vector3.Up * height)) return next;
            tried = nextDistance;
        }
    }

    private static PlayerCharacter? Body(IBotWorld match, int actorId)
    {
        var bodies = match.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].ActorId == actorId) return bodies[i];
        }
        return null;
    }

    /// <summary>Cover from an observer, found again when there is none yet or the observer sees it; null when there is none.</summary>
    private Vector3? Cover(IBotWorld match, PlayerCharacter player, PlayerCharacter observer, Tuning tuning, float dt)
    {
        coverTimer -= dt;
        if (observer != coverObserver || coverTimer <= 0)
        {
            coverObserver = observer;
            coverTimer = tuning.BotCoverInterval;
            if (cover == null || CanSeeFrom(match, observer, cover.Value + Vector3.Up * 1.2f))
                cover = FindCover(match, player, observer, tuning);
        }
        return cover;
    }

    /// <summary>The nearest spot within a short run that the shooter cannot see, if there is one.</summary>
    private Vector3? FindCover(IBotWorld match, PlayerCharacter player, PlayerCharacter shooter, Tuning tuning)
    {
        var services = match.Services;
        var here = player.GlobalPosition;
        var eye = shooter.GlobalPosition + Vector3.Up * 1.6f;
        Vector3? best = null;
        var bestCost = float.MaxValue;
        for (var ring = 0; ring < 4; ring++)
        {
            var radius = ring == 0 ? 3f : ring == 1 ? 6f : ring == 2 ? 10f : 14f;
            for (var i = 0; i < 12; i++)
            {
                var angle = i * Mathf.Tau / 12f;
                var p = here + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                if (!services.Ray(new Vector3(p.X, 4f, p.Z), new Vector3(p.X, -1f, p.Z), Layers.World | Layers.Props | Layers.Floor, out var ground) ||
                    ground.Point.Y > 0.6f)
                {
                    continue;
                }
                var spot = ground.Point;
                if (services.Clear(eye, spot + Vector3.Up * 1.2f, Layers.World))
                {
                    continue;
                }
                var blockedPath = !services.Clear(here + Vector3.Up * 0.6f, spot + Vector3.Up * 0.6f, Layers.World | Layers.Props);
                var cost = radius + (blockedPath ? tuning.BotCoverDetourCost : 0) + spot.DistanceTo(eye) * -0.05f;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = spot;
                }
            }
            if (best != null)
            {
                return best;
            }
        }
        return null;
    }

    /// <summary>Whether a target is in sight from this bot's eye, asked at most every <see cref="Tuning.BotSightInterval"/>.</summary>
    public bool Sees(IBotWorld match, PlayerCharacter player, Node3D what, Vector3 point, Tuning tuning)
    {
        if (what != sighted || sightTimer <= 0)
        {
            sighted = what;
            sightTimer = tuning.BotSightInterval;
            sightedVisible = CanSee(match, player, point);
        }
        return sightedVisible;
    }

    public Attack Fight(IBotWorld match, PlayerCharacter player, PlayerCharacter enemy, Vector3 here, Tuning tuning, BotAim aimControl, float skill, float clock,
        float dt, RandomNumberGenerator rng, BotStatus status)
    {
        var distance = enemy.GlobalPosition.DistanceTo(here);
        if (enemy != fightTarget)
        {
            fightTarget = enemy;
            fightTime = 0;
        }
        if (distance > PlayerRange || !Sees(match, player, enemy, Aim(enemy), tuning))
        {
            // Clearing the suburb must not strand a trailing bot outside rifle range forever. Route to the
            // richer opponent's actual position, rather than a short leg that may end inside a house.
            fightTime = 0;
            aimControl.Track(player, Aim(enemy), dt, skill);
            var travel = Flat((enemy.IsRagdolled ? enemy.PelvisPosition : enemy.GlobalPosition) - here);
            status.Set("closing on {0} at {1:0.0} m", enemy.ActorId, distance);
            return new Attack(Travel: travel, Sprint: distance > tuning.BotSprintDistance);
        }
        fightTime += dt;
        // Aim like a person: a moment to react, then an error that wanders and tightens while tracking.
        var settle = Mathf.Clamp(fightTime / 1.2f, 0, 1);
        var error = Mathf.Lerp(tuning.BotAimErrorEasy, tuning.BotAimErrorHard, skill) * Mathf.Lerp(1.6f, 0.55f, settle);
        var t = clock;
        var wobble = new Vector3(Mathf.Sin(t * 1.7f + 0.3f), 0.6f * Mathf.Sin(t * 2.3f + 1.1f), Mathf.Cos(t * 1.9f + 2.2f)) * error;
        var aimPoint = Aim(enemy) + wobble;
        aimControl.Track(player, aimPoint, dt, skill);
        var aiming = distance > 9f;
        // Strafe to be harder to hit; close in if far, back off if right on top of them.
        strafeTimer -= dt;
        if (strafeTimer <= 0)
        {
            strafeTimer = rng.RandfRange(0.5f, 1.3f);
            strafeSide = rng.Randf() < 0.5f ? -1f : 1f;
        }
        var forward = distance > 24f ? 1f : distance < 7f ? -0.6f : 0f;
        var strafe = new Vector2(strafeSide * 0.8f, forward);

        var aim = player.Aim;
        var off = Mathf.RadToDeg((-aim.EyeTransform.Basis.Z).AngleTo(aimPoint - aim.EyeOrigin));
        var reacted = fightTime > Mathf.Lerp(tuning.BotReactionEasy, tuning.BotReactionHard, skill);
        var firing = reacted && off < 2f && Sees(match, player, enemy, Aim(enemy), tuning);
        // Short bursts, letting the aim settle in between.
        burstClock += dt;
        var inBurst = Mathf.PosMod(burstClock, 0.65f) < 0.35f;
        status.Set("fighting {0} at {1:0.0} m, error {2:0.00} m, firing {4}", enemy.ActorId, distance, error, word: firing ? "True" : "False");
        return new Attack(Aim: aiming, Strafe: strafe, Fire: firing && inBurst);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    private static Vector3 Aim(PlayerCharacter enemy) => BotMotor.AimPoint(enemy);
    private static bool CanSee(IBotWorld world, PlayerCharacter player, Vector3 point) => BotMotor.CanSee(world, player, point);
    private static bool CanSeeFrom(IBotWorld world, PlayerCharacter observer, Vector3 point) =>
        world.Services.Clear(observer.GlobalPosition + Vector3.Up * 1.6f, point, Layers.World | Layers.Props);
}
