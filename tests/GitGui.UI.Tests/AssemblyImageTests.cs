using System.Reflection.PortableExecutable;
using System.Text;
using GitGui.Diagnostics;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class AssemblyImageTests
{
    [Fact]
    public void Physical_image_identity_matches_loaded_module()
    {
        var assembly = typeof(AssemblyImageTests).Assembly;
        var image = AssemblyImage.Read(assembly);
        Assert.Equal(assembly.ManifestModule.ModuleVersionId, image.Mvid);
        Assert.Equal("file", image.Storage);
        Assert.Equal(File.ReadAllBytes(assembly.Location), image.Bytes);
    }

    [Fact]
    public void Bundle_identity_comes_from_exact_manifest_entry_bytes()
    {
        using var bundle = new TestBundle();
        var image = AssemblyImage.ReadBundleEntry(bundle.Path, "Test.dll");
        Assert.Equal(bundle.Payload, image.Bytes);
        Assert.Equal(typeof(AssemblyImageTests).Assembly.ManifestModule.ModuleVersionId, image.Mvid);
        Assert.Equal("bundle", image.Storage);
        Assert.Equal(bundle.Offset, image.Offset);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("compressed")]
    [InlineData("kind")]
    [InlineData("bounds")]
    [InlineData("version")]
    [InlineData("marker")]
    [InlineData("string")]
    public void Malformed_or_ambiguous_bundle_fails_closed(string fault)
    {
        using var bundle = new TestBundle(fault);
        Assert.Throws<InvalidDataException>(() => AssemblyImage.ReadBundleEntry(bundle.Path, "Test.dll"));
    }

    [Theory]
    [InlineData("../Test.dll")]
    [InlineData("..\\Test.dll")]
    [InlineData("Test.exe")]
    public void Entry_names_cannot_escape_or_select_an_executable(string name)
    {
        using var bundle = new TestBundle();
        Assert.Throws<InvalidDataException>(() => AssemblyImage.ReadBundleEntry(bundle.Path, name));
    }

    private sealed class TestBundle : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitgui-bundle-test-" + Guid.NewGuid().ToString("N"));
        public byte[] Payload { get; } = File.ReadAllBytes(typeof(AssemblyImageTests).Assembly.Location);
        public long Offset { get; }
        public TestBundle(string? fault = null)
        {
            var prefix = Payload.ToArray();
            using (var pe = new PEReader(new MemoryStream(prefix)))
                Assert.True(pe.PEHeaders.PEHeaderStartOffset >= 104);
            Offset = prefix.Length;
            var header = Offset + Payload.Length;
            BitConverter.TryWriteBytes(prefix.AsSpan(64, 8), header);
            Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE").CopyTo(prefix, 72);
            if (fault == "marker") prefix[72] ^= 1;
            using var stream = File.Create(Path);
            stream.Write(prefix); stream.Write(Payload);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(fault == "version" ? 7u : 6u); writer.Write(0u);
            writer.Write(fault == "duplicate" ? 2 : 1);
            if (fault == "string") { writer.Write7BitEncodedInt(4097); return; }
            writer.Write("test-bundle");
            for (var i = 0; i < 5; i++) writer.Write(0L);
            for (var i = 0; i < (fault == "duplicate" ? 2 : 1); i++)
            {
                writer.Write(fault == "bounds" ? header : Offset); writer.Write((long)Payload.Length);
                writer.Write(fault == "compressed" ? 1L : 0L);
                writer.Write(fault == "kind" ? (byte)3 : (byte)1);
                writer.Write(fault == "missing" ? "Other.dll" : "Test.dll");
            }
        }
        public void Dispose() => File.Delete(Path);
    }
}

