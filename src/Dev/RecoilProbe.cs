using System;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Actual full-auto shots and the final skinned firing frames, with no mouse input.</summary>
public partial class RecoilProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public CaptureDirector Capture { get; init; } = null!;
    private readonly StringBuilder csv = new("time,pitch,yaw,shots,angle,render_angle,ready_angle,muzzle_error\n");
    private Rifle rifle = null!;
    private float time;
    private float initialPitch;
    private float maximumPitch;
    private float lastShotPitch;
    private float expectedRetained;
    private float maxAngle, maxRenderAngle, maxReadyAngle;
    private float maxMuzzleError;
    private int readyFrames;
    private float Yaw => Mathf.RadToDeg(Mathf.Atan2(-Player.Aim.Forward.X, -Player.Aim.Forward.Z));
    private int shots, renderedShots, airShots;
    private ShotReport? pendingShot;

    public static RecoilProbe Attach(PlayerCharacter player, CaptureDirector capture)
    {
        var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("hand_l") >= 0);
        var probe = new RecoilProbe { Player = player, Capture = capture };
        skeleton.AddChild(probe);
        return probe;
    }

    public override void _Ready()
    {
        rifle = Player.GetNode<Rifle>("Rifle");
        initialPitch = maximumPitch = Player.Aim.Pitch;
        Player.Fired += Shot;
    }

    private void Shot(ShotReport shot)
    {
        shots++;
        if (!Player.ResolvedState.Grounded) airShots++;
        pendingShot = shot;
        var angle = Mathf.RadToDeg(rifle.BarrelForward.AngleTo(shot.End - shot.Muzzle));
        maxAngle = Mathf.Max(maxAngle, angle);
        lastShotPitch = Player.Aim.Pitch;
        var tuning = Player.World.Tuning;
        var kick = tuning.RecoilKickDegrees * (Player.ResolvedState.EffectiveAim ? tuning.AimRecoilScale : 1);
        // The logical pitch ceiling is 45 degrees; blocked kick cannot leave a hidden settle budget.
        expectedRetained += Mathf.Max(0, Mathf.Min(kick, 45 - lastShotPitch)) * (1 - tuning.RecoilReturnShare);
        csv.AppendLine(FormattableString.Invariant($"{time:F6},{Player.Aim.Pitch:F6},{Yaw:F6},{shots},{angle:F6},,,"));
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        time += (float)delta;
        maximumPitch = Mathf.Max(maximumPitch, Player.Aim.Pitch);
        var angle = "";
        var muzzleError = "";
        if (pendingShot is ShotReport shot)
        {
            renderedShots++;
            var degrees = Mathf.RadToDeg(rifle.BarrelForward.AngleTo(shot.End - shot.Muzzle));
            maxRenderAngle = Mathf.Max(maxRenderAngle, degrees);
            angle = degrees.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            var error = rifle.MuzzlePosition.DistanceTo(shot.Muzzle);
            maxMuzzleError = Mathf.Max(maxMuzzleError, error);
            muzzleError = error.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            if (shots is 1 or 10 or 20 or 30) CaptureAfterDraw($"burst_{shots:00}");
            pendingShot = null;
        }
        var rig = GetSkeleton().GetNode<RifleRig>("RifleRig");
        var readyAngle = "";
        if (!rig.AlignedShotThisFrame && rig.TrackingBarrelThisFrame)
        {
            readyFrames++;
            var degrees = Mathf.RadToDeg(rifle.BarrelForward.AngleTo(rig.AimTarget - rifle.MuzzlePosition));
            maxReadyAngle = Mathf.Max(maxReadyAngle, degrees);
            readyAngle = degrees.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        }
        csv.AppendLine(FormattableString.Invariant($"{time:F6},{Player.Aim.Pitch:F6},{Yaw:F6},{shots},,{angle},{readyAngle},{muzzleError}"));
    }

    private async void CaptureAfterDraw(string label)
    {
        if (DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Capture.ScreenshotAsync(label);
    }

    public void Finish()
    {
        Player.Fired -= Shot;
        Active = false;
        var retained = Player.Aim.Pitch - initialPitch;
        var failed = shots != Player.World.Tuning.MagazineSize || renderedShots != shots ||
            !float.IsFinite(retained) || !float.IsFinite(maxAngle) || !float.IsFinite(maxRenderAngle) || !float.IsFinite(maxReadyAngle) ||
            !float.IsFinite(maxMuzzleError) || maxMuzzleError > 0.0001f ||
            maxAngle > 0.1f || maxRenderAngle > 0.1f || maxReadyAngle > 0.1f || readyFrames < 30 ||
            expectedRetained <= 0 || Math.Abs(retained - expectedRetained) > 0.01f || maximumPitch <= initialPitch || Player.Ammo!.Magazine != 0 ||
            DevArgs.Get("stance") == "air" && airShots == 0;
        GD.Print(FormattableString.Invariant($"[recoil] {(failed ? "FAIL" : "PASS")} shots={shots} rendered={renderedShots} air-shots={airShots} peak={maximumPitch - initialPitch:F6} retained={retained:F6} last-before-kick={lastShotPitch - initialPitch:F6} shot-angle={maxAngle:F6} render-angle={maxRenderAngle:F6} ready-frames={readyFrames} ready-angle={maxReadyAngle:F6} muzzle-error={maxMuzzleError:F6}m"));
        if (DevArgs.Get("capture-dir") is string directory)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/recoil.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        if (failed) { GD.PushError("[recoil] FAIL climb, ammunition or barrel alignment"); ShutdownGuard.Quit(GetTree(), 1); }
    }
}
