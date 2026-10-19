using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

internal sealed partial class Scanner
{
    // Only checker-lifetime observations; no inspected code is loaded or executed.
    private readonly Dictionary<string, int> stateSites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> stateLocations = new(StringComparer.Ordinal);

    private void StateSite(string rule, string type, string member)
    {
        var key = $"{rule}\t{type}\t{member}";
        stateSites[key] = stateSites.GetValueOrDefault(key) + 1;
        stateLocations[key] = location;
    }

    public void StateCandidate()
    {
        foreach (var (key, count) in stateSites.OrderBy(p => p.Key, StringComparer.Ordinal))
            Console.WriteLine($"{key}\t{count}\tREVIEW\tSupply owner, lifetime and reason");
    }

    public int CheckState(string path)
    {
        var approved = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        if (!lines.SequenceEqual(lines.Order(StringComparer.Ordinal))) throw new InvalidDataException("process-state.tsv must be sorted ordinally");
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            if (fields.Length != 6 || fields[0] is not ("launch-read" or "static-field" or "static-property" or "static-collection") ||
                !int.TryParse(fields[3], out var count) || count < 1 || string.IsNullOrWhiteSpace(fields[4]) || fields[4] == "REVIEW" || string.IsNullOrWhiteSpace(fields[5]))
                throw new InvalidDataException("process state needs rule, type, member, count, owner and reason: " + line);
            approved.Add(string.Join('\t', fields.Take(3)), count);
        }
        var failures = 0;
        foreach (var (key, count) in stateSites.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (approved.GetValueOrDefault(key) != count)
            {
                Console.Error.WriteLine($"NEW {stateLocations[key]}: {key.Replace('\t', ' ')} count={count}, approved={approved.GetValueOrDefault(key)}");
                failures++;
            }
        foreach (var key in approved.Keys.Except(stateSites.Keys).Order(StringComparer.Ordinal))
        {
            Console.Error.WriteLine("STALE process state (remove it): " + key.Replace('\t', ' '));
            failures++;
        }
        Console.WriteLine($"Architecture process state: {stateSites.Count(p => p.Key.StartsWith("launch-read\t", StringComparison.Ordinal))} launch groups " +
            $"({stateSites.Where(p => p.Key.StartsWith("launch-read\t", StringComparison.Ordinal)).Sum(p => p.Value)} reads), " +
            $"{stateSites.Count(p => !p.Key.StartsWith("launch-read\t", StringComparison.Ordinal))} mutable statics, {failures} failures");
        return failures;
    }

    private bool Generated(TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return reader.GetString(type.Name).StartsWith('<') && type.GetCustomAttributes().Any(h =>
        {
            var constructor = reader.GetCustomAttribute(h).Constructor;
            var owner = constructor.Kind == HandleKind.MemberReference
                ? reader.GetMemberReference((MemberReferenceHandle)constructor).Parent
                : reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
            return Types(owner).Contains("System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }) ||
            type.IsNested && Generated(type.GetDeclaringType());
    }

    private void StateDeclarations(TypeDefinitionHandle handle)
    {
        if (module == "Dev" || Generated(handle)) return;
        var type = reader.GetTypeDefinition(handle);
        foreach (var h in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(h);
            var setter = property.GetAccessors().Setter;
            if (!setter.IsNil && (reader.GetMethodDefinition(setter).Attributes & MethodAttributes.Static) != 0)
                StateSite("static-property", source, reader.GetString(property.Name));
        }
        foreach (var h in type.GetFields())
        {
            var field = reader.GetFieldDefinition(h);
            if ((field.Attributes & FieldAttributes.Static) == 0 || (field.Attributes & FieldAttributes.Literal) != 0) continue;
            var name = reader.GetString(field.Name);
            // Attribute-based exclusion would hide event storage too. Only auto-property storage is folded
            // into its property; event backing fields and all authored nested types remain inventoried.
            if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            {
                var propertyName = name[1..name.IndexOf('>')];
                var propertyHandle = type.GetProperties().FirstOrDefault(h => reader.GetString(reader.GetPropertyDefinition(h).Name) == propertyName);
                if (propertyHandle.IsNil) throw new BadImageFormatException("auto-property storage without property: " + source + "." + name);
                var property = reader.GetPropertyDefinition(propertyHandle);
                if (!property.GetAccessors().Setter.IsNil) continue;
                name = reader.GetString(property.Name);
            }
            if ((field.Attributes & FieldAttributes.InitOnly) == 0) StateSite("static-field", source, name);
            else if (MutableCollection(field.DecodeSignature(new StateTypes(this), (object?)null))) StateSite("static-collection", source, name);
        }
    }

    private bool MutableCollection(string type, HashSet<string>? seen = null)
    {
        if (type.EndsWith("[]", StringComparison.Ordinal)) return true;
        // Interface/read-only wrappers are conservative approvals: their backing storage or elements can
        // still be mutable. Immutable/Frozen containers themselves cannot be structurally mutated.
        var name = type.Split('<')[0];
        if (name.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) || name.StartsWith("System.Collections.Frozen.", StringComparison.Ordinal)) return false;
        if (name.StartsWith("System.Collections.", StringComparison.Ordinal) || name.StartsWith("Godot.Collections.", StringComparison.Ordinal) ||
            name is "System.Array" or "System.ArraySegment`1" or "System.Memory`1" or "System.ReadOnlyMemory`1" or "System.Runtime.CompilerServices.ConditionalWeakTable`2") return true;
        seen ??= new(StringComparer.Ordinal);
        if (!seen.Add(name) || !definitions.TryGetValue(name, out var handle)) return false;
        var definition = reader.GetTypeDefinition(handle);
        string Parent(EntityHandle h) => h.IsNil ? "" : h.Kind == HandleKind.TypeSpecification
            ? reader.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(new StateTypes(this), (object?)null) : Name(h);
        return new[] { Parent(definition.BaseType) }.Concat(definition.GetInterfaceImplementations().Select(h => Parent(reader.GetInterfaceImplementation(h).Interface)))
            .Any(parent => MutableCollection(parent, seen));
    }

    // Preserve outer type/array shape, unlike the dependency signature reader which flattens arguments.
    private sealed class StateTypes(Scanner scanner) : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(',', typeArguments) + ">";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => scanner.Name(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => scanner.Name(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetByReferenceType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";
    }
}
