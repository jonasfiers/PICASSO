using System;
using System.IO;

namespace Picasso.Core.Tests;

internal static class SamplePaths
{
    private static readonly string SamplesDir = FindSamplesDir();

    public static string Catalog74(string fileName) => Path.Combine(SamplesDir, "catalog74", fileName);
    public static string Data(string fileName) => Path.Combine(SamplesDir, "data", fileName);
    public static string Root(string fileName) => Path.Combine(SamplesDir, fileName);

    private static string FindSamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "Picasso.Core", "Samples");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("Could not locate src/Picasso.Core/Samples by walking up from the test binary.");
    }
}
