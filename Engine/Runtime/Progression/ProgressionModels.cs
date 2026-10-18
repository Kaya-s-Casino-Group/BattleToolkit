using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum UnlockKind { Card = 1, Relic = 2, StartingDeck, Character, GameMode, Difficulty, Challenge }
    public enum RunOutcome { Won = 1, Lost, Abandoned }

    /// <summary>Permission to use a definition in future Runs; never a card or Relic instance.</summary>
    [DataContract]
    public sealed class ProgressionUnlock : IEquatable<ProgressionUnlock>
    {
        [DataMember(Order = 1, IsRequired = true)] public UnlockKind Kind { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string Id { get; private set; }
        public ProgressionUnlock(UnlockKind kind, string id)
        {
            if (!Enum.IsDefined(typeof(UnlockKind), kind)) throw new ArgumentException("Invalid unlock kind.");
            Kind = kind; Id = Guard.Text(id, nameof(id));
        }
        public bool Equals(ProgressionUnlock other) => other != null && Kind == other.Kind && StringComparer.Ordinal.Equals(Id, other.Id);
        public override bool Equals(object other) => Equals(other as ProgressionUnlock);
        public override int GetHashCode() => ((int)Kind * 397) ^ (Id == null ? 0 : StringComparer.Ordinal.GetHashCode(Id));
    }

    [DataContract]
    public sealed class ProgressionCurrency
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Amount { get; private set; }
        public ProgressionCurrency(string id, long amount)
        { Id = Guard.Text(id, nameof(id)); if (amount < 0) throw new ArgumentException("Negative progression currency."); Amount = amount; }
    }

    [DataContract]
    public sealed class ProgressionUpgradeLevel
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int Level { get; private set; }
        public ProgressionUpgradeLevel(string id, int level)
        { Id = Guard.Text(id, nameof(id)); if (level < 0) throw new ArgumentException("Negative upgrade level."); Level = level; }
    }

    [DataContract]
    public sealed class ProgressionReward
    {
        [DataMember(Name = "Currencies", Order = 1, IsRequired = true)] private ProgressionCurrency[] currencies;
        [DataMember(Name = "Unlocks", Order = 2, IsRequired = true)] private ProgressionUnlock[] unlocks;
        public IReadOnlyList<ProgressionCurrency> Currencies => Guard.View(currencies);
        public IReadOnlyList<ProgressionUnlock> Unlocks => Guard.View(unlocks);
        public ProgressionReward(IEnumerable<ProgressionCurrency> currencies = null, IEnumerable<ProgressionUnlock> unlocks = null)
        {
            this.currencies = Guard.Copy(currencies ?? new ProgressionCurrency[0], nameof(currencies));
            this.unlocks = Guard.Copy(unlocks ?? new ProgressionUnlock[0], nameof(unlocks));
            if (this.currencies.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != this.currencies.Length ||
                this.unlocks.Distinct().Count() != this.unlocks.Length) throw new ArgumentException("Duplicate reward identities.");
        }
    }

    /// <summary>Authoritative terminal Run summary and its resolved meta rewards. No mutable Run state is retained.</summary>
    [DataContract]
    public sealed class RunSettlement
    {
        [DataMember(Order = 1, IsRequired = true)] public string RunId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public ParticipantId PlayerId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public RunOutcome Outcome { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public string ModeId { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public string RunRulesVersion { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public string StartingDeckVersion { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public long TotalScore { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public int CompletedFloors { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public int TotalFloors { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] public ProgressionReward Reward { get; private set; }
        public RunSettlement(string runId, ParticipantId player, RunOutcome outcome, string modeId, string runRulesVersion,
            string startingDeckVersion, long totalScore, int completedFloors, int totalFloors, ProgressionReward reward)
        {
            RunId = Guard.Text(runId, nameof(runId)); ModeId = Guard.Text(modeId, nameof(modeId));
            RunRulesVersion = Guard.Text(runRulesVersion, nameof(runRulesVersion)); StartingDeckVersion = Guard.Text(startingDeckVersion, nameof(startingDeckVersion));
            if (!player.IsValid || !Enum.IsDefined(typeof(RunOutcome), outcome) || totalScore < 0 || totalFloors < 1 || totalFloors > 32 ||
                completedFloors < 0 || completedFloors > totalFloors || (outcome == RunOutcome.Won) != (completedFloors == totalFloors))
                throw new ArgumentException("Invalid Run settlement.");
            PlayerId = player; Outcome = outcome; TotalScore = totalScore; CompletedFloors = completedFloors; TotalFloors = totalFloors;
            Reward = reward ?? throw new ArgumentNullException(nameof(reward));
        }
        internal bool SameAs(RunSettlement other) => RunId == other.RunId && PlayerId == other.PlayerId && Outcome == other.Outcome &&
            ModeId == other.ModeId && RunRulesVersion == other.RunRulesVersion && StartingDeckVersion == other.StartingDeckVersion &&
            TotalScore == other.TotalScore && CompletedFloors == other.CompletedFloors && TotalFloors == other.TotalFloors &&
            Reward.Currencies.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new { c.Id, c.Amount })
                .SequenceEqual(other.Reward.Currencies.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new { c.Id, c.Amount })) &&
            new HashSet<ProgressionUnlock>(Reward.Unlocks).SetEquals(other.Reward.Unlocks);
    }

    public sealed class RunStatistics
    {
        public int RunsFinished { get; }
        public int Wins { get; }
        public int Losses { get; }
        public int Abandoned { get; }
        public long BestScore { get; }
        public int MostFloorsCompleted { get; }
        internal RunStatistics(IEnumerable<RunSettlement> settlements)
        {
            var values = settlements.ToArray(); RunsFinished = values.Length;
            Wins = values.Count(r => r.Outcome == RunOutcome.Won); Losses = values.Count(r => r.Outcome == RunOutcome.Lost);
            Abandoned = values.Count(r => r.Outcome == RunOutcome.Abandoned);
            BestScore = values.Length == 0 ? 0 : values.Max(r => r.TotalScore);
            MostFloorsCompleted = values.Length == 0 ? 0 : values.Max(r => r.CompletedFloors);
        }
    }

    /// <summary>Cross-Run player growth. ActiveRun snapshots and UI preferences belong to the host save container.</summary>
    [DataContract]
    public sealed class PlayerProgressionState
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId PlayerId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string RulesVersion { get; private set; }
        [DataMember(Name = "Currencies", Order = 4, IsRequired = true)] private ProgressionCurrency[] currencies;
        [DataMember(Name = "Unlocks", Order = 5, IsRequired = true)] private ProgressionUnlock[] unlocks;
        [DataMember(Name = "Upgrades", Order = 6, IsRequired = true)] private ProgressionUpgradeLevel[] upgrades;
        [DataMember(Name = "Settlements", Order = 7, IsRequired = true)] private RunSettlement[] settlements;
        [DataMember(Order = 8, IsRequired = true)] public string RulesFingerprint { get; private set; }
        public IReadOnlyList<ProgressionCurrency> Currencies => Guard.View(currencies);
        public IReadOnlyList<ProgressionUnlock> Unlocks => Guard.View(unlocks);
        public IReadOnlyList<ProgressionUpgradeLevel> Upgrades => Guard.View(upgrades);
        public IReadOnlyList<RunSettlement> Settlements => Guard.View(settlements);
        public RunStatistics Statistics => new RunStatistics(settlements);
        public RunStatistics StatisticsForMode(string modeId)
        { Guard.Text(modeId, nameof(modeId)); return new RunStatistics(settlements.Where(r => r.ModeId == modeId)); }
        public bool IsUnlocked(ProgressionUnlock unlock) => unlock != null && unlocks.Contains(unlock);
        public long Balance(string currencyId) => currencies.Single(c => c.Id == currencyId).Amount;
        public int UpgradeLevel(string upgradeId) => upgrades.Single(u => u.Id == upgradeId).Level;
        public PlayerProgressionState(ParticipantId player, long revision, string rulesVersion, string rulesFingerprint, IEnumerable<ProgressionCurrency> currencies,
            IEnumerable<ProgressionUnlock> unlocks, IEnumerable<ProgressionUpgradeLevel> upgrades, IEnumerable<RunSettlement> settlements)
        {
            if (!player.IsValid || revision < 0) throw new ArgumentException("Invalid player progression identity/revision.");
            PlayerId = player; Revision = revision; RulesVersion = Guard.Text(rulesVersion, nameof(rulesVersion));
            RulesFingerprint = Guard.Text(rulesFingerprint, nameof(rulesFingerprint));
            this.currencies = Guard.Copy(currencies, nameof(currencies)); this.unlocks = Guard.Copy(unlocks, nameof(unlocks));
            this.upgrades = Guard.Copy(upgrades, nameof(upgrades)); this.settlements = Guard.Copy(settlements, nameof(settlements));
        }
    }
}
