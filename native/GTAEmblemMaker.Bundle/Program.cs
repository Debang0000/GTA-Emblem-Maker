using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace GTAEmblemMaker.Bundle
{
    internal static class Program
    {
        private const string PayloadResource = "GTAEmblemMaker.Payload.zip";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 1 || (args.Length == 1 && !String.Equals(args[0], "--prepare-only", StringComparison.Ordinal)))
                {
                    throw new ArgumentException("Unknown launcher argument.");
                }

                var assembly = Assembly.GetExecutingAssembly();
                var version = BundleLauncher.VersionLabel(assembly.GetName().Version);
                string folder;
                using (var payload = assembly.GetManifestResourceStream(PayloadResource))
                {
                    if (payload == null) throw new InvalidDataException("The embedded GTA Emblem Maker payload is missing.");
                    folder = BundleLauncher.PreparePayload(payload, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), version);
                }

                if (args.Length == 0) BundleLauncher.Launch(folder);
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "GTA Emblem Maker", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
