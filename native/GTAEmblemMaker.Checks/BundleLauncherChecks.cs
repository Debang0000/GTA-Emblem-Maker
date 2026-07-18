using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using GTAEmblemMaker.Bundle;

namespace GTAEmblemMaker.Checks
{
    internal static class BundleLauncherChecks
    {
        internal static void Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "GTAEmblemMaker-BundleCheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Check.Equal("v1.1.2", BundleLauncher.VersionLabel(new Version(1, 1, 2, 0)), "bundle version label");
                using (var payload = CreatePayload())
                {
                    var first = BundleLauncher.PreparePayload(payload, root, "v1.1.2");
                    Check.True(Path.GetFileName(first).StartsWith("v1.1.2-", StringComparison.Ordinal), "bundle cache uses version tag");
                    Check.True(File.Exists(Path.Combine(first, "GTAEmblemMaker.exe")), "bundle extracts application");
                    Check.True(File.Exists(Path.Combine(first, "profiles", "v1-beam-clean.json")), "bundle extracts profiles");

                    payload.Position = 0;
                    Check.Equal(first, BundleLauncher.PreparePayload(payload, root, "v1.1.2"), "bundle reuses cache");

                    File.Delete(Path.Combine(first, "profiles", "v1-beam-clean.json"));
                    payload.Position = 0;
                    Check.Equal(first, BundleLauncher.PreparePayload(payload, root, "v1.1.2"), "bundle repairs cache");
                    Check.True(File.Exists(Path.Combine(first, "profiles", "v1-beam-clean.json")), "bundle repair restores file");
                }

                using (var unsafePayload = CreateUnsafePayload())
                {
                    Check.Throws<InvalidDataException>(
                        () => BundleLauncher.PreparePayload(unsafePayload, root, "v1.1.2"),
                        "bundle rejects zip traversal");
                    Check.False(File.Exists(Path.Combine(root, "escape.txt")), "bundle traversal stays contained");
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static MemoryStream CreatePayload()
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                WriteEntry(archive, "GTAEmblemMaker.exe", "application");
                WriteEntry(archive, "profiles/v1-beam-clean.json", "{\"id\":\"v1-beam-clean\"}");
            }
            stream.Position = 0;
            return stream;
        }

        private static MemoryStream CreateUnsafePayload()
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                WriteEntry(archive, "../../escape.txt", "escape");
            }
            stream.Position = 0;
            return stream;
        }

        private static void WriteEntry(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(value);
        }
    }
}
