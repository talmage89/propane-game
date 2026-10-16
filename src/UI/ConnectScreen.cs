using System;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Connection form composition and its field callbacks.</summary>
internal sealed class ConnectScreen
{
    private readonly NetClient net;
    private readonly MenuCommands commands;
    private readonly MenuRefreshState refresh;
    private readonly Action<string> host;
    private readonly Action back;

    public ConnectScreen(NetClient net, MenuCommands commands, MenuRefreshState refresh, Action<string> host, Action back)
    {
        this.net = net;
        this.commands = commands;
        this.refresh = refresh;
        this.host = host;
        this.back = back;
    }

    public void Build(VBoxContainer layout, bool connecting, string message)
    {
        layout.AddChild(UiStyle.Title("MULTIPLAYER", 40));
        layout.AddChild(MenuContent.Wrap(UiStyle.Text("Tank bank: blow up tanks to bank them. Get shot and they spill out for anyone to take.", 16,
            UiStyle.SoftInk, HorizontalAlignment.Center)));
        layout.AddChild(new HSeparator());

        layout.AddChild(UiStyle.Text("Your name", 16, UiStyle.SoftInk));
        var name = refresh.NameField = UiStyle.Field(MenuProfile.DefaultName(), "Name");
        name.MaxLength = Protocol.MaxNameLength;
        layout.AddChild(name);

        layout.AddChild(UiStyle.Text("Server", 16, UiStyle.SoftInk));
        var server = refresh.ServerField = UiStyle.Field(PlayerSettings.Server, $"address or address:port (port {Protocol.DefaultPort} if left out)");
        layout.AddChild(server);
        server.TextSubmitted += _ => commands.Connect(server.Text, name.Text);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var connect = UiStyle.Button(connecting ? "Connecting..." : "Connect");
        connect.Disabled = connecting;
        connect.Pressed += () => commands.Connect(server.Text, name.Text);
        var host = UiStyle.Button("Host on this computer");
        host.Disabled = connecting;
        host.Pressed += () =>
        {
            this.host(name.Text);
        };
        var back = UiStyle.Button("Back");
        back.Pressed += () =>
        {
            MenuProfile.SaveName(name.Text);
            commands.Disconnect();
            this.back();
        };
        buttons.AddChild(connect);
        buttons.AddChild(host);
        buttons.AddChild(back);
        layout.AddChild(buttons);
        MenuContent.AddMessage(layout, message);
        // Turned away by a newer server: offer the download that plays there.
        if (message.Length > 0 && net.State == NetClient.Status.Offline && net.RejectedBy is { } needed && BuildInfo.IsNewer(needed))
        {
            MenuContent.AddDownload(layout, $"Download Propane {BuildInfo.Series(needed)}", needed);
        }
    }
}
