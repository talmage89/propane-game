using Godot;
using Propane.Core;
using Propane.UI;

namespace Propane.Player;

/// <summary>Process-local camera, HUD and device assignment attached to a simulated actor.</summary>
public partial class LocalView : Node
{
    public required PlayerCharacter Actor { get; init; }
    private DeviceInput? input;
    public DeviceInput? Input { get => input; init => input = value; }
    public CameraRig CameraRig { get; private set; } = null!;
    public Hud Hud { get; private set; } = null!;
    private LocalViewDeviceInput? receiver;
    internal bool Initialized { get; private set; }

    public override void _Ready()
    {
        CameraRig = new CameraRig { World = Actor.World, Aim = Actor.Aim, Name = "CameraRig" };
        Actor.World.AddView(this, CameraRig, followsPause: true);
        Hud = new Hud { Name = "Hud" };
        AddChild(Hud);
        Actor.View ??= this;
        AssignDevice(Input);
        Godot.Input.JoyConnectionChanged += OnJoyConnectionChanged;
        Initialized = true;
    }

    /// <summary>Reassign a view's device without replacing its camera, HUD or actor.</summary>
    public void AssignDevice(DeviceInput? device)
    {
        Input?.Reset();
        if (ReferenceEquals(Actor.Runtime.IntentSource, Input)) Actor.Runtime.IntentSource = null;
        input = device;
        receiver?.Free();
        receiver = null;
        if (device == null) return;
        Actor.Runtime.IntentSource = device;
        // Devices arrive at the application viewport, even when this view renders in a SubViewport.
        receiver = new LocalViewDeviceInput { View = this, Name = "ViewDeviceInput" };
        var parent = GetViewport() == Actor.World.ViewRoot.GetViewport() ? this : Actor.World.ViewRoot;
        parent.AddChild(receiver);
    }

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        if (!connected) Input?.Disconnect(device);
    }
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
        // Child endpoints follow this node's native teardown. A child-viewport endpoint lives under the
        // facade; free it when closing this view, or let the exiting facade free its already-exited child.
        if (receiver != null && GodotObject.IsInstanceValid(receiver) && receiver.GetParent() != this && receiver.IsInsideTree())
            receiver.Free();
        receiver = null;
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

/// <summary>A view-owned device endpoint in the application viewport, independent of presentation layout.</summary>
internal partial class LocalViewDeviceInput : Node
{
    public required LocalView View { get; init; }
    // Explicit assignments observe releases even when a menu consumes them. Primary polls aggregate held state.
    public override void _Input(InputEvent @event)
    {
        if (View.Actor.World.IsAlive && View.Input is { Assignment.IsPrimary: false } input) input.Accumulate(@event);
    }
    public override void _UnhandledInput(InputEvent @event)
    {
        if (View.Actor.World.IsAlive && View.Input is { Assignment.IsPrimary: true } input) input.Accumulate(@event);
    }
}
