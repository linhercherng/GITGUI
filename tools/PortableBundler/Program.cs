using System.Runtime.InteropServices;
using Microsoft.NET.HostModel.Bundle;

if (args.Length != 2) throw new ArgumentException("Usage: PortableBundler <ordinary-publish> <new-bundle-directory>");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Existing publications are preserved.");
var files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
    .Select(file => new FileSpec(file, Path.GetRelativePath(input, file))).ToArray();
// Use the pinned SDK's public bundler. The native runtime remains external and self-contained.
var bundler = new Bundler("GitGui.App.exe", output, BundleOptions.None, OSPlatform.Windows,
    Architecture.X64, new Version(10, 0), false, "GitGui.App", false);
Console.WriteLine(bundler.GenerateBundle(files));
foreach (var file in files.Where(file => file.Excluded || Path.GetExtension(file.SourcePath) == ".dll"))
{
    // Retain exact image sidecars for offline inspection and the existing isolated smoke driver.
    // Runtime provenance independently checks the loaded bundle entry against each sidecar.
    var destination = Path.Combine(output, file.BundleRelativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(file.SourcePath, destination);
}
