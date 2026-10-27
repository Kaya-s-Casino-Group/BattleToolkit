using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace PokerEngine
{
    /// <summary>Assembles explicitly imported XML documents before semantic compilation.
    /// The resolver receives normalized, case-sensitive paths relative to the bundle root.
    /// Unity can resolve TextAssets without giving the rules engine a Unity or filesystem dependency.</summary>
    public static class BattleContentLoader
    {
        public const int MaximumFiles = 128;
        public const int MaximumImportDepth = 16;
        public const int MaximumBundleCharacters = 8 * 1024 * 1024;
        public const int MaximumBundleNodes = 100000;

        public static BattleContentReadResult Load(string entryPath, Func<string, string> readText)
        {
            if (readText == null) throw new ArgumentNullException(nameof(readText));
            return new Assembly(readText).Load(entryPath);
        }

        /// <summary>Disk convenience adapter. Imports are relative to their declaring file and stay within the entry directory.</summary>
        public static BattleContentReadResult LoadFile(string entryPath)
        {
            string full = Path.GetFullPath(entryPath);
            string root = Path.GetDirectoryName(full);
            return Load(Path.GetFileName(full), name => File.ReadAllText(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar))));
        }

        /// <summary>Writes an assembled source as a compatible single-file document. Compile before exporting for semantic validation.</summary>
        public static string ExportXml(BattleContentSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var text = new StringBuilder();
            using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\n" }))
            {
                Action<BattleContentNode> write = null;
                write = node => {
                    writer.WriteStartElement(node.Name);
                    foreach (var attribute in node.Attributes) writer.WriteAttributeString(attribute.Key, attribute.Value);
                    foreach (var child in node.Children) write(child);
                    writer.WriteEndElement();
                };
                write(source.Root);
            }
            return text.ToString();
        }

        private sealed class Assembly
        {
            private readonly Func<string, string> readText;
            private readonly List<BattleDiagnostic> diagnostics = new List<BattleDiagnostic>();
            private readonly HashSet<string> loaded = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> active = new List<string>();
            private readonly Dictionary<string, List<BattleContentNode>> definitions = new Dictionary<string, List<BattleContentNode>>(StringComparer.Ordinal);
            private readonly Dictionary<string, BattleContentNode> sectionSources = new Dictionary<string, BattleContentNode>(StringComparer.Ordinal);
            private BattleContentNode entry;
            private int characters, nodes;
            internal Assembly(Func<string, string> readText) { this.readText = readText; }

            internal BattleContentReadResult Load(string entryPath)
            {
                var location = new BattleSourceLocation(entryPath, 0, 0, "/");
                string path = ResolvePath("", entryPath, location);
                if (path != null) Visit(path, location);
                if (entry == null || diagnostics.Count > 0) return new BattleContentReadResult(null, diagnostics);
                var sections = definitions.Select(pair => new BattleContentNode(pair.Key, sectionSources[pair.Key].Attributes,
                    pair.Value, sectionSources[pair.Key].Location));
                return new BattleContentReadResult(new BattleContentSource(new BattleContentNode(entry.Name, entry.Attributes, sections, entry.Location)), diagnostics);
            }

            private void Visit(string path, BattleSourceLocation importLocation)
            {
                if (active.Contains(path)) { Error("IMPORT_CYCLE", "Circular import: " + string.Join(" -> ", active.Concat(new[] { path })), importLocation); return; }
                if (loaded.Contains(path)) return; // A shared dependency is loaded once, even through a diamond.
                if (loaded.Count >= MaximumFiles) { Error("IMPORT_FILE_LIMIT", "Too many imported files.", importLocation); return; }
                if (active.Count >= MaximumImportDepth) { Error("IMPORT_DEPTH_LIMIT", "Import nesting exceeds the limit.", importLocation); return; }
                loaded.Add(path);
                string xml;
                try { xml = readText(path); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is KeyNotFoundException)
                { Error("IMPORT_READ", "Cannot read '" + path + "': " + exception.Message, importLocation); return; }
                if (xml == null) { Error("IMPORT_READ", "Cannot resolve '" + path + "'.", importLocation); return; }
                characters = checked(characters + xml.Length);
                if (characters > MaximumBundleCharacters) { Error("IMPORT_SIZE_LIMIT", "Imported XML exceeds the bundle size limit.", importLocation); return; }
                var parsed = BattleContentXmlReader.Read(xml, path);
                diagnostics.AddRange(parsed.Diagnostics);
                if (!parsed.Success) return;
                var root = parsed.Source.Root;
                nodes += CountNodes(root);
                if (nodes > MaximumBundleNodes) { Error("IMPORT_NODE_LIMIT", "Imported XML exceeds the bundle node limit.", root.Location); return; }
                if (entry == null) entry = root;
                foreach (var attribute in root.Attributes.Keys.Where(k => k != "schemaVersion" && k != "id" && k != "version" && k != "name"))
                    Error("UnknownAttribute", "Unknown content attribute: " + attribute, root.Location);
                active.Add(path);
                var localSections = new HashSet<string>(StringComparer.Ordinal);
                foreach (var child in root.Children)
                {
                    if (child.Name == "Import")
                    {
                        string target;
                        if (child.Children.Count != 0 || child.Attributes.Count != 1 || !child.Attributes.TryGetValue("path", out target))
                        { Error("IMPORT_FORMAT", "Import requires only a path attribute and no children.", child.Location); continue; }
                        string resolved = ResolvePath(path, target, child.Location);
                        if (resolved != null) Visit(resolved, child.Location);
                        continue;
                    }
                    if (!BattleContentBuildContext.Sections.Contains(child.Name))
                    { Error("UnknownElement", "Unknown content section: " + child.Name, child.Location); continue; }
                    if (!localSections.Add(child.Name))
                    { Error("DuplicateSection", "Duplicate section in one file: " + child.Name, child.Location); continue; }
                    if (child.Attributes.Count != 0)
                    { Error("UnknownAttribute", "Content sections do not accept attributes.", child.Location); continue; }
                    if (!definitions.ContainsKey(child.Name))
                    { definitions.Add(child.Name, new List<BattleContentNode>()); sectionSources.Add(child.Name, child); }
                    definitions[child.Name].AddRange(child.Children); // Retain each definition's original filename/line/path.
                }
                active.RemoveAt(active.Count - 1);
            }

            private string ResolvePath(string importer, string target, BattleSourceLocation location)
            {
                if (string.IsNullOrWhiteSpace(target)) { Error("IMPORT_PATH", "Import path cannot be empty.", location); return null; }
                target = target.Trim().Replace('\\', '/');
                if (target.StartsWith("/", StringComparison.Ordinal) || target.IndexOf(':') >= 0 || target.IndexOf('\0') >= 0)
                { Error("IMPORT_PATH", "Use a relative bundle path, not an absolute path or URL: " + target, location); return null; }
                var parts = importer.Split('/').Take(Math.Max(0, importer.Split('/').Length - 1)).ToList();
                foreach (string part in target.Split('/'))
                {
                    if (part == "" || part == ".") continue;
                    if (part == "..")
                    {
                        if (parts.Count == 0) { Error("IMPORT_PATH", "Import escapes the bundle root: " + target, location); return null; }
                        parts.RemoveAt(parts.Count - 1);
                    }
                    else parts.Add(part);
                }
                if (parts.Count == 0) { Error("IMPORT_PATH", "Import must name a file.", location); return null; }
                return string.Join("/", parts);
            }
            private static int CountNodes(BattleContentNode node) => 1 + node.Children.Sum(CountNodes);
            private void Error(string code, string message, BattleSourceLocation location) => diagnostics.Add(
                new BattleDiagnostic(code, BattleDiagnosticSeverity.Error, message, location));
        }
    }
}
