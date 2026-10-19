using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// Lists every method in Assembly-CSharp that calls something whose result differs per process on .NET
// (string/tuple/enum hash codes, System.HashCode) or that runs work in parallel (Waylith jobs, Parallel,
// Task.Run, ThreadPool). Output: a Markdown file grouped by the calling type.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DeterminismScan <Assembly-CSharp.dll> <output.md>");
    return 1;
}

var scanner = new Scanner(args[0]);
var hits = scanner.Scan();

// The assessment at the end of the report is written by hand; keep it when regenerating
const string AssessmentHeader = "## Multiplayer assessment";
string assessment = "";
if (File.Exists(args[1]))
{
    var existing = File.ReadAllText(args[1]);
    int idx = existing.IndexOf(AssessmentHeader, StringComparison.Ordinal);
    if (idx >= 0) assessment = existing[idx..];
}

File.WriteAllText(args[1], Report.Build(args[0], hits) + assessment);
Console.WriteLine($"{hits.Count} call sites in {hits.Select(h => h.CallerType).Distinct().Count()} types -> {args[1]}");
return 0;

record Hit(string CallerType, string Caller, string Category, string Target);

sealed class Scanner
{
    private readonly PEReader pe;
    private readonly MetadataReader md;

    public Scanner(string path)
    {
        pe = new PEReader(File.OpenRead(path));
        md = pe.GetMetadataReader();
    }

    // Category name, declaring type full name, method name (null = any method of the type)
    private static readonly (string category, string type, string? method)[] Targets =
    [
        ("string.GetHashCode", "System.String", "GetHashCode"),
        ("HashCode", "System.HashCode", "Combine"),
        ("HashCode", "System.HashCode", "Add"),
        ("HashCode", "System.HashCode", "ToHashCode"),
        ("ValueTuple.GetHashCode", "System.ValueTuple", "GetHashCode"),
        ("Enum.GetHashCode", "System.Enum", "GetHashCode"),
        ("Waylith jobs", "Waylith.Jobs.WaylithJobRuntime", null),
        ("Unity jobs", "Unity.Jobs.IJobExtensions", null),
        ("Unity jobs", "Unity.Jobs.IJobParallelForExtensions", null),
        ("Unity jobs", "Unity.Jobs.IJobForExtensions", null),
        ("Unity jobs", "Unity.Jobs.JobHandle", "Schedule"),
        ("Unity jobs", "Unity.Jobs.JobHandle", "ScheduleBatchedJobs"),
        ("Parallel", "System.Threading.Tasks.Parallel", null),
        ("Task.Run", "System.Threading.Tasks.Task", "Run"),
        ("ThreadPool", "System.Threading.ThreadPool", null),
    ];

    public List<Hit> Scan()
    {
        var hits = new List<Hit>();

        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            var typeName = FullTypeName(th);

            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;

                var body = pe.GetMethodBody(m.RelativeVirtualAddress);
                foreach (var (constrained, token) in CallTokens(body.GetILReader()))
                {
                    var (category, target) = Classify(token, constrained);
                    if (category != null)
                        hits.Add(new Hit(typeName, md.GetString(m.Name), category, target!));
                }
            }
        }

        return hits;
    }

    private (string? category, string? target) Classify(EntityHandle handle, EntityHandle? constrained = null)
    {
        string? type = null, method = null;

        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var def = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                type = FullTypeName(def.GetDeclaringType());
                method = md.GetString(def.Name);
                break;
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                type = TypeNameOf(mr.Parent);
                method = md.GetString(mr.Name);
                break;
            case HandleKind.MethodSpecification:
                var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                return Classify(spec.Method, constrained);
        }

        if (type == null || method == null) return (null, null);

        // "constrained. T callvirt Object::GetHashCode" is how C# calls GetHashCode on a struct or enum
        if (constrained != null && type == "System.Object" && method == "GetHashCode")
        {
            var receiver = TypeNameOf(constrained.Value);
            var receiverNoArity = receiver.Contains('`') ? receiver[..receiver.IndexOf('`')] : receiver;
            if (receiverNoArity == "System.ValueTuple")
                return ("ValueTuple.GetHashCode", $"{receiver}.GetHashCode");
            if (IsEnum(constrained.Value))
                return ("Enum.GetHashCode", $"{receiver}.GetHashCode");
            return (null, null);
        }

        // ValueTuple`2.. all count as ValueTuple
        var typeNoArity = type.Contains('`') ? type[..type.IndexOf('`')] : type;

        foreach (var (category, tType, tMethod) in Targets)
            if (typeNoArity == tType && (tMethod == null || tMethod == method))
                return (category, $"{type}.{method}");

        return (null, null);
    }

    private bool IsEnum(EntityHandle type)
    {
        if (type.Kind != HandleKind.TypeDefinition) return false; // enums from other assemblies aren't resolved here
        var td = md.GetTypeDefinition((TypeDefinitionHandle)type);
        return !td.BaseType.IsNil && TypeNameOf(td.BaseType) == "System.Enum";
    }

    private string TypeNameOf(EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                var tr = md.GetTypeReference((TypeReferenceHandle)parent);
                var ns = md.GetString(tr.Namespace);
                var name = md.GetString(tr.Name);
                if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                    return TypeNameOf(tr.ResolutionScope) + "+" + name;
                return ns.Length == 0 ? name : ns + "." + name;
            case HandleKind.TypeDefinition:
                return FullTypeName((TypeDefinitionHandle)parent);
            case HandleKind.TypeSpecification:
                var ts = md.GetTypeSpecification((TypeSpecificationHandle)parent);
                var reader = md.GetBlobReader(ts.Signature);
                return DecodeTypeSpecName(ref reader);
            default:
                return "?";
        }
    }

    // Enough of the signature decoder to name the generic type of a TypeSpec (ValueTuple`2<...> etc)
    private string DecodeTypeSpecName(ref BlobReader reader)
    {
        var code = reader.ReadSignatureTypeCode();
        switch (code)
        {
            case SignatureTypeCode.GenericTypeInstance:
                reader.ReadSignatureTypeCode(); // class or valuetype
                return TypeNameOf(reader.ReadTypeHandle());
            case SignatureTypeCode.TypeHandle:
                return TypeNameOf(reader.ReadTypeHandle());
            default:
                return code.ToString();
        }
    }

    private string FullTypeName(TypeDefinitionHandle th)
    {
        var td = md.GetTypeDefinition(th);
        var name = md.GetString(td.Name);
        if (td.IsNested)
            return FullTypeName(td.GetDeclaringType()) + "+" + name;
        var ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // Tokens of every call, callvirt, newobj, ldftn and ldvirtftn in the body, with the type of a preceding
    // constrained. prefix
    private static IEnumerable<(EntityHandle? constrained, EntityHandle method)> CallTokens(BlobReader il)
    {
        EntityHandle? constrained = null;

        while (il.RemainingBytes > 0)
        {
            int b = il.ReadByte();
            OpCode op = b == 0xFE ? TwoByte[il.ReadByte()] : OneByte[b];

            if (op == OpCodes.Constrained)
            {
                constrained = MetadataTokens.EntityHandle(il.ReadInt32());
                continue;
            }

            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    yield return (constrained, MetadataTokens.EntityHandle(il.ReadInt32()));
                    break;
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: il.ReadByte(); break;
                case OperandType.InlineVar: il.ReadInt16(); break;
                case OperandType.InlineI8:
                case OperandType.InlineR: il.ReadInt64(); break;
                case OperandType.ShortInlineR: il.ReadInt32(); break;
                case OperandType.InlineSwitch:
                    int n = il.ReadInt32();
                    for (int i = 0; i < n; i++) il.ReadInt32();
                    break;
                default: il.ReadInt32(); break; // InlineBrTarget, InlineField, InlineI, InlineSig, InlineString, InlineTok, InlineType
            }

            constrained = null;
        }
    }

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static Scanner()
    {
        foreach (var f in typeof(OpCodes).GetFields())
        {
            if (f.GetValue(null) is not OpCode op) continue;
            if (op.Size == 1) OneByte[op.Value & 0xFF] = op;
            else TwoByte[op.Value & 0xFF] = op;
        }
    }
}

static class Report
{
    public static string Build(string dll, List<Hit> hits)
    {
        var sb = new StringBuilder();
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion;

        sb.AppendLine("# Determinism scan of Assembly-CSharp");
        sb.AppendLine();
        sb.AppendLine($"Assembly-CSharp.dll {version}, scanned {DateTime.Now:yyyy-MM-dd}. ");
        sb.AppendLine("Every method that calls string.GetHashCode, System.HashCode, a ValueTuple or Enum GetHashCode ");
        sb.AppendLine("(all differ per process on .NET), WaylithJobRuntime, Parallel, Task.Run or ThreadPool (run in parallel).");
        sb.AppendLine();
        sb.AppendLine("| Category | Call sites | Types |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in hits.GroupBy(h => h.Category).OrderByDescending(g => g.Count()))
            sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Select(h => h.CallerType).Distinct().Count()} |");
        sb.AppendLine();

        foreach (var typeGroup in hits.GroupBy(h => h.CallerType).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {typeGroup.Key}");
            sb.AppendLine();
            foreach (var callerGroup in typeGroup.GroupBy(h => h.Caller).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var targets = callerGroup.GroupBy(h => h.Target)
                    .Select(t => t.Count() > 1 ? $"{t.Key} x{t.Count()}" : t.Key);
                sb.AppendLine($"- `{callerGroup.Key}`: {string.Join(", ", targets)}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
