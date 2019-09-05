using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DepScanner
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var dir = Path.GetFullPath(args[0]);
            var input = Path.GetFullPath(args[1]);
            var versionFile = Path.Combine(dir, "eng", "Versions.props");

            Console.WriteLine($"Scanning {dir} with input {input}");

            var dependencies = new List<Dependency>();
            var allProjectFiles = new List<Tuple<string, string>>();
            var srcProjectFiles = new List<Tuple<string, string>>();

            foreach (var file in Directory.GetFiles(dir, "*.*proj", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);

                if (text.Contains("<PackageReference") || text.Contains("<Reference"))
                {
                    allProjectFiles.Add(new Tuple<string, string>(file.Replace(dir, string.Empty), text));
                }
            }

            srcProjectFiles = allProjectFiles.Where(p =>
                !(p.Item1.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}testassets{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}samples{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}perf{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}eng{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}submodules{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}stress{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}benchmark{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}benchmarks{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}FunctionalTests{Path.DirectorySeparatorChar}")
                    || p.Item1.Contains($"{Path.DirectorySeparatorChar}.packages{Path.DirectorySeparatorChar}"))).ToList();

            if (Path.GetFileName(input) == "Dependencies.props")
            {
                var doc = XDocument.Parse(File.ReadAllText(input));
                var versionsDoc = XDocument.Parse(File.ReadAllText(versionFile));

                foreach (var dep in doc.Descendants("LatestPackageReference"))
                {
                    if (string.IsNullOrEmpty(dep.Attribute("Include")?.Value))
                    {
                        // ItemDefinition, skip
                        continue;
                    }

                    var name = dep.Attribute("Include").Value;
                    var versionString = dep.Attribute("Version").Value;

                    if (!NuGetVersion.TryParse(versionString, out var version))
                    {
                        version = ResolveVersion(versionsDoc, versionString.Substring(2, versionString.Length-3));

                        if (version == null)
                        {
                            //Value is not controlled by us, skip
                            continue;
                        }
                    }

                    dependencies.Add(new Dependency
                    {
                        Name = name,
                        CurrentVersion = version
                    });
                }
            }
            else if (Path.GetFileName(input) == "Versions.props")
            {
                var doc = XDocument.Parse(File.ReadAllText(input));

                foreach (var dep in doc.Descendants().Where(d => d.Name.ToString().EndsWith("Version")))
                {
                    var srcFiles = allProjectFiles.Where(s => s.Item2.Contains(dep.Name.LocalName, StringComparison.OrdinalIgnoreCase));

                    if (!srcFiles.Any())
                    {
                        // This version isn't a reference
                        continue;
                    }

                    foreach (var srcFile in srcFiles)
                    {
                        var srcDoc = XDocument.Parse(srcFile.Item2);
                        var srcReference = srcDoc.Descendants().FirstOrDefault(d => d.Attribute("Version")?.Value.Contains(dep.Name.ToString(), StringComparison.OrdinalIgnoreCase) == true);

                        if (srcReference == null)
                        {
                            continue;
                        }

                        var srcName = srcReference.Attribute("Include").Value;

                        dependencies.Add(new Dependency
                        {
                            Name = srcName,
                            CurrentVersion = new NuGetVersion(dep.Value)
                        });

                        break;
                    }
                }
            }

            var providers = new List<Lazy<INuGetResourceProvider>>();
            providers.AddRange(Repository.Provider.GetCoreV3());  // Add v3 API support
            var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
            var sourceRepository = new SourceRepository(packageSource, providers);
            var packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();

            foreach (var dep in dependencies)
            {
                var packageMetadata = await packageMetadataResource.GetMetadataAsync(dep.Name, false, false, NullLogger.Instance, default(CancellationToken));
                dep.LatestVersion = packageMetadata.Select(p => p.Identity.Version).Max();

                if (dep.LatestVersion > dep.CurrentVersion)
                {
                    Console.WriteLine($"{dep.Name} {dep.CurrentVersion} => {dep.LatestVersion}");

                    foreach (var src in srcProjectFiles)
                    {
                        if (src.Item2.Contains(dep.Name))
                        {
                            Console.WriteLine($" - {src.Item1}");
                        }
                    }
                }
            }
        }

        private static NuGetVersion ResolveVersion(XDocument versionFile, string package)
        {
            var versionValue = versionFile.Descendants().FirstOrDefault(d => string.Equals(d.Name.ToString(), package, StringComparison.OrdinalIgnoreCase));

            if (versionValue == null)
            {
                //Value is not controlled by us, skip
                return null;
            }

            if (NuGetVersion.TryParse(versionValue.Value, out var version))
            {
                return version;
            }

            // There could be a recursive value definition
            return ResolveVersion(versionFile, versionValue.Value.Substring(2, versionValue.Value.Length-3));
        }

        private class Dependency
        {
            public string Name {get; set;}
            public NuGetVersion CurrentVersion {get; set;}
            public NuGetVersion LatestVersion {get; set;}
            public List<string> Occurences {get; set;} = new List<string>();
        }
    }
}
