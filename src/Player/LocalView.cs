using Godot;
using Propane.Core;
using Propane.UI;

namespace Propane.Player;

/// <summary>Process-local camera, HUD and device assignment attached to a simulated actor.</summary>
public partial class LocalView : Node
{
    public required PlayerCharacter Actor { get; init; }
    public DeviceInput? Input { get; init; }
    public CameraRig CameraRig { get; private set; } = null!;
    public Hud Hud { get; private set; } = null!;
    internal bool Initialized { get; private set; }

    public override void _Ready()
    {
        CameraRig = new CameraRig { World = Actor.World, Aim = Actor.Aim, Name = "CameraRig" };
        Actor.World.AddView(this, CameraRig, followsPause: true);
        Hud = new Hud { Name = "Hud" };
        AddChild(Hud);
        Actor.View ??= this;
        if (Input != null) Actor.Runtime.IntentSource = Input;
        Godot.Input.JoyConnectionChanged += OnJoyConnectionChanged;
        Initialized = true;
    }

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        if (!connected) Input?.Disconnect(device);
    }
    // Explicit assignments observe releases even when a menu consumes them. Primary keeps the established path.
    public override void _Input(InputEvent @event)
    {
        if (Input is { Assignment.IsPrimary: false }) Input.Accumulate(@event);
    }
    public override void _UnhandledInput(InputEvent @event) { if (Input is { Assignment.IsPrimary: true }) Input.Accumulate(@event); }
    public override void _Notification(int what)
    {
        if (what == NotificationPaused || what == NotificationUnpaused || what == NotificationDisabled || what == NotificationEnabled ||
            what == NotificationApplicationFocusOut || what == NotificationApplicationFocusIn)
        {
            Input?.Reset();
        }
    }

    public override void _ExitTree()
    {
        Godot.Input.JoyConnectionChanged -= OnJoyConnectionChanged;
        if (Actor.View == this) Actor.View = null;
        if (Input != null && ReferenceEquals(Actor.Runtime.IntentSource, Input)) Actor.Runtime.IntentSource = null;
    }

    public static LocalView Attach(PlayerCharacter actor, Node parent, bool devices = false)
    {
        var view = new LocalView { Actor = actor, Input = devices ? new DeviceInput(actor.World.Tuning) : null, Name = "LocalView" };
        actor.World.AddView(parent, view);
        return view;
    }
}
