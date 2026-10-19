using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

// For every lambda Multiplayer looks up by compiler-generated name and every method it transpiles, checks
// that the member still exists in the new game build and whether its IL changed. The lambda resolution
// mirrors MpMethodUtil.GetLambda: parent method debug id from the IL, then <Parent>b__<id>_<ordinal> on the
// type or its <>c class, or <Parent>b__<ordinal> on a <>c__DisplayClass<id>_* type.

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: RwDiff <old Assembly-CSharp.dll> <new Assembly-CSharp.dll> <targets.txt> <output.md>");
    return 1;
}

var oldAsm = new Asm(args[0]);
var newAsm = new Asm(args[1]);
var report = new StringBuilder();
int problems = 0;

report.AppendLine("# Multiplayer targets: old vs new Assembly-CSharp");
report.AppendLine();
report.AppendLine($"Old: {Asm.Version(args[0])}, new: {Asm.Version(args[1])}, compared {DateTime.Now:yyyy-MM-dd}.");
report.AppendLine("Targets from scripts/mp_targets.py. \"changed\" compares opcodes and resolved operands, ignoring branch offsets.");
report.AppendLine();

var lambdaRows = new List<string>();
var transpiledRows = new List<string>();

foreach (var line in File.ReadLines(args[2]))
{
    var parts = line.Split(' ');
    if (parts.Length < 3) continue;

    if (parts[0] == "lambda")
    {
        var (type, method, ordinal) = (parts[1], parts[2], int.Parse(parts[3]));
        var o = oldAsm.FindLambda(type, method, ordinal);
        var n = newAsm.FindLambda(type, method, ordinal);
        string status;
        if (n.body == null) { status = o.body == null ? "not found in either" : "MISSING in new"; if (o.body != null) problems++; }
        else if (o.body == null) status = "new only";
        else status = o.body == n.body ? "unchanged" : "CHANGED";
        if (status == "CHANGED") problems++;
        lambdaRows.Add($"| `{type}.{method}` #{ordinal} | {o.name ?? "-"} | {n.name ?? "-"} | {status} |");
    }
    else if (parts[0] == "transpiled")
    {
        var (type, method) = (parts[1], parts[2]);
        var o = oldAsm.FindMethod(type, method);
        var n = newAsm.FindMethod(type, method);
        string status;
        if (n == null) { status = o == null ? "not found in either" : "MISSING in new"; if (o != null) problems++; }
        else if (o == null) status = "new only";
        else status = o == n ? "unchanged" : "CHANGED";
        if (status == "CHANGED") problems++;
        transpiledRows.Add($"| `{type}.{method}` | {status} |");
    }
}

report.AppendLine($"**{problems} items need attention** (missing in the new build or changed IL). \"Not found in either\" rows are Multiplayer's own helpers or lookups the extractor couldn't parse.");
report.AppendLine();
report.AppendLine("## Lambda lookups");
report.AppendLine();
report.AppendLine("| Lookup | Old member | New member | Status |");
report.AppendLine("|---|---|---|---|");
foreach (var r in lambdaRows.OrderBy(r => r.Contains("| unchanged |") ? 1 : 0).ThenBy(r => r)) report.AppendLine(r);
report.AppendLine();
report.AppendLine("## Transpiled methods");
report.AppendLine();
report.AppendLine("| Method | Status |");
report.AppendLine("|---|---|");
foreach (var r in transpiledRows.OrderBy(r => r.Contains("| unchanged |") ? 1 : 0).ThenBy(r => r)) report.AppendLine(r);

// The review at the end is written by hand; keep it when regenerating
const string ReviewHeader = "## Review";
string review = "";
if (File.Exists(args[3]))
{
    var existing = File.ReadAllText(args[3]);
    int idx = existing.IndexOf(ReviewHeader, StringComparison.Ordinal);
    if (idx >= 0) review = "\n" + existing[idx..];
}

File.WriteAllText(args[3], report.ToString() + review);
Console.WriteLine($"{lambdaRows.Count} lambdas, {transpiledRows.Count} transpiled methods, {problems} need attention -> {args[3]}");
return 0;

sealed class Asm
{
    private readonly PEReader pe;
    private readonly MetadataReader md;
    private readonly Dictionary<string, List<TypeDefinitionHandle>> typesBySimpleName = new();

    public Asm(string path)
    {
        pe = new PEReader(File.OpenRead(path));
        md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var name = md.GetString(md.GetTypeDefinition(th).Name);
            if (!typesBySimpleName.TryGetValue(name, out var list)) typesBySimpleName[name] = list = new();
            list.Add(th);
        }
    }

    public static string Version(string path) => System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?";

    // Normalized IL of the first method with that name on the type (simple type name), or null
    public string? FindMethod(string typeName, string methodName)
    {
        var mh = FindMethodHandle(typeName, methodName);
        return mh == null ? null : Body(mh.Value);
    }

    private MethodDefinitionHandle? FindMethodHandle(string typeName, string methodName)
    {
        if (!typesBySimpleName.TryGetValue(typeName, out var types)) return null;
        foreach (var th in types)
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var name = md.GetString(m.Name);
                if (name == methodName || (methodName != ".ctor" && (name == "get_" + methodName || name == "set_" + methodName)))
                    return mh;
            }
        return null;
    }

    // Tries every overload of the parent, the first one with a lambda of that ordinal wins
    public (string? name, string? body) FindLambda(string typeName, string parentMethod, int ordinal)
    {
        if (!typesBySimpleName.TryGetValue(typeName, out var types)) return (null, null);
        foreach (var th in types)
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var name = md.GetString(md.GetMethodDefinition(mh).Name);
                if (name != parentMethod && !(parentMethod != ".ctor" && (name == "get_" + parentMethod || name == "set_" + parentMethod)))
                    continue;
                var found = FindLambdaIn(mh, ordinal);
                if (found.body != null) return found;
            }
        return (null, null);
    }

    private (string? name, string? body) FindLambdaIn(MethodDefinitionHandle parent, int ordinal)
    {
        var parentDef = md.GetMethodDefinition(parent);
        var parentName = md.GetString(parentDef.Name);
        var owner = parentDef.GetDeclaringType();

        int? debugId = DebugId(parentDef);
        if (debugId == null) return (null, null);

        // Capturing lambda: <>c__DisplayClass{id}_N.<Parent>b__{ordinal}
        string shortName = $"<{parentName}>b__{ordinal}";
        foreach (var nested in md.GetTypeDefinition(owner).GetNestedTypes())
        {
            var nestedName = md.GetString(md.GetTypeDefinition(nested).Name);
            if (!nestedName.StartsWith($"<>c__DisplayClass{debugId}_", StringComparison.Ordinal)) continue;
            foreach (var mh in md.GetTypeDefinition(nested).GetMethods())
                if (md.GetString(md.GetMethodDefinition(mh).Name) == shortName)
                    return ($"{nestedName}.{shortName}", Body(mh));
        }

        // Non-capturing: <Parent>b__{id}_{ordinal} on the type itself or on <>c
        string fullName = $"<{parentName}>b__{debugId}_{ordinal}";
        foreach (var mh in md.GetTypeDefinition(owner).GetMethods())
            if (md.GetString(md.GetMethodDefinition(mh).Name) == fullName)
                return (fullName, Body(mh));
        foreach (var nested in md.GetTypeDefinition(owner).GetNestedTypes())
        {
            if (md.GetString(md.GetTypeDefinition(nested).Name) != "<>c") continue;
            foreach (var mh in md.GetTypeDefinition(nested).GetMethods())
                if (md.GetString(md.GetMethodDefinition(mh).Name) == fullName)
                    return ($"<>c.{fullName}", Body(mh));
        }

        return (null, null);
    }

    // Same rules as MpMethodUtil.GetMethodDebugId
    private int? DebugId(MethodDefinition parent)
    {
        if (parent.RelativeVirtualAddress == 0) return null;
        foreach (var (op, operand) in Instructions(pe.GetMethodBody(parent.RelativeVirtualAddress).GetILReader()))
        {
            if (op.OperandType != OperandType.InlineMethod || operand is not EntityHandle h) continue;
            var (declType, name) = MethodNameOf(h);
            if (op == OpCodes.Newobj)
            {
                var m = Regex.Match(declType, @"<>c__DisplayClass(\d+)_");
                if (m.Success) return int.Parse(m.Groups[1].Value);
                m = Regex.Match(declType, @"d__(\d+)$");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            else if ((op == OpCodes.Ldftn || op == OpCodes.Call) && name.StartsWith('<'))
            {
                var m = Regex.Match(name, @"^<[^>]*>b__(\d+)_\d+$");
                if (m.Success) return int.Parse(m.Groups[1].Value);
                m = Regex.Match(name, @"^<[^>]*>g__[^|]*\|(\d+)_\d+$");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
        }
        return null;
    }

    private string Body(MethodDefinitionHandle mh)
    {
        var m = md.GetMethodDefinition(mh);
        if (m.RelativeVirtualAddress == 0) return "(no body)";
        var sb = new StringBuilder();
        foreach (var (op, operand) in Instructions(pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader()))
        {
            sb.Append(op.Name).Append(' ');
            sb.Append(operand switch
            {
                EntityHandle h => NameOf(h),
                UserStringHandle s => '"' + md.GetUserString(s) + '"',
                null => "",
                _ => operand.ToString(),
            });
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private string NameOf(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
            case HandleKind.MemberReference:
            case HandleKind.MethodSpecification:
                var (t, n) = MethodNameOf(h);
                return t + "::" + n;
            case HandleKind.FieldDefinition:
                var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                return TypeName(f.GetDeclaringType()) + "::" + md.GetString(f.Name);
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
                return TypeName(h);
            default:
                return h.Kind.ToString();
        }
    }

    private (string type, string name) MethodNameOf(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
                var def = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return (TypeName(def.GetDeclaringType()), md.GetString(def.Name));
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                return (TypeName(mr.Parent), md.GetString(mr.Name));
            case HandleKind.MethodSpecification:
                return MethodNameOf(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            default:
                return ("?", "?");
        }
    }

    private string TypeName(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
                var td = md.GetTypeDefinition((TypeDefinitionHandle)h);
                var name = md.GetString(td.Name);
                return td.IsNested ? TypeName(td.GetDeclaringType()) + "+" + name : name;
            case HandleKind.TypeReference:
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                return md.GetString(tr.Name);
            case HandleKind.TypeSpecification:
                var ts = md.GetTypeSpecification((TypeSpecificationHandle)h);
                var reader = md.GetBlobReader(ts.Signature);
                var code = reader.ReadSignatureTypeCode();
                if (code == SignatureTypeCode.GenericTypeInstance)
                {
                    reader.ReadSignatureTypeCode();
                    return TypeName(reader.ReadTypeHandle()) + "<...>";
                }
                return code == SignatureTypeCode.TypeHandle ? TypeName(reader.ReadTypeHandle()) : code.ToString();
            default:
                return h.Kind.ToString();
        }
    }

    private static IEnumerable<(OpCode op, object? operand)> Instructions(BlobReader il)
    {
        while (il.RemainingBytes > 0)
        {
            int b = il.ReadByte();
            OpCode op = b == 0xFE ? TwoByte[il.ReadByte()] : OneByte[b];
            object? operand = op.OperandType switch
            {
                OperandType.InlineNone => null,
                OperandType.ShortInlineBrTarget => Branch(il.ReadSByte()), // branch offsets vary with unrelated edits
                OperandType.InlineBrTarget => Branch(il.ReadInt32()),
                OperandType.ShortInlineI => il.ReadSByte(),
                OperandType.ShortInlineVar => il.ReadByte(),
                OperandType.InlineVar => il.ReadInt16(),
                OperandType.InlineI => il.ReadInt32(),
                OperandType.InlineI8 => il.ReadInt64(),
                OperandType.ShortInlineR => il.ReadSingle(),
                OperandType.InlineR => il.ReadDouble(),
                OperandType.InlineString => MetadataTokens.UserStringHandle(il.ReadInt32()),
                OperandType.InlineSwitch => SkipSwitch(ref il),
                _ => MetadataTokens.EntityHandle(il.ReadInt32()), // method, field, type, tok, sig
            };
            yield return (op, operand);
        }
    }

    private static object Branch(long offset) => "->";

    private static object SkipSwitch(ref BlobReader il)
    {
        int n = il.ReadInt32();
        for (int i = 0; i < n; i++) il.ReadInt32();
        return $"switch({n})";
    }

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static Asm()
    {
        foreach (var f in typeof(OpCodes).GetFields())
        {
            if (f.GetValue(null) is not OpCode op) continue;
            if (op.Size == 1) OneByte[op.Value & 0xFF] = op;
            else TwoByte[op.Value & 0xFF] = op;
        }
    }
}
