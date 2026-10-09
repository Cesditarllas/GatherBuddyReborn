using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GatherBuddy.Plugin;

namespace GatherBuddy.Config;

internal static class FullBackup
{
    private const string Manifest = "GatherBuddyReborn full backup v1";
    private const long MaxImportBytes = 128L * 1024 * 1024;

    internal static string Export()
    {
        GatherBuddy.Config.SaveIfDirty(true);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var directory = Directory.Exists(desktop) ? desktop : Dalamud.PluginInterface.ConfigDirectory.Parent!.FullName;
        var path = Path.Combine(directory, $"GatherBuddyReborn-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        WriteArchive(path);
        return path;
    }

    internal static string Import(string archivePath)
    {
        GatherBuddy.Config.SaveIfDirty(true);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 512)
            throw new InvalidDataException("Backup contains too many files.");
        if (archive.GetEntry("manifest.txt") is not { } manifestEntry)
            throw new InvalidDataException("This is not a GatherBuddy Reborn full backup.");
        if (manifestEntry.Length > 128)
            throw new InvalidDataException("Invalid backup manifest.");
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            if (reader.ReadToEnd() != Manifest)
                throw new InvalidDataException("Unsupported backup format.");

        var configFile = Dalamud.PluginInterface.ConfigFile.FullName;
        var dataDirectory = Dalamud.PluginInterface.ConfigDirectory.FullName;
        var dataRoot = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var imported = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "manifest.txt"))
        {
            string destination;
            if (entry.FullName == "config.json")
                destination = configFile;
            else if (entry.FullName.StartsWith("data/", StringComparison.Ordinal))
            {
                var relative = entry.FullName[5..].Replace('/', Path.DirectorySeparatorChar);
                destination = Path.GetFullPath(Path.Combine(dataDirectory, relative));
                if (!destination.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup contains an invalid file path.");
            }
            else
                throw new InvalidDataException("Backup contains an unexpected file.");

            if (entry.Length < 0 || entry.Length > MaxImportBytes || (totalBytes += entry.Length) > MaxImportBytes || imported.ContainsKey(destination))
                throw new InvalidDataException("Backup is too large or contains duplicate files.");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var block = new byte[81920];
            int read;
            while ((read = stream.Read(block)) > 0)
            {
                if (buffer.Length + read > MaxImportBytes)
                    throw new InvalidDataException("Backup exceeds the import size limit.");
                buffer.Write(block, 0, read);
            }
            imported.Add(destination, buffer.ToArray());
        }
        if (!imported.ContainsKey(configFile))
            throw new InvalidDataException("Backup does not contain the main configuration.");

        var safetyCopy = Path.Combine(Dalamud.PluginInterface.ConfigDirectory.Parent!.FullName,
            $"GatherBuddyReborn-before-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        WriteArchive(safetyCopy);
        foreach (var (path, contents) in imported)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }
        return safetyCopy;
    }

    private static void WriteArchive(string archivePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("manifest.txt");
        using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8)) writer.Write(Manifest);

        var configFile = Dalamud.PluginInterface.ConfigFile.FullName;
        if (File.Exists(configFile)) archive.CreateEntryFromFile(configFile, "config.json", CompressionLevel.Optimal);
        var dataDirectory = Dalamud.PluginInterface.ConfigDirectory.FullName;
        if (!Directory.Exists(dataDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
            archive.CreateEntryFromFile(file,
                "data/" + Path.GetRelativePath(dataDirectory, file).Replace('\\', '/'), CompressionLevel.Optimal);
    }
}
