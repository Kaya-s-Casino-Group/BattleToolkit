using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Derived from commitments. Never independently edited alongside those commitments.</summary>
    [DataContract]
    public sealed class PotLayer
    {
        [DataMember(Order = 1, IsRequired = true)] public Chips Amount { get; private set; }
        [DataMember(Name = "Contributors", Order = 2, IsRequired = true)] private ParticipantId[] contributors;
        [DataMember(Name = "EligibleWinners", Order = 3, IsRequired = true)] private ParticipantId[] eligibleWinners;
        public IReadOnlyList<ParticipantId> Contributors => Guard.View(contributors);
        public IReadOnlyList<ParticipantId> EligibleWinners => Guard.View(eligibleWinners);
        public PotLayer(Chips amount, IEnumerable<ParticipantId> contributors, IEnumerable<ParticipantId> eligibleWinners)
        { Amount = amount; this.contributors = Guard.Copy(contributors, nameof(contributors)); this.eligibleWinners = Guard.Copy(eligibleWinners, nameof(eligibleWinners)); }
    }

    [DataContract]
    public sealed class ChipTransfer
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId ParticipantId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public Chips Amount { get; private set; }
        public ChipTransfer(ParticipantId participantId, Chips amount) { ParticipantId = participantId; Amount = amount; }
    }

    [DataContract]
    public sealed class PotAward
    {
        [DataMember(Order = 1, IsRequired = true)] public int PotIndex { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public ParticipantId ParticipantId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public Chips Amount { get; private set; }
        public PotAward(int potIndex, ParticipantId participantId, Chips amount)
        { PotIndex = potIndex; ParticipantId = participantId; Amount = amount; }
    }

    public sealed class PotBreakdown
    {
        public IReadOnlyList<PotLayer> Pots { get; }
        public IReadOnlyList<ChipTransfer> Refunds { get; }
        internal PotBreakdown(IEnumerable<PotLayer> pots, IEnumerable<ChipTransfer> refunds)
        { Pots = Guard.View(Guard.Copy(pots, nameof(pots))); Refunds = Guard.View(Guard.Copy(refunds, nameof(refunds))); }
    }

    /// <summary>Standard contribution layers only; custom bonus pots require an explicit future policy.</summary>
    public static class PotCalculator
    {
        public static PotBreakdown Calculate(IEnumerable<HandParticipantState> participants)
        {
            var players = Guard.Copy(participants, nameof(participants));
            if (players.Any(p => !p.ParticipantId.IsValid || p.HandCommitted.Value < 0 || p.StreetCommitted.Value < 0 ||
                    p.StreetCommitted > p.HandCommitted || !Enum.IsDefined(typeof(Participation), p.Participation)) ||
                players.Select(p => p.ParticipantId).Distinct().Count() != players.Length)
                throw new ArgumentException("Invalid or duplicate contribution record.");
            var levels = players.Select(p => p.HandCommitted.Value).Where(v => v > 0).Distinct().OrderBy(v => v);
            var pots = new List<PotLayer>(); var refunds = new List<ChipTransfer>(); long previous = 0;
            foreach (long level in levels)
            {
                var contributors = players.Where(p => p.HandCommitted.Value >= level).ToArray();
                var amount = new Chips(checked((level - previous) * contributors.LongLength));
                if (contributors.Length == 1) refunds.Add(new ChipTransfer(contributors[0].ParticipantId, amount));
                else pots.Add(new PotLayer(amount, contributors.Select(p => p.ParticipantId),
                    contributors.Where(p => p.Participation == Participation.InHand).Select(p => p.ParticipantId)));
                previous = level;
            }
            return new PotBreakdown(pots, refunds);
        }
    }

    /// <summary>Presence means payouts are ALREADY reflected in stacks. Not a request to pay again.</summary>
    [DataContract]
    public sealed class PotSettlement
    {
        [DataMember(Order = 1, IsRequired = true)] public string SettlementId { get; private set; }
        [DataMember(Name = "Evaluations", Order = 2, IsRequired = true)] private HandEvaluation[] evaluations;
        [DataMember(Name = "Awards", Order = 3, IsRequired = true)] private PotAward[] awards;
        [DataMember(Name = "Refunds", Order = 4, IsRequired = true)] private ChipTransfer[] refunds;
        public IReadOnlyList<HandEvaluation> Evaluations => Guard.View(evaluations);
        public IReadOnlyList<PotAward> Awards => Guard.View(awards);
        public IReadOnlyList<ChipTransfer> Refunds => Guard.View(refunds);
        public PotSettlement(string settlementId, IEnumerable<HandEvaluation> evaluations,
            IEnumerable<PotAward> awards, IEnumerable<ChipTransfer> refunds)
        {
            SettlementId = Guard.Text(settlementId, nameof(settlementId)); this.evaluations = Guard.Copy(evaluations, nameof(evaluations));
            this.awards = Guard.Copy(awards, nameof(awards)); this.refunds = Guard.Copy(refunds, nameof(refunds));
        }
    }
}
