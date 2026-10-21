using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PokerEngine
{
    public static class BattleAdventureXml
    {
        public const int MaxCharacters = 1024 * 1024;
        public static BattleAdventureDefinition Load(string xml, CompiledBattleContent battleContent)
        {
            if (xml == null || battleContent == null) throw new ArgumentNullException();
            if (xml.Length > MaxCharacters) throw new ArgumentException("Adventure XML exceeds size limit.");
            XElement root;
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCharacters }))
                root = XDocument.Load(reader).Root;
            Require(root != null && root.Name == "AdventureContent" && Attr(root, "schemaVersion") == "1", "Unsupported adventure XML.");
            Check(root, new[] { "schemaVersion", "id", "name", "version", "startNode", "startingEncounter", "defaultTarot", "startingGold", "relicCapacity", "minimumDeckSize", "maximumDeckSize", "economyVersion", "shopOfferCount", "restHeal" },
                new[] { "Tarots", "Nodes", "Shop", "Services", "Rest" });
            int economyVersion = Int(root, "economyVersion", 1, 2, 1);
            string id = Text(root, "id"), version = Text(root, "version"), encounter = Text(root, "startingEncounter");
            ValidateEncounter(battleContent, encounter);
            string start = Text(root, "startNode"), defaultTarot = Text(root, "defaultTarot");
            var nodes = Section(root, "Nodes", "Node").Select(n =>
            {
                Check(n, new[] { "id", "name", "kind", "chapter", "row", "column", "encounter", "gold", "next", "enemyHealthMultiplier" });
                var kind = EnumValue<AdventureNodeKind>(n, "kind");
                string reference = Attr(n, "encounter");
                if (kind == AdventureNodeKind.Battle || kind == AdventureNodeKind.Elite || kind == AdventureNodeKind.Boss) ValidateEncounter(battleContent, reference);
                else Require(reference == null, "Non-battle node cannot reference an encounter.");
                var next = (Attr(n, "next") ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                Require(next.Length <= 16 && next.Distinct(StringComparer.Ordinal).Count() == next.Length, "Duplicate or excessive next nodes.");
                return new AdventureNodeDefinition(Text(n, "id"), Name(n), kind, Int(n, "chapter", 1, 32),
                    Int(n, "row", 0, 255, 0), Int(n, "column", 0, 32, 0), reference, Long(n, "gold", 0, 1000000, 0), next,
                    Decimal(n, "enemyHealthMultiplier", 0.01m, 1000m, 1m));
            }).ToArray();
            Unique(nodes.Select(n => n.Id), 256);
            Require(nodes.Length > 0 && nodes.Any(n => n.Id == start), "Missing start node.");
            var index = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            Require(nodes.All(n => n.NextIds.All(index.ContainsKey)), "Unresolved next node.");
            Require(nodes.All(n => n.NextIds.All(next => index[next].Chapter >= n.Chapter)), "Chapter order cannot go backwards.");
            Require(nodes.All(n => n.NextIds.Select(next => index[next].Chapter).Distinct().Count() <= 1), "Next choices must belong to one chapter.");
            Require(nodes.Where(n => n.NextIds.Count == 0).All(n => n.Kind == AdventureNodeKind.Boss), "Terminal nodes must be bosses.");
            var visiting = new HashSet<string>(); var visited = new HashSet<string>();
            Action<string> visit = null;
            visit = node =>
            {
                Require(!visiting.Contains(node), "Adventure route must be acyclic.");
                if (visited.Contains(node)) return;
                visiting.Add(node);
                foreach (string next in index[node].NextIds) visit(next);
                visiting.Remove(node); visited.Add(node);
            };
            visit(start); Require(visited.Count == nodes.Length, "All nodes must be reachable.");
            int chapters = nodes.Max(n => n.Chapter);
            Require(nodes.Select(n => n.Chapter).Distinct().Count() == chapters &&
                Enumerable.Range(1, chapters).All(ch => nodes.Any(n => n.Chapter == ch && n.Kind == AdventureNodeKind.Boss)), "Each consecutive chapter requires a boss.");
            Require(index[start].Chapter == 1 && nodes.Where(n => n.NextIds.Count == 0).All(n => n.Chapter == chapters), "Routes must start in chapter one and finish in the last chapter.");
            Require(nodes.All(n => n.NextIds.All(next => index[next].Chapter == n.Chapter ||
                n.Kind == AdventureNodeKind.Boss && index[next].Chapter == n.Chapter + 1)), "Only a boss may advance to the next chapter.");
            var tarots = Section(root, "Tarots", "Tarot").Select(t =>
            {
                Check(t, new[] { "id", "name", "description", "initiallyUnlocked", "unlockOnVictory", "maxHealthMultiplier", "startingGoldBonus", "startingRelic" });
                string relic = Attr(t, "startingRelic"); ValidateRelic(battleContent, relic);
                return new AdventureTarotDefinition(Text(t, "id"), Name(t), Attr(t, "description") ?? "",
                    Bool(t, "initiallyUnlocked", false), Bool(t, "unlockOnVictory", false),
                    Decimal(t, "maxHealthMultiplier", 0.01m, 100m, 1m), Long(t, "startingGoldBonus", 0, 1000000, 0), relic);
            }).ToArray();
            Unique(tarots.Select(t => t.Id), 128);
            Require(tarots.Any(t => t.Id == defaultTarot && t.InitiallyUnlocked), "Default tarot must be initially unlocked.");
            var shop = Section(root, "Shop", "Relic").Select(t =>
            {
                Check(t, new[] { "id", "name", "description", "ref", "price", "sellPrice" });
                string relic = Text(t, "ref"); ValidateRelic(battleContent, relic);
                return new AdventureShopItem(Text(t, "id"), relic, Name(t), Attr(t, "description") ?? "",
                    Long(t, "price", 0, 1000000), Long(t, "sellPrice", 0, 1000000));
            }).ToArray();
            Unique(shop.Select(s => s.Id), 128);
            Require(shop.All(s => s.SellPrice <= s.Price) && shop.GroupBy(s => s.RelicId).All(g => g.Select(s => s.SellPrice).Distinct().Count() == 1), "Invalid relic prices.");
            var services = Section(root, "Services", "Service").Select(t =>
            {
                Check(t, new[] { "kind", "price", "increase" });
                var kind = EnumValue<AdventureCommandKind>(t, "kind");
                Require(IsService(kind), "Unknown shop service.");
                return new AdventureServicePrice(kind, Long(t, "price", 0, 1000000), Long(t, "increase", 0, 1000000, 0));
            }).ToArray();
            Require(services.Select(s => s.Kind).Distinct().Count() == services.Length, "Duplicate service.");
            var rest = Section(root, "Rest", "Choice").Select(t =>
            {
                Check(t, new[] { "id", "name", "description", "kind", "healRatio", "gold", "relic" });
                var kind = EnumValue<AdventureRestKind>(t, "kind"); string relic = Attr(t, "relic"); ValidateRelic(battleContent, relic);
                Require(kind != AdventureRestKind.Relic || relic != null, "Relic rest choice requires a relic.");
                return new AdventureRestChoice(Text(t, "id"), Name(t), Attr(t, "description") ?? "", kind,
                    Decimal(t, "healRatio", 0, 1, 0), Long(t, "gold", 0, 1000000, 0), relic);
            }).ToArray();
            Unique(rest.Select(r => r.Id), 32);
            Require(economyVersion == 2 || !nodes.Any(n => n.Kind == AdventureNodeKind.Rest) || rest.Length > 0, "Rest node requires choices.");
            int minimum = Int(root, "minimumDeckSize", 1, 512, 5), maximum = Int(root, "maximumDeckSize", minimum, 512, 128);
            string normalized = root.ToString(SaveOptions.DisableFormatting);
            return new BattleAdventureDefinition(id, Name(root), version, Hash(normalized + "\n" + battleContent.Fingerprint), battleContent.Fingerprint,
                xml, start, encounter, defaultTarot, Long(root, "startingGold", 0, 1000000, 0), Int(root, "relicCapacity", 0, 32, 5), minimum, maximum,
                nodes, tarots, shop, services, rest, economyVersion, Int(root, "shopOfferCount", 0, 32, 4),
                Long(root, "restHeal", 0, 1000000, 20));
        }
        internal static bool IsService(AdventureCommandKind kind) => kind == AdventureCommandKind.DeleteCard || kind == AdventureCommandKind.DuplicateCard || kind == AdventureCommandKind.ChangeSuit;
        internal static string Hash(string text)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static void ValidateEncounter(CompiledBattleContent content, string id)
        { Require(id != null && content.Encounters.ContainsKey(id) && content.Encounters[id].Players.Count == 1, "Adventure requires a known single-player encounter: " + id); }
        private static void ValidateRelic(CompiledBattleContent content, string id)
        { Require(id == null || content.Relics.ContainsKey(id), "Unknown relic: " + id); }
        private static IEnumerable<XElement> Section(XElement root, string name, string item)
        {
            var sections = root.Elements(name).ToArray(); Require(sections.Length == 1, "Expected one " + name + " section.");
            Check(sections[0], Array.Empty<string>(), new[] { item }); return sections[0].Elements(item);
        }
        private static void Check(XElement e, IEnumerable<string> attributes, IEnumerable<string> children = null)
        {
            Require(e.Attributes().All(a => attributes.Contains(a.Name.LocalName) && a.Name.NamespaceName == ""), "Unknown attribute on " + e.Name);
            var allowed = (children ?? Array.Empty<string>()).ToArray();
            Require(e.Elements().All(c => allowed.Contains(c.Name.LocalName) && c.Name.NamespaceName == ""), "Unknown child on " + e.Name);
        }
        private static void Unique(IEnumerable<string> ids, int maximum)
        { var values = ids.ToArray(); Require(values.Length <= maximum && values.Distinct(StringComparer.Ordinal).Count() == values.Length, "Duplicate or excessive definitions."); }
        private static string Attr(XElement e, string name) => (string)e.Attribute(name);
        private static string Name(XElement e) => Attr(e, "name") ?? Text(e, "id");
        private static string Text(XElement e, string name)
        { string value = Attr(e, name); Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 256, "Missing/invalid " + name); return value; }
        private static long Long(XElement e, string name, long minimum, long maximum, long? fallback = null)
        {
            string text = Attr(e, name); if (text == null && fallback.HasValue) return fallback.Value;
            long value; Require(long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum, "Invalid " + name);
            return value;
        }
        private static int Int(XElement e, string name, int minimum, int maximum, int? fallback = null) => (int)Long(e, name, minimum, maximum, fallback);
        private static decimal Decimal(XElement e, string name, decimal minimum, decimal maximum, decimal fallback)
        {
            string text = Attr(e, name); if (text == null) return fallback;
            decimal value; Require(decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum, "Invalid " + name); return value;
        }
        private static bool Bool(XElement e, string name, bool fallback)
        { string text = Attr(e, name); if (text == null) return fallback; bool value; Require(bool.TryParse(text, out value), "Invalid " + name); return value; }
        private static T EnumValue<T>(XElement e, string name) where T : struct
        { T value; Require(Enum.TryParse(Attr(e, name), false, out value) && Enum.IsDefined(typeof(T), value), "Invalid " + name); return value; }
        private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    }
}
