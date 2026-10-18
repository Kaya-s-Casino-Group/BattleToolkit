using System;
using System.IO;

namespace PokerEngine.Tests
{
    /// <summary>Resolves shared content in a standalone checkout or its Unity submodule checkout.</summary>
    internal static class ToolkitTestFiles
    {
        internal static string Read(string relativePath)
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory })
                for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                    foreach (string prefix in new[] { "", "External/BattleToolkit" })
                    {
                        string candidate = Path.Combine(directory.FullName, prefix, relativePath);
                        if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    }
            throw new FileNotFoundException("Cannot locate shared BattleToolkit test content.", relativePath);
        }
    }
}
