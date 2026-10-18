using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace PokerEngine
{
    /// <summary>Minimal duel rules, not a character base class or a complete Draw/Run configuration.</summary>
    public sealed class ScoredDuelDefinition
    {
        public string Version { get; }
        public CombatantDefinition Player { get; }
        public CombatantDefinition Enemy { get; }
        public ScoreDamagePolicy DamagePolicy { get; }
        public long EnemyDamage { get; }
        public int RoundLimit { get; }
        public int MaxEnergy { get; }
        public int InitialEnergy { get; }
        public int PlayEnergyCost { get; }
        public int DiscardEnergyCost { get; }
        public string Fingerprint { get; }
        public ScoredDuelDefinition(string version, CombatantDefinition player, CombatantDefinition enemy,
            ScoreDamagePolicy damagePolicy, long enemyDamage, int roundLimit, int maxEnergy = 3, int playEnergyCost = 1,
            int? initialEnergy = null, int discardEnergyCost = 1)
        {
            Version = Guard.Text(version, nameof(version)); Player = player ?? throw new ArgumentNullException(nameof(player));
            Enemy = enemy ?? throw new ArgumentNullException(nameof(enemy)); DamagePolicy = damagePolicy ?? throw new ArgumentNullException(nameof(damagePolicy));
            if (enemyDamage < 0 || roundLimit < 1 || roundLimit > 1000 || maxEnergy < 1 || initialEnergy < 0 ||
                playEnergyCost < 1 || discardEnergyCost < 1) throw new ArgumentException("Invalid duel budget.");
            EnemyDamage = enemyDamage; RoundLimit = roundLimit; MaxEnergy = maxEnergy; InitialEnergy = initialEnergy ?? maxEnergy;
            PlayEnergyCost = playEnergyCost; DiscardEnergyCost = discardEnergyCost;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(ScoredDuelEngine.RulesVersion); writer.Write(CombatEngine.RulesVersion); writer.Write(Version);
                    foreach (var unit in new[] { Player, Enemy })
                    { writer.Write(unit.Id); writer.Write(unit.MaxHealth); writer.Write(unit.InitialBlock); }
                    writer.Write(DamagePolicy.PointsPerDamage); writer.Write(EnemyDamage); writer.Write(RoundLimit);
                    writer.Write(MaxEnergy); writer.Write(InitialEnergy); writer.Write(PlayEnergyCost); writer.Write(DiscardEnergyCost);
                }
                using (var hash = SHA256.Create()) Fingerprint = BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }
    public enum ScoredDuelPhase { AwaitingPlayerHand = 1, AwaitingEnemyAction, Won, Lost }
    public enum ScoredDuelError { None, InvalidState, StaleRevision, InvalidPhase, InvalidScore, InsufficientEnergy, Overflow }

    /// <summary>Player-only action budget. Monsters and common combatants do not own energy.</summary>
    [DataContract]
    public sealed class PlayerTurnState
    {
        [DataMember(Order = 1, IsRequired = true)] public int MaxEnergy { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int Energy { get; private set; }
        public PlayerTurnState(int maxEnergy, int energy)
        {
            if (maxEnergy < 1 || energy < 0) throw new ArgumentException("Invalid player energy.");
            MaxEnergy = maxEnergy; Energy = energy;
        }
    }
    public enum ScoredDuelActionKind { ScoredHand = 1, EndPlayerTurn, EnemyAction, Discard, SkipPlayerTurn }
    [DataContract]
    public sealed class ScoredDuelActionRecord
    {
        [DataMember(Order = 1, IsRequired = true)] public ScoredDuelActionKind Kind { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int? ScoreIndex { get; private set; }
        public ScoredDuelActionRecord(ScoredDuelActionKind kind, int? scoreIndex = null)
        {
            if (!Enum.IsDefined(typeof(ScoredDuelActionKind), kind) || kind == ScoredDuelActionKind.SkipPlayerTurn ||
                (kind == ScoredDuelActionKind.ScoredHand) != scoreIndex.HasValue || scoreIndex < 0)
                throw new ArgumentException("Invalid duel action record.");
            Kind = kind; ScoreIndex = scoreIndex;
        }
    }

    /// <summary>Canonical duel state. Common progress and terminal results are derived projections.</summary>
    [DataContract]
    public sealed class ScoredDuelState
    {
        [DataMember(Order = 1, IsRequired = true)] public string DefinitionFingerprint { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public CombatState Combat { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public ParticipantId PlayerId { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public ParticipantId EnemyId { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int InitialPlayerHealth { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public ScoredDuelPhase Phase { get; private set; }
        [DataMember(Name = "Scores", Order = 7, IsRequired = true)] private ScoreResult[] scores;
        public IReadOnlyList<ScoreResult> Scores => Guard.View(scores);
        [DataMember(Order = 8, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public int CompletedRounds { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] public PlayerTurnState PlayerTurn { get; private set; }
        [DataMember(Name = "Actions", Order = 11, IsRequired = true)] private ScoredDuelActionRecord[] actions;
        public IReadOnlyList<ScoredDuelActionRecord> Actions => Guard.View(actions);
        public bool IsFinished => Phase == ScoredDuelPhase.Won || Phase == ScoredDuelPhase.Lost;
        public long TotalScore => scores.Aggregate(0L, (sum, score) => checked(sum + score.Total));
        internal ScoredDuelState(string fingerprint, CombatState combat, ParticipantId player, ParticipantId enemy,
            int initialPlayerHealth, ScoredDuelPhase phase, IEnumerable<ScoreResult> scores, long revision,
            int completedRounds, PlayerTurnState playerTurn, IEnumerable<ScoredDuelActionRecord> actions)
        {
            DefinitionFingerprint = fingerprint; Combat = combat; PlayerId = player; EnemyId = enemy;
            InitialPlayerHealth = initialPlayerHealth; Phase = phase; this.scores = Guard.Copy(scores, nameof(scores));
            Revision = revision; CompletedRounds = completedRounds; PlayerTurn = playerTurn; this.actions = Guard.Copy(actions, nameof(actions));
        }
    }
    public sealed class ScoredDuelTransition
    {
        public ScoredDuelState State { get; }
        public ScoredDuelError Error { get; }
        public bool Accepted => Error == ScoredDuelError.None;
        public IReadOnlyList<CombatEvent> Events { get; }
        internal ScoredDuelTransition(ScoredDuelState state, ScoredDuelError error = ScoredDuelError.None, IEnumerable<CombatEvent> events = null)
        { State = state; Error = error; Events = Guard.View(Guard.Copy(events ?? new CombatEvent[0], nameof(events))); }
    }
}
