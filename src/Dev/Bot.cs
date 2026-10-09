using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>A bot's brain: it reads the match each physics step and produces the player's input.</summary>
public interface IBotBrain : IPlayerInputSource
{
    string Status { get; }

    void Think(Match match, float dt);
}

/// <summary>
/// A bot player for matches. It plays the tank bank to win rather than to test:
/// - it sets tanks off from outside their throw radius (wider for a pile, whose chain reaction reaches further);
/// - it runs from venting tanks that come close;
/// - it shoots players only when they have tanks to spill, and then sets off what they spilled;
/// - it prefers venting tanks (one shot from a point) and big piles, especially piles with another player near them;
/// - it strafes in a fight, keeps its magazine topped up when nothing is happening, and fetches ammo when low.
/// </summary>
public sealed class Bot : IBotBrain
{
    private const float RetargetInterval = 0.35f;
    private const float PlayerRange = 38f;
    private const float AmmoLow = 15;
    private const float TurnSharpness = 18f;

    private readonly RandomNumberGenerator rng = new();
    private readonly Dictionary<ulong, float> blacklist = new();
    private PlayerIntent intent;
    private Node3D? target;
    private float retarget;
    private float blocked;
    private float stuck;
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
    private Vector3 lastPosition;
    private float yaw;
    private float pitch;
    private bool hasView;

    /// <summary>What the bot is doing, for logs.</summary>
    public string Status { get; private set; } = "";

    /// <summary>0..1: how quickly it turns onto a target and how tightly it holds aim. 1 is the hardest.</summary>
    public float Skill { get; set; } = 1f;

    public PlayerIntent Read()
    {
        var read = intent;
        intent.FirePressed = false;
        intent.JumpPressed = false;
        intent.ReloadPressed = false;
        return read;
    }

    public void Think(Match match, float dt)
    {
        clock += dt;
        tapCooldown -= dt;
        var player = match.Player;
        intent = new PlayerIntent();
        if (!match.IsPlaying || player.IsRagdolled || player.Ammo == null)
        {
            hasView = false;
            return;
        }
        var tuning = Tuning.Current;
        var here = player.GlobalPosition;
        var ammo = player.Ammo;

        // 1. Get away from a venting tank that comes close: it may go up any moment, and it hits like a car.
        // Once running, keep going until well clear, so it does not hover at the edge.
        if (fleeing != null && (!GodotObject.IsInstanceValid(fleeing) || fleeing.State == PropaneTank.TankState.Exploded ||
                                fleeing.GlobalPosition.DistanceTo(here) > tuning.RagdollRadius + 4f))
        {
            fleeing = null;
        }
        var danger = fleeing ?? match.LiveTanks.Where(t => t.State == PropaneTank.TankState.Venting)
            .Where(t => t.GlobalPosition.DistanceTo(here) < tuning.RagdollRadius + 1f)
            .OrderBy(t => t.GlobalPosition.DistanceTo(here)).FirstOrDefault();
        fleeing = danger;
        if (danger != null)
        {
            var away = here - danger.GlobalPosition;
            away.Y = 0;
            away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward;
            Face(player, here + away * 10f + Vector3.Up * 1.4f, dt, snap: true);
            MoveToward(player, away);
            intent.Sprint = true;
            Status = $"fleeing venting tank {danger.GlobalPosition.DistanceTo(here):0.0} m away";
            Unstick(player, dt);
            return;
        }

        // 2. Under fire while holding more than the shooter: trading hits loses more than it wins, so break their
        //    line of sight. A shooter holding more gets shot back.
        if (match.MyScore > 0 && NetTransport.Now - match.LastHitTime < 2.5)
        {
            var shooter = match.RemoteBodies.FirstOrDefault(b => b.PeerId == match.LastHitBy);
            if (shooter != null && match.Scores.GetValueOrDefault(shooter.PeerId) < match.MyScore && CanSee(shooter, here + Vector3.Up * 1.2f))
            {
                coverTimer -= dt;
                if (cover == null || coverTimer <= 0)
                {
                    coverTimer = 0.5f;
                    cover = FindCover(player, shooter);
                }
                if (cover != null)
                {
                    Face(player, cover.Value + Vector3.Up * 1.4f, dt, snap: false);
                    MoveToward(player, Flat(cover.Value - here));
                    intent.Sprint = true;
                    Status = $"taking cover {Flat(cover.Value - here).Length():0.0} m away";
                    Unstick(player, dt);
                    return;
                }
            }
        }
        // 3. Protecting a bank (leading, or late in the match): stay out of everyone's sight.
        protecting = Protecting(match);
        if (protecting)
        {
            var watcher = match.RemoteBodies.Where(e => !e.IsRagdolled && e.GlobalPosition.DistanceTo(here) < 45f &&
                                                        match.Scores.GetValueOrDefault(e.PeerId) <= match.MyScore && CanSee(e, here + Vector3.Up * 1.2f))
                .OrderBy(e => e.GlobalPosition.DistanceTo(here)).FirstOrDefault();
            if (watcher != null)
            {
                coverTimer -= dt;
                if (cover == null || coverTimer <= 0)
                {
                    coverTimer = 0.5f;
                    cover = FindCover(player, watcher);
                }
                if (cover != null && Flat(cover.Value - here).Length() > 0.8f)
                {
                    Face(player, cover.Value + Vector3.Up * 1.4f, dt, snap: false);
                    MoveToward(player, Flat(cover.Value - here));
                    intent.Sprint = true;
                    Status = $"protecting {match.MyScore}: hiding from {watcher.PeerId}";
                    Unstick(player, dt);
                    return;
                }
            }
        }
        cover = null;

        retarget -= dt;
        if (retarget <= 0 || !IsValid(target))
        {
            retarget = RetargetInterval;
            var chosen = Choose(match, ammo);
            if (chosen != target)
            {
                blocked = 0;
                vantage = null;
                target = chosen;
            }
        }
        if (target == null)
        {
            Status = "no target";
            return;
        }

        switch (target)
        {
            case AmmoPickup pickup:
                Status = $"fetching ammo {pickup.GlobalPosition.DistanceTo(here):0.0} m away";
                Face(player, pickup.GlobalPosition + Vector3.Up * 0.4f, dt, snap: false);
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
        Unstick(player, dt);
    }

    // ------------------------------------------------------------------ Choosing

    /// <summary>Holding a bank worth keeping: leading with a few, or holding several late in the match.</summary>
    private static bool Protecting(Match match)
    {
        var mine = match.MyScore;
        var best = match.Scores.Where(s => s.Key != match.Player.PeerId).Select(s => s.Value).DefaultIfEmpty(0).Max();
        return mine >= 4 && (mine > best || match.TimeLeft < 40f);
    }

    private void Tap()
    {
        if (tapCooldown <= 0)
        {
            intent.FirePressed = true;
            tapCooldown = 0.16f;
        }
    }

    private Node3D? Choose(Match match, AmmoState ammo)
    {
        var player = match.Player;
        var here = player.GlobalPosition;
        var tuning = Tuning.Current;
        foreach (var key in blacklist.Keys.Where(k => blacklist[k] < clock).ToList())
        {
            blacklist.Remove(key);
        }

        // Ammo: go out of the way for it when low; grab a can on the way when it is close.
        var rounds = ammo.Magazine + ammo.Reserve;
        var pickup = match.Pickups.Where(p => p.Available).OrderBy(p => p.GlobalPosition.DistanceTo(here)).FirstOrDefault();
        if (pickup != null && ammo.Reserve < tuning.MaxReserve &&
            (rounds < AmmoLow || rounds < AmmoLow * 3 && pickup.GlobalPosition.DistanceTo(here) < 12f))
        {
            return pickup;
        }

        Node3D? best = null;
        var bestScore = float.MinValue;
        // Players worth shooting: in sight, in range, with tanks to spill.
        foreach (var enemy in match.RemoteBodies)
        {
            var banked = match.Scores.GetValueOrDefault(enemy.PeerId);
            var distance = enemy.GlobalPosition.DistanceTo(here);
            // A fight costs ammo and invites return fire: worth it for a real bank, for a cheap close one, or
            // against whoever just shot this bot.
            var avenging = enemy.PeerId == match.LastHitBy && NetTransport.Now - match.LastHitTime < 3;
            var worth = protecting ? banked > match.MyScore : banked >= 3 || banked >= 1 && distance < 18f || avenging && banked > 0;
            if (!worth || distance > PlayerRange || !CanSee(player, Aim(enemy)))
            {
                continue;
            }
            // Worth it against anyone holding more; against smaller banks only when this bot has little to lose.
            var score = 60f + banked * 6f - distance - (match.MyScore > banked + 2 ? 45f : 0f);
            if (score > bestScore)
            {
                bestScore = score;
                best = enemy;
            }
        }

        var tanks = match.LiveTanks.Where(t => !t.Invulnerable || t.GlobalPosition.DistanceTo(here) < 30f).ToList();
        foreach (var tank in tanks)
        {
            if (blacklist.ContainsKey(tank.GetInstanceId()))
            {
                continue;
            }
            var at = tank.GlobalPosition;
            var distance = at.DistanceTo(here);
            var pile = tanks.Count(t => t.GlobalPosition.DistanceTo(at) < tuning.ChainRadius);
            if (protecting && match.RemoteBodies.Any(e => e.GlobalPosition.DistanceTo(at) < 25f))
            {
                continue;
            }
            var score = (tank.State == PropaneTank.TankState.Venting ? 25f : 10f) + Mathf.Min(pile, 10) * 6f - distance * 0.7f;
            if (distance < 20f)
            {
                // Close tanks are quick points, piles or not.
                score += 12f;
            }
            // A pile with a rival beside it: setting it off throws them, spilling five.
            foreach (var enemy in match.RemoteBodies)
            {
                if (!enemy.IsRagdolled && match.Scores.GetValueOrDefault(enemy.PeerId) > 0 &&
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

    // ------------------------------------------------------------------ Acting

    private void Fight(Match match, PlayerCharacter player, PlayerCharacter enemy, float dt)
    {
        var distance = enemy.GlobalPosition.DistanceTo(player.GlobalPosition);
        if (enemy != fightTarget)
        {
            fightTarget = enemy;
            fightTime = 0;
        }
        fightTime += dt;
        // Aim like a person: a moment to react, then an error that wanders and tightens while tracking.
        var settle = Mathf.Clamp(fightTime / 1.2f, 0, 1);
        var error = Mathf.Lerp(1.1f, 0.22f, Skill) * Mathf.Lerp(1.6f, 0.55f, settle);
        var t = clock;
        var wobble = new Vector3(Mathf.Sin(t * 1.7f + 0.3f), 0.6f * Mathf.Sin(t * 2.3f + 1.1f), Mathf.Cos(t * 1.9f + 2.2f)) * error;
        var aimPoint = Aim(enemy) + wobble;
        Face(player, aimPoint, dt, snap: false);
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

        var camera = player.CameraRig.Camera;
        var off = Mathf.RadToDeg((-camera.GlobalBasis.Z).AngleTo(aimPoint - camera.GlobalPosition));
        var reacted = fightTime > Mathf.Lerp(0.5f, 0.2f, Skill);
        var firing = reacted && off < 2f && CanSee(player, Aim(enemy));
        // Short bursts, letting the aim settle in between.
        burstClock += dt;
        var inBurst = Mathf.PosMod(burstClock, 0.65f) < 0.35f;
        if (firing && inBurst)
        {
            intent.FireHeld = true;
            intent.FirePressed = true;
        }
        Status = $"fighting {enemy.PeerId} at {distance:0.0} m, error {error:0.00} m, firing {firing}";
    }

    private void Demolish(Match match, PlayerCharacter player, PropaneTank tank, float dt)
    {
        var tuning = Tuning.Current;
        var here = player.GlobalPosition;
        var center = tank.GlobalTransform * PropaneTank.LocalCenter;
        var distance = Flat(center - here).Length();
        // Setting a tank off needs distance: past its blast, and past the blasts of the pile it sets off. Both shots
        // come from there, in one burst: the first punctures, the next sets it (and its pile) off before it moves.
        var spread = match.LiveTanks.Where(t => t.GlobalPosition.DistanceTo(tank.GlobalPosition) < tuning.ChainRadius)
            .Select(t => t.GlobalPosition.DistanceTo(tank.GlobalPosition)).DefaultIfEmpty(0).Max();
        var safe = tuning.RagdollRadius + 1.2f + spread;
        var near = safe;
        var far = Mathf.Max(near + 8f, 18f);
        var clear = distance >= near && distance <= far + 4f && CanSee(player, center);

        if (clear && blocked < 1f)
        {
            // In a good spot: stand, shoulder the rifle and fire when the crosshair is on it.
            Face(player, center, dt, snap: false);
            intent.Aim = true;
            vantage = null;
            Status = $"shooting {tank.State} tank at {distance:0.0} m (keeping {near:0.0})";
            if (tank.Invulnerable)
            {
                return;
            }
            if (OnTarget(player, tank))
            {
                // Single taps: one punctures, the next sets it off. Ammo is scarce.
                Tap();
                blocked = 0;
            }
            else
            {
                blocked += dt;
            }
            return;
        }

        // Somewhere better: a spot at the right distance with a clear view of it, that can be walked to.
        vantageTimer -= dt;
        if (vantage == null || vantageTimer <= 0)
        {
            vantageTimer = 0.6f;
            vantage = FindVantage(player, center, near, far);
            blocked = 0;
            if (vantage == null)
            {
                blacklist[tank.GetInstanceId()] = clock + 10f;
                retarget = 0;
                Status = "no vantage point";
                return;
            }
        }
        var to = Flat(vantage.Value - here);
        if (to.Length() < 0.6f)
        {
            // Arrived but still no shot (the tank moved): look again.
            vantageTimer = 0;
            Face(player, center, dt, snap: false);
            return;
        }
        if (distance < 30f && to.Length() < 12f)
        {
            // Close: walk there keeping the tank in view, ready to shoot.
            Face(player, center, dt, snap: false);
            MoveToward(player, to);
        }
        else
        {
            Face(player, vantage.Value + Vector3.Up * 1.4f, dt, snap: false);
            MoveToward(player, to);
            intent.Sprint = to.Length() > 8f;
        }
        Status = $"moving to a vantage {to.Length():0.0} m away for a {tank.State} tank at {distance:0.0} m (keeping {near:0.0})";
    }

    /// <summary>
    /// The nearest open spot between <paramref name="near"/> and <paramref name="far"/> from a target with a clear
    /// view of it, on the ground (not a roof), preferring spots this bot can walk to in a straight line.
    /// </summary>
    private Vector3? FindVantage(PlayerCharacter player, Vector3 target, float near, float far)
    {
        var space = player.GetWorld3D().DirectSpaceState;
        var here = player.GlobalPosition;
        Vector3? best = null;
        var bestCost = float.MaxValue;
        var turn = rng.RandfRange(0, Mathf.Tau);
        foreach (var radius in new[] { near + 1f, (near + far) / 2f, far })
        {
            for (var i = 0; i < 16; i++)
            {
                var angle = turn + i * Mathf.Tau / 16f;
                var p = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(p.X, 4f, p.Z), new Vector3(p.X, -1f, p.Z),
                    Layers.World | Layers.Props | Layers.Floor));
                if (ground.Count == 0 || ground["position"].AsVector3().Y > 0.6f || ground["normal"].AsVector3().Y < 0.8f)
                {
                    continue;
                }
                var spot = ground["position"].AsVector3();
                var eye = spot + Vector3.Up * 1.6f;
                if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, target, Layers.World | Layers.Props)).Count > 0)
                {
                    continue;
                }
                var path = space.IntersectRay(PhysicsRayQueryParameters3D.Create(here + Vector3.Up * 0.6f, spot + Vector3.Up * 0.6f, Layers.World | Layers.Props));
                var cost = here.DistanceTo(spot) + (path.Count > 0 ? 40f : 0f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = spot;
                }
            }
        }
        return best;
    }

    /// <summary>The nearest spot within a short run that the shooter cannot see, if there is one.</summary>
    private Vector3? FindCover(PlayerCharacter player, PlayerCharacter shooter)
    {
        var space = player.GetWorld3D().DirectSpaceState;
        var here = player.GlobalPosition;
        var eye = shooter.GlobalPosition + Vector3.Up * 1.6f;
        Vector3? best = null;
        var bestCost = float.MaxValue;
        foreach (var radius in new[] { 3f, 6f, 10f, 14f })
        {
            for (var i = 0; i < 12; i++)
            {
                var angle = i * Mathf.Tau / 12f;
                var p = here + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(p.X, 4f, p.Z), new Vector3(p.X, -1f, p.Z),
                    Layers.World | Layers.Props | Layers.Floor));
                if (ground.Count == 0 || ground["position"].AsVector3().Y > 0.6f)
                {
                    continue;
                }
                var spot = ground["position"].AsVector3();
                if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, spot + Vector3.Up * 1.2f, Layers.World)).Count == 0)
                {
                    continue;
                }
                if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(here + Vector3.Up * 0.6f, spot + Vector3.Up * 0.6f, Layers.World | Layers.Props)).Count > 0)
                {
                    continue;
                }
                var cost = radius + spot.DistanceTo(eye) * -0.05f;
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

    /// <summary>Walks along a world direction whatever way the view faces.</summary>
    private void MoveToward(PlayerCharacter player, Vector3 direction)
    {
        var yawRad = Mathf.DegToRad(player.CameraRig.Yaw);
        var forward = new Vector3(-Mathf.Sin(yawRad), 0, -Mathf.Cos(yawRad));
        var right = new Vector3(Mathf.Cos(yawRad), 0, -Mathf.Sin(yawRad));
        if (direction.LengthSquared() < 0.0001f)
        {
            return;
        }
        var d = new Vector3(direction.X, 0, direction.Z).Normalized();
        intent.Move = new Vector2(d.Dot(right), d.Dot(forward));
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Turns the view toward a point: quickly but not instantly, unless running for cover.</summary>
    private void Face(PlayerCharacter player, Vector3 point, float dt, bool snap)
    {
        var camera = player.CameraRig.Camera;
        var view = point - camera.GlobalPosition;
        if (view.LengthSquared() < 0.0001f)
        {
            return;
        }
        var wantYaw = Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z));
        var wantPitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1)));
        if (!hasView || snap)
        {
            yaw = player.CameraRig.Yaw;
            pitch = player.CameraRig.Pitch;
            hasView = true;
        }
        var k = snap ? 1f : 1f - Mathf.Exp(-TurnSharpness * Mathf.Lerp(0.35f, 1f, Skill) * dt);
        yaw += Mathf.Wrap(wantYaw - yaw, -180f, 180f) * k;
        pitch = Mathf.Lerp(pitch, wantPitch, k);
        player.CameraRig.SetOrientation(yaw, pitch);
    }

    private static Vector3 Aim(PlayerCharacter enemy) => enemy.IsRagdolled ? enemy.PelvisPosition : enemy.GlobalPosition + Vector3.Up * 1.15f;

    /// <summary>The crosshair is on the target, with nothing in the way.</summary>
    private static bool OnTarget(PlayerCharacter player, Node3D target)
    {
        var camera = player.CameraRig.Camera;
        var forward = -camera.GlobalBasis.Z;
        var start = camera.GlobalPosition + forward * 2.5f;
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(start, start + forward * 80f, Layers.ShotMask));
        var collider = hit.Count > 0 ? hit["collider"].As<GodotObject>() : null;
        return collider == target || target is PlayerCharacter pc && PlayerCharacter.Owning(collider) == pc;
    }

    private static bool CanSee(PlayerCharacter player, Vector3 point)
    {
        var eye = player.GlobalPosition + Vector3.Up * 1.6f;
        return player.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, point, Layers.World | Layers.Props)).Count == 0;
    }

    private static bool IsValid(Node3D? node) => node != null && GodotObject.IsInstanceValid(node) &&
                                                 node is not PropaneTank { State: PropaneTank.TankState.Exploded } &&
                                                 node is not AmmoPickup { Available: false };

    /// <summary>Jumps and sidesteps when running into something.</summary>
    private void Unstick(PlayerCharacter player, float dt)
    {
        var moved = player.GlobalPosition.DistanceTo(lastPosition);
        lastPosition = player.GlobalPosition;
        var trying = intent.Move.LengthSquared() > 0.25f;
        stuck = trying && moved < 0.02f ? stuck + dt : 0;
        if (stuck > 0.4f)
        {
            intent.JumpPressed = true;
            intent.Move = new Vector2(strafeSide, 0.4f);
            if (stuck > 2.5f)
            {
                stuck = 0;
                strafeSide = -strafeSide;
                if (target is PropaneTank tank)
                {
                    blacklist[tank.GetInstanceId()] = clock + 8f;
                }
                retarget = 0;
            }
        }
    }
}
