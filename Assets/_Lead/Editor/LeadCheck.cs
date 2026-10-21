using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace PawNoonLead
{
    /// <summary>
    /// Fast C# check without a domain reload: compiles one project assembly in memory with the Roslyn that ships in
    /// Assets/Plugins/NuGet, against the exact references and defines Unity used for its last build of that assembly.
    /// Bridge: "call PawNoonLead.LeadCheck.Check PawNoon.Runtime" -> Lead/check.log, result line "ok 0 errors" or
    /// "FAILED n errors". About a second per assembly once Roslyn is warm; nothing under Assets is touched.
    /// Source files are taken from the asmdef folder on disk (so files added since the last Unity compile count).
    /// </summary>
    public static class LeadCheck
    {
        private static readonly Dictionary<string, MetadataReference> refCache = new Dictionary<string, MetadataReference>();

        public static string Check(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) assemblyName = "PawNoon.Runtime";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string logPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Lead", "check.log"));
            var log = new StringBuilder();
            try
            {
                var asm = CompilationPipeline.GetAssemblies(AssembliesType.Editor).FirstOrDefault(a => a.name == assemblyName);
                if (asm == null) return "FAILED no assembly " + assemblyName;
                string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
                var sources = SourceFiles(asm, asmdef);
                var parse = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: asm.defines);
                var trees = sources.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f)).ToList();
                var refs = new List<MetadataReference>();
                foreach (var r in asm.compiledAssemblyReferences) refs.Add(Ref(r));
                foreach (var r in asm.assemblyReferences) if (File.Exists(r.outputPath)) refs.Add(Ref(r.outputPath));
                var opts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: asm.compilerOptions.AllowUnsafeCode,
                    nullableContextOptions: NullableContextOptions.Disable, warningLevel: 1);
                var comp = CSharpCompilation.Create(assemblyName + ".check", trees, refs, opts);
                var diags = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                log.AppendLine("=== check " + assemblyName + " " + DateTime.Now.ToString("HH:mm:ss") + " files=" + sources.Count + " refs=" + refs.Count);
                foreach (var d in diags.Take(40))
                {
                    var pos = d.Location.GetLineSpan();
                    log.AppendLine(Rel(pos.Path) + "(" + (pos.StartLinePosition.Line + 1) + "," + (pos.StartLinePosition.Character + 1) + "): " + d.Id + ": " + d.GetMessage());
                }
                log.AppendLine("=== " + diags.Count + " errors in " + sw.ElapsedMilliseconds + " ms");
                File.WriteAllText(logPath, log.ToString());
                return (diags.Count == 0 ? "ok 0 errors" : "FAILED " + diags.Count + " errors") + " (" + sources.Count + " files, " + sw.ElapsedMilliseconds + " ms) -> Lead/check.log";
            }
            catch (Exception e)
            {
                log.AppendLine("check crashed: " + e);
                File.WriteAllText(logPath, log.ToString());
                return "FAILED check crashed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Checks every PawNoon.* assembly in dependency order; stops at the first one with errors.</summary>
        public static string CheckAll()
        {
            string[] order = { "PawNoon.Core", "PawNoon.Runtime", "PawNoon.Editor", "PawNoon.Tests.EditMode", "PawNoon.Tests.Runtime" };
            var sb = new StringBuilder();
            foreach (var a in order)
            {
                string r = Check(a);
                sb.Append(a).Append(": ").Append(r).Append(" | ");
                if (r.StartsWith("FAILED")) break;
            }
            return sb.ToString();
        }

        private static MetadataReference Ref(string path)
        {
            MetadataReference m;
            if (!refCache.TryGetValue(path, out m)) { m = MetadataReference.CreateFromFile(path); refCache[path] = m; }
            return m;
        }

        /// <summary>All .cs under the asmdef folder, skipping subfolders that own another asmdef (they are other assemblies).</summary>
        private static List<string> SourceFiles(Assembly asm, string asmdefPath)
        {
            if (string.IsNullOrEmpty(asmdefPath)) return asm.sourceFiles.Select(Path.GetFullPath).ToList();
            string root = Path.GetFullPath(Path.GetDirectoryName(asmdefPath));
            var files = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                files.AddRange(Directory.GetFiles(d, "*.cs"));
                foreach (var sub in Directory.GetDirectories(d))
                {
                    if (Path.GetFileName(sub).StartsWith(".")) continue;
                    if (Directory.GetFiles(sub, "*.asmdef").Length > 0 || Directory.GetFiles(sub, "*.asmref").Length > 0) continue;
                    stack.Push(sub);
                }
            }
            return files;
        }

        private static string Rel(string p)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            p = Path.GetFullPath(p);
            return p.StartsWith(root) ? p.Substring(root.Length + 1) : p;
        }
    }
}
