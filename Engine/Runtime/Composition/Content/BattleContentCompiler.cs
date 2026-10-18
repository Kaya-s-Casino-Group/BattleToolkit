using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    public static class BattleContentCompiler
    {
        public static BattleContentBuildResult CompileXml(string xml, string sourceName = "content.xml")
        {
            var read = BattleContentXmlReader.Read(xml, sourceName);
            return read.Success ? Compile(read.Source) : new BattleContentBuildResult(null, read.Diagnostics);
        }
        public static BattleContentBuildResult Compile(BattleContentSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new BattleContentBuildContext().Build(source);
        }
    }
    internal partial class BattleContentBuildContext
    {
        internal Dictionary<string, CombatStatusDefinition> Statuses;
        internal string ContentVersion;
        private readonly List<BattleDiagnostic> diagnostics = new List<BattleDiagnostic>();
        private static readonly string[] Sections = { "ScoringProfiles", "BattleRules", "HandRules", "Statuses", "Relics", "Players", "Enemies", "Decks", "Loadouts", "Encounters" };

        internal BattleContentBuildResult Build(BattleContentSource source)
        {
            var root = source.Root;
            Check(root, new[] { "schemaVersion", "id", "version", "name" }, Sections);
            var metadata = Capture(root, () =>
            {
                if (root.Name != "BattleContent" || Text(root, "schemaVersion") != "1") throw new ArgumentException("Expected BattleContent schemaVersion 1.");
                return new[] { Text(root, "id"), Text(root, "version") };
            });
            if (metadata == null) return new BattleContentBuildResult(null, diagnostics);
            ContentVersion = metadata[1];
            var sections = new Dictionary<string, BattleContentNode>(StringComparer.Ordinal);
            foreach (var node in root.Children.Where(item => Sections.Contains(item.Name)))
                if (sections.ContainsKey(node.Name)) AddError(node, "DuplicateSection", "Duplicate content section: " + node.Name);
                else sections.Add(node.Name, node);
            Func<string, BattleContentNode> section = name => sections.ContainsKey(name) ? sections[name] : null;
            Statuses = ReadStatuses(section("Statuses"));
            var catalog = new CombatStatusCatalog(ContentVersion, Statuses.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal));
            var hands = ReadHandRules(section("HandRules"));
            var relics = ReadRelics(section("Relics"));
            var scores = ReadScores(section("ScoringProfiles"));
            var rules = ReadDefinitions(section("BattleRules"), "Rules", node =>
            {
                Check(node, new[] { "id", "name", "version", "scoringRef", "roundLimit", "blockExpiry", "maxReactionEffects", "maxReactionDepth" }, new string[0]);
                string score = Text(node, "scoringRef"); Reference(node, "scoringRef", score, scores);
                return new BattleRuleContentDefinition(score, new BattleRules(Text(node, "version", ContentVersion), Int(node, "roundLimit", 20),
                    Enum(node, "blockExpiry", PlayerBlockExpiry.Retain), catalog, reactionLimits: new CombatReactionLimits(Int(node, "maxReactionEffects", 256), Int(node, "maxReactionDepth", 16))));
            });
            var players = ReadPlayers(section("Players"), hands);
            var enemies = ReadEnemies(section("Enemies"));
            var decks = ReadDecks(section("Decks"));
            var loadouts = ReadLoadouts(section("Loadouts"), relics, decks);
            var locations = new Dictionary<string, BattleSourceLocation>(StringComparer.Ordinal);
            var encounters = ReadDefinitions(section("Encounters"), "Encounter", node =>
            {
                Check(node, new[] { "id", "name", "rulesRef" }, new[] { "Player", "Enemy" });
                string id = Text(node, "id"), rulesId = Text(node, "rulesRef"); Reference(node, "rulesRef", rulesId, rules);
                locations[id] = node.Location;
                var playerSlots = node.Children.Where(item => item.Name == "Player").Select(slot => Capture(slot, () =>
                {
                    Check(slot, new[] { "id", "ref", "loadoutRef", "initialHealth" }, new string[0]);
                    string template = Text(slot, "ref"), loadout = Optional(slot, "loadoutRef");
                    Reference(slot, "ref", template, players); if (loadout != null) Reference(slot, "loadoutRef", loadout, loadouts);
                    return new BattlePlayerSlotDefinition(Text(slot, "id"), template, loadout, OptionalInt(slot, "initialHealth"));
                })).Where(item => item != null).ToArray();
                var enemySlots = node.Children.Where(item => item.Name == "Enemy").Select(slot => Capture(slot, () =>
                {
                    Check(slot, new[] { "id", "ref" }, new string[0]); string template = Text(slot, "ref"); Reference(slot, "ref", template, enemies);
                    return new BattleEnemySlotDefinition(Text(slot, "id"), template);
                })).Where(item => item != null).ToArray();
                return new BattleEncounterContentDefinition(rulesId, playerSlots, enemySlots);
            });
            var content = new CompiledBattleContent(metadata[0], ContentVersion, Fingerprint(root), root.Location, scores, rules, hands, Statuses,
                relics, players, enemies, decks, loadouts, encounters, locations);
            if (!HasErrors) ValidateCombinations(content);
            // Validate actual combinations with the same setup path used by the game; never start a battle here.
            if (!HasErrors)
                foreach (var entry in encounters)
                {
                    try { BattleContentFactory.CreateSession(content, new BattleSetupRequest(entry.Key, "content-validation", HmacDeckShuffler.FromSeed(new byte[32]))); }
                    catch (ArgumentException error) { AddAssemblyError(locations[entry.Key], entry.Key, error.Message); }
                    catch (OverflowException error) { AddAssemblyError(locations[entry.Key], entry.Key, error.Message); }
                }
            return new BattleContentBuildResult(HasErrors ? null : content, diagnostics);
        }
        private bool HasErrors => diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error);
        private void AddAssemblyError(BattleSourceLocation location, string id, string message) => diagnostics.Add(
            new BattleDiagnostic("InvalidCombination", BattleDiagnosticSeverity.Error, message, location, id));
        internal void AddError(BattleContentNode node, string code, string message, string field = null) => diagnostics.Add(
            new BattleDiagnostic(code, BattleDiagnosticSeverity.Error, message, node.Location,
                node.Attributes.ContainsKey("id") ? node.Attributes["id"] : null, field));
        internal T Capture<T>(BattleContentNode node, Func<T> read) where T : class
        {
            try { return read(); }
            catch (ArgumentException error) { AddError(node, "InvalidValue", error.Message); }
            catch (OverflowException error) { AddError(node, "Overflow", error.Message); }
            return null;
        }
        internal void Check(BattleContentNode node, string[] attributes, string[] children)
        {
            foreach (string name in node.Attributes.Keys.Where(name => !attributes.Contains(name))) AddError(node, "UnknownAttribute", "Unknown attribute: " + name, name);
            foreach (var child in node.Children.Where(child => !children.Contains(child.Name))) AddError(child, "UnknownElement", "Unknown element: " + child.Name);
        }
        internal string Optional(BattleContentNode node, string attribute)
        {
            string value; return node.Attributes.TryGetValue(attribute, out value) ? value.Trim() : null;
        }
        internal string Text(BattleContentNode node, string attribute, string defaultValue = null)
        {
            string value = Optional(node, attribute) ?? defaultValue;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required nonempty field: " + attribute);
            return value;
        }
        internal int Int(BattleContentNode node, string attribute, int fallback)
        {
            string raw = Optional(node, attribute); int value;
            if (raw == null) return fallback;
            if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Invalid integer: " + attribute);
            return value;
        }
        internal int? OptionalInt(BattleContentNode node, string attribute) => Optional(node, attribute) == null ? (int?)null : Int(node, attribute, 0);
        internal long Long(BattleContentNode node, string attribute, long fallback)
        {
            string raw = Optional(node, attribute); long value;
            if (raw == null) return fallback;
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Invalid integer: " + attribute);
            return value;
        }
        internal decimal Decimal(BattleContentNode node, string attribute, decimal fallback)
        {
            string raw = Optional(node, attribute); decimal value;
            if (raw == null) return fallback;
            if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Invalid decimal: " + attribute);
            return value;
        }
        internal bool Bool(BattleContentNode node, string attribute, bool fallback)
        {
            string raw = Optional(node, attribute);
            if (raw == null) return fallback;
            if (raw != "true" && raw != "false") throw new ArgumentException("Expected true or false: " + attribute);
            return raw == "true";
        }
        internal T Enum<T>(BattleContentNode node, string attribute, T fallback) where T : struct
        {
            string raw = Optional(node, attribute); T value;
            if (raw == null) return fallback;
            bool flags = typeof(T).IsDefined(typeof(FlagsAttribute), false);
            var names = System.Enum.GetNames(typeof(T));
            if ((flags ? raw.Split(',').Any(item => !names.Contains(item.Trim())) : !names.Contains(raw)) || !System.Enum.TryParse(raw, out value))
                throw new ArgumentException("Unknown " + attribute + ": " + raw);
            return value;
        }
        private bool Reference<T>(BattleContentNode node, string field, string id, IDictionary<string, T> values)
        {
            if (values.ContainsKey(id)) return true;
            AddError(node, "UnknownReference", "Unknown " + field + ": " + id, field); return false;
        }
        private static string Fingerprint(BattleContentNode root)
        {
            var buffer = new StringBuilder();
            Action<string> token = value => buffer.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            token(CompiledBattleContent.CurrentRulesContractVersion); token(ScoreProfile.Default.Version);
            Action<BattleContentNode> visit = null;
            visit = node =>
            {
                token(node.Name);
                foreach (var pair in node.Attributes.Where(pair => pair.Key != "name" && pair.Key != "explanation").OrderBy(pair => pair.Key, StringComparer.Ordinal))
                { token(pair.Key); token(pair.Value.Trim()); }
                token("children");
                IEnumerable<BattleContentNode> children = node.Children;
                if (node.Name == "BattleContent") children = children.OrderBy(child => child.Name, StringComparer.Ordinal);
                else if (Sections.Contains(node.Name)) children = children.OrderBy(child => child.Attributes.ContainsKey("id") ? child.Attributes["id"].Trim() : "", StringComparer.Ordinal);
                foreach (var child in children) visit(child); token("end");
            };
            visit(root);
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(buffer.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
