using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Reads a .NET assembly's metadata tables directly (no dependency resolution needed,
// works fully offline). Dumps every SleepyNodes node type (State_*/Event_*) together
// with its public instance fields and their declared field names, in IL2CPP-interop
// friendly form (strips NativeFieldInfoPtr_/NativeMethodInfoPtr_ noise).
//
// Usage: NodeMetaDump <path-to-Assembly-CSharp.dll> [filter]
//   filter: substring matched against type name (default: SleepyNodes.)

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: NodeMetaDump <assembly.dll> [typeFilter]");
            Environment.Exit(2);
        }
        string path = args[0];
        string filter = args.Length > 1 ? args[1] : "SleepyNodes.";
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("file not found: " + path);
            Environment.Exit(2);
        }

        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        // Build type-name -> field list (public + non-static instance fields that are
        // declared on that exact type, not inherited).
        var rows = new List<string>();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            TypeDefinition td = md.GetTypeDefinition(typeHandle);
            string name = GetFullName(md, td);
            if (name == null || name.IndexOf(filter, StringComparison.Ordinal) < 0) continue;

            var fields = new List<string>();
            foreach (var fh in td.GetFields())
            {
                FieldDefinition fd = md.GetFieldDefinition(fh);
                FieldAttributes attrs = fd.Attributes;
                // skip literal (const) and static
                if ((attrs & FieldAttributes.Static) != 0) continue;
                if ((attrs & FieldAttributes.Literal) != 0) continue;
                string fname = md.GetString(fd.Name);
                if (string.IsNullOrEmpty(fname)) continue;
                if (fname.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)) continue;
                if (fname.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)) continue;
                string ftype = fd.DecodeSignature(new FieldSigProvider(), null);
                fields.Add(fname + " : " + ftype);
            }

            // base type name
            string baseName = "?";
            try
            {
                var baseHandle = td.BaseType;
                if (!baseHandle.IsNil)
                    baseName = GetTypeName(md, baseHandle);
            }
            catch { }

            rows.Add("TYPE " + name + " : " + baseName + "  fields=" + fields.Count);
            foreach (var f in fields.OrderBy(x => x, StringComparer.Ordinal)) rows.Add("    " + f);
        }

        rows.Sort(StringComparer.Ordinal);
        foreach (var r in rows) Console.WriteLine(r);
    }

    private static string GetFullName(MetadataReader md, TypeDefinition td)
    {
        string ns = md.GetString(td.Namespace);
        string n = md.GetString(td.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    private static string GetTypeName(MetadataReader md, EntityHandle handle)
    {
        try
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                {
                    var td = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    return GetFullName(md, td);
                }
                case HandleKind.TypeReference:
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)handle);
                    string ns = md.GetString(tr.Namespace);
                    string n = md.GetString(tr.Name);
                    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                }
                case HandleKind.TypeSpecification:
                    return "TypeSpec";
                default:
                    return handle.Kind.ToString();
            }
        }
        catch { return "?"; }
    }

    private sealed class FieldSigProvider : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => GetFullName(reader, reader.GetTypeDefinition(handle));
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            string ns = reader.GetString(tr.Namespace);
            string n = reader.GetString(tr.Name);
            return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        }
        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "TypeSpec";
    }
}
