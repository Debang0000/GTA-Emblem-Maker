using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GTAEmblemMaker.Bundle
{
    internal static class BundleLauncher
    {
        private const string MarkerName = ".payload-sha256";

        internal static string PreparePayload(Stream payload, string localAppData, string version)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (!payload.CanSeek) throw new ArgumentException("The embedded payload stream must support seeking.", "payload");
            if (String.IsNullOrWhiteSpace(localAppData)) throw new ArgumentException("Local application data path is required.", "localAppData");
            if (String.IsNullOrWhiteSpace(version)) throw new ArgumentException("Bundle version is required.", "version");

            var hash = ComputeSha256(payload);
            var appRoot = Path.GetFullPath(Path.Combine(localAppData, "GTAEmblemMaker", "app"));
            var folder = Path.Combine(appRoot, version + "-" + hash.Substring(0, 12));
            Directory.CreateDirectory(appRoot);
            if (CacheMatches(payload, folder, hash)) return folder;

            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var temporary = Path.Combine(appRoot, ".tmp-" + version + "-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temporary);
                Extract(payload, temporary);
                File.WriteAllText(Path.Combine(temporary, MarkerName), hash);
                if (!CacheMatches(payload, temporary, hash)) throw new InvalidDataException("The extracted application payload failed validation.");

                try
                {
                    Directory.Move(temporary, folder);
                }
                catch (IOException)
                {
                    if (!CacheMatches(payload, folder, hash)) throw;
                }
                return folder;
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }

        internal static void Launch(string packageFolder)
        {
            if (String.IsNullOrWhiteSpace(packageFolder)) throw new ArgumentException("Package folder is required.", "packageFolder");
            var executable = Path.Combine(packageFolder, "GTAEmblemMaker.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("The extracted GTA Emblem Maker application was not found.", executable);
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = packageFolder,
                UseShellExecute = true,
            });
        }

        private static string ComputeSha256(Stream payload)
        {
            payload.Position = 0;
            byte[] digest;
            using (var sha256 = SHA256.Create()) digest = sha256.ComputeHash(payload);
            payload.Position = 0;
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }

        private static bool CacheMatches(Stream payload, string folder, string hash)
        {
            if (!Directory.Exists(folder)) return false;
            var marker = Path.Combine(folder, MarkerName);
            if (!File.Exists(marker) || !String.Equals(File.ReadAllText(marker).Trim(), hash, StringComparison.Ordinal)) return false;

            payload.Position = 0;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                foreach (var entry in archive.Entries)
                {
                    if (IsDirectory(entry)) continue;
                    var file = EntryPath(folder, entry);
                    if (!File.Exists(file) || new FileInfo(file).Length != entry.Length) return false;
                }
            }
            payload.Position = 0;
            return true;
        }

        private static void Extract(Stream payload, string folder)
        {
            payload.Position = 0;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                foreach (var entry in archive.Entries)
                {
                    var destination = EntryPath(folder, entry);
                    if (IsDirectory(entry))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(destination);
                    if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    using (var input = entry.Open())
                    using (var output = File.Create(destination)) input.CopyTo(output);
                    if (new FileInfo(destination).Length != entry.Length) throw new InvalidDataException("An extracted payload file has the wrong length: " + entry.FullName);
                }
            }
            payload.Position = 0;
        }

        private static string EntryPath(string root, ZipArchiveEntry entry)
        {
            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = rootPath + Path.DirectorySeparatorChar;
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(rootPath, relative));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The payload contains an unsafe path: " + entry.FullName);
            }
            return destination;
        }

        private static bool IsDirectory(ZipArchiveEntry entry)
        {
            return String.IsNullOrEmpty(entry.Name)
                || entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
        }
    }
}
