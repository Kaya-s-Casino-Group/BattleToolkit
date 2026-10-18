using System;

namespace PokerEngine
{
    public sealed class AbilityCostQuery
    {
        public ParticipantId PayerId { get; }
        public AbilityInstanceId AbilityId { get; }
        public AbilityCostQuery(ParticipantId payerId, AbilityInstanceId abilityId)
        {
            if (!payerId.IsValid || !abilityId.IsValid) throw new ArgumentException("Invalid cost query.");
            PayerId = payerId; AbilityId = abilityId;
        }
    }

    /// <summary>Final cost still needs affordability checking and atomic payment by the command executor.</summary>
    public sealed class AbilityCost
    {
        public Chips Chips { get; }
        public int Energy { get; }
        public AbilityCost(Chips chips, int energy)
        {
            if (chips.Value < 0 || energy < 0) throw new ArgumentException("Cost must be nonnegative.");
            Chips = chips; Energy = energy;
        }
    }

    public sealed class CardFaceQuery
    {
        public CardInstanceId CardId { get; }
        public CardFaceQuery(CardInstanceId cardId)
        { if (!cardId.IsValid) throw new ArgumentException("Invalid card query."); CardId = cardId; }
    }
}
