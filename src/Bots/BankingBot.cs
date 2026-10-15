using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Navigation;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Tank Bank's bot goal policy. It plays the tank bank to win rather than to test:
/// - it sets tanks off from outside their throw radius (wider for a pile, whose chain reaction reaches further);
/// - it runs from venting tanks that come close;
/// - it shoots players only when they have tanks to spill, and then sets off what they spilled;
/// - it prefers venting tanks (one shot from a point) and big piles, especially piles with another player near them;
/// - it strafes in a fight, keeps its magazine topped up when nothing is happening, and fetches ammo when low.
/// It chooses a few times a second and steers every tick; whether something is in sight is asked at most
/// <see cref="Tuning.BotSightInterval"/> apart; hot paths reuse their storage.
/// </summary>
public sealed class BankingBot : IBotGoalPolicy
{
    private const float RetargetInterval = 0.35f;
    private const float PlayerRange = 38f;
    private const float AmmoLow = 15;

    private readonly RandomNumberGenerator rng = new();
    private readonly Dictionary<ulong, float> blacklist = new(1024);
    private readonly List<ulong> expired = new(1024);
    private readonly BotStatus status = new();
    private readonly Vantage[] vantages = new Vantage[48];
    private PlayerIntent intent;
    private Node3D? target;
    private float retarget;
    private float blocked;
    private float unaimed;
    private Vector3 unaimedAnchor;
    private Tuning tuning = null!;
    private readonly BotNavigator navigator = new();
    private CapsuleShape3D? capsule;
    private PlayerCharacter? capsuleOwner;
    private Vector3? navigationGoal;
    private Vector3 fleeGoal;
    private int fleeAttempt;
    public int StuckEvents => navigator.StuckEvents;
    public int Replans => navigator.Replans;
    public int Jumps => navigator.Jumps;
    public int BlacklistedTargets { get; private set; }
    private float strafeTimer;
    private Vector3? vantage;
    private Vector3? cover;
    private PlayerCharacter? fightTarget;
    private float fightTime;
    private float tapCooldown;
    private bool protecting;
    private PropaneTank? fleeing;
    private float burstClock;
    private float coverTimer;
    private float vantageTimer;
    private float strafeSide = 1f;
    private float clock;
    private readonly BotAim aimControl = new();
    private readonly BotPoints points = new();
    // Sight lines asked a few times a second and remembered in between.
    private Node3D? sighted;
    private bool sightedVisible;
    private float sightTimer;
    private PlayerCharacter? coverObserver;
    private PlayerCharacter? shooterSeen;
    private PlayerCharacter? watcher;
    private float watchTimer;
    private bool shooterVisible;
    private float shooterTimer;

    /// <summary>What the bot is doing, for logs.</summary>
    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => navigator.Diagnostics;

    /// <summary>0..1: how quickly it turns onto a target and how tightly it holds aim. 1 is the hardest.</summary>
    public float Skill { get; set; } = 1f;
    public bool PointsDriven { get; set; }

    public BankingBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        // Bots that start together choose on different ticks.
        retarget = rng.Randf() * RetargetInterval;
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world)
    {
        tuning = world.World.Tuning;
        capsule = BotServices.Capsule(world.Player);
        capsuleOwner = world.Player;
        world.Services.Navigation(BotServices.Capabilities(capsule, tuning));
        world.Services.PrepareActor(world.Player);
        for (var i = 0; i < world.RemoteBodies.Count; i++) world.Services.PrepareActor(world.RemoteBodies[i]);
        navigator.Prepare(tuning);
    }

    public PlayerIntent Read()
    {
        var read = intent;
        intent.FirePressed = false;
        intent.JumpPressed = false;
        intent.ReloadPressed = false;
        intent.MeleePressed = false;
        return read;
    }

    public void Think(IBotWorld match, float dt)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.BotBrain);
        tuning = match.World.Tuning;
        clock += dt;
        tapCooldown -= dt;
        sightTimer -= dt;
        watchTimer -= dt;
        shooterTimer -= dt;
        var player = match.Player;
        intent = new PlayerIntent();
        navigationGoal = null;
        if (!match.IsPlaying || player.IsRagdolled || player.Ammo == null)
        {
            aimControl.Reset();
            navigator.Reset();
            return;
        }
        var here = player.GlobalPosition;
        var ammo = player.Ammo;

        // 1. Get away from a venting tank that comes close: it may go up any moment, and it hits like a car.
        // Once running, keep going until well clear, so it does not hover at the edge.
        if (fleeing != null && (!GodotObject.IsInstanceValid(fleeing) || fleeing.State == PropaneTank.TankState.Exploded ||
                                fleeing.GlobalPosition.DistanceTo(here) > tuning.RagdollRadius + 4f))
        {
            fleeing = null;
        }
        var danger = fleeing ?? match.Services.NearestVenting(here, tuning.RagdollRadius + 1f);
        if (danger != null && (danger != fleeing || Flat(fleeGoal - here).Length() < tuning.BotGoalRadius * 2))
        {
            // Keep the destination fixed while searching/traveling, but do not retry a failed escape forever.
            fleeAttempt = 0;
            fleeGoal = BotMotor.EscapeGoal(here, danger.GlobalPosition, tuning, fleeAttempt);
        }
        fleeing = danger;
        if (danger != null)
        {
            Face(player, fleeGoal + Vector3.Up * 1.4f, dt);
            MoveToward(player, fleeGoal - here);
            intent.Sprint = true;
            status.Set("fleeing venting tank {1:0.0} m away", first: danger.GlobalPosition.DistanceTo(here));
            Navigate(match, dt);
            return;
        }

        // A close opponent is worth knocking down even with an empty bank. Do not abandon a tank in the sights.
        if (target is not PropaneTank || !player.ResolvedState.EffectiveAim)
        {
            var reach = tuning.MeleeRange + tuning.MeleeLungeSpeed * (tuning.MeleeWindup + tuning.MeleeActive);
            var close = Closest(match, here, reach, tuning.MeleeContactHeight);
            if (close != null)
            {
                Face(player, Aim(close), dt);
                var direction = Flat(close.GlobalPosition - here);
                var forward = Flat(-player.Aim.EyeTransform.Basis.Z).Normalized();
                intent.MeleePressed = player.CanMelee && MeleeContact.InArc(direction, forward, reach, tuning.MeleeHalfAngleDegrees);
                MoveToward(player, direction);
                status.Set("melee {0} at {1:0.0} m", close.ActorId, direction.Length());
                Navigate(match, dt);
                return;
            }
        }

        // 2. Under fire while holding more than the shooter: trading hits loses more than it wins, so break their
        //    line of sight. A shooter holding more gets shot back.
        if (match.MyScore > 0 && match.Now - match.LastHitTime < 2.5)
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
                if (shooterVisible && Cover(match, player, shooter, dt) is { } spot)
                {
                    RunTo(player, spot, dt);
                    status.Set("taking cover {1:0.0} m away", first: Flat(spot - here).Length());
                    Navigate(match, dt);
                    return;
                }
            }
        }
        // 3. Protecting a bank (leading, or late in the match): stay out of everyone's sight.
        protecting = Protecting(match);
        if (protecting)
        {
            if (watchTimer <= 0 || watcher != null && (!GodotObject.IsInstanceValid(watcher) || watcher.IsRagdolled))
            {
                watchTimer = tuning.BotWatchInterval;
                watcher = Watcher(match, here);
            }
            if (watcher != null && Cover(match, player, watcher, dt) is { } spot && Flat(spot - here).Length() > 0.8f)
            {
                RunTo(player, spot, dt);
                status.Set("protecting {1:0}: hiding from {0}", watcher.ActorId, match.MyScore);
                Navigate(match, dt);
                return;
            }
        }
        cover = null;

        retarget -= dt;
        if (retarget <= 0 || target != null && !IsValid(target))
        {
            retarget = RetargetInterval;
            var chosen = Choose(match, ammo);
            // Keep a valid demolition goal stable while its route is being followed. Piles contain many
            // nearly equivalent tanks; distance-based retargeting otherwise discards the runway every 0.35 s.
            if (chosen is PropaneTank nextTank && target is PropaneTank current && IsValid(current) &&
                !blacklist.ContainsKey(current.GetInstanceId()) && (!PointsDriven ||
                    nextTank.GlobalPosition.DistanceTo(current.GlobalPosition) <= tuning.ChainRadius)) { chosen = target; }
            if (chosen != target)
            {
                blocked = 0;
                unaimed = 0;
                if (!navigator.Jumping) { navigator.Reset(); }
                vantage = null;
                target = chosen;
            }
        }
        if (target == null)
        {
            status.Set("no target");
            if (navigator.Jumping) { Navigate(match, dt); }
            return;
        }

        switch (target)
        {
            case AmmoPickup pickup:
                status.Set("fetching ammo {1:0.0} m away", first: pickup.GlobalPosition.DistanceTo(here));
                Face(player, pickup.GlobalPosition + Vector3.Up * 0.4f, dt);
                MoveToward(player, Flat(pickup.GlobalPosition - here));
                intent.Sprint = true;
                break;
            case PlayerCharacter enemy:
                Fight(match, player, enemy, dt);
                break;
            case PropaneTank tank:
                Demolish(match, player, tank, dt);
                break;
        }

        // Top up the magazine while nothing is in the sights, or when it is empty.
        if (!intent.FireHeld && ammo.Reserve > 0 && (ammo.Magazine == 0 || ammo.Magazine < tuning.MagazineSize / 2 && target is not PlayerCharacter))
        {
            intent.ReloadPressed = true;
        }
        Navigate(match, dt);
    }

    // ------------------------------------------------------------------ Choosing

    /// <summary>Holding a bank worth keeping: leading with a few, or holding several late in the match.</summary>
    private bool Protecting(IBotWorld match)
    {
        var mine = match.MyScore;
        if (mine < 4) return false;
        // A trailing Hard bot must keep gaining/denying points even late; a leader still protects its bank.
        if (!PointsDriven && match.TimeLeft < 40f) return true;
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
    private static PlayerCharacter? Closest(IBotWorld match, Vector3 here, float reach, float height)
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
                var distance = e.GlobalPosition.DistanceSquaredTo(here);
                if (!e.IsRagdolled && distance > tried && distance < nextDistance && e.GlobalPosition.DistanceTo(here) <= reach)
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
    private Vector3? Cover(IBotWorld match, PlayerCharacter player, PlayerCharacter observer, float dt)
    {
        coverTimer -= dt;
        if (observer != coverObserver || coverTimer <= 0)
        {
            coverObserver = observer;
            coverTimer = tuning.BotCoverInterval;
            if (cover == null || CanSeeFrom(match, observer, cover.Value + Vector3.Up * 1.2f))
                cover = FindCover(match, player, observer);
        }
        return cover;
    }

    private void RunTo(PlayerCharacter player, Vector3 spot, float dt)
    {
        Face(player, spot + Vector3.Up * 1.4f, dt);
        MoveToward(player, Flat(spot - player.GlobalPosition));
        intent.Sprint = true;
    }

    private void Tap()
    {
        if (tapCooldown <= 0)
        {
            intent.FirePressed = true;
            tapCooldown = 0.16f;
        }
    }

    private Node3D? Choose(IBotWorld match, AmmoState ammo)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.BotChoose);
        var player = match.Player;
        var here = player.GlobalPosition;
        expired.Clear();
        foreach (var (key, until) in blacklist)
        {
            if (until < clock) expired.Add(key);
        }
        foreach (var key in expired) blacklist.Remove(key);

        // Ammo: go out of the way for it when low; grab a can on the way when it is close.
        var rounds = ammo.Magazine + ammo.Reserve;
        AmmoPickup? pickup = null;
        var pickups = match.Pickups;
        for (var i = 0; i < pickups.Count; i++)
        {
            var p = pickups[i];
            if (p.Available && !blacklist.ContainsKey(p.GetInstanceId()) &&
                (pickup == null || p.GlobalPosition.DistanceTo(here) < pickup.GlobalPosition.DistanceTo(here)))
            {
                pickup = p;
            }
        }
        if (pickup != null && ammo.Reserve < tuning.MaxReserve &&
            (rounds < AmmoLow || rounds < AmmoLow * 3 && pickup.GlobalPosition.DistanceTo(here) < 12f))
        {
            return pickup;
        }

        Node3D? best = null;
        var bestScore = PointsDriven ? 0 : float.MinValue;
        if (PointsDriven) points.Begin(match);
        // Players worth shooting: in sight, in range, with tanks to spill.
        var bodies = match.RemoteBodies;
        var cleared = tuning.BotHuntWhenEmpty && match.Services.Tanks.Count == 0;
        for (var i = 0; i < bodies.Count; i++)
        {
            var enemy = bodies[i];
            var banked = match.Scores.GetValueOrDefault(enemy.ActorId);
            var distance = enemy.GlobalPosition.DistanceTo(here);
            // A fight costs ammo and invites return fire: worth it for a real bank, for a cheap close one, or
            // against whoever just shot this bot.
            var avenging = enemy.ActorId == match.LastHitBy && match.Now - match.LastHitTime < 3;
            var hunting = cleared && banked > match.MyScore;
            var worth = PointsDriven ? banked > 0 && !enemy.IsRagdolled : protecting ? banked > match.MyScore : banked >= 3 || banked >= 1 && distance < 18f || avenging && banked > 0;
            if (blacklist.ContainsKey(enemy.GetInstanceId()) || !worth && !hunting || distance > PlayerRange && !hunting)
            {
                continue;
            }
            // Worth it against anyone holding more; against smaller banks only when this bot has little to lose.
            var score = PointsDriven ? points.Fight(match, enemy, distance) :
                60f + banked * 6f - distance - (match.MyScore > banked + 2 ? 45f : 0f);
            if (PointsDriven && enemy == target) score *= 1 + tuning.BotPointsHysteresis;
            if (score > bestScore && (hunting || CanSee(match, player, Aim(enemy))))
            {
                bestScore = score;
                best = enemy;
            }
        }

        var services = match.Services;
        var tanks = services.Tanks;
        for (var i = 0; i < tanks.Count; i++)
        {
            var tank = tanks[i];
            var at = tank.GlobalPosition;
            var distance = at.DistanceTo(here);
            if (PointsDriven)
            {
                if (blacklist.ContainsKey(tank.GetInstanceId()) || protecting && Near(bodies, at, 25f, upright: false)) continue;
                var rate = points.Tank(match, tank, services.PileCount(i), distance);
                if (tank == target) rate *= 1 + tuning.BotPointsHysteresis;
                if (rate > bestScore) { bestScore = rate; best = tank; }
                continue;
            }
            if (tank.Invulnerable && distance >= 30f || blacklist.ContainsKey(tank.GetInstanceId()))
            {
                continue;
            }
            if (protecting && Near(bodies, at, 25f, upright: false))
            {
                continue;
            }
            var pile = services.PileCount(i);
            var score = (tank.State == PropaneTank.TankState.Venting ? 25f : 10f) + Mathf.Min(pile, 10) * 6f - distance * 0.7f;
            if (distance < 20f)
            {
                // Close tanks are quick points, piles or not.
                score += 12f;
            }
            // A pile with a rival beside it: setting it off throws them, spilling five.
            for (var j = 0; j < bodies.Count; j++)
            {
                var enemy = bodies[j];
                if (!enemy.IsRagdolled && match.Scores.GetValueOrDefault(enemy.ActorId) > 0 &&
                    enemy.GlobalPosition.DistanceTo(at) < tuning.RagdollRadius)
                {
                    score += 30f;
                }
            }
            if (tank.Invulnerable)
            {
                score -= 8f;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = tank;
            }
        }
        return best;
    }

    private static bool Near(IReadOnlyList<PlayerCharacter> bodies, Vector3 at, float within, bool upright)
    {
        for (var i = 0; i < bodies.Count; i++)
        {
            if ((!upright || !bodies[i].IsRagdolled) && bodies[i].GlobalPosition.DistanceTo(at) < within) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ Acting

    /// <summary>Whether a target is in sight from this bot's eye, asked at most every <see cref="Tuning.BotSightInterval"/>.</summary>
    private bool Sees(IBotWorld match, PlayerCharacter player, Node3D what, Vector3 point)
    {
        if (what != sighted || sightTimer <= 0)
        {
            sighted = what;
            sightTimer = tuning.BotSightInterval;
            sightedVisible = CanSee(match, player, point);
        }
        return sightedVisible;
    }

    private void Fight(IBotWorld match, PlayerCharacter player, PlayerCharacter enemy, float dt)
    {
        var distance = enemy.GlobalPosition.DistanceTo(player.GlobalPosition);
        if (enemy != fightTarget)
        {
            fightTarget = enemy;
            fightTime = 0;
        }
        if (distance > PlayerRange || !Sees(match, player, enemy, Aim(enemy)))
        {
            // Clearing the suburb must not strand a trailing bot outside rifle range forever. Route to the
            // richer opponent's actual position, rather than a short leg that may end inside a house.
            fightTime = 0;
            Face(player, Aim(enemy), dt);
            MoveToward(player, Flat((enemy.IsRagdolled ? enemy.PelvisPosition : enemy.GlobalPosition) - player.GlobalPosition));
            intent.Sprint = distance > tuning.BotSprintDistance;
            status.Set("closing on {0} at {1:0.0} m", enemy.ActorId, distance);
            return;
        }
        fightTime += dt;
        // Aim like a person: a moment to react, then an error that wanders and tightens while tracking.
        var settle = Mathf.Clamp(fightTime / 1.2f, 0, 1);
        var error = Mathf.Lerp(tuning.BotAimErrorEasy, tuning.BotAimErrorHard, Skill) * Mathf.Lerp(1.6f, 0.55f, settle);
        var t = clock;
        var wobble = new Vector3(Mathf.Sin(t * 1.7f + 0.3f), 0.6f * Mathf.Sin(t * 2.3f + 1.1f), Mathf.Cos(t * 1.9f + 2.2f)) * error;
        var aimPoint = Aim(enemy) + wobble;
        Face(player, aimPoint, dt);
        intent.Aim = distance > 9f;
        // Strafe to be harder to hit; close in if far, back off if right on top of them.
        strafeTimer -= dt;
        if (strafeTimer <= 0)
        {
            strafeTimer = rng.RandfRange(0.5f, 1.3f);
            strafeSide = rng.Randf() < 0.5f ? -1f : 1f;
        }
        var forward = distance > 24f ? 1f : distance < 7f ? -0.6f : 0f;
        intent.Move = new Vector2(strafeSide * 0.8f, forward);

        var aim = player.Aim;
        var off = Mathf.RadToDeg((-aim.EyeTransform.Basis.Z).AngleTo(aimPoint - aim.EyeOrigin));
        var reacted = fightTime > Mathf.Lerp(tuning.BotReactionEasy, tuning.BotReactionHard, Skill);
        var firing = reacted && off < 2f && Sees(match, player, enemy, Aim(enemy));
        // Short bursts, letting the aim settle in between.
        burstClock += dt;
        var inBurst = Mathf.PosMod(burstClock, 0.65f) < 0.35f;
        if (firing && inBurst)
        {
            intent.FireHeld = true;
            intent.FirePressed = true;
        }
        status.Set("fighting {0} at {1:0.0} m, error {2:0.00} m, firing {4}", enemy.ActorId, distance, error, word: firing ? "True" : "False");
    }

    private void Demolish(IBotWorld match, PlayerCharacter player, PropaneTank tank, float dt)
    {
        var here = player.GlobalPosition;
        var center = tank.GlobalTransform * PropaneTank.LocalCenter;
        var distance = Flat(center - here).Length();
        // Setting a tank off needs distance: past its blast, and past the blasts of the pile it sets off. Both shots
        // come from there, in one burst: the first punctures, the next sets it (and its pile) off before it moves.
        var index = match.Services.IndexOf(tank);
        var spread = index >= 0 ? match.Services.PileSpread(index) : 0;
        var safe = tuning.RagdollRadius + 1.2f + spread;
        var near = safe;
        var far = Mathf.Max(near + 8f, 18f);
        var clear = distance >= near && distance <= far + 4f && Sees(match, player, tank, center);
        if (here.DistanceTo(unaimedAnchor) > 2f)
        {
            unaimedAnchor = here;
            unaimed = 0;
        }

        if (clear && blocked < 1f)
        {
            // In a good spot: stand, shoulder the rifle and fire when the crosshair is on it.
            Face(player, center, dt);
            intent.Aim = true;
            vantage = null;
            status.Set("shooting {4} tank at {1:0.0} m (keeping {2:0.0})", first: distance, second: near, word: BotStatus.Name(tank.State));
            if (tank.Invulnerable)
            {
                return;
            }
            if (OnTarget(match, player, tank))
            {
                // Single taps: one punctures, the next sets it off. Ammo is scarce.
                Tap();
                blocked = 0;
                unaimed = 0;
            }
            else
            {
                blocked += dt;
                unaimed += dt;
                if (unaimed >= tuning.BotStuckAimTime)
                {
                    // The vantage search sees past things the shot cannot (a loose fence panel in front of the
                    // rifle), so it can keep choosing this very spot. Leave this pile for a while.
                    var tanks = match.Services.Tanks;
                    for (var i = 0; i < tanks.Count; i++)
                    {
                        if (tanks[i].GlobalPosition.DistanceTo(tank.GlobalPosition) <= tuning.ChainRadius) Blacklist(tanks[i], tuning.BotTargetRetryTime);
                    }
                    Blacklist(tank, tuning.BotTargetRetryTime);
                    target = null;
                    vantage = null;
                    retarget = 0;
                    unaimed = 0;
                    status.Set("no clear shot: leaving this pile");
                }
            }
            return;
        }

        // Somewhere better: a spot at the right distance with a clear view of it, that can be walked to.
        vantageTimer -= dt;
        if (vantage == null || vantageTimer <= 0 &&
            (Flat(vantage.Value - center).Length() < near || Flat(vantage.Value - center).Length() > far + 4f ||
             !VisibleFrom(match, vantage.Value + Vector3.Up * 1.6f, center)))
        {
            vantageTimer = 0.6f;
            vantage = FindVantage(match, player, center, near, far);
            blocked = 0;
            if (vantage == null)
            {
                Blacklist(tank, 10f);
                retarget = 0;
                status.Set("no vantage point");
                return;
            }
        }
        vantageTimer = Mathf.Max(vantageTimer, 0);
        var to = Flat(vantage.Value - here);
        if (to.Length() < 0.6f)
        {
            // Arrived but still no shot (the tank moved): look again.
            vantage = null;
            Face(player, center, dt);
            return;
        }
        if (distance < 30f && to.Length() < 12f)
        {
            // Close: walk there keeping the tank in view, ready to shoot.
            Face(player, center, dt);
            MoveToward(player, to);
        }
        else
        {
            Face(player, vantage.Value + Vector3.Up * 1.4f, dt);
            MoveToward(player, to);
            intent.Sprint = to.Length() > 8f;
        }
        status.Set("moving to a vantage {1:0.0} m away for a {4} tank at {2:0.0} m (keeping {3:0.0})", first: to.Length(), second: distance,
            third: near, word: BotStatus.Name(tank.State));
    }

    private struct Vantage
    {
        public Vector3 Point;
        public float Distance;
    }

    /// <summary>
    /// The nearest open spot between <paramref name="near"/> and <paramref name="far"/> from a target with a clear
    /// view of it, on the ground (not a roof), preferring spots this bot can walk to in a straight line. Spots are
    /// tried nearest first and the search stops once no untried spot could beat the best, so it casts few rays.
    /// </summary>
    private Vector3? FindVantage(IBotWorld match, PlayerCharacter player, Vector3 target, float near, float far)
    {
        var services = match.Services;
        var here = player.GlobalPosition;
        var turn = rng.RandfRange(0, Mathf.Tau);
        var count = 0;
        for (var ring = 0; ring < 3; ring++)
        {
            var radius = ring == 0 ? near + 1f : ring == 1 ? (near + far) / 2f : far;
            for (var i = 0; i < 16; i++)
            {
                var angle = turn + i * Mathf.Tau / 16f;
                var p = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                vantages[count++] = new Vantage { Point = p, Distance = Flat(p - here).Length() };
            }
        }
        // Insertion sort: 48 spots, nearest first.
        for (var i = 1; i < count; i++)
        {
            var item = vantages[i];
            var j = i - 1;
            for (; j >= 0 && vantages[j].Distance > item.Distance; j--) vantages[j + 1] = vantages[j];
            vantages[j + 1] = item;
        }
        Vector3? best = null;
        var bestCost = float.MaxValue;
        for (var i = 0; i < count && vantages[i].Distance < bestCost; i++)
        {
            var p = vantages[i].Point;
            if (!services.Ray(new Vector3(p.X, 4f, p.Z), new Vector3(p.X, -1f, p.Z), Layers.World | Layers.Props | Layers.Floor, out var ground) ||
                ground.Point.Y > 0.6f || ground.Normal.Y < 0.8f)
            {
                continue;
            }
            var spot = ground.Point;
            // Routes keep out of tank clearances and vent throws: a spot inside one can never be reached.
            if (!services.Hazards.Safe(spot) || !services.Clear(spot + Vector3.Up * 1.6f, target, Layers.World | Layers.Props))
            {
                continue;
            }
            var walk = services.Clear(here + Vector3.Up * 0.6f, spot + Vector3.Up * 0.6f, Layers.World | Layers.Props);
            var cost = here.DistanceTo(spot) + (walk ? 0f : 40f);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = spot;
            }
        }
        return best;
    }

    /// <summary>The nearest spot within a short run that the shooter cannot see, if there is one.</summary>
    private Vector3? FindCover(IBotWorld match, PlayerCharacter player, PlayerCharacter shooter)
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

    /// <summary>Requests a destination; navigation resolves its route after tactical aim is chosen.</summary>
    private void MoveToward(PlayerCharacter player, Vector3 direction)
    {
        navigationGoal = player.GlobalPosition + Flat(direction);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Turns the view toward a point with skill-dependent smoothing, including while fleeing.</summary>
    internal void Face(PlayerCharacter player, Vector3 point, float dt) => aimControl.Track(player, point, dt, Skill);

    private static Vector3 Aim(PlayerCharacter enemy) => enemy.IsRagdolled ? enemy.PelvisPosition : enemy.GlobalPosition + Vector3.Up * 1.15f;

    /// <summary>The crosshair is on the target, with nothing in the way.</summary>
    private static bool OnTarget(IBotWorld match, PlayerCharacter player, Node3D target) => BotMotor.OnTarget(match, player, target);

    private static bool CanSee(IBotWorld match, PlayerCharacter player, Vector3 point) => BotMotor.CanSee(match, player, point);

    private static bool IsValid(Node3D? node) => node != null && GodotObject.IsInstanceValid(node) &&
                                                 node is not PropaneTank { State: PropaneTank.TankState.Exploded } &&
                                                 node is not AmmoPickup { Available: false };

    internal void Blacklist(Node3D node, float seconds)
    {
        // Fleeing can bypass retargeting while a previous tank is freed by an authoritative explosion.
        if (!GodotObject.IsInstanceValid(node)) { return; }
        blacklist[node.GetInstanceId()] = clock + seconds;
        BlacklistedTargets++;
    }

    private static bool CanSeeFrom(IBotWorld match, PlayerCharacter observer, Vector3 point) =>
        VisibleFrom(match, observer.GlobalPosition + Vector3.Up * 1.6f, point);

    private static bool VisibleFrom(IBotWorld match, Vector3 eye, Vector3 point) => match.Services.Clear(eye, point, Layers.World | Layers.Props);

    private void Navigate(IBotWorld match, float dt)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.BotNavigation);
        var player = match.Player;
        if (navigationGoal == null)
        {
            // Combat strafing uses the same collision checks and recovery as travel. Standing to shoot is
            // intentional; reset its progress window so a later walk is never counted as already stuck.
            if (intent.Move.LengthSquared() < 0.01f && !navigator.Jumping) { navigator.Reset(); return; }
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(player.Aim.Yaw));
            var wish = basis * new Vector3(intent.Move.X, 0, -intent.Move.Y);
            navigationGoal = player.GlobalPosition + wish.Normalized() * tuning.BotRouteSpacing * 2;
        }
        // The actor's capsule never changes; finding it once avoids marshaling every child each tick.
        if (capsuleOwner != player || capsule == null)
        {
            capsule = BotServices.Capsule(player);
            capsuleOwner = player;
        }
        var services = match.Services;
        var grid = services.Navigation(BotServices.Capabilities(capsule, tuning));
        var move = navigator.Navigate(grid, services.Hazards, new NavigationState(player.GlobalPosition, player.Velocity, player.IsOnFloor()),
            navigationGoal.Value, tuning, dt);
        if (move.Failed)
        {
            if (fleeing != null && GodotObject.IsInstanceValid(fleeing))
            {
                fleeGoal = BotMotor.EscapeGoal(player.GlobalPosition, fleeing.GlobalPosition, tuning, ++fleeAttempt);
            }
            if (target != null) { Blacklist(target, tuning.BotTargetRetryTime); }
            target = null;
            vantage = cover = null;
            retarget = 0;
            navigator.Reset();
            intent.Move = Vector2.Zero;
            return;
        }
        if (navigator.Jumping || move.Sprint)
        {
            Face(player, player.GlobalPosition + move.Direction * 10 + Vector3.Up * 1.4f, dt);
            intent.Aim = false;
        }
        var yawRad = Mathf.DegToRad(player.Aim.Yaw);
        var forward = new Vector3(-Mathf.Sin(yawRad), 0, -Mathf.Cos(yawRad));
        var right = new Vector3(Mathf.Cos(yawRad), 0, -Mathf.Sin(yawRad));
        intent.Move = new Vector2(move.Direction.Dot(right), move.Direction.Dot(forward));
        intent.Sprint = move.Sprint && intent.Move.Y > tuning.SprintForwardThreshold;
        intent.JumpPressed = move.Jump;
        if (!player.IsOnFloor()) { intent.Aim = false; }
    }
}
