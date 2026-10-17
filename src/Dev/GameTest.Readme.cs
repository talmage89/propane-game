using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>
/// The README media: footage of a chain reaction through one of the suburb's tank piles, and a scout that finds the
/// camera positions worth filming it from. The commands that made the shipped images are in docs/development.md.
/// <para>
/// Both scenarios share <c>--tune.FixedSeed</c> (the suburb), <c>--pile</c> (the pile by rank: 0 is the biggest),
/// <c>--angle</c> (the compass degrees around the pile's center where the player stands), <c>--distance</c> (meters
/// from the pile's center to the player, default 6) and <c>--hud=0</c> (hide the HUD). A free camera, which hides the HUD,
/// is given as <c>--cam=back,side,height,ahead,fov[,look]</c>: meters behind the player along the line to the pile, to the
/// player's right and above the ground, how far ahead of the player it looks, the vertical field of view in degrees and
/// the height of the point it looks at (default 0.9). Without <c>--cam</c> the footage uses the player's own camera.
/// The scout always uses a free camera and takes its field of view from <c>--fov</c>.
/// </para>
/// </summary>
public partial class GameTest
{
    /// <summary>
    /// <c>--scenario=readme</c>: the player stands at <c>--angle</c> and <c>--distance</c> from the pile, punctures one of
    /// its tanks, punctures and then detonates a second into a chain, and a numbered frame sequence is saved:
    /// <c>--shot-start</c> (seconds, default 2.1), <c>--shot-fps</c> (default 30) and <c>--frames</c> (default 1).
    /// Pair it with <c>--effects-seed</c> to film the same chain again.
    /// </summary>
    private void ScriptReadme(CaptureDirector director)
    {
        var shotStart = DevArgs.GetFloat("shot-start", 2.1f);
        var shotFps = DevArgs.GetFloat("shot-fps", 30f);
        var frames = (int)DevArgs.GetFloat("frames", 1f);
        PropaneTank? second = null;
        // The director runs its timeline in list order, so the frames and the script go in together, sorted by time.
        var script = new List<(float Time, System.Action Action)>();
        script.Add((2.0f, () =>
        {
            HideReadmeHud();
            var pile = ReadmePile(out var center);
            var angle = Mathf.DegToRad(DevArgs.GetFloat("angle", 0f));
            var stand = center with { Y = 0.2f } + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * ReadmeDistance();
            // Fire at the pile's tank nearest the player, from a spot where nothing stands between them.
            target = pile.OrderBy(t => t.GlobalPosition.DistanceTo(stand)).FirstOrDefault(t => TryStandAt(t, stand, angle));
            if (target == null)
            {
                GD.PushError("[readme] FAIL no tank of the pile has a clear shot from that spot");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            second = pile.Where(t => t != target).OrderBy(t => t.GlobalPosition.DistanceTo(target.GlobalPosition)).FirstOrDefault();
            input.Hold(T(0), T(7f), new Player.PlayerIntent { Aim = true });
            if (DevArgs.Get("cam") != null)
            {
                AddReadmeCamera(stand, center);
            }
        }
        ));
        script.Add((2.6f, () => input.Fire(T(0))));
        script.Add((3.0f, () => target = second));
        script.Add((3.3f, () => input.Fire(T(0))));
        script.Add((4.6f, () => input.Fire(T(0))));
        // A venting tank can jink out of the way of that shot; the footage needs the blast either way.
        script.Add((4.65f, () =>
        {
            if (second != null && IsInstanceValid(second) && second.State != PropaneTank.TankState.Exploded)
            {
                second.Detonate();
            }
        }
        ));
        for (var i = 0; i < frames; i++)
        {
            var name = $"frame_{i:0000}";
            script.Add((shotStart + i / shotFps, () => director.Screenshot(name)));
        }
        foreach (var (time, action) in script.OrderBy(entry => entry.Time))
        {
            director.At(time, action);
        }
    }

    /// <summary>
    /// <c>--scenario=readme-scout</c>: tries random stand angles and free-camera poses around the pile, keeps those that
    /// see both the pile and the player with the pile on the right of the frame, and saves one still of each as
    /// <c>c000_...png</c>, printing the <c>--angle</c> and <c>--cam</c> values that reproduce it. <c>--count</c> sets how
    /// many (default 30), <c>--scout-seed</c> the random stream, <c>--fov</c> the field of view, <c>--min-sun</c> how far
    /// toward the sun the camera must look (-1 any, 1 straight into it) and <c>--max-dist</c> how far it may be from the pile.
    /// </summary>
    private void ScriptReadmeScout(CaptureDirector director)
    {
        var count = (int)DevArgs.GetFloat("count", 30f);
        var fov = DevArgs.GetFloat("fov", 55f);
        var minSun = DevArgs.GetFloat("min-sun", -1f);
        var maxDistance = DevArgs.GetFloat("max-dist", 12f);
        var rng = new System.Random((int)DevArgs.GetFloat("scout-seed", 1f));
        director.At(2.0f, () =>
        {
            HideReadmeHud();
            var pile = ReadmePile(out var center);
            var space = game.Player.GetWorld3D().DirectSpaceState;
            var towardSun = game.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>().First().GlobalBasis.Z;
            var camera = new Camera3D { Fov = fov, Near = 0.05f, Far = 3000f };
            AddChild(camera);
            camera.Current = true;
            var size = GetViewport().GetVisibleRect().Size;
            var found = new List<(Vector3 Stand, float Yaw, Vector3 Cam, Vector3 Look, string Args)>();
            for (var tries = 0; tries < 4000 && found.Count < count; tries++)
            {
                var angle = (float)(rng.NextDouble() * Mathf.Tau);
                var stand = center with { Y = 0.2f } + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * ReadmeDistance();
                var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(stand + Vector3.Up * 3f, stand + Vector3.Down, Layers.World | Layers.Props));
                if (ground.Count == 0 || ground["position"].AsVector3().Y > 0.2f || !ReadmeClear(space, stand + Vector3.Up * 0.9f, 0.45f) ||
                    !pile.OrderBy(t => t.GlobalPosition.DistanceTo(stand)).Any(t => TryStandAt(t, stand, angle)))
                {
                    continue;
                }
                var forward = ((center - stand) with { Y = 0 }).Normalized();
                var back = -1f + (float)rng.NextDouble() * 8f;
                var side = (rng.Next(2) == 0 ? -1f : 1f) * (1.5f + (float)rng.NextDouble() * 6.5f);
                var height = 1f + (float)rng.NextDouble() * 2.8f;
                var ahead = ReadmeDistance() / 2f;
                var cam = ReadmeCameraPose(stand, forward, back, side, height, ahead);
                if (cam.Position.DistanceTo(center) > maxDistance || !ReadmeClear(space, cam.Position, 0.5f) ||
                    !ReadmeSeesTank(space, cam.Position, center, pile) || space.IntersectRay(PhysicsRayQueryParameters3D.Create(cam.Position, stand + Vector3.Up * 1.3f, Layers.World | Layers.Props)).Count > 0)
                {
                    continue;
                }
                camera.GlobalTransform = cam.Transform;
                var onPile = camera.UnprojectPosition(center) / size;
                var onPlayer = camera.UnprojectPosition(stand + Vector3.Up) / size;
                var sun = (-camera.GlobalBasis.Z).Dot(towardSun);
                if (camera.IsPositionBehind(center) || onPile.X < 0.4f || onPile.X > 0.82f || onPile.Y < 0.3f || onPile.Y > 0.75f ||
                    onPlayer.X < 0.08f || onPlayer.X > 0.5f || onPlayer.Y < 0.3f || sun < minSun)
                {
                    continue;
                }
                var yaw = Mathf.Atan2(-forward.X, -forward.Z);
                var args = string.Create(CultureInfo.InvariantCulture, $"--angle={Mathf.RadToDeg(angle):0} --cam={back:0.0},{side:0.0},{height:0.0},{ahead:0.0},{fov:0}");
                found.Add((stand, yaw, cam.Position, cam.Target, args));
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"[scout] c{found.Count - 1:000} {args} sun {sun:0.0}"));
            }
            GD.Print($"[scout] {found.Count} candidates of {pile.Count}-tank pile");
            for (var i = 0; i < found.Count; i++)
            {
                var c = found[i];
                var name = $"c{i:000}_{c.Args.Replace("--", "").Replace(" ", "_").Replace("=", "").Replace(",", "_")}";
                director.At(2.1f + 0.1f * i, () =>
                {
                    game.Player.Teleport(c.Stand, c.Yaw);
                    camera.LookAtFromPosition(c.Cam, c.Look);
                });
                director.At(2.2f + 0.1f * i, () => director.Screenshot(name));
            }
            director.QuitAfter = 2.5f + 0.1f * found.Count;
        });
    }

    private float ReadmeDistance() => DevArgs.GetFloat("distance", 6f);

    /// <summary>The tanks of the pile ranked <c>--pile</c> by size, and the middle of them.</summary>
    private List<PropaneTank> ReadmePile(out Vector3 center)
    {
        var placed = game.Current!.Plan.Tanks.Zip(game.Current.MapTanks).GroupBy(t => t.First.Pile)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var rank = Mathf.Min((int)DevArgs.GetFloat("pile", 0f), placed.Count - 1);
        var pile = placed[rank].Select(t => t.Second).ToList();
        center = pile.Aggregate(Vector3.Zero, (sum, tank) => sum + tank.GlobalPosition) / pile.Count + Vector3.Up * 0.4f;
        GD.Print($"[readme] pile {rank} of {placed.Count}: {pile.Count} tanks, {placed[rank].First().First.Formation}, sizes {string.Join(",", placed.Select(g => g.Count()))}");
        return pile;
    }

    /// <summary>Hides the HUD when <c>--hud=0</c> asks for it, and whenever a free camera is used: the crosshair and tank arrows follow the player's camera.</summary>
    private void HideReadmeHud()
    {
        if (DevArgs.Get("hud") == "0" || DevArgs.Get("cam") != null)
        {
            foreach (var hud in game.FindChildren("*", "CanvasLayer", true, false).OfType<UI.Hud>())
            {
                hud.ShowHud(false);
            }
        }
    }

    /// <summary>Puts a free camera where <c>--cam</c> says, relative to the player's spot, and makes it current.</summary>
    private void AddReadmeCamera(Vector3 stand, Vector3 center)
    {
        var v = DevArgs.Get("cam")!.Split(',').Select(c => float.Parse(c, CultureInfo.InvariantCulture)).ToArray();
        var forward = ((center - stand) with { Y = 0 }).Normalized();
        var pose = ReadmeCameraPose(stand, forward, v[0], v[1], v[2], v[3], v.Length > 5 ? v[5] : 0.9f);
        var camera = new Camera3D { Fov = v[4], Near = 0.05f, Far = 3000f };
        AddChild(camera);
        camera.Current = true;
        camera.GlobalTransform = pose.Transform;
        GD.Print($"[readme] camera at {pose.Position} looking at {pose.Target}");
    }

    private static (Vector3 Position, Vector3 Target, Transform3D Transform) ReadmeCameraPose(Vector3 stand, Vector3 forward, float back, float side, float height, float ahead, float look = 0.9f)
    {
        var position = stand - forward * back + forward.Cross(Vector3.Up) * side + Vector3.Up * height;
        var target = stand + forward * ahead + Vector3.Up * look;
        return (position, target, new Transform3D(Basis.Identity, position).LookingAt(target, Vector3.Up));
    }

    private static bool ReadmeClear(PhysicsDirectSpaceState3D space, Vector3 at, float radius)
    {
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = radius },
            Transform = new Transform3D(Basis.Identity, at),
            CollisionMask = Layers.World | Layers.Props,
        };
        return space.IntersectShape(query, 1).Count == 0;
    }

    /// <summary>True when the first thing the camera sees toward the pile's middle is one of its tanks.</summary>
    private static bool ReadmeSeesTank(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 center, List<PropaneTank> pile)
    {
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, center + (center - from).Normalized(), Layers.Shootable));
        return hit.Count > 0 && hit["collider"].As<GodotObject>() is PropaneTank tank && pile.Contains(tank);
    }
}
