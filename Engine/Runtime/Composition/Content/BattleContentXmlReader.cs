using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace PokerEngine
{
    /// <summary>Reads only supplied text; it never loads files, schemas, includes or external entities.</summary>
    public static class BattleContentXmlReader
    {
        public const int MaximumDocumentCharacters = 1024 * 1024;
        public const int MaximumElementDepth = 32;
        public const int MaximumNodes = 20000;
        public const int MaximumAttributesPerElement = 64;

        public static BattleContentReadResult Read(string xml, string sourceName = "content.xml")
        {
            var diagnostics = new List<BattleDiagnostic>();
            var fallback = new BattleSourceLocation(sourceName, 0, 0, "/");
            if (xml == null || xml.Length == 0)
                return Failure("XML_EMPTY", "XML content is empty.", fallback);
            if (xml.Length > MaximumDocumentCharacters)
                return Failure("XML_SIZE_LIMIT", "XML content exceeds the document character limit.", fallback);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = false,
                MaxCharactersInDocument = MaximumDocumentCharacters,
                MaxCharactersFromEntities = 1024,
                ConformanceLevel = ConformanceLevel.Document
            };
            var stack = new Stack<NodeBuilder>();
            BattleContentNode root = null;
            int count = 0;
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    while (reader.Read())
                    {
                        var location = Location(reader, sourceName, stack.Count == 0 ? "/" : stack.Peek().Location.Path);
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Element:
                                if (++count > MaximumNodes) return Failure("XML_NODE_LIMIT", "XML content exceeds the element count limit.", location);
                                if (stack.Count >= MaximumElementDepth) return Failure("XML_DEPTH_LIMIT", "XML content exceeds the element depth limit.", location);
                                if (reader.AttributeCount > MaximumAttributesPerElement)
                                    return Failure("XML_ATTRIBUTE_LIMIT", "An element exceeds the attribute count limit.", location);
                                string path = stack.Count == 0 ? "/" + reader.Name : stack.Peek().NextPath(reader.Name);
                                location = Location(reader, sourceName, path);
                                if (HasNamespace(reader)) return Failure("XML_NAMESPACE", "XML namespaces are not supported.", location);
                                var node = new NodeBuilder(reader.Name, location);
                                while (reader.MoveToNextAttribute())
                                {
                                    if (HasNamespace(reader)) return Failure("XML_NAMESPACE", "XML namespaces are not supported.", Location(reader, sourceName, path + "/@" + reader.Name));
                                    node.Attributes.Add(reader.Name, reader.Value);
                                }
                                reader.MoveToElement();
                                if (reader.IsEmptyElement) Attach(node.Build(), stack, ref root);
                                else stack.Push(node);
                                break;
                            case XmlNodeType.EndElement:
                                Attach(stack.Pop().Build(), stack, ref root);
                                break;
                            case XmlNodeType.ProcessingInstruction:
                                return Failure("XML_PROCESSING_INSTRUCTION", "XML processing instructions are not supported.", location);
                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                            case XmlNodeType.SignificantWhitespace:
                                if (!string.IsNullOrWhiteSpace(reader.Value))
                                    return Failure("XML_TEXT", "Content values must be attributes; non-whitespace element text is not supported.", location);
                                break;
                            case XmlNodeType.EntityReference:
                            case XmlNodeType.DocumentType:
                                return Failure("XML_ENTITY", "DTDs and entity references are not supported.", location);
                        }
                    }
                }
            }
            catch (XmlException exception)
            {
                return Failure("XML_INVALID", exception.Message,
                    new BattleSourceLocation(sourceName, exception.LineNumber, exception.LinePosition,
                        stack.Count == 0 ? "/" : stack.Peek().Location.Path));
            }

            if (root == null) return Failure("XML_EMPTY", "XML content has no root element.", fallback);
            if (root.Name != "BattleContent")
                diagnostics.Add(Error("CONTENT_ROOT", "The root element must be BattleContent.", root.Location));
            string schema;
            if (!root.Attributes.TryGetValue("schemaVersion", out schema) || schema != "1")
                diagnostics.Add(Error("CONTENT_SCHEMA_VERSION", "BattleContent requires schemaVersion=\"1\".", root.Location, "schemaVersion"));
            foreach (string field in new[] { "id", "version" })
            {
                string value;
                if (!root.Attributes.TryGetValue(field, out value) || string.IsNullOrWhiteSpace(value))
                    diagnostics.Add(Error("CONTENT_REQUIRED_ATTRIBUTE", "BattleContent requires a non-empty " + field + " attribute.", root.Location, field));
            }
            return new BattleContentReadResult(diagnostics.Count == 0 ? new BattleContentSource(root) : null, diagnostics);
        }

        private static bool HasNamespace(XmlReader reader) => reader.Prefix.Length > 0 || reader.NamespaceURI.Length > 0 || reader.Name == "xmlns";
        private static BattleSourceLocation Location(XmlReader reader, string sourceName, string path)
        {
            var info = (IXmlLineInfo)reader;
            return new BattleSourceLocation(sourceName, info.HasLineInfo() ? info.LineNumber : 0, info.HasLineInfo() ? info.LinePosition : 0, path);
        }
        private static BattleDiagnostic Error(string code, string message, BattleSourceLocation location, string field = null) =>
            new BattleDiagnostic(code, BattleDiagnosticSeverity.Error, message, location, fieldPath: field);
        private static BattleContentReadResult Failure(string code, string message, BattleSourceLocation location) =>
            new BattleContentReadResult(null, new[] { Error(code, message, location) });
        private static void Attach(BattleContentNode node, Stack<NodeBuilder> stack, ref BattleContentNode root)
        {
            if (stack.Count > 0) stack.Peek().Children.Add(node);
            else root = node;
        }
        private sealed class NodeBuilder
        {
            internal readonly string Name;
            internal readonly BattleSourceLocation Location;
            internal readonly Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly List<BattleContentNode> Children = new List<BattleContentNode>();
            private readonly Dictionary<string, int> childCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            internal NodeBuilder(string name, BattleSourceLocation location) { Name = name; Location = location; }
            internal string NextPath(string name)
            {
                int index; childCounts.TryGetValue(name, out index); childCounts[name] = ++index;
                return Location.Path + "/" + name + "[" + index + "]";
            }
            internal BattleContentNode Build() => new BattleContentNode(Name, Attributes, Children, Location);
        }
    }
}
