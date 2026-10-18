using System;
using System.Linq;

namespace PokerEngine
{
    internal partial class BattleContentBuildContext
    {
        /// <summary>Checks static structural invariants without evaluating scores, selecting targets or executing effects.</summary>
        internal void ValidateCombinations(CompiledBattleContent content)
        {
            foreach (var encounter in content.Encounters)
            {
                var location = content.EncounterLocations[encounter.Key];
                foreach (var player in encounter.Value.Players)
                {
                    if (player.LoadoutId == null) continue;
                    var handRule = content.Players[player.TemplateId].Rules.EffectRule as ConfiguredHandEffectRule;
                    if (handRule == null) continue; // The existing setup validator rejects structural relics without an effect rule.
                    var entries = content.Loadouts[player.LoadoutId].Relics.SelectMany(relic => relic.BattleRules.HandEffects
                        .Select(spec => new { Relic = relic, Spec = spec })).ToArray();
                    try { RelicHandEffectValidation.Combinations(entries.Select(entry => entry.Spec)); }
                    catch (ArgumentException error)
                    {
                        diagnostics.Add(new BattleDiagnostic("InvalidHandCombination", BattleDiagnosticSeverity.Error,
                            error.Message, location, encounter.Key));
                        continue;
                    }
                    foreach (var entry in entries.Where(entry => entry.Spec.StepId != null))
                    {
                        bool exists = handRule.Entries.Where(branch => branch.Category == entry.Spec.HandCategory)
                            .Any(branch => branch.Effects.Any(step => step.Id == entry.Spec.StepId));
                        if (!exists)
                            diagnostics.Add(new BattleDiagnostic("UnmatchedHandStep", BattleDiagnosticSeverity.Warning,
                                "Player " + player.Id + ": relic " + entry.Relic.Definition.Id + " modifier " + entry.Spec.Id +
                                " matches no base step " + entry.Spec.StepId + " in any " + entry.Spec.HandCategory + " branch.",
                                location, entry.Relic.Definition.Id, entry.Spec.Id + ".stepId"));
                    }
                    foreach (var branch in handRule.Entries)
                    {
                        var applicable = entries.Where(entry => entry.Spec.HandCategory == branch.Category).ToArray();
                        int count = branch.Effects.Count + applicable.Count(entry => entry.Spec.Operation == RelicHandEffectOperation.AppendEffect);
                        foreach (var step in branch.Effects)
                        {
                            var edits = applicable.Where(entry => entry.Spec.StepId == step.Id).ToArray();
                            var replacement = edits.FirstOrDefault(entry => entry.Spec.Operation == RelicHandEffectOperation.ReplaceEffect);
                            var resultingKind = replacement == null ? step.Kind : replacement.Spec.Effect.Kind;
                            foreach (var entry in edits.Where(entry => entry.Spec.Operation == RelicHandEffectOperation.AddHits))
                            {
                                if (step.Kind != CombatEffectKind.Damage || resultingKind != CombatEffectKind.Damage)
                                    diagnostics.Add(new BattleDiagnostic("InvalidAdditionalHits", BattleDiagnosticSeverity.Error,
                                        "Player " + player.Id + ": " + branch.Category + "/" + branch.Variant + " step " + step.Id +
                                        " must be damage both before and after replacement to receive additional hits.",
                                        location, entry.Relic.Definition.Id, entry.Spec.Id + ".stepId"));
                                count += entry.Spec.AdditionalHits;
                            }
                        }
                        if (count > 64)
                            diagnostics.Add(new BattleDiagnostic("HandEffectLimit", BattleDiagnosticSeverity.Error,
                                "Player " + player.Id + ": " + branch.Category + "/" + branch.Variant +
                                " expands to " + count + " effect steps; the maximum is 64.", location, encounter.Key));
                    }
                }
            }
        }
    }
}
