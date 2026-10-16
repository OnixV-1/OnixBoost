using System;
using System.IO;
using System.Linq;
using System.Text;

namespace OnixBoost.Services;

/// <summary>
/// winws / winws2 (cygwin) break on non-ASCII absolute paths (Desktop\Новая папка → ?????).
/// Prefer paths relative to the process WorkingDirectory; keep .cmd ASCII-only.
/// </summary>
public static class PathAscii
{
    public static bool RootHasNonAscii =>
        AppPaths.Root.Any(c => c > 127);

    public static string RelFrom(string fromDir, string absolutePath)
    {
        var abs = Path.GetFullPath(absolutePath);
        var from = Path.GetFullPath(fromDir);
        var rel = Path.GetRelativePath(from, abs);
        // winws prefers backslash on Windows
        return rel.Replace('/', '\\');
    }

    public static Encoding CmdEncoding =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
