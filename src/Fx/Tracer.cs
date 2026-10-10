using Godot;

namespace Propane.Fx;

/// <summary>A bright streak that races from the muzzle to the impact point, then frees itself.</summary>
public partial class Tracer : MeshInstance3D
{
    private const float Speed = 260f;
    private const float StreakLength = 7f;
    private const float Width = 0.05f;

    private static ShaderMaterial? material;

    private Vector3 from;
    private Vector3 to;
    private float traveled;
    private float total;

    public static Tracer Create(Vector3 start, Vector3 end)
    {
        material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_tracer.gdshader") };
        var tracer = new Tracer
        {
            Mesh = new QuadMesh { Size = Vector2.One, Material = material },
            CastShadow = ShadowCastingSetting.Off,
            ExtraCullMargin = 400f,
        };
        tracer.from = start;
        tracer.to = end;
        tracer.total = start.DistanceTo(end);
        return tracer;
    }

    public override void _Ready()
    {
        TopLevel = true;
        Update();
    }

    public override void _Process(double delta)
    {
        traveled += Speed * (float)delta;
        if (traveled - StreakLength > total)
        {
            QueueFree();
            return;
        }
        Update();
    }

    private void Update()
    {
        var dir = (to - from).Normalized();
        var head = Mathf.Min(traveled + 0.8f, total);
        var tail = Mathf.Clamp(traveled - StreakLength, 0, total);
        var length = Mathf.Max(head - tail, 0.01f);
        var center = from + dir * (tail + head) * 0.5f;
        // Ribbon along dir, turned toward the camera in the shader; X carries the width.
        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.98f ? Vector3.Right : Vector3.Up;
        var basis = Basis.LookingAt(dir, up);
        GlobalTransform = new Transform3D(new Basis(basis.Column0 * Width, -basis.Column2 * length, basis.Column1), center);
    }
}
