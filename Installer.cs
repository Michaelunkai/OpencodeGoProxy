using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace OpencodeGoProxy.Installer
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Ask user for install location
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select installation folder for OpenCode Go Proxy";
                fbd.RootFolder = Environment.SpecialFolder.ProgramFiles;
                if (fbd.ShowDialog() != DialogResult.OK)
                    return;

                string installDir = fbd.SelectedPath;

                try
                {
                    // Extract embedded files
                    ExtractFiles(installDir);

                    MessageBox.Show(
                        "OpenCode Go Proxy installed successfully!\n\n" +
                        "Location: " + installDir + "\n\n" +
                        "Run OpencodeGoProxy.exe to start the proxy.\n" +
                        "Add your API keys to api.txt before starting.",
                        "Installation Complete",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    // Launch the proxy
                    System.Diagnostics.Process.Start(Path.Combine(installDir, "OpencodeGoProxy.exe"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Installation failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        static void ExtractFiles(string installDir)
        {
            if (!Directory.Exists(installDir))
                Directory.CreateDirectory(installDir);

            // Extract files from embedded resources
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            foreach (string resourceName in resourceNames)
            {
                if (!resourceName.StartsWith("OpencodeGoProxy.Installer.Resources."))
                    continue;

                string fileName = resourceName.Substring("OpencodeGoProxy.Installer.Resources.".Length);
                string filePath = Path.Combine(installDir, fileName);

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                {
                    stream.CopyTo(fs);
                }
            }
        }
    }
}
