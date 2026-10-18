using System;
using System.Reflection;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Keep the same assertions across the actor-state move; never used by measured production callbacks.</summary>
internal readonly struct ClientActorAccess(object state)
{
    internal object State => state;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private T Get<T>(string name) => (T)state.GetType().GetProperty(name, Flags)!.GetValue(state)!;
    private void Set<T>(string name, T value) => state.GetType().GetProperty(name, Flags)!.SetValue(state, value);
    public DropPredictions DropPredictions => Get<DropPredictions>(nameof(DropPredictions));
    public uint ActionSequence { get => Get<uint>(nameof(ActionSequence)); set => Set(nameof(ActionSequence), value); }
    public int AllocationSlot { get => Get<int>(nameof(AllocationSlot)); set => Set(nameof(AllocationSlot), value); }
    public int DropCounter { get => Get<int>(nameof(DropCounter)); set => Set(nameof(DropCounter), value); }
    public uint AllocateDropId()
    {
        try { return (uint)state.GetType().GetMethod(nameof(AllocateDropId))!.Invoke(state, null)!; }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
    public static ClientActorAccess For(PlayerCharacter actor)
    {
        var owner = actor.World.ViewRoot;
        var replicas = owner.GetType().GetField("actorReplication", Flags)?.GetValue(owner);
        return new ClientActorAccess(replicas == null ? actor.Runtime : replicas.GetType().GetMethod("Get")!.Invoke(replicas, new object[] { actor.Runtime })!);
    }
    public static ClientActorAccess Standalone(PlayerCharacter actor)
    {
        var type = typeof(Match).Assembly.GetType("Propane.Net.ClientActorReplication");
        return new ClientActorAccess(type == null ? actor.Runtime : Activator.CreateInstance(type, actor)!);
    }
}
