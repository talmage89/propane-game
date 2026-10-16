using Godot;
using Propane.Net;

namespace Propane.UI;

/// <summary>Shared presentation primitives; callers retain screen and command ownership.</summary>
internal static class MenuContent
{
    public static void AddHostingNote(VBoxContainer layout, string hostingNote)
    {
        if (hostingNote.Length > 0)
        {
            layout.AddChild(Wrap(UiStyle.Text(hostingNote, 15, UiStyle.SoftInk, HorizontalAlignment.Center)));
        }
    }

    public static void AddDownload(VBoxContainer layout, string text, string release)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var download = UiStyle.Button(text);
        download.Pressed += () => OS.ShellOpen(BuildInfo.DownloadUrl(release, OS.GetName()));
        row.AddChild(download);
        layout.AddChild(row);
    }

    public static void AddMessage(VBoxContainer layout, string message)
    {
        var status = UiStyle.Text(message, 16, new Color(0.75f, 0.2f, 0.12f), HorizontalAlignment.Center);
        Wrap(status);
        status.Visible = message.Length > 0;
        layout.AddChild(status);
    }

    public static Label Wrap(Label label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(480, 0);
        return label;
    }
}
