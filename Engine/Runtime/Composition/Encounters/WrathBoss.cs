using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    public enum WrathHandRole { Blood, Binding }

    /// <summary>Opt-in encounter rules. Ordinary enemy behavior does not instantiate this definition.</summary>
    public sealed class WrathBossDefinition
    {
        public IReadOnlyList<string> BloodHandRefs { get; }
        public string BindingHandRef { get; }
        public IReadOnlyList<string> HandRefs { get; }
        public long BaseAttack { get; }
        public long AttackPerHand { get; }
        public long AttackPerCharge { get; }
        public string ChargeActionId { get; }
        public int InitialWarningRound { get; }
        public int CooldownRounds { get; }
        public int RegenerationRounds { get; }
        public decimal UltimateScale { get; }
        public WrathBossDefinition(IEnumerable<string> bloodHandRefs, string bindingHandRef, long baseAttack = 8,
            long attackPerHand = 5, long attackPerCharge = 10, string chargeActionId = "charge", int initialWarningRound = 5,
            int cooldownRounds = 5, int regenerationRounds = 8, decimal ultimateScale = 3)
        {
            var blood = (bloodHandRefs ?? Array.Empty<string>()).ToArray();
            BindingHandRef = Guard.Text(bindingHandRef, nameof(bindingHandRef));
            if (blood.Length != 4 || blood.Any(string.IsNullOrWhiteSpace) || blood.Concat(new[] { bindingHandRef }).Distinct(StringComparer.Ordinal).Count() != 5 ||
                baseAttack < 1 || attackPerHand < 0 || attackPerCharge < 0 || initialWarningRound < 1 || cooldownRounds < 1 || regenerationRounds < 1 || ultimateScale <= 0)
                throw new ArgumentException("Wrath needs four distinct blood hands and one binding hand with valid timing and attack values.");
            BloodHandRefs = Array.AsReadOnly(blood); HandRefs = Array.AsReadOnly(blood.Concat(new[] { bindingHandRef }).ToArray());
            BaseAttack = baseAttack; AttackPerHand = attackPerHand; AttackPerCharge = attackPerCharge;
            ChargeActionId = Guard.Text(chargeActionId, nameof(chargeActionId)); InitialWarningRound = initialWarningRound;
            CooldownRounds = cooldownRounds; RegenerationRounds = regenerationRounds; UltimateScale = ultimateScale;
        }
    }

    /// <summary>Immutable authority owned by the body. Deadlines use one-based full battle rounds.</summary>
    public sealed class WrathBossRuntime
    {
        public int Charges { get; }
        public int ReadyRound { get; }
        public int? ReleaseRound { get; }
        public IReadOnlyDictionary<ParticipantId, int> ReviveRounds { get; }
        internal WrathBossRuntime(int charges, int readyRound, int? releaseRound = null, IDictionary<ParticipantId, int> reviveRounds = null)
        {
            Charges = charges; ReadyRound = readyRound; ReleaseRound = releaseRound;
            ReviveRounds = new ReadOnlyDictionary<ParticipantId, int>(new Dictionary<ParticipantId, int>(reviveRounds ?? new Dictionary<ParticipantId, int>()));
        }
        internal WrathBossRuntime With(int? charges = null, int? readyRound = null, int? releaseRound = null, bool clearRelease = false,
            IDictionary<ParticipantId, int> reviveRounds = null) => new WrathBossRuntime(charges ?? Charges, readyRound ?? ReadyRound,
                clearRelease ? null : releaseRound ?? ReleaseRound, reviveRounds ?? ReviveRounds.ToDictionary(p => p.Key, p => p.Value));
    }

    public sealed class WrathHandView
    {
        public ParticipantId Id { get; }
        public WrathHandRole Role { get; }
        public bool IsAlive { get; }
        public int? ReviveRound { get; }
        public int ReviveRemaining { get; }
        internal WrathHandView(ParticipantId id, WrathHandRole role, bool alive, int? due, int round)
        { Id = id; Role = role; IsAlive = alive; ReviveRound = due; ReviveRemaining = due.HasValue ? Math.Max(0, due.Value - round) : 0; }
    }
    public sealed class WrathBossView
    {
        public ParticipantId BodyId { get; }
        public long Attack { get; }
        public int Charges { get; }
        public long UltimateBaseDamage { get; }
        public long PendingUltimateBaseDamage { get; }
        public bool WarningActive { get; }
        public int? ReleaseRound { get; }
        public int CooldownRemaining { get; }
        public IReadOnlyList<WrathHandView> Hands { get; }
        internal WrathBossView(ParticipantId bodyId, long attack, WrathBossRuntime runtime, int round, IEnumerable<WrathHandView> hands,
            long ultimateBaseDamage, long pendingUltimateBaseDamage)
        {
            BodyId = bodyId; Attack = attack; Charges = runtime.Charges; WarningActive = runtime.ReleaseRound.HasValue;
            UltimateBaseDamage = ultimateBaseDamage; PendingUltimateBaseDamage = pendingUltimateBaseDamage;
            ReleaseRound = runtime.ReleaseRound; CooldownRemaining = Math.Max(0, runtime.ReadyRound - round); Hands = Array.AsReadOnly(hands.ToArray());
        }
    }
    public static class WrathBossQuery
    {
        /// <summary>Pure shared view for all clients; never advances a clock or consumes an intent.</summary>
        public static WrathBossView Read(TeamBattleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var body = state.Enemies.SingleOrDefault(e => e.Behavior?.Wrath != null);
            if (body == null) return null;
            var definition = body.Behavior.Wrath; var runtime = WrathBossRules.Runtime(body); int round = state.CompletedRounds + 1;
            var hands = WrathBossRules.Hands(state.Enemies, definition).Select(hand => {
                bool alive = state.Combat.Get(hand.Id).IsAlive;
                int? due = !alive ? runtime.ReviveRounds.TryGetValue(hand.Id, out int value) ? value : checked(round + definition.RegenerationRounds) : (int?)null;
                return new WrathHandView(hand.Id, hand.Definition.Id == definition.BindingHandRef ? WrathHandRole.Binding : WrathHandRole.Blood, alive, due, round);
            });
            long attack = WrathBossRules.Attack(state, body);
            long pendingAttack = runtime.ReleaseRound.HasValue && body.BehaviorState.Intent?.Id == definition.ChargeActionId
                ? checked(attack + definition.AttackPerCharge) : attack;
            return new WrathBossView(body.Id, attack, runtime, round, hands,
                checked((long)decimal.Floor(checked(attack * definition.UltimateScale))),
                checked((long)decimal.Floor(checked(pendingAttack * definition.UltimateScale))));
        }
    }
    internal static class WrathBossRules
    {
        internal static WrathBossRuntime Runtime(BattleEnemySetup body) => body.BehaviorState.Wrath ?? new WrathBossRuntime(0, body.Behavior.Wrath.InitialWarningRound);
        internal static IEnumerable<BattleEnemySetup> Hands(IEnumerable<BattleEnemySetup> enemies, WrathBossDefinition definition) => enemies.Where(e => definition.HandRefs.Contains(e.Definition.Id));
        internal static long Attack(TeamBattleState state, BattleEnemySetup body)
        {
            var d = body.Behavior.Wrath;
            return checked(d.BaseAttack + checked(d.AttackPerHand * Hands(state.Enemies, d).Count(e => state.Combat.Get(e.Id).IsAlive)) + checked(d.AttackPerCharge * Runtime(body).Charges));
        }
        /// <summary>Every immutable combat snapshot captures deaths immediately, including item/admin/reaction paths.</summary>
        internal static BattleEnemySetup[] Observe(IEnumerable<BattleEnemySetup> enemies, CombatState combat, int round)
        {
            var copy = enemies.ToArray();
            for (int index = 0; index < copy.Length; index++)
            {
                var body = copy[index]; if (body.Behavior?.Wrath == null) continue;
                var d = body.Behavior.Wrath; var current = Runtime(body); var due = current.ReviveRounds.ToDictionary(p => p.Key, p => p.Value);
                bool death = false, changed = body.BehaviorState.Wrath == null;
                foreach (var hand in Hands(copy, d))
                {
                    if (!combat.Get(hand.Id).IsAlive && !due.ContainsKey(hand.Id)) { due[hand.Id] = checked(round + d.RegenerationRounds); death = true; changed = true; }
                    else if (combat.Get(hand.Id).IsAlive && due.Remove(hand.Id)) changed = true;
                }
                if (changed) copy[index] = body.WithBehaviorState(body.BehaviorState.WithWrath(current.With(
                    readyRound: death && current.ReleaseRound.HasValue ? checked(round + d.CooldownRounds) : (int?)null,
                    clearRelease: death && current.ReleaseRound.HasValue, reviveRounds: due)));
            }
            return copy;
        }
    }
}
