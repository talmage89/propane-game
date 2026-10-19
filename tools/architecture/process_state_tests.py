"""Independent process-state oracle, compiled with the architecture fixtures' plain SDK project."""


def exercise_state(compile_source, inspect, policy, require):
    source = '''
namespace Propane.Core { public class LaunchArgs { public static LaunchArgs Current => new(); } }
namespace Godot.Collections { public class Array<T> { } }
namespace Outside
{
    public class Global
    {
        public static int Field = 1;
        public object Read() => Propane.Core.LaunchArgs.Current;
    }
}
namespace Propane.Session
{
    using Alias = global::Propane.Core.LaunchArgs;
    using static Propane.Core.LaunchArgs;
    public class Ambient
    {
        public object Direct() => Alias.Current;
        public object Imported() => Current;
        public System.Func<object> Closure() => () => Alias.Current;
        public async System.Threading.Tasks.Task<object> Async()
        { await System.Threading.Tasks.Task.Yield(); return Alias.Current; }
    }
    public class State
    {
        public static int Field = 1;
        private static int hidden = 2;
        public static int Auto { get; private set; }
        public static int Explicit { get => hidden; private set => hidden = value; }
        private static int Private { get; set; }
        public static event System.Action? Changed;
        public static void Fire() => Changed?.Invoke();
        public static readonly System.Collections.Generic.List<int> List = new();
        public static readonly System.Collections.Generic.Dictionary<int, int> Map = new();
        public static readonly int[] Array = [];
        public static readonly int[,] Matrix = new int[0, 0];
        public static readonly System.Array ArrayBase = new int[0];
        public static readonly System.ArraySegment<int> Segment = new(new int[0]);
        public static readonly System.Memory<int> Memory = new int[0];
        public static readonly System.ReadOnlyMemory<int> ReadOnlyMemory = new int[0];
        public static System.Collections.Generic.IReadOnlyList<int> Table { get; } = new int[0];
        public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> Weak = new();
        public class Custom : System.Collections.Generic.List<int> { }
        public static readonly Custom Derived = new();
        public static readonly Godot.Collections.Array<int> NativeTable = new();
        [System.Runtime.CompilerServices.CompilerGenerated]
        public class Authored { public static int Value = 1; }
        public class Nested { public static int Value = 1; }
    }
}
'''
    # A private uncalled property still appears in metadata; warnings are errors in this fixture project.
    expected = {
        ('launch-read', 'Propane.Session.Ambient', 'LaunchArgs.Current'): 4,
        ('launch-read', 'Outside.Global', 'LaunchArgs.Current'): 1,
        ('static-field', 'Outside.Global', 'Field'): 1,
        **{('static-field', 'Propane.Session.State', name): 1 for name in ['Field', 'hidden', 'Changed']},
        **{('static-property', 'Propane.Session.State', name): 1 for name in ['Auto', 'Explicit', 'Private']},
        **{('static-collection', 'Propane.Session.State', name): 1 for name in ['List', 'Map', 'Array', 'Matrix', 'ArrayBase', 'Segment', 'Memory', 'ReadOnlyMemory', 'Table', 'Weak', 'Derived', 'NativeTable']},
        ('static-field', 'Propane.Session.State+Nested', 'Value'): 1,
        ('static-field', 'Propane.Session.State+Authored', 'Value'): 1,
    }

    def approvals(items):
        return ''.join('\t'.join((*key, str(count), 'Fixture', 'Deliberate process-state witness')) + '\n'
                       for key, count in sorted(items.items()))

    compile_source(source)
    code, output = inspect('state-candidate')
    actual = {tuple(line.split('\t')[:3]): int(line.split('\t')[3]) for line in output.splitlines()}
    require(code == 0 and actual == expected, 'process-state candidate differs from independent oracle', output)
    for key in expected:
        policy.write_text(approvals({other: count for other, count in expected.items() if other != key}))
        code, output = inspect()
        require(code == 1 and ' '.join(key) in output and 'src/Fixture.cs' in output,
                'unreviewed process state accepted: ' + str(key), output)
        print(next(line for line in output.splitlines() if line.startswith('NEW ')))
    policy.write_text(approvals(expected))
    before = policy.read_bytes()
    code, output = inspect()
    require(code == 0, 'exact process-state approvals rejected', output)
    inspect('state-candidate')
    require(policy.read_bytes() == before, 'state candidate rewrote approvals')
    compile_source(source.replace('Alias.Current;', 'new object();', 1))
    code, output = inspect()
    require(code == 1 and 'count=3, approved=4' in output, 'decreased ambient read count accepted', output)
    compile_source(source.replace('Alias.Current;', 'new[] { Alias.Current, Alias.Current };', 1))
    code, output = inspect()
    require(code == 1 and 'count=5, approved=4' in output, 'increased ambient read count accepted', output)
    compile_source('namespace Propane.Session { public class Empty {} }')
    code, output = inspect()
    require(code == 1 and output.count('STALE process state') == len(expected), 'stale process-state approval accepted', output)
    for bad_policy in [approvals(expected).replace('\tFixture\t', '\tREVIEW\t', 1),
                       ''.join(sorted((approvals(expected) * 2).splitlines(keepends=True))), '\n'.join(reversed(approvals(expected).splitlines())),
                       approvals(expected).replace('\t4\t', '\t0\t', 1),
                       approvals(expected).replace('launch-read', 'unknown-rule', 1),
                       approvals(expected).replace('\tFixture\t', '\t\t', 1)]:
        policy.write_text(bad_policy)
        code, output = inspect()
        require(code == 2, 'malformed process-state policy accepted', output)
    policy.unlink()
    code, output = inspect()
    require(code == 2 and 'process-state.tsv' in output, 'missing process-state inventory accepted', output)
    policy.write_text('')
    # Const/scalar readonly, immutable/frozen containers and compiler lambda caches are not mutable owners.
    # Dev state/ambient reads are deliberately outside this production-only inventory.
    compile_source('''
namespace Propane.Session
{
    public class Good
    {
        public int this[int key] => key;
        public string this[string key] => key;
        public const int Constant = 1;
        public static readonly int Scalar = 2;
        public static readonly System.Collections.Immutable.ImmutableArray<int> Immutable = [];
        public static readonly System.Collections.Frozen.FrozenDictionary<int, int> Frozen = System.Collections.Frozen.FrozenDictionary<int, int>.Empty;
        public static System.Func<int> Callback() => () => 1;
    }
}
namespace Propane.Core { public class LaunchArgs { public static object Current => new(); } }
namespace Propane.Dev
{
    public class Harness
    {
        public static int Field = 1;
        public static object Read() => Propane.Core.LaunchArgs.Current;
    }
}
''')
    code, output = inspect()
    require(code == 0, 'harmless statics/Dev exclusion rejected', output)
    print(f'PASS architecture process state: {len(expected)} independent negatives; exact/stale/count/schema/candidate and harmless/Dev witnesses')
