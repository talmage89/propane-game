using Godot;
using Propane.Core;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Session;

/// <summary>The construct's floor and sky, without generated content.</summary>
public sealed class VoidWorldSource : ISessionWorldSource
{
    public VoidEnvironment Environment { get; private set; } = null!;
    public void Build(SessionContext session)
    {
        Environment = new VoidEnvironment { Name = "Void" };
        session.World.Root.AddChild(Environment);
    }
}

/// <summary>A finite generated suburb. A streamed source can later own several retiring content roots.</summary>
public sealed class SuburbWorldSource(SuburbSettings settings, int seed) : ISessionWorldSource
{
    private readonly VoidWorldSource voidSource = new();
    public VoidEnvironment Environment => voidSource.Environment;
    public Suburb Suburb { get; private set; } = null!;
    public void Build(SessionContext session)
    {
        voidSource.Build(session);
        var plan = SuburbPlans.Generate(settings, seed);
        Suburb = Suburb.Build(session.World, plan, settings);
        session.World.Root.AddChild(Suburb);
        session.World.SetContentRoot(Suburb.DynamicRoot);
    }
}
