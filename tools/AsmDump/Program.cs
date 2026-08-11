using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Usage:
//   AsmDump <assembly.dll> [output.txt]                -> dump all types (compact)
//   AsmDump --type <nameSubstr> <assembly.dll>         -> dump matching types incl. nested, bases & Invoke
//   AsmDump --il <nameSubstr> <assembly.dll> [methodSubstr] -> dump IL bodies of matching methods
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: AsmDump <assembly.dll> [output.txt] | AsmDump --type <substr> <assembly.dll> | AsmDump --il <typeSubstr> <assembly.dll> [methodSubstr]");
    return 1;
}

if (args[0] == "--type")
{
    if (args.Length < 3) { Console.Error.WriteLine("AsmDump --type <substr> <assembly.dll>"); return 1; }
    return DumpMatching(args[1], args[2]);
}

if (args[0] == "--il")
{
    if (args.Length < 3) { Console.Error.WriteLine("AsmDump --il <typeSubstr> <assembly.dll> [methodSubstr]"); return 1; }
    var mSub = args.Length > 3 ? args[3] : null;
    return DumpIl(args[1], args[2], mSub);
}

static int DumpIl(string typeSub, string asmPath, string methodSub)
{
    var resolver = MakeResolver(Path.GetFullPath(asmPath));
    var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
    var asm = AssemblyDefinition.ReadAssembly(Path.GetFullPath(asmPath), rp);
    var types = asm.MainModule.Types.SelectMany(t => new[] { t }.Concat(t.NestedTypes));
    foreach (var t in types.Where(t => t.FullName.Contains(typeSub, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"=== {t.FullName} ===");
        foreach (var m in t.Methods.Where(m => methodSub == null || m.Name.Contains(methodSub, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"--- {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))}) ---");
            if (!m.HasBody) { Console.WriteLine("  (no body)"); continue; }
            foreach (var ins in m.Body.Instructions)
            {
                string operand = "";
                switch (ins.Operand)
                {
                    case MethodReference mr: operand = mr.DeclaringType?.Name + "::" + mr.Name; break;
                    case FieldReference fr: operand = fr.DeclaringType?.Name + "::" + fr.Name; break;
                    case string s: operand = "\"" + s + "\""; break;
                    case Instruction target: operand = "IL_" + target.Offset.ToString("X4"); break;
                    default: operand = ins.Operand?.ToString() ?? ""; break;
                }
                Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode.Name,-12} {operand}");
            }
        }
        Console.WriteLine();
    }
    return 0;
}

var asmPath = Path.GetFullPath(args[0]);
var outPath = args.Length > 1 ? Path.GetFullPath(args[1]) : null;

var resolver = MakeResolver(asmPath);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
AssemblyDefinition asm;
try { asm = AssemblyDefinition.ReadAssembly(asmPath, rp); }
catch (Exception ex) { Console.Error.WriteLine($"Failed to read assembly: {ex.Message}"); return 1; }

var sb = new StringBuilder();
sb.AppendLine($"Assembly: {asm.Name.Name} ({asm.Name.Version})");
sb.AppendLine();
foreach (var type in asm.MainModule.Types.OrderBy(t => t.FullName))
    DumpType(sb, type, 0);
if (outPath != null) { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); Console.WriteLine($"Written {sb.Length} chars to {outPath}"); }
else Console.Write(sb.ToString());
return 0;

static int DumpMatching(string substr, string asmPath)
{
    var resolver = MakeResolver(asmPath);
    var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
    var asm = AssemblyDefinition.ReadAssembly(Path.GetFullPath(asmPath), rp);
    var sb = new StringBuilder();
    var matches = AllTypes(asm.MainModule.Types).Where(t => t.FullName.Contains(substr, StringComparison.OrdinalIgnoreCase));
    foreach (var t in matches)
    {
        DumpFull(sb, t, 0);
        sb.AppendLine();
    }
    Console.Write(sb.ToString());
    return 0;
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var t in types)
    {
        yield return t;
        foreach (var n in AllTypes(t.NestedTypes)) yield return n;
    }
}

static void DumpFull(StringBuilder sb, TypeDefinition t, int depth)
{
    var indent = new string(' ', depth * 2);
    var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : "class";
    sb.AppendLine($"{indent}[{kind}] {t.FullName}  <-- {t.BaseType?.FullName}");
    foreach (var m in t.Methods)
        sb.AppendLine($"{indent}  meth : {m.ReturnType.FullName} {m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");
    foreach (var f in t.Fields)
        sb.AppendLine($"{indent}  field: {f.FieldType.FullName} {f.Name}");
}

static DefaultAssemblyResolver MakeResolver(string asmPath)
{
    var resolver = new DefaultAssemblyResolver();
    var dir = Path.GetDirectoryName(asmPath)!;
    resolver.AddSearchDirectory(dir);
    var interopDir = Path.GetFullPath(Path.Combine(dir, ".."));
    if (Directory.Exists(interopDir)) resolver.AddSearchDirectory(interopDir);
    return resolver;
}

static void DumpType(StringBuilder sb, TypeDefinition type, int depth)
{
    var indent = new string(' ', depth * 2);
    var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsAbstract && type.IsSealed ? "static class" : type.IsValueType ? "struct" : "class";
    var baseType = type.BaseType != null && type.BaseType.FullName != "System.Object" ? $" : {type.BaseType.FullName}" : "";

    var n = type.FullName;
    if (n.Contains("/") || n.Contains("<PrivateImplementationDetails>") || n.Contains("__") && type.IsCompilerGenerated())
        return;

    sb.AppendLine($"{indent}[{kind}] {n}{baseType}");

    foreach (var f in type.Fields.Where(x => !x.IsPrivate || x.IsPublic).OrderBy(x => x.Name))
    {
        if (f.IsCompilerGenerated()) continue;
        sb.AppendLine($"{indent}  field: {f.FieldType.FullName} {f.Name}");
    }
    foreach (var p in type.Properties.OrderBy(x => x.Name))
    {
        if (p.GetMethod?.IsCompilerGenerated() == true) continue;
        sb.AppendLine($"{indent}  prop : {p.PropertyType.FullName} {p.Name}");
    }
    foreach (var m in type.Methods.Where(x => !x.IsCompilerGenerated()).OrderBy(x => x.Name))
    {
        var paramsStr = string.Join(", ", m.Parameters.Select(p => $"{p.ParameterType.FullName} {p.Name}"));
        var staticStr = m.IsStatic ? "static " : "";
        sb.AppendLine($"{indent}  meth : {staticStr}{m.ReturnType.FullName} {m.Name}({paramsStr})");
    }
    sb.AppendLine();

    foreach (var nt in type.NestedTypes.OrderBy(t => t.Name))
        DumpType(sb, nt, depth + 1);
}

static class Ext
{
    public static bool IsCompilerGenerated(this ICustomAttributeProvider p)
    {
        try
        {
            return p.HasCustomAttributes &&
                   p.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }
        catch { return false; }
    }
}
