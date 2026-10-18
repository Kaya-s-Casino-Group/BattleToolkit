using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    public enum BattleDiagnosticSeverity { Error = 1, Warning }

    /// <summary>Authoring location, retained through content validation and assembly.</summary>
    public sealed class BattleSourceLocation
    {
        public string SourceName { get; }
        public int Line { get; }
        public int Column { get; }
        public string Path { get; }
        public BattleSourceLocation(string sourceName, int line, int column, string path)
        {
            if (line < 0 || column < 0) throw new ArgumentOutOfRangeException(nameof(line));
            SourceName = sourceName ?? "content.xml"; Line = line; Column = column; Path = path ?? "/";
        }
    }

    public sealed class BattleDiagnostic
    {
        public string Code { get; }
        public BattleDiagnosticSeverity Severity { get; }
        public string Message { get; }
        public BattleSourceLocation Location { get; }
        public string DefinitionId { get; }
        public string FieldPath { get; }
        public BattleDiagnostic(string code, BattleDiagnosticSeverity severity, string message, BattleSourceLocation location,
            string definitionId = null, string fieldPath = null)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(message) || location == null ||
                !Enum.IsDefined(typeof(BattleDiagnosticSeverity), severity)) throw new ArgumentException("Invalid content diagnostic.");
            Code = code; Severity = severity; Message = message; Location = location;
            DefinitionId = definitionId; FieldPath = fieldPath;
        }
    }

    /// <summary>Immutable XML syntax tree. Semantic names and supported fields are checked by the compiler.</summary>
    public sealed class BattleContentNode
    {
        public string Name { get; }
        public IReadOnlyDictionary<string, string> Attributes { get; }
        public IReadOnlyList<BattleContentNode> Children { get; }
        public BattleSourceLocation Location { get; }
        public BattleContentNode(string name, IEnumerable<KeyValuePair<string, string>> attributes,
            IEnumerable<BattleContentNode> children, BattleSourceLocation location)
        {
            if (string.IsNullOrWhiteSpace(name) || location == null) throw new ArgumentException("Invalid content node.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in attributes ?? Array.Empty<KeyValuePair<string, string>>())
            {
                if (string.IsNullOrWhiteSpace(field.Key) || field.Value == null) throw new ArgumentException("Invalid content attribute.");
                fields.Add(field.Key, field.Value);
            }
            var entries = (children ?? Array.Empty<BattleContentNode>()).ToArray();
            if (entries.Any(child => child == null)) throw new ArgumentException("A content child cannot be null.");
            Name = name; Attributes = new ReadOnlyDictionary<string, string>(fields);
            Children = Array.AsReadOnly(entries); Location = location;
        }
    }

    public sealed class BattleContentSource
    {
        public BattleContentNode Root { get; }
        public BattleContentSource(BattleContentNode root)
        { Root = root ?? throw new ArgumentNullException(nameof(root)); }
    }

    public sealed class BattleContentReadResult
    {
        public BattleContentSource Source { get; }
        public IReadOnlyList<BattleDiagnostic> Diagnostics { get; }
        public bool Success => Source != null && !Diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error);
        public BattleContentReadResult(BattleContentSource source, IEnumerable<BattleDiagnostic> diagnostics)
        {
            var entries = (diagnostics ?? Array.Empty<BattleDiagnostic>()).ToArray();
            if (entries.Any(item => item == null)) throw new ArgumentException("A diagnostic cannot be null.");
            Source = source; Diagnostics = Array.AsReadOnly(entries);
        }
    }
}
