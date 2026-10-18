using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    /// <summary>A validated persistent relic and its immutable definition, ready to bind to one battle owner.</summary>
    public sealed class BattleRelicSetup
    {
        public RelicDefinition Definition { get; }
        public RunRelic Instance { get; }
        public BattleRelicRules BattleRules { get; }

        public BattleRelicSetup(RelicDefinition definition, RunRelic instance, BattleRelicRules battleRules = null)
        {
            BattleRelicValidation.Definition(definition);
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            Guard.Text(instance.Id, nameof(instance));
            if (!StringComparer.Ordinal.Equals(definition.Id, instance.DefinitionId) || instance.Level < 1 ||
                instance.Level > definition.MaxLevel || instance.Growth < 0 || instance.CooldownRounds < 0)
                throw new ArgumentException("Persistent relic does not match its definition.", nameof(instance));
            BattleRelicValidation.Charges(definition, instance.Charges);
            BattleRelicValidation.Memory(instance.Memory, true);
            Definition = definition; Instance = instance; BattleRules = battleRules ?? BattleRelicRules.Empty;
        }
    }

    /// <summary>Battle-owned relic identity and runtime memory. Restoring data does not install definitions or execute effects.</summary>
    [DataContract]
    public sealed class BattleRelicState
    {
        [DataMember(Order = 1, IsRequired = true)] public string InstanceId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string DefinitionId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public ParticipantId OwnerId { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public int Order { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int Level { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public long Growth { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public int? RemainingCharges { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public int UsesThisRound { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public int CooldownRounds { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] public AbilityMemory Memory { get; private set; }

        public BattleRelicState(string instanceId, string definitionId, ParticipantId ownerId, int order, int level = 1,
            long growth = 0, int? remainingCharges = null, int usesThisRound = 0, int cooldownRounds = 0, AbilityMemory memory = null)
        {
            InstanceId = Guard.Text(instanceId, nameof(instanceId)); DefinitionId = Guard.Text(definitionId, nameof(definitionId));
            OwnerId = ownerId; Order = order; Level = level; Growth = growth; RemainingCharges = remainingCharges;
            UsesThisRound = usesThisRound; CooldownRounds = cooldownRounds;
            Memory = memory ?? AbilityMemory.Empty;
            BattleRelicValidation.State(this);
            Memory = BattleRelicValidation.CopyMemory(Memory);
        }

        public BattleRelicState WithRuntime(int? remainingCharges, int usesThisRound, int cooldownRounds, AbilityMemory memory) =>
            new BattleRelicState(InstanceId, DefinitionId, OwnerId, Order, Level, Growth, remainingCharges, usesThisRound, cooldownRounds, memory);
    }

    /// <summary>
    /// Ordered immutable battle binding. Runtime replacement may update only existing instances' runtime fields.
    /// Definitions and the fingerprint describe the static binding, including passive amounts, effect structure and battle rules.
    /// </summary>
    public sealed class BattleRelicLoadout
    {
        private readonly Dictionary<string, RelicDefinition> definitions;
        private readonly Dictionary<string, BattleRelicRules> battleRules;
        public ParticipantId OwnerId { get; }
        public IReadOnlyList<BattleRelicState> Relics { get; }
        public string Fingerprint { get; }

        public BattleRelicLoadout(ParticipantId ownerId, IEnumerable<BattleRelicSetup> relics = null)
        {
            if (!ownerId.IsValid) throw new ArgumentException("A relic loadout requires a valid owner.", nameof(ownerId));
            var setup = Guard.Copy(relics ?? Array.Empty<BattleRelicSetup>(), nameof(relics));
            if (setup.Length > 32) throw new ArgumentException("A battle supports at most 32 relic instances per owner.", nameof(relics));
            // Repeat setup validation so objects restored without constructors cannot bypass the binding contract.
            foreach (var item in setup) new BattleRelicSetup(item.Definition, item.Instance, item.BattleRules);
            RelicHandEffectValidation.Combinations(setup.SelectMany(item => item.BattleRules.HandEffects));
            RelicRuleValidation.Refills(setup.SelectMany(item => item.BattleRules.RefillRules));
            if (setup.Select(item => item.Instance.Id).Distinct(StringComparer.Ordinal).Count() != setup.Length)
                throw new ArgumentException("Battle relic instance identities must be unique.", nameof(relics));
            foreach (var group in setup.GroupBy(item => item.Definition.Id, StringComparer.Ordinal))
            {
                var definition = group.First().Definition;
                var rules = group.First().BattleRules;
                if (group.Count() > definition.MaxCopies || group.Any(item =>
                    !BattleRelicValidation.DefinitionBytes(definition).SequenceEqual(BattleRelicValidation.DefinitionBytes(item.Definition)) ||
                    !rules.Bytes().SequenceEqual(item.BattleRules.Bytes())))
                    throw new ArgumentException("Copies must share one definition and respect its copy limit.", nameof(relics));
            }
            OwnerId = ownerId;
            definitions = setup.ToDictionary(item => item.Instance.Id, item => item.Definition, StringComparer.Ordinal);
            battleRules = setup.ToDictionary(item => item.Instance.Id, item => item.BattleRules, StringComparer.Ordinal);
            Relics = Guard.View(setup.Select((item, order) => new BattleRelicState(item.Instance.Id, item.Definition.Id,
                ownerId, order, item.Instance.Level, item.Instance.Growth, item.Instance.Charges, 0,
                item.Instance.CooldownRounds, item.Instance.Memory)).ToArray());
            Fingerprint = BuildFingerprint();
        }

        private BattleRelicLoadout(BattleRelicLoadout binding, BattleRelicState[] states)
        {
            OwnerId = binding.OwnerId;
            definitions = new Dictionary<string, RelicDefinition>(binding.definitions, StringComparer.Ordinal);
            battleRules = new Dictionary<string, BattleRelicRules>(binding.battleRules, StringComparer.Ordinal);
            Relics = Guard.View(states); Fingerprint = binding.Fingerprint;
        }

        public RelicDefinition Definition(string instanceId)
        {
            Guard.Text(instanceId, nameof(instanceId));
            RelicDefinition definition;
            if (!definitions.TryGetValue(instanceId, out definition)) throw new ArgumentException("Unknown battle relic instance.", nameof(instanceId));
            return definition;
        }

        public BattleRelicRules RulesFor(string instanceId)
        {
            Guard.Text(instanceId, nameof(instanceId));
            BattleRelicRules rules;
            if (!battleRules.TryGetValue(instanceId, out rules)) throw new ArgumentException("Unknown battle relic instance.", nameof(instanceId));
            return rules;
        }

        public BattleRelicLoadout WithRuntime(IEnumerable<BattleRelicState> relics)
        {
            var incoming = Guard.Copy(relics, nameof(relics));
            if (incoming.Length != Relics.Count) throw new ArgumentException("Runtime replacement must contain the complete loadout.", nameof(relics));
            var copies = new BattleRelicState[incoming.Length];
            for (int index = 0; index < incoming.Length; index++)
            {
                var next = incoming[index]; var before = Relics[index];
                BattleRelicValidation.State(next);
                if (!StringComparer.Ordinal.Equals(next.InstanceId, before.InstanceId) ||
                    !StringComparer.Ordinal.Equals(next.DefinitionId, before.DefinitionId) || next.OwnerId != OwnerId ||
                    next.Order != before.Order || next.Level != before.Level || next.Growth != before.Growth)
                    throw new ArgumentException("Runtime replacement cannot alter relic identity, owner, order or progression.", nameof(relics));
                BattleRelicValidation.Charges(Definition(next.InstanceId), next.RemainingCharges);
                copies[index] = next.WithRuntime(next.RemainingCharges, next.UsesThisRound, next.CooldownRounds, next.Memory);
            }
            return new BattleRelicLoadout(this, copies);
        }

        public IEnumerable<RunRelic> ExportRunRelics() => Guard.View(Relics.Select(state =>
            new RunRelic(state.InstanceId, state.DefinitionId, state.Level, state.Growth, state.RemainingCharges,
                state.CooldownRounds, BattleRelicValidation.CopyMemory(state.Memory, true))).ToArray());

        private string BuildFingerprint()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write("battle-relic-loadout.1"); writer.Write(OwnerId.Value); writer.Write(Relics.Count);
                    foreach (var state in Relics)
                    {
                        writer.Write(state.InstanceId); writer.Write(state.DefinitionId); writer.Write(state.Order);
                        writer.Write(state.Level); writer.Write(state.Growth);
                        BattleRelicValidation.WriteDefinition(writer, Definition(state.InstanceId));
                    }
                    // Preserve the exact pre-modifier fingerprint for bindings with no numeric behavior.
                    if (Relics.Any(state => RulesFor(state.InstanceId).Modifiers.Count != 0))
                    {
                        writer.Write("battle-relic.numeric.1");
                        foreach (var state in Relics) RulesFor(state.InstanceId).Write(writer);
                    }
                    // Append a separate extension so all existing numeric-only fingerprints remain byte-for-byte stable.
                    if (Relics.Any(state => RulesFor(state.InstanceId).HandEffects.Count != 0))
                    {
                        writer.Write("battle-relic.hand-effects.1");
                        foreach (var state in Relics) RulesFor(state.InstanceId).WriteHandEffects(writer);
                    }
                    if (Relics.Any(state => RulesFor(state.InstanceId).RuleValues.Count != 0 || RulesFor(state.InstanceId).RefillRules.Count != 0))
                    {
                        writer.Write("battle-relic.rules.1");
                        foreach (var state in Relics) RulesFor(state.InstanceId).WriteRules(writer);
                    }
                    if (Relics.Any(state => RulesFor(state.InstanceId).Triggers.Count != 0))
                    {
                        writer.Write("battle-relic.triggers.1");
                        foreach (var state in Relics) RulesFor(state.InstanceId).WriteTriggers(writer);
                    }
                    if (Relics.Any(state => RulesFor(state.InstanceId).ActionTriggers.Count != 0))
                    {
                        writer.Write("battle-relic.actions.1");
                        foreach (var state in Relics) RulesFor(state.InstanceId).WriteActions(writer);
                    }
                    if (Relics.Any(state => RulesFor(state.InstanceId).HasBoundaryConfiguration))
                    {
                        writer.Write("battle-relic.boundaries.1");
                        foreach (var state in Relics) RulesFor(state.InstanceId).WriteBoundaries(writer);
                    }
                }
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    internal static class BattleRelicValidation
    {
        internal static void Definition(RelicDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            Guard.Text(definition.Id, nameof(definition));
            if (definition.Slots < 1 || definition.Slots > 32 || definition.MaxCopies < 1 || definition.MaxLevel < 1 ||
                definition.MaxLevel > 100 || definition.SellPrice < 0 || definition.UpgradeCost < 0 ||
                definition.GrowthPerScoredRound < 0 || definition.Charges < 0 ||
                definition.AbilityId.HasValue && !definition.AbilityId.Value.IsValid)
                throw new ArgumentException("Invalid battle relic definition.", nameof(definition));
            var effects = Guard.Copy(definition.ScoreEffects, nameof(definition));
            foreach (var effect in effects)
                new ScoreEffect(effect.Timing, effect.Operation, effect.Amount, effect.PerLevel, effect.PerGrowth,
                    effect.Category, effect.Suit, effect.Rank);
        }

        internal static void Charges(RelicDefinition definition, int? charges)
        {
            if (charges.HasValue != definition.Charges.HasValue || charges < 0 || charges > definition.Charges)
                throw new ArgumentException("Relic charges must match the definition's bounded or unbounded form.", nameof(charges));
        }

        internal static void State(BattleRelicState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            Guard.Text(state.InstanceId, nameof(state)); Guard.Text(state.DefinitionId, nameof(state));
            if (!state.OwnerId.IsValid || state.Order < 0 || state.Level < 1 || state.Growth < 0 ||
                state.RemainingCharges < 0 || state.UsesThisRound < 0 || state.CooldownRounds < 0)
                throw new ArgumentException("Invalid battle relic runtime state.", nameof(state));
            Memory(state.Memory);
        }

        internal static void Memory(AbilityMemory memory, bool encounterOnly = false)
        {
            if (memory == null || memory.Version != 1) throw new ArgumentException("Relic memory must use version one.", nameof(memory));
            var counters = Guard.Copy(memory.Counters, nameof(memory));
            if (memory.Marks.Count != 0 || counters.Length > 256 ||
                counters.Select(counter => counter.Key).Distinct(StringComparer.Ordinal).Count() != counters.Length ||
                counters.Any(counter => string.IsNullOrWhiteSpace(counter.Key) || counter.Value < 0 ||
                    !Enum.IsDefined(typeof(MemoryScope), counter.Scope) || encounterOnly && counter.Scope != MemoryScope.Encounter))
                throw new ArgumentException("Relic memory requires unique nonnegative counters and no marks.", nameof(memory));
        }

        internal static AbilityMemory CopyMemory(AbilityMemory memory, bool encounterOnly = false)
        {
            Memory(memory);
            return new AbilityMemory(memory.Counters.Where(counter => !encounterOnly || counter.Scope == MemoryScope.Encounter)
                .Select(counter => new AbilityCounter(counter.Key, counter.Value, counter.Scope)), Array.Empty<AbilityMark>());
        }

        internal static byte[] DefinitionBytes(RelicDefinition definition)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) WriteDefinition(writer, definition);
                return stream.ToArray();
            }
        }

        internal static void WriteDefinition(BinaryWriter writer, RelicDefinition definition)
        {
            writer.Write(definition.Id); writer.Write(definition.Slots); writer.Write(definition.MaxCopies); writer.Write(definition.MaxLevel);
            writer.Write(definition.SellPrice); writer.Write(definition.UpgradeCost); writer.Write(definition.GrowthPerScoredRound);
            writer.Write(definition.AbilityId.HasValue);
            if (definition.AbilityId.HasValue) writer.Write(definition.AbilityId.Value.Value);
            writer.Write(definition.Charges.HasValue);
            if (definition.Charges.HasValue) writer.Write(definition.Charges.Value);
            writer.Write(definition.ScoreEffects.Count);
            foreach (var effect in definition.ScoreEffects)
            {
                writer.Write((int)effect.Timing); writer.Write((int)effect.Operation); writer.Write(effect.Amount);
                writer.Write(effect.PerLevel); writer.Write(effect.PerGrowth);
                writer.Write(effect.Category.HasValue); if (effect.Category.HasValue) writer.Write((int)effect.Category.Value);
                writer.Write(effect.Suit.HasValue); if (effect.Suit.HasValue) writer.Write((int)effect.Suit.Value);
                writer.Write(effect.Rank.HasValue); if (effect.Rank.HasValue) writer.Write((int)effect.Rank.Value);
            }
        }
    }
}
