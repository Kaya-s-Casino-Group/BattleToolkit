using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleContentXmlReaderTests
    {
        private const string Open = "<BattleContent schemaVersion=\"1\" id=\"sample\" version=\"1.0\">";
        private const string Close = "</BattleContent>";
        private static BattleContentReadResult Read(string inner = "") => BattleContentXmlReader.Read(Open + inner + Close, "fixture.xml");
        private static void Error(BattleContentReadResult result, string code)
        {
            Assert.IsFalse(result.Success); Assert.IsNull(result.Source);
            Assert.That(result.Diagnostics.Any(item => item.Code == code), Is.True, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
            Assert.That(result.Diagnostics.All(item => item.Severity == BattleDiagnosticSeverity.Error), Is.True);
        }

        [Test] public void ReadsOrderedCaseSensitiveSyntaxWithoutApplyingSemanticDefaults()
        {
            var result = Read("<Relics><Relic id=\"b\" name=\"弃牌回血\"/><Relic id=\"A\" Unknown=\"true\"/></Relics><Unknown future=\"1\"/>");
            Assert.IsTrue(result.Success); Assert.IsEmpty(result.Diagnostics);
            var root = result.Source.Root;
            Assert.AreEqual("BattleContent", root.Name); Assert.AreEqual("sample", root.Attributes["id"]);
            CollectionAssert.AreEqual(new[] { "Relics", "Unknown" }, root.Children.Select(node => node.Name));
            var relics = root.Children[0].Children;
            CollectionAssert.AreEqual(new[] { "b", "A" }, relics.Select(node => node.Attributes["id"]));
            Assert.IsTrue(relics[1].Attributes.ContainsKey("Unknown")); Assert.IsFalse(relics[1].Attributes.ContainsKey("unknown"));
            Assert.AreEqual("弃牌回血", relics[0].Attributes["name"]);
        }

        [Test] public void RetainsSourceCoordinatesAndSiblingSpecificPaths()
        {
            var result = Read("\n  <Statuses>\n    <Status id=\"a\"/>\n    <Status id=\"b\"/>\n  </Statuses>");
            var second = result.Source.Root.Children.Single().Children[1];
            Assert.AreEqual("fixture.xml", second.Location.SourceName); Assert.AreEqual(4, second.Location.Line);
            Assert.AreEqual(6, second.Location.Column);
            Assert.AreEqual("/BattleContent/Statuses[1]/Status[2]", second.Location.Path);
        }

        [Test] public void DeclarationCommentsWhitespaceAndEscapedAttributeCharactersAreSupported()
        {
            var result = BattleContentXmlReader.Read("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- comment -->\n" + Open +
                "\n <Relics><!-- ignored --><Relic name=\"A &amp; B &lt; C &#x4E2D;\"/></Relics>\n" + Close);
            Assert.IsTrue(result.Success); Assert.AreEqual("A & B < C 中", result.Source.Root.Children.Single().Children.Single().Attributes["name"]);
            Assert.AreEqual("content.xml", result.Source.Root.Location.SourceName);
        }

        [TestCase(null)] [TestCase("")]
        public void EmptyInputReturnsDiagnostic(string input) { Error(BattleContentXmlReader.Read(input), "XML_EMPTY"); }

        [TestCase(" ")] [TestCase("<!-- only comment -->")] [TestCase("<BattleContent>")]
        [TestCase("<BattleContent/><BattleContent/>")] [TestCase("<BattleContent id=\"x\" id=\"y\"/>")]
        [TestCase("<BattleContent schemaVersion=\"1\" id=\"x\" version=\"1\">&undefined;</BattleContent>")]
        public void MalformedXmlReturnsDiagnosticsRatherThanThrowing(string input)
        { Error(BattleContentXmlReader.Read(input), "XML_INVALID"); }

        [Test] public void ParseErrorRetainsMostSpecificOpenElementLocation()
        {
            var result = Read("\n<Relics><Relic></Bad></Relics>");
            Error(result, "XML_INVALID"); var error = result.Diagnostics.Single();
            Assert.AreEqual(2, error.Location.Line); Assert.Greater(error.Location.Column, 0);
            Assert.AreEqual("/BattleContent/Relics[1]/Relic[1]", error.Location.Path);
        }

        [TestCase("<Other schemaVersion=\"1\" id=\"x\" version=\"1\"/>", "CONTENT_ROOT")]
        [TestCase("<BattleContent id=\"x\" version=\"1\"/>", "CONTENT_SCHEMA_VERSION")]
        [TestCase("<BattleContent schemaVersion=\"2\" id=\"x\" version=\"1\"/>", "CONTENT_SCHEMA_VERSION")]
        [TestCase("<BattleContent schemaVersion=\"1\" version=\"1\"/>", "CONTENT_REQUIRED_ATTRIBUTE")]
        [TestCase("<BattleContent schemaVersion=\"1\" id=\" \" version=\"1\"/>", "CONTENT_REQUIRED_ATTRIBUTE")]
        [TestCase("<BattleContent schemaVersion=\"1\" id=\"x\"/>", "CONTENT_REQUIRED_ATTRIBUTE")]
        public void RootContractIsRequired(string input, string code) { Error(BattleContentXmlReader.Read(input), code); }

        [Test] public void CollectsAllMissingRootMetadataInOneResult()
        {
            var result = BattleContentXmlReader.Read("<BattleContent/>");
            Assert.IsFalse(result.Success); Assert.IsNull(result.Source); Assert.AreEqual(3, result.Diagnostics.Count);
            CollectionAssert.AreEquivalent(new[] { "schemaVersion", "id", "version" }, result.Diagnostics.Select(item => item.FieldPath));
        }

        [TestCase("<!DOCTYPE BattleContent SYSTEM \"file:///path-that-must-never-be-read.xml\">")]
        [TestCase("<!DOCTYPE BattleContent SYSTEM \"https://invalid.example/never-request.dtd\">")]
        [TestCase("<!DOCTYPE BattleContent [<!ENTITY private \"secret\">]>")]
        public void DtdAndExternalEntitiesAreProhibited(string declaration)
        { Error(BattleContentXmlReader.Read(declaration + Open + Close), "XML_INVALID"); }

        [TestCase("<Relics xmlns=\"urn:test\"/>")] [TestCase("<Relics xmlns:q=\"urn:test\"/>")]
        [TestCase("<Relics xml:lang=\"zh\"/>")]
        public void NamespaceUseIsRejected(string inner) { Error(Read(inner), "XML_NAMESPACE"); }

        [Test] public void DefaultNamespaceOnRootIsRejected()
        { Error(BattleContentXmlReader.Read("<BattleContent xmlns=\"urn:test\" schemaVersion=\"1\" id=\"x\" version=\"1\"/>"), "XML_NAMESPACE"); }

        [TestCase("<?xml-stylesheet href=\"file:///never-read.css\"?>")]
        [TestCase("<Relics><?custom load=\"file:///never-read.xml\"?></Relics>")]
        public void ProcessingInstructionsAreRejected(string inner) { Error(Read(inner), "XML_PROCESSING_INSTRUCTION"); }

        [TestCase("unexpected")] [TestCase("<Relics>unexpected</Relics>")] [TestCase("<Relics><![CDATA[unexpected]]></Relics>")]
        public void NonWhitespaceTextIsRejected(string inner) { Error(Read(inner), "XML_TEXT"); }

        [Test] public void WhitespaceCdataIsHarmless() { Assert.IsTrue(Read("<![CDATA[ \n\t ]]>").Success); }

        [Test] public void OversizedDocumentIsRejectedBeforeParsing()
        { Error(BattleContentXmlReader.Read(new string(' ', BattleContentXmlReader.MaximumDocumentCharacters + 1)), "XML_SIZE_LIMIT"); }

        [Test] public void MaximumDepthIsBounded()
        {
            int depth = BattleContentXmlReader.MaximumElementDepth - 1;
            var valid = string.Concat(Enumerable.Repeat("<Item>", depth)) + string.Concat(Enumerable.Repeat("</Item>", depth));
            Assert.IsTrue(Read(valid).Success);
            Error(Read("<Item>" + valid + "</Item>"), "XML_DEPTH_LIMIT");
        }

        [Test] public void MaximumNodeCountIsBounded()
        {
            var valid = string.Concat(Enumerable.Repeat("<I/>", BattleContentXmlReader.MaximumNodes - 1));
            Assert.IsTrue(Read(valid).Success); Error(Read(valid + "<I/>"), "XML_NODE_LIMIT");
        }

        [Test] public void MaximumAttributeCountIsBounded()
        {
            var attributes = string.Join(" ", Enumerable.Range(0, BattleContentXmlReader.MaximumAttributesPerElement).Select(i => "v" + i + "=\"x\""));
            Assert.IsTrue(Read("<Item " + attributes + "/>").Success);
            Error(Read("<Item " + attributes + " extra=\"x\"/>"), "XML_ATTRIBUTE_LIMIT");
        }

        [Test] public void ModelsDefensivelyCopyInputCollections()
        {
            var location = new BattleSourceLocation("test.xml", 1, 2, "/Item");
            var attributes = new Dictionary<string, string> { { "id", "original" } };
            var children = new List<BattleContentNode>();
            var node = new BattleContentNode("Item", attributes, children, location);
            attributes["id"] = "changed"; children.Add(new BattleContentNode("Child", null, null, location));
            Assert.AreEqual("original", node.Attributes["id"]); Assert.IsEmpty(node.Children);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)node.Attributes).Add("key", "value"));
            Assert.Throws<NotSupportedException>(() => ((IList<BattleContentNode>)node.Children).Add(node));
            var diagnostics = new List<BattleDiagnostic> { new BattleDiagnostic("test", BattleDiagnosticSeverity.Warning, "test", location) };
            var result = new BattleContentReadResult(new BattleContentSource(node), diagnostics);
            diagnostics.Clear(); Assert.IsTrue(result.Success); Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.Throws<NotSupportedException>(() => ((IList<BattleDiagnostic>)result.Diagnostics).Clear());
        }

        [Test] public void ErrorResultCannotReportSuccess()
        {
            var source = Read().Source;
            var result = new BattleContentReadResult(source, new[] { new BattleDiagnostic("error", BattleDiagnosticSeverity.Error, "invalid", source.Root.Location) });
            Assert.IsFalse(result.Success);
        }
    }
}
