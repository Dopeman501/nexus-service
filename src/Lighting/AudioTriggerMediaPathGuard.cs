using System;
using System.IO;

namespace Nexus.Service.Lighting;

/// <summary>Reject links before following any cache/input file or creating a child through redirected ancestry.</summary>
internal static class AudioTriggerMediaPathGuard
{
    internal static void Check(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try { CheckAttributes(File.GetAttributes(current)); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static void CheckAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Audio Trigger media paths cannot contain symbolic links or reparse points.");
    }
}
