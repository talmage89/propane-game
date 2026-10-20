using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Session;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane;

public partial class Game
{
    private sealed class SandboxSource(Game game) : ISessionWorldSource
    {
        public void Build(SessionContext session)
        {
            game.environment = new VoidEnvironment { Name = "Void" };
            session.World.Root.AddChild(game.environment);
            game.effects = session.World.EffectsRoot;
            game.seeds.Randomize();
            session.World.Reveal.Radius = 0;
            game.LoadSuburb();
        }
    }

    private sealed class SandboxMode(Game game) : ISessionMode
    {
        public void Configure(SessionContext session) => game.ProcessMode = ProcessModeEnum.Always;
        public void CreateActors(SessionContext session)
        {
            game.player = new PlayerCharacter { World = session.World, Name = "Player" };
            game.player.SetHat(game.HatId);
            session.AddActor(game.player);
            var plan = game.current!.Plan;
            game.player.Teleport(PlanCoordinates.ToWorld(plan.Spawn, .05f), plan.SpawnYaw + Mathf.Pi);
        }
        public void CreateViews(SessionContext session)
        {
            var view = session.AddLocalView(game.player.Runtime, game, new DeviceInput(session.World.Tuning));
            game.localView = view;
            view.TreeExiting += () => game.localView = null;
            game.environment.Follow = view.CameraRig.Camera;
            game.hud = view.Hud;
            game.hud.SetTanks(game.current!.TanksRemaining, animate: false);
            game.hitstop = session.AddView(game, new Hitstop { World = session.World, Name = "Hitstop", Focus = game.player });
            game.tuningPanel = session.AddView(game, new TuningPanel { Target = session.World.Tuning, Name = "Tuning" });
            game.pauseMenu = new PauseMenu { Name = "Pause" };
            game.pauseMenu.ResumeRequested += game.Resume;
            if (game.ExitRequested != null)
                game.pauseMenu.MainMenuRequested += () => session.World.Defer(() => game.ExitRequested?.Invoke(), whilePaused: true);
            session.AddView(game, game.pauseMenu);
        }
        public void Ready(SessionContext session)
        {
            game.BeginMaterialize();
            Warmup.Run(session.World, session.World.Root, new Vector3(0, -400, 0));
            CaptureMouse(true);
            if (game.GetTree().CurrentScene == game && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed)
                session.World.Defer(() => DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized));
        }
    }
}
