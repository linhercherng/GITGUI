using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace GitGui.Diagnostics;

// Shared with the independent profiler. Identity always comes from the actual loaded
// image (including its SDK bundle entry), never an assumed neighbouring DLL.
internal sealed record AssemblyImage(string FileName, byte[] Bytes, Guid Mvid, Version Version, string Storage, long Offset)
{
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    public static AssemblyImage Read(Assembly assembly)
    {
        var name = assembly.GetName().Name + ".dll";
        var image = string.IsNullOrEmpty(assembly.Location)
            ? ReadBundleEntry(Path.Combine(AppContext.BaseDirectory, "GitGui.App.exe"), name)
            : FromBytes(name, File.ReadAllBytes(assembly.Location), "file", 0);
        if (image.Mvid != assembly.ManifestModule.ModuleVersionId || image.Version != assembly.GetName().Version)
            throw new InvalidDataException("Loaded assembly does not match the recorded image: " + name);
        // Portable bundles retain these nine small sidecars for offline candidate inspection.
        // They are evidence only; verify against the actual bundle rather than assuming equality.
        if (image.Storage == "bundle")
        {
            var sidecar = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.ReadAllBytes(sidecar).AsSpan().SequenceEqual(image.Bytes))
                throw new InvalidDataException("Bundle/inspection sidecar mismatch: " + name);
        }
        return image;
    }

    internal static AssemblyImage FromBytes(string name, byte[] bytes, string storage, long offset)
    {
        using var reader = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = reader.GetMetadataReader();
        return new(name, bytes, metadata.GetGuid(metadata.GetModuleDefinition().Mvid), metadata.GetAssemblyDefinition().Version, storage, offset);
    }

    internal static AssemblyImage ReadBundleEntry(string path, string name)
    {
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name != Path.GetFileName(name) || !name.EndsWith(".dll", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid assembly entry name.");
        using var stream = File.OpenRead(path);
        int prefixLength;
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
            prefixLength = pe.PEHeaders.SectionHeaders.Max(s => checked(s.PointerToRawData + s.SizeOfRawData));
        if (prefixLength <= 0 || prefixLength > 64 * 1024 * 1024 || prefixLength > stream.Length)
            throw new InvalidDataException("Invalid bundle native prefix.");
        stream.Position = 0;
        var prefix = new byte[prefixLength]; stream.ReadExactly(prefix);
        var marker = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        var index = prefix.AsSpan().IndexOf(marker);
        if (index < 8 || prefix.AsSpan(index + marker.Length).IndexOf(marker) >= 0)
            throw new InvalidDataException("Missing or ambiguous SDK bundle marker.");
        var header = BitConverter.ToInt64(prefix, index - 8);
        if (header < prefixLength || header >= stream.Length) throw new InvalidDataException("Invalid SDK bundle header offset.");
        stream.Position = header;
        using var manifest = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (manifest.ReadUInt32() != 6 || manifest.ReadUInt32() != 0) throw new InvalidDataException("Unsupported SDK bundle format.");
        var count = manifest.ReadInt32();
        if (count < 1 || count > 10000) throw new InvalidDataException("Invalid SDK bundle entry count.");
        _ = ReadString(manifest);
        for (var i = 0; i < 5; i++) _ = manifest.ReadInt64(); // deps/runtime locations, flags
        (long Offset, long Length)? match = null;
        for (var i = 0; i < count; i++)
        {
            var offset = manifest.ReadInt64(); var length = manifest.ReadInt64(); var compressed = manifest.ReadInt64();
            var kind = manifest.ReadByte(); var relative = ReadString(manifest);
            if (relative != name) continue;
            if (match is not null || kind != 1 || compressed != 0 || length <= 0 || length > 256 * 1024 * 1024 ||
                offset < prefixLength || offset > header || length > header - offset)
                throw new InvalidDataException("Invalid, duplicate or compressed assembly entry: " + name);
            match = (offset, length);
        }
        if (match is not { } entry) throw new InvalidDataException("Assembly absent from SDK bundle: " + name);
        stream.Position = entry.Offset;
        var bytes = new byte[checked((int)entry.Length)]; stream.ReadExactly(bytes);
        return FromBytes(name, bytes, "bundle", entry.Offset);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 4096) throw new InvalidDataException("Invalid SDK bundle string length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
