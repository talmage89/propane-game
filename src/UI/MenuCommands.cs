using System;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Connection and profile commands; the router supplies status presentation.</summary>
internal sealed class MenuCommands
{
    private readonly NetClient net;
    private readonly Action<string> report;
    private readonly Action clearMessage;
    private readonly Action<int> host;

    public MenuCommands(NetClient net, Action<string> report, Action clearMessage, Action<int> host)
    {
        this.net = net;
        this.report = report;
        this.clearMessage = clearMessage;
        this.host = host;
    }

    public void Connect(string address, string name)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            report("Type the server's address.");
            return;
        }
        var host = text;
        var port = Protocol.DefaultPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var parsed))
        {
            host = text[..colon];
            port = parsed;
        }
        PlayerSettings.Server = text;
        MenuProfile.SaveName(name);
        clearMessage();
        net.Connect(host, port, Protocol.CleanName(name), MenuProfile.DefaultAppearance());
    }

    public void Host(int port, string name)
    {
        MenuProfile.SaveName(name);
        host(port);
    }

    public void Disconnect() => net.Disconnect();
    public void CreateLobby() => net.CreateLobby();
    public void JoinLobby(int id) => net.JoinLobby(id);
    public void LeaveLobby() => net.LeaveLobby();
    public void StartMatch() => net.StartMatch();

    public void SetProfile(string name, AppearanceRecord appearance)
    {
        MenuProfile.Save(name, appearance);
        net.SetProfile(name, appearance);
    }
}
