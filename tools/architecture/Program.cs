using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

// Read metadata without loading Godot or executing any code from the inspected assembly.
if (!(args.Length == 3 && args[0] is "check" or "candidate") && !(args.Length == 4 && args[0] == "inventory"))
{
    Console.Error.WriteLine("usage: Architecture check|candidate assembly.dll policy-directory | inventory assembly.dll game.pck policy-directory");
    return 2;
}
try
{
    var policyDirectory = args[^1];
    var policy = JsonSerializer.Deserialize<Policy>(File.ReadAllText(Path.Combine(policyDirectory, "modules.json")))!;
    using var assembly = File.OpenRead(args[1]);
    using var pe = new PEReader(assembly);
    if (args[0] == "inventory")
    {
        return new Scanner(pe, default, policy, Path.GetDirectoryName(Path.GetFullPath(args[1]))!)
            .Inventory(args[2], Path.Combine(policyDirectory, "artifact.json"));
    }
    using var symbols = File.OpenRead(Path.ChangeExtension(args[1], ".pdb"));
    using var pdb = MetadataReaderProvider.FromPortablePdbStream(symbols);
    var scanner = new Scanner(pe, pdb.GetMetadataReader(), policy, Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    var violations = scanner.Scan();
    if (args[0] == "candidate")
    {
        // Deliberately stdout only: the checker never rewrites the reviewed baseline.
        foreach (var edge in violations.Keys.Order(StringComparer.Ordinal)) Console.WriteLine(edge + "\tREVIEW\tSupply a reason and removing package");
        return 0;
    }
    var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
    var lines = File.ReadAllLines(Path.Combine(args[2], "baseline.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
    if (!lines.SequenceEqual(lines.Order(StringComparer.Ordinal))) throw new InvalidDataException("baseline.tsv must be sorted ordinally");
    foreach (var line in lines)
    {
        var fields = line.Split('\t');
        if (fields.Length != 5 || fields[3] is "" or "REVIEW" || string.IsNullOrWhiteSpace(fields[4]))
            throw new InvalidDataException("baseline needs rule, source, target, removing package and reason: " + line);
        baseline.Add(string.Join('\t', fields.Take(3)), line);
    }
    var failures = 0;
    foreach (var (edge, location) in violations.OrderBy(e => e.Key, StringComparer.Ordinal))
        if (!baseline.ContainsKey(edge)) { Console.Error.WriteLine($"NEW {location}: {edge.Replace('\t', ' ')}"); failures++; }
    foreach (var edge in baseline.Keys.Order(StringComparer.Ordinal))
        if (!violations.ContainsKey(edge)) { Console.Error.WriteLine("STALE baseline (remove it): " + edge.Replace('\t', ' ')); failures++; }
    Console.WriteLine($"Architecture: {scanner.TypeCount} types, {violations.Count} reviewed exceptions, {failures} failures");
    return failures == 0 ? 0 : 1;
}
catch (Exception error) when (error is IOException or BadImageFormatException or ArgumentException or JsonException or InvalidOperationException)
{
    Console.Error.WriteLine("Architecture: " + error);
    return 2;
}

internal sealed record Policy(Dictionary<string, string[]> Allowed, Dictionary<string, string> Namespaces,
    Dictionary<string, string> Types, Dictionary<string, string> Files, string[] WorkerProbes, Dictionary<string, string[]> GlobalOwners, Dictionary<string, string[]> TimerOwners);

internal sealed partial class Scanner
{
    private readonly PEReader pe;
    private readonly MetadataReader reader;
    private readonly MetadataReader? pdb;
    private readonly Policy policy;
    private readonly string directory;
    private readonly Dictionary<string, string> violations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeDefinitionHandle> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeDefinitionHandle, string> files = new();
    private readonly SignatureTypes signatures;
    private readonly Dictionary<TypeDefinitionHandle, MethodDefinitionHandle> initializers = new();
    private string source = "";
    private string location = "";
    private string module = "";
    private MethodDefinitionHandle currentMethod;
    private readonly Dictionary<MethodDefinitionHandle, HashSet<MethodDefinitionHandle>> calls = new();
    private readonly Dictionary<MethodDefinitionHandle, HashSet<MethodDefinitionHandle>> delegates = new();
    private readonly HashSet<MethodDefinitionHandle> startsWorker = new();
    private readonly Dictionary<MethodDefinitionHandle, HashSet<string>> nativeUses = new();
    public int TypeCount { get; private set; }
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => op.Value);

    public Scanner(PEReader pe, MetadataReader? pdb, Policy policy, string directory)
    {
        this.pe = pe;
        reader = pe.GetMetadataReader();
        this.pdb = pdb;
        this.policy = policy;
        this.directory = directory;
        signatures = new SignatureTypes(this);
        foreach (var handle in reader.TypeDefinitions)
        {
            definitions.Add(Name(handle), handle);
            foreach (var method in reader.GetTypeDefinition(handle).GetMethods())
                if (reader.GetString(reader.GetMethodDefinition(method).Name) == ".cctor") initializers.Add(handle, method);
        }
    }

    private string Name(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => DefinitionName((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => ReferenceName((TypeReferenceHandle)handle),
        _ => throw new BadImageFormatException("not a named type: " + handle.Kind)
    };

    private string DefinitionName(TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return type.IsNested ? Name(type.GetDeclaringType()) + "+" + reader.GetString(type.Name)
            : Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private string ReferenceName(TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference ? Name((EntityHandle)type.ResolutionScope) + "+" + reader.GetString(type.Name)
            : Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }
    private static string Join(string space, string name) => space.Length == 0 ? name : space + "." + name;
    private static string Owner(string name) => name.Split('+')[0];

    private string FileOf(TypeDefinitionHandle handle)
    {
        if (pdb is null) return "(export; no symbols)";
        if (files.TryGetValue(handle, out var found)) return found;
        var type = reader.GetTypeDefinition(handle);
        foreach (var method in type.GetMethods())
        {
            var debug = pdb.GetMethodDebugInformation(method);
            foreach (var point in debug.GetSequencePoints())
            {
                var document = point.Document.IsNil ? debug.Document : point.Document;
                if (document.IsNil) continue;
                var path = pdb.GetString(pdb.GetDocument(document).Name).Replace('\\', '/');
                var index = path.IndexOf("/src/", StringComparison.Ordinal);
                if (index >= 0) return files[handle] = path[(index + 1)..];
            }
        }
        if (type.IsNested) return files[handle] = FileOf(type.GetDeclaringType());
        // Interfaces, enums and fields-only types can have no sequence points. Source is used only for
        // diagnostics/file mapping; dependency discovery above and below always comes from metadata.
        var typeName = reader.GetString(type.Name).Split('`')[0];
        var space = reader.GetString(type.Namespace);
        foreach (var document in pdb.Documents)
        {
            var path = pdb.GetString(pdb.GetDocument(document).Name).Replace('\\', '/');
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            if (!Regex.IsMatch(text, @"\bnamespace\s+" + Regex.Escape(space) + @"\b") ||
                !Regex.IsMatch(text, @"\b(?:class|record|interface|enum|struct)\s+" + Regex.Escape(typeName) + @"\b"))
            {
                continue;
            }

            var index = path.IndexOf("/src/", StringComparison.Ordinal);
            return files[handle] = index >= 0 ? path[(index + 1)..] : path;
        }
        return files[handle] = "(metadata; source unavailable)";
    }

    private string ModuleOf(string name)
    {
        var owner = Owner(name);
        if (policy.Types.TryGetValue(owner, out var assigned)) return assigned;
        if (definitions.TryGetValue(owner, out var handle) && policy.Files.TryGetValue(FileOf(handle), out assigned)) return assigned;
        foreach (var (space, target) in policy.Namespaces.OrderByDescending(p => p.Key.Length))
            if (owner.StartsWith(space + ".", StringComparison.Ordinal)) return target;
        throw new InvalidDataException("unmapped type: " + name);
    }

    private void Fail(string rule, string target) => violations.TryAdd($"{rule}\t{Owner(source)}\t{target}", location);

    private bool NativeObject(string name, HashSet<string>? seen = null)
    {
        if (NativeTypes.Contains(name)) return true;
        seen ??= new(StringComparer.Ordinal);
        if (!seen.Add(name) || !definitions.TryGetValue(name, out var handle)) return false;
        var type = reader.GetTypeDefinition(handle);
        return !type.BaseType.IsNil && Types(type.BaseType).Any(t => NativeObject(t, seen));
    }
    private readonly HashSet<string> NativeTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> PhysicalTypes = new(StringComparer.Ordinal);

    private bool PhysicalObject(string name, HashSet<string>? seen = null)
    {
        if (PhysicalTypes.Contains(name)) return true;
        seen ??= new(StringComparer.Ordinal);
        if (!seen.Add(name) || !definitions.TryGetValue(name, out var handle)) return false;
        return Types(reader.GetTypeDefinition(handle).BaseType).Any(t => PhysicalObject(t, seen));
    }

    public Dictionary<string, string> Scan()
    {
        // GodotSharp is beside the game in source builds. Fixtures define their own Godot stubs.
        var godot = Path.Combine(directory, "GodotSharp.dll");
        if (!File.Exists(godot) && reader.AssemblyReferences.Any(h => reader.GetString(reader.GetAssemblyReference(h).Name) == "GodotSharp"))
            throw new FileNotFoundException("GodotSharp.dll must be beside the inspected assembly", godot);
        if (File.Exists(godot))
        {
            using var stream = File.OpenRead(godot);
            using var library = new PEReader(stream);
            var metadata = library.GetMetadataReader();
            string LibraryName(TypeDefinitionHandle h)
            {
                var t = metadata.GetTypeDefinition(h);
                return t.IsNested ? LibraryName(t.GetDeclaringType()) + "+" + metadata.GetString(t.Name)
                    : Join(metadata.GetString(t.Namespace), metadata.GetString(t.Name));
            }
            var parents = metadata.TypeDefinitions.ToDictionary(LibraryName, h =>
            {
                var parent = metadata.GetTypeDefinition(h).BaseType;
                return !parent.IsNil && parent.Kind == HandleKind.TypeDefinition ? LibraryName((TypeDefinitionHandle)parent) : "";
            });
            bool IsNative(string type) => type is "Godot.GodotObject" || parents.TryGetValue(type, out var parent) && parent != "" && IsNative(parent);
            bool IsPhysical(string type) => type is "Godot.Node3D" or "Godot.CanvasItem" or "Godot.Viewport" or "Godot.World3D" ||
                parents.TryGetValue(type, out var parent) && parent != "" && IsPhysical(parent);
            foreach (var type in parents.Keys)
            {
                if (IsNative(type)) NativeTypes.Add(type);
                if (IsPhysical(type)) PhysicalTypes.Add(type);
            }
        }
        PhysicalTypes.UnionWith(["Godot.Node3D", "Godot.Control", "Godot.CanvasItem", "Godot.Viewport", "Godot.World3D", "Godot.PhysicsServer3D"]);
        NativeTypes.UnionWith(["Godot.Node", "Godot.Resource", "Godot.GodotObject", "Godot.Engine", "Godot.OS", "Godot.ResourceLoader", "Godot.GD", "Godot.RenderingServer", "Godot.PhysicsServer3D"]);
        foreach (var handle in reader.TypeDefinitions)
        {
            source = Name(handle);
            if (source == "<Module>" || !source.StartsWith("Propane.", StringComparison.Ordinal)) continue;
            TypeCount++;
            module = ModuleOf(source);
            location = FileOf(handle) + " [" + source + "]";
            var type = reader.GetTypeDefinition(handle);
            Visit(type.BaseType);
            Attributes(type.GetCustomAttributes());
            Generics(type.GetGenericParameters());
            foreach (var iface in type.GetInterfaceImplementations()) Visit(reader.GetInterfaceImplementation(iface).Interface);
            foreach (var field in type.GetFields())
            {
                var item = reader.GetFieldDefinition(field);
                Add(item.DecodeSignature(signatures, null));
                Attributes(item.GetCustomAttributes());
            }
            foreach (var property in type.GetProperties())
            {
                var item = reader.GetPropertyDefinition(property);
                AddSignature(item.DecodeSignature(signatures, null));
                Attributes(item.GetCustomAttributes());
            }
            foreach (var evt in type.GetEvents())
            {
                var item = reader.GetEventDefinition(evt);
                Visit(item.Type);
                Attributes(item.GetCustomAttributes());
            }
            foreach (var method in type.GetMethods()) Method(method);
        }
        // Portable PDBs identify generated async/iterator MoveNext bodies without guessing compiler names.
        foreach (var method in reader.MethodDefinitions)
        {
            var kickoff = pdb!.GetMethodDebugInformation(method).GetStateMachineKickoffMethod();
            if (kickoff.IsNil) continue;
            if (!calls.TryGetValue(kickoff, out var targets)) calls[kickoff] = targets = [];
            targets.Add(method);
        }
        // Conservative direct-call closure for delegates passed from worker-starting methods. Interface/
        // reflection dispatch is not inferred. Newly introduced worker starts also require source approval.
        foreach (var starter in startsWorker)
        {
            var entry = reader.GetMethodDefinition(starter);
            if (policy.WorkerProbes.Contains(Name(entry.GetDeclaringType()) + "." + reader.GetString(entry.Name))) continue;
            var pending = new Stack<MethodDefinitionHandle>(delegates.GetValueOrDefault(starter) ?? []);
            var visited = new HashSet<MethodDefinitionHandle>();
            while (pending.TryPop(out var worker))
            {
                if (!visited.Add(worker)) continue;
                var owner = reader.GetMethodDefinition(worker).GetDeclaringType();
                source = Name(owner);
                location = FileOf(owner) + " [" + source + "]";
                foreach (var native in nativeUses.GetValueOrDefault(worker) ?? []) Fail("worker-native", native);
                foreach (var call in calls.GetValueOrDefault(worker) ?? []) pending.Push(call);
            }
        }
        return violations;
    }

    private void Add(IEnumerable<string> types)
    {
        foreach (var target in types.Distinct(StringComparer.Ordinal))
        {
            if (!currentMethod.IsNil && NativeObject(target))
            {
                if (!nativeUses.TryGetValue(currentMethod, out var uses)) nativeUses[currentMethod] = uses = [];
                uses.Add(target);
            }
            if (Owner(target) == Owner(source)) continue;
            if (target.StartsWith("Propane.", StringComparison.Ordinal))
            {
                var targetModule = ModuleOf(target);
                var smokeEntry = Owner(source) == "Propane.Main" && Owner(target) == "Propane.ReleaseSmoke.ReleaseTest";
                if (module != targetModule && !policy.Allowed[module].Contains(targetModule) && !smokeEntry) Fail("direction", Owner(target));
                if (module is not ("Dev" or "Smoke") && targetModule == "Smoke" &&
                    !smokeEntry)
                {
                    Fail("smoke-entry", Owner(target));
                }

                if (module != "Dev" && targetModule == "Dev") Fail("runtime-dev", Owner(target));
                if (module == "Bots" && (target == "Propane.Net.Match" || target == "Propane.Net.NetClient" || IsLiveRule(target))) Fail("bot-live-owner", Owner(target));
                if (module == "Simulation" && (target == "Propane.Net.NetClient" || target.EndsWith("Codec", StringComparison.Ordinal) || target.StartsWith("Propane.UI.", StringComparison.Ordinal))) Fail("simulation-boundary", Owner(target));
                if (module == "Server" && (targetModule == "Simulation" && target.StartsWith("Propane.Player.", StringComparison.Ordinal) || targetModule == "Presentation" && (target.StartsWith("Propane.Player.", StringComparison.Ordinal) || target.StartsWith("Propane.UI.", StringComparison.Ordinal)))) Fail("server-physical", Owner(target));
                if (module == "Wire" && Owner(target) == "Propane.Net.NetTransport") Fail("transport-backend", Owner(target));
            }
            if (module == "Server" && PhysicalObject(target)) Fail("server-physical", target);
            if (module == "Wire" && Owner(source) != "Propane.Net.NetTransport" && target.StartsWith("Godot.ENet", StringComparison.Ordinal)) Fail("transport-backend", target);
            if (module is "Rules" or "Plans" && NativeObject(target)) Fail("pure-native", target);
        }
    }

    private bool IsLiveRule(string target, HashSet<string>? seen = null)
    {
        if (target == "Propane.Modes.IModeRules") return true;
        seen ??= new(StringComparer.Ordinal);
        if (!seen.Add(target) || !definitions.TryGetValue(target, out var handle)) return false;
        var type = reader.GetTypeDefinition(handle);
        return type.GetInterfaceImplementations().Any(i => Types(reader.GetInterfaceImplementation(i).Interface).Any(t => IsLiveRule(t, seen))) ||
            Types(type.BaseType).Any(t => IsLiveRule(t, seen));
    }

    private IEnumerable<string> Types(EntityHandle handle)
    {
        if (handle.IsNil) return [];
        return handle.Kind switch
        {
            HandleKind.TypeDefinition or HandleKind.TypeReference => [Name(handle)],
            HandleKind.MethodDefinition => [Name(reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType())],
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(signatures, (object?)null),
            _ => []
        };
    }
    private void AddSignature(MethodSignature<ImmutableArray<string>> signature)
    {
        Add(signature.ReturnType);
        foreach (var parameter in signature.ParameterTypes) Add(parameter);
    }
    private void Generics(GenericParameterHandleCollection parameters)
    {
        foreach (var parameter in parameters)
        {
            var item = reader.GetGenericParameter(parameter);
            Attributes(item.GetCustomAttributes());
            foreach (var constraint in item.GetConstraints()) Visit(reader.GetGenericParameterConstraint(constraint).Type);
        }
    }
    private void Attributes(CustomAttributeHandleCollection attributes)
    {
        foreach (var attribute in attributes)
        {
            // Constructor signature and attribute class are real dependencies. Serialized System.Type values
            // inside custom-attribute blobs are outside this check (see README).
            Visit(reader.GetCustomAttribute(attribute).Constructor);
        }
    }
    private void Method(MethodDefinitionHandle handle)
    {
        currentMethod = handle;
        var method = reader.GetMethodDefinition(handle);
        AddSignature(method.DecodeSignature(signatures, null));
        Attributes(method.GetCustomAttributes());
        Generics(method.GetGenericParameters());
        foreach (var parameter in method.GetParameters()) Attributes(reader.GetParameter(parameter).GetCustomAttributes());
        foreach (var impl in reader.GetTypeDefinition(method.GetDeclaringType()).GetMethodImplementations())
        {
            var implementation = reader.GetMethodImplementation(impl);
            if (implementation.MethodBody == handle) Visit(implementation.MethodDeclaration);
        }
        if (method.RelativeVirtualAddress == 0) { currentMethod = default; return; }
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (!body.LocalSignature.IsNil)
            foreach (var local in reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(signatures, (object?)null)) Add(local);
        foreach (var region in body.ExceptionRegions) if (region.Kind == ExceptionRegionKind.Catch) Visit(region.CatchType);
        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            var first = il.ReadByte();
            var opcode = Opcodes[first == 0xfe ? (short)(0xfe00 | il.ReadByte()) : first];
            switch (opcode.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    var token = MetadataTokens.EntityHandle(il.ReadInt32());
                    if (token.Kind == HandleKind.MethodDefinition)
                    {
                        if (!calls.TryGetValue(handle, out var targets)) calls[handle] = targets = [];
                        targets.Add((MethodDefinitionHandle)token);
                        if (opcode == OpCodes.Ldftn || opcode == OpCodes.Ldvirtftn)
                        {
                            if (!delegates.TryGetValue(handle, out var callbacks)) delegates[handle] = callbacks = [];
                            callbacks.Add((MethodDefinitionHandle)token);
                        }
                    }
                    var declaring = token.Kind switch
                    {
                        HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)token).GetDeclaringType(),
                        HandleKind.FieldDefinition => reader.GetFieldDefinition((FieldDefinitionHandle)token).GetDeclaringType(),
                        _ => default
                    };
                    if (!declaring.IsNil && initializers.TryGetValue(declaring, out var initialize))
                    {
                        if (!calls.TryGetValue(handle, out var targets)) calls[handle] = targets = [];
                        targets.Add(initialize);
                    }
                    Visit(token); break;
                case OperandType.InlineSwitch:
                    var count = il.ReadInt32();
                    il.Offset += count * 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: il.Offset += 8; break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.InlineString:
                case OperandType.ShortInlineR: il.Offset += 4; break;
                case OperandType.InlineVar: il.Offset += 2; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: il.Offset++; break;
                case OperandType.InlineNone: break;
                default: throw new BadImageFormatException("unsupported IL operand: " + opcode.OperandType);
            }
        }
        currentMethod = default;
    }
    private void Visit(EntityHandle handle)
    {
        if (handle.IsNil) return;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification: Add(Types(handle)); break;
            case HandleKind.MethodSpecification:
                var spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                Visit(spec.Method);
                foreach (var type in spec.DecodeSignature(signatures, (object?)null)) Add(type);
                break;
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                Add([Name(method.GetDeclaringType())]);
                AddSignature(method.DecodeSignature(signatures, null));
                Member(Name(method.GetDeclaringType()), reader.GetString(method.Name));
                break;
            case HandleKind.FieldDefinition:
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                Add([Name(field.GetDeclaringType())]);
                Add(field.DecodeSignature(signatures, null));
                break;
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                var owners = Types(member.Parent).ToArray();
                Add(owners);
                if (member.GetKind() == MemberReferenceKind.Field) Add(member.DecodeFieldSignature(signatures, null));
                else AddSignature(member.DecodeMethodSignature(signatures, null));
                foreach (var owner in owners) Member(owner, reader.GetString(member.Name));
                break;
            case HandleKind.StandaloneSignature:
                AddSignature(reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(signatures, null)); break;
            default: throw new BadImageFormatException("unsupported reference: " + handle.Kind);
        }
    }
    private void Member(string owner, string name)
    {
        var member = owner + "." + name;
        if (policy.TimerOwners.TryGetValue(member, out var timerOwners) && !timerOwners.Contains(Owner(source))) Fail("timer-owner", member);
        if (policy.GlobalOwners.TryGetValue(member, out var owners) && !owners.Contains(Owner(source))) Fail("global-owner", member);
        if (!currentMethod.IsNil && (owner == "System.Threading.Tasks.Task" && name == "Run" ||
            owner.StartsWith("System.Threading.Tasks.TaskFactory", StringComparison.Ordinal) && name == "StartNew" ||
            owner == "System.Threading.Thread" && name == ".ctor" ||
            owner == "System.Threading.ThreadPool" && name.Contains("QueueUserWorkItem", StringComparison.Ordinal)))
        {
            startsWorker.Add(currentMethod);
        }

        if (module is "Rules" or "Plans" && (owner == "Godot.Time" && name.StartsWith("Get", StringComparison.Ordinal) ||
            owner is "System.DateTime" or "System.DateTimeOffset" && name is "get_Now" or "get_UtcNow" or "get_Today" ||
            owner == "System.Environment" && name is "get_TickCount" or "get_TickCount64" ||
            owner is "System.Diagnostics.Stopwatch" or "System.TimeProvider"))
        {
            Fail("rule-clock", owner + "." + name);
        }

        if (module == "Plans" && owner == "Godot.GD" && name is "Load" or "LoadResource") Fail("plan-resource", owner + "." + name);
        if (module is "Rules" or "Plans" && (owner == "System.Threading.Tasks.Task" && name == "Delay" || owner == "Godot.SceneTree" && name == "CreateTimer")) Fail("rule-clock", owner + "." + name);
    }

    private sealed class SignatureTypes(Scanner scanner) : ISignatureTypeProvider<ImmutableArray<string>, object?>
    {
        public ImmutableArray<string> GetArrayType(ImmutableArray<string> elementType, ArrayShape shape) => elementType;
        public ImmutableArray<string> GetByReferenceType(ImmutableArray<string> elementType) => elementType;
        public ImmutableArray<string> GetFunctionPointerType(MethodSignature<ImmutableArray<string>> signature) =>
            [.. signature.ReturnType, .. signature.ParameterTypes.SelectMany(t => t)];
        public ImmutableArray<string> GetGenericInstantiation(ImmutableArray<string> genericType, ImmutableArray<ImmutableArray<string>> typeArguments) =>
            [.. genericType, .. typeArguments.SelectMany(t => t)];
        public ImmutableArray<string> GetGenericMethodParameter(object? context, int index) => [];
        public ImmutableArray<string> GetGenericTypeParameter(object? context, int index) => [];
        public ImmutableArray<string> GetModifiedType(ImmutableArray<string> modifier, ImmutableArray<string> unmodifiedType, bool isRequired) => [.. modifier, .. unmodifiedType];
        public ImmutableArray<string> GetPinnedType(ImmutableArray<string> elementType) => elementType;
        public ImmutableArray<string> GetPointerType(ImmutableArray<string> elementType) => elementType;
        public ImmutableArray<string> GetPrimitiveType(PrimitiveTypeCode typeCode) => [];
        public ImmutableArray<string> GetSZArrayType(ImmutableArray<string> elementType) => elementType;
        public ImmutableArray<string> GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => [scanner.Name(handle)];
        public ImmutableArray<string> GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => [scanner.Name(handle)];
        public ImmutableArray<string> GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
