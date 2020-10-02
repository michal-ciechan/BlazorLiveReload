using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;

namespace RazorComponentsPreview
{
    public class Generator
    {
        public Generator()
        {
            Declarations = new Dictionary<string, string>();
            References = new List<MetadataReference>();

            GC.KeepAlive(typeof(EditForm));

            var currentAssemblies = AppDomain.CurrentDomain.GetAssemblies();

            var assembliesToProcess = new Queue<Assembly>(currentAssemblies);

            var seenAssemblies = new Dictionary<string, Assembly>();

            LoadAssemblies(assembliesToProcess, seenAssemblies);

            LoadAllNetFrameworkAssemblies(seenAssemblies);

            LoadWebAssemblyAssembliesIfMissing(seenAssemblies);

            var wa = seenAssemblies.Keys.Where(x => x.Contains("WebAsse")).ToList();

            var assembliesToReference =
                seenAssemblies.Values.Where(assembly => !assembly.IsDynamic && assembly.Location != null)
                    .ToList();

            var httpA = assembliesToReference.Where(x => x.FullName.Contains("System")).OrderBy(x => x.FullName);


            foreach (var assembly in assembliesToReference)
            {
                References.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            BaseCompilation = CSharpCompilation.Create(
                assemblyName: "__Test",
                Array.Empty<SyntaxTree>(),
                References.ToArray(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            References.Add(BaseCompilation.ToMetadataReference());

            FileSystem = new TestRazorProjectFileSystem();
            Engine = RazorProjectEngine.Create(RazorConfiguration.Default, FileSystem, builder =>
            {
                builder.Features.Add(new CompilationTagHelperFeature());
                builder.Features.Add(new DefaultMetadataReferenceFeature() { References = References, });
                CompilerFeatures.Register(builder);
            });
        }

        private void LoadWebAssemblyAssembliesIfMissing(Dictionary<string, Assembly> loadedAssemblies)
        {
            // This seems to be referenced
            // C:\Users\xxx\.nuget\packages\microsoft.aspnetcore.components.webassembly.server\5.0.0-preview.8.20414.8\lib\net5.0

            // Therefore we infer the following from above, using the same nuget package version
            // C:\Users\xxx\.nuget\packages\microsoft.aspnetcore.components.webassembly\5.0.0-preview.8.20414.8\lib\net5.0
            var webAssemblyServer = loadedAssemblies["Microsoft.AspNetCore.Components.WebAssembly.Server"];

            var webAssemblyLocation = webAssemblyServer.Location.Replace(
                "microsoft.aspnetcore.components.webassembly.server",
                "microsoft.aspnetcore.components.webassembly",
                StringComparison.OrdinalIgnoreCase);

            var webAssemblyDir = Directory.GetParent(webAssemblyLocation);

            LoadAssembliesFromDirectory(webAssemblyDir, loadedAssemblies);
        }


        // TODO: better error handling if for whatever reason cant be found
        private void LoadAllNetFrameworkAssemblies(Dictionary<string, Assembly> seenAssemblies)
        {
            var systemNetHttpLocation = seenAssemblies["System.Net.Http"].Location;

            var netFxFolder = Directory.GetParent(systemNetHttpLocation);

            LoadAssembliesFromDirectory(netFxFolder, seenAssemblies);
        }

        private void LoadAssembliesFromDirectory(DirectoryInfo directory, Dictionary<string, Assembly> seenAssemblies)
        {
            var assemblyQueue = new Queue<Assembly>();

            foreach (var file in directory.GetFiles("*.dll"))
            {
                AssemblyName assemblyName;
                try
                {
                    assemblyName = AssemblyName.GetAssemblyName(file.FullName);
                }
                catch (Exception e)
                {
                    Console.WriteLine(
                        $"Could not get assembly name for '{file.FullName}'. Skipping. Exception:\r\n{e}");
                    continue;
                }

                if (seenAssemblies.ContainsKey(assemblyName.Name))
                    continue;

                var assembly = Assembly.Load(assemblyName);

                assemblyQueue.Enqueue(assembly);
            }

            LoadAssemblies(assemblyQueue, seenAssemblies);
        }

        private void LoadAssemblies(Queue<Assembly> assembliesToLoad, Dictionary<string, Assembly> loadedAssemblies)
        {
            while (assembliesToLoad.TryDequeue(out var assembly))
            {
                var assemblyName = assembly.GetName().Name;

                if (!loadedAssemblies.TryAdd(assemblyName, assembly)) continue;

                var refAssemblies = assembly.GetReferencedAssemblies();

                foreach (var refAssemblyName in refAssemblies)
                {
                    if (loadedAssemblies.ContainsKey(refAssemblyName.Name)) continue;

                    Assembly refAssembly;
                    try
                    {
                        refAssembly = Assembly.Load(refAssemblyName);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Could not load Referenced Assembly '{refAssemblyName}'. Skipping. Exception:\r\n{e}");
                        continue;
                    }

                    assembliesToLoad.Enqueue(refAssembly);
                }
            }
        }

        private RazorProjectEngine Engine { get; }
        private TestRazorProjectFileSystem FileSystem { get; }
        private Dictionary<string, string> Declarations { get; }
        private CSharpCompilation BaseCompilation { get; }
        private List<MetadataReference> References { get; }
        public CSharpCompilation GetBaseCompilation => BaseCompilation;
        public List<MetadataReference> GetReferences => References;

        private Dictionary<string, RazorCodeDocument> RazorCodeDocumentCache { get; } = new Dictionary<string, RazorCodeDocument>();
        public void Add(string filePath, string content)
        {
            if (filePath is null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            var item = new TestRazorProjectItem(filePath, fileKind: FileKinds.Component)
            {
                Content = content ?? string.Empty,
            };

            FileSystem.Add(item);
        }


        public RazorCodeDocument Update(string filePath, string content)
        {
            //RazorCodeDocument razorCodeDocument;
            //if (RazorCodeDocumentCache.TryGetValue(content,out razorCodeDocument))
            //{
            //    return RazorCodeDocumentCache.GetValueOrDefault(content);
            //}

            var obj = FileSystem.GetItem(filePath, fileKind: FileKinds.Component);
            if (obj.Exists && obj is TestRazorProjectItem item)
            {
                Declarations.TryGetValue(filePath, out var existing);

                var declaration = Engine.ProcessDeclarationOnly(item);
                var declarationText = declaration.GetCSharpDocument().GeneratedCode;

                // Updating a declaration, create a new compilation
                if (!string.Equals(existing, declarationText, StringComparison.Ordinal))
                {
                    Declarations[filePath] = declarationText;

                    // Yeet the old one.
                    References.RemoveAt(References.Count - 1);

                    var compilation = BaseCompilation.AddSyntaxTrees(Declarations.Select(kvp =>
                        {
                            return CSharpSyntaxTree.ParseText(kvp.Value, path: kvp.Key);
                        }));
                    References.Add(compilation.ToMetadataReference());
                }

                item.Content = content ?? string.Empty;
                var generated = Engine.Process(item);
                //RazorCodeDocumentCache.Add(content, generated);
                return generated;
            }

            throw new InvalidOperationException($"Cannot find item '{filePath}'.");
        }
    }
}