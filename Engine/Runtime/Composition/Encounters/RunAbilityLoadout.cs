using System;
using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
    internal static class RunAbilityLoadout
    {
        private static AbilityMemory BuildMemory(RunRelic relic) => relic.Memory
            .WithCounter("build.level", relic.Level, MemoryScope.Encounter).WithCounter("build.growth", relic.Growth, MemoryScope.Encounter);
        internal static IEnumerable<AbilityInstance> Install(RunEncounterContext context) => context.Relics.Select((j, order) => new { j, order })
            .Where(x => context.Content.Relic(x.j.DefinitionId).AbilityId.HasValue).Select(x => new AbilityInstance(new AbilityInstanceId(x.j.Id),
                context.Content.Relic(x.j.DefinitionId).AbilityId.Value, context.PlayerId, EntityRef.Participant(context.PlayerId), x.j.Charges,
                cooldownRounds: Math.Max(0, x.j.CooldownRounds - 1), memory: BuildMemory(x.j), order: x.order));
        internal static IEnumerable<AbilityInstance> Sync(RunEncounterContext context, IEnumerable<AbilityInstance> abilities) => abilities.Select(a => {
            var relic = context.Relics.FirstOrDefault(j => j.Id == a.Id.Value);
            return relic == null ? a : new AbilityInstance(a.Id, a.DefinitionId, a.OwnerId, a.Source, a.RemainingCharges, a.UsesThisRound,
                a.CooldownRounds, a.Memory.WithCounter("build.level", relic.Level, MemoryScope.Encounter).WithCounter("build.growth", relic.Growth, MemoryScope.Encounter), a.Order);
        });
        internal static IEnumerable<RunRelicRuntime> Export(IEnumerable<AbilityInstance> abilities) => abilities.Select(a => new RunRelicRuntime(a.Id.Value,
            a.RemainingCharges, a.CooldownRounds, new AbilityMemory(a.Memory.Counters.Where(c => c.Scope == MemoryScope.Encounter &&
                c.Key != "build.level" && c.Key != "build.growth"), new AbilityMark[0])));
        internal static void Validate(RunEncounterContext context, IReadOnlyList<AbilityInstance> abilities)
        {
            var expected = context.Relics.Select((j, order) => new { j, order }).Where(x => context.Content.Relic(x.j.DefinitionId).AbilityId.HasValue).ToArray();
            if (abilities.Count != expected.Length || expected.Any(x => !abilities.Any(a => a.Id.Value == x.j.Id &&
                a.DefinitionId == context.Content.Relic(x.j.DefinitionId).AbilityId.Value && a.OwnerId == context.PlayerId && a.Order == x.order)))
                throw new StateValidationException("InvalidEncounterBuild", "Encounter.Abilities");
        }
    }
}
