using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.ReleaseSmoke;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Allocate production instance uniforms beyond the old 4096-mesh ceiling, then retire and repeat.</summary>
public partial class ShaderCapacityTests : Node3D
{
    private const int Instances = 6000;
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        using var mesh = new BoxMesh { Size = Vector3.One * .8f };
        using var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/world_lit.gdshader") };
        material.SetShaderParameter("tintable", true);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-65, -30, 0) });
        var camera = new Camera3D { Position = new Vector3(0, 100, 50), Current = true };
        AddChild(camera);
        camera.LookAt(Vector3.Zero);
        var capture = new CaptureDirector();
        AddChild(capture);
        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var root = new Node3D();
                AddChild(root);
                for (var i = 0; i < Instances; i++)
                {
                    var instance = new MeshInstance3D
                    {
                        Mesh = mesh,
                        MaterialOverride = material,
                        Position = new Vector3(i % 100 - 49.5f, 0, i / 100 - 29.5f),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                    };
                    root.AddChild(instance);
                    instance.SetInstanceShaderParameter("instance_tint", Color.FromHsv(i / (float)Instances, .6f, .8f));
                }
                await Frames(12).ConfigureAwait(true);
                Assert(root.GetChildCount() == Instances, "shader pressure fixture did not allocate every mesh");
                Assert(ProjectSettings.GetSetting("rendering/limits/global_shader_variables/buffer_size").AsInt32() / 16 >= Instances,
                    "instance uniform budget cannot hold the tested capacity pressure");
                if (cycle == 0) capture.Screenshot("shader_capacity");
                root.Free();
                await Frames(3).ConfigureAwait(true);
            }
            GD.Print($"[shader capacity] PASS {Instances} production-material instances, two allocation/retirement cycles");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[shader capacity] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
