using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;

namespace GGScale.Tests
{
    /// <summary>
    /// Checks the SDK against the server's openapi.yaml, the only contract.
    /// make openapi-check downloads the spec and sets GGSCALE_SPEC.
    /// </summary>
    public class ContractTests
    {
        private static readonly string[] Methods = { "get", "post", "put", "patch", "delete" };

        private readonly ITestOutputHelper _output;

        public ContractTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Each spec operation must occur as Operation = "METHOD path" in
        /// src/GGScale. Each operation whose only security is SecretKey must
        /// be in ServerService.cs, and no other operation may be only there.
        /// </summary>
        [Fact]
        public void OpenApi_operations_have_wrappers_on_the_right_client()
        {
            var specPath = Environment.GetEnvironmentVariable("GGSCALE_SPEC");
            if (string.IsNullOrEmpty(specPath))
            {
                // xUnit 2 has no dynamic skip; the unit suite stays offline.
                _output.WriteLine("GGSCALE_SPEC is not set; not checked. Run make openapi-check.");
                return;
            }

            var sources = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "GGScale"), "*.cs")
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
            var failures = new List<string>();
            var count = 0;
            foreach (var (operation, secretOnly) in SpecOperations(specPath!))
            {
                count++;
                var needle = "Operation = \"" + operation + "\"";
                var files = sources.Where(kv => kv.Value.Contains(needle, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
                if (files.Count == 0)
                {
                    failures.Add(operation + ": no SDK wrapper");
                    continue;
                }
                if (secretOnly && !files.Contains("ServerService.cs"))
                {
                    failures.Add(operation + ": secret-key operation must be on the server client");
                }
                if (!secretOnly && files.Count == 1 && files[0] == "ServerService.cs")
                {
                    failures.Add(operation + ": publishable-key operation is only on the server client");
                }
            }
            _output.WriteLine(count + " spec operations checked.");

            Assert.True(count > 0 && failures.Count == 0,
                count == 0 ? "the spec has no operations" : string.Join(Environment.NewLine, failures));
        }

        private static IEnumerable<(string Operation, bool SecretOnly)> SpecOperations(string specPath)
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(specPath))
            {
                yaml.Load(reader);
            }
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            var paths = (YamlMappingNode)root.Children[new YamlScalarNode("paths")];
            foreach (var path in paths.Children)
            {
                var item = (YamlMappingNode)path.Value;
                foreach (var method in Methods)
                {
                    if (!item.Children.TryGetValue(new YamlScalarNode(method), out var op))
                    {
                        continue;
                    }
                    yield return (method.ToUpperInvariant() + " " + ((YamlScalarNode)path.Key).Value, SecretOnly((YamlMappingNode)op));
                }
            }
        }

        /// <summary>True when every security requirement of the operation is the secret key alone.</summary>
        private static bool SecretOnly(YamlMappingNode op)
        {
            if (!op.Children.TryGetValue(new YamlScalarNode("security"), out var node) || node is not YamlSequenceNode security || security.Children.Count == 0)
            {
                return false;
            }
            return security.Children.All(req =>
                req is YamlMappingNode m && m.Children.Count == 1 && m.Children.ContainsKey(new YamlScalarNode("SecretKey")));
        }

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "GGScale.sln")))
                {
                    return dir.FullName;
                }
            }
            throw new InvalidOperationException("GGScale.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
