using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleContentLoaderTests
    {
        private static string Document(string children) => "<BattleContent schemaVersion=\"1\" id=\"bundle\" version=\"v1\">\n" + children + "\n</BattleContent>";
        private static string Import(string path) => "<Import path=\"" + path + "\" />";
        private static string ReadChapter(string path) => ToolkitTestFiles.Read("Engine/Content/SingleChapter/" + path);
        private static BattleContentReadResult Load(Dictionary<string, string> files) =>
            BattleContentLoader.Load("entry.xml", path => files.TryGetValue(path, out var value) ? value : null);
        private static void AssertSuccess(BattleContentBuildResult build) =>
            Assert.IsTrue(build.Success, string.Join("\n", build.Diagnostics.Select(d => d.Location.SourceName + ":" + d.Location.Line + " " + d.Message)));

        [Test] public void SplitChapterAndFlattenedExportHaveExactlyTheOriginalFingerprint()
        {
            var single = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            var loaded = BattleContentLoader.Load("AllBattles.xml", ReadChapter);
            Assert.IsTrue(loaded.Success, string.Join("\n", loaded.Diagnostics.Select(d => d.Message)));
            var bundle = BattleContentCompiler.Compile(loaded.Source);
            var exported = BattleContentCompiler.CompileXml(BattleContentLoader.ExportXml(loaded.Source));
            AssertSuccess(single); AssertSuccess(bundle); AssertSuccess(exported);
            Assert.AreEqual(single.Content.Fingerprint, bundle.Content.Fingerprint);
            Assert.AreEqual(single.Content.Fingerprint, exported.Content.Fingerprint);
            Assert.AreEqual(4, bundle.Content.Encounters.Count);
        }

        [TestCase("Battle1.xml", "single-chapter-battle-1", 1)]
        [TestCase("Battle2.xml", "single-chapter-battle-2", 1)]
        [TestCase("Gatekeeper.xml", "single-chapter-gatekeeper", 1)]
        [TestCase("Wrath.xml", "single-chapter-wrath", 6)]
        public void EachEntryStartsIndependentlyAndMatchesOriginalBattle(string entry, string encounter, int enemyCount)
        {
            var split = BattleContentCompiler.CompileFiles(entry, ReadChapter);
            var single = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            AssertSuccess(split); AssertSuccess(single);
            CollectionAssert.AreEqual(new[] { encounter }, split.Content.Encounters.Keys);
            Func<CompiledBattleContent, BattleSession> start = content =>
            {
                var built = BattleContentFactory.Create(content, new BattleSetupRequest(encounter, "loader-test", HmacDeckShuffler.FromSeed(new byte[32])));
                Assert.IsTrue(built.Success);
                var result = built.Session.Execute(0, new ParticipantId("player"), CardDuelAction.Start);
                Assert.IsTrue(result.Accepted, result.Error.ToString());
                return built.Session;
            };
            var actual = start(split.Content); var expected = start(single.Content);
            var player = new ParticipantId("player");
            Assert.AreEqual(8, actual.State.Zone(CardZoneKinds.Hand, player).Cards.Count);
            Assert.AreEqual(enemyCount, actual.State.Duel.Enemies.Count);
            CollectionAssert.AreEqual(expected.State.Zone(CardZoneKinds.Hand, player).Cards, actual.State.Zone(CardZoneKinds.Hand, player).Cards);
            CollectionAssert.AreEqual(expected.State.Duel.Enemies.Select(e => expected.State.Duel.Combat.Get(e.Id).Health),
                actual.State.Duel.Enemies.Select(e => actual.State.Duel.Combat.Get(e.Id).Health));
            var cards = actual.State.Zone(CardZoneKinds.Hand, player).Cards.Take(1).ToArray();
            var target = new[] { new ParticipantId("enemy") };
            Assert.IsTrue(actual.Play(actual.State.Revision, player, cards, target).Accepted);
            Assert.IsTrue(expected.Play(expected.State.Revision, player, cards, target).Accepted);
            CollectionAssert.AreEqual(expected.State.Duel.Enemies.Select(e => expected.State.Duel.Combat.Get(e.Id).Health),
                actual.State.Duel.Enemies.Select(e => actual.State.Duel.Combat.Get(e.Id).Health));
            CollectionAssert.AreEqual(expected.State.Zone(CardZoneKinds.Hand, player).Cards, actual.State.Zone(CardZoneKinds.Hand, player).Cards);
        }

        [Test] public void NestedPathsAndDiamondDependenciesResolveOnce()
        {
            var files = new Dictionary<string, string> {
                ["entry.xml"] = Document(Import("a/module.xml") + Import("shared.xml")),
                ["a/module.xml"] = Document(Import("..\\.\\shared.xml")),
                ["shared.xml"] = Document("<Players><Player id=\"hero\" /></Players>")
            };
            var reads = new List<string>();
            var result = BattleContentLoader.Load("entry.xml", path => { reads.Add(path); return files[path]; });
            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "entry.xml", "a/module.xml", "shared.xml" }, reads);
            Assert.AreEqual(1, result.Source.Root.Children.Single().Children.Count);
            Assert.AreEqual("shared.xml", result.Source.Root.Children.Single().Children.Single().Location.SourceName);
        }

        [Test] public void CycleReportsTheImportThatClosesIt()
        {
            var result = Load(new Dictionary<string, string> { ["entry.xml"] = Document(Import("child.xml")), ["child.xml"] = Document(Import("entry.xml")) });
            Assert.IsFalse(result.Success);
            var error = result.Diagnostics.Single(d => d.Code == "IMPORT_CYCLE");
            Assert.AreEqual("child.xml", error.Location.SourceName); Assert.AreEqual(2, error.Location.Line);
            StringAssert.Contains("entry.xml -> child.xml -> entry.xml", error.Message);
        }

        [Test] public void MissingFileReportsImportLocation()
        {
            var result = Load(new Dictionary<string, string> { ["entry.xml"] = Document(Import("missing.xml")) });
            Assert.IsFalse(result.Success);
            var error = result.Diagnostics.Single(); Assert.AreEqual("IMPORT_READ", error.Code);
            Assert.AreEqual("entry.xml", error.Location.SourceName); Assert.AreEqual(2, error.Location.Line);
        }

        [TestCase("../outside.xml")]
        [TestCase("/absolute.xml")]
        [TestCase("C:/absolute.xml")]
        [TestCase("https://example.com/content.xml")]
        [TestCase(".")]
        [TestCase("")]
        public void InvalidPathsNeverReachResolver(string path)
        {
            var reads = new List<string>();
            var result = BattleContentLoader.Load("entry.xml", key => { reads.Add(key); return Document(Import(path)); });
            Assert.IsFalse(result.Success); Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "IMPORT_PATH"));
            CollectionAssert.AreEqual(new[] { "entry.xml" }, reads);
        }

        [TestCase("<Import />", "IMPORT_FORMAT")]
        [TestCase("<Import path=\"child.xml\" typo=\"x\" />", "IMPORT_FORMAT")]
        [TestCase("<Import path=\"child.xml\"><Players /></Import>", "IMPORT_FORMAT")]
        [TestCase("<Players /><Players />", "DuplicateSection")]
        [TestCase("<Players typo=\"x\" />", "UnknownAttribute")]
        [TestCase("<Unknown />", "UnknownElement")]
        public void InvalidImportAndSectionsAreRejected(string xml, string code)
        {
            var result = Load(new Dictionary<string, string> { ["entry.xml"] = Document(xml) });
            Assert.IsFalse(result.Success); Assert.IsTrue(result.Diagnostics.Any(d => d.Code == code));
        }

        [Test] public void DuplicateDefinitionReportsSecondFileInsteadOfSilentlyOverriding()
        {
            var result = BattleContentCompiler.CompileFiles("entry.xml", path => path == "entry.xml"
                ? Document(Import("first.xml") + Import("second.xml"))
                : Document("<Players>\n<Player id=\"hero\" maxHealth=\"120\" />\n</Players>"));
            Assert.IsFalse(result.Success);
            var error = result.Diagnostics.Single(d => d.Message.Contains("Duplicate definition ID"));
            Assert.AreEqual("second.xml", error.Location.SourceName); Assert.AreEqual(3, error.Location.Line);
        }

        [Test] public void MissingCrossFileReferenceReportsTheDefinitionFile()
        {
            var result = BattleContentCompiler.CompileFiles("AllBattles.xml", path => path == "Rules/Wrath.xml"
                ? ReadChapter(path).Replace("victoryEnemyRef=\"wrath\"", "victoryEnemyRef=\"missing\"") : ReadChapter(path));
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostics.Any(d => d.Location.SourceName == "Rules/Wrath.xml" && d.Message.Contains("missing")));
        }

        [Test] public void ImportedChangesAffectFingerprint()
        {
            var first = BattleContentCompiler.CompileFiles("Battle1.xml", ReadChapter);
            var changed = BattleContentCompiler.CompileFiles("Battle1.xml", path => path == "Players.xml"
                ? ReadChapter(path).Replace("maxHealth=\"120\"", "maxHealth=\"121\"") : ReadChapter(path));
            AssertSuccess(first); AssertSuccess(changed);
            Assert.AreNotEqual(first.Content.Fingerprint, changed.Content.Fingerprint);
        }

        [Test] public void ImportedDtdIsRejectedWithoutReadingExternalEntities()
        {
            var result = Load(new Dictionary<string, string> {
                ["entry.xml"] = Document(Import("child.xml")),
                ["child.xml"] = "<!DOCTYPE BattleContent SYSTEM 'https://example.com/schema'>" + Document("")
            });
            Assert.IsFalse(result.Success); Assert.AreEqual("child.xml", result.Diagnostics.Single().Location.SourceName);
        }

        [Test] public void ImportDepthAndFileCountsAreBounded()
        {
            var depth = BattleContentLoader.Load("0.xml", path => Document(Import((int.Parse(path.Split('.')[0]) + 1) + ".xml")));
            Assert.IsTrue(depth.Diagnostics.Any(d => d.Code == "IMPORT_DEPTH_LIMIT"));
            var width = BattleContentLoader.Load("entry.xml", path => Document(path == "entry.xml"
                ? string.Join("", Enumerable.Range(0, BattleContentLoader.MaximumFiles).Select(i => Import(i + ".xml"))) : ""));
            Assert.IsTrue(width.Diagnostics.Any(d => d.Code == "IMPORT_FILE_LIMIT"));
        }

        [Test] public void TotalBundleSizeAndNodeCountsAreBounded()
        {
            string padding = new string(' ', BattleContentXmlReader.MaximumDocumentCharacters - 200);
            var size = BattleContentLoader.Load("entry.xml", path => Document(path == "entry.xml"
                ? string.Join("", Enumerable.Range(0, 9).Select(i => Import(i + ".xml"))) : padding));
            Assert.IsTrue(size.Diagnostics.Any(d => d.Code == "IMPORT_SIZE_LIMIT"));
            string nodes = "<Players>" + string.Concat(Enumerable.Repeat("<Player />", 19000)) + "</Players>";
            var count = BattleContentLoader.Load("entry.xml", path => Document(path == "entry.xml"
                ? string.Join("", Enumerable.Range(0, 6).Select(i => Import(i + ".xml"))) : nodes));
            Assert.IsTrue(count.Diagnostics.Any(d => d.Code == "IMPORT_NODE_LIMIT"));
        }

        [Test] public void DiskAdapterLoadsImportsAndReportsMissingFiles()
        {
            string folder = Path.Combine(Path.GetTempPath(), "battle-bundle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "entry.xml"), Document(Import("child.xml")));
                File.WriteAllText(Path.Combine(folder, "child.xml"), Document("<Players />"));
                Assert.IsTrue(BattleContentLoader.LoadFile(Path.Combine(folder, "entry.xml")).Success);
                var missing = BattleContentLoader.LoadFile(Path.Combine(folder, "missing.xml"));
                Assert.AreEqual("IMPORT_READ", missing.Diagnostics.Single().Code);
            }
            finally
            {
                File.Delete(Path.Combine(folder, "entry.xml")); File.Delete(Path.Combine(folder, "child.xml"));
                Directory.Delete(folder); // Empty directory only; never recursively delete a computed path.
            }
        }
    }
}
