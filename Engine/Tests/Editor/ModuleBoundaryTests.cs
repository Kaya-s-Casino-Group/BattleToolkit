using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class ModuleBoundaryTests
    {
        private static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]> {
            { "Core", new string[0] }, { "Abilities", new[] { "Core" } }, { "Scoring", new[] { "Core" } },
            { "Holdem", new[] { "Core", "Abilities" } }, { "Draw", new[] { "Core", "Abilities", "Scoring" } },
            { "Runs", new[] { "Core", "Abilities", "Scoring" } },
            { "Progression", new[] { "Core" } },
            { "Encounters", new[] { "Core" } },
            { "Combat", new[] { "Core", "Abilities" } },
            { "Composition", new[] { "Core", "Abilities", "Scoring", "Holdem", "Draw", "Runs", "Progression", "Encounters", "Combat" } }
        };
        private static readonly Type[] Roots = { typeof(CardInstance), typeof(AbilityExecutor), typeof(ScoreEngine),
            typeof(HoldemMatchEngine), typeof(DrawEngine), typeof(RunEngine<,,>), typeof(HoldemRunAdapter), typeof(PlayerProgressionState), typeof(EncounterDefinition), typeof(CombatEngine) };
        [Test] public void ModulesLiveInSeparateAssemblies()
        {
            var assemblies = Roots.Select(t => t.Assembly.GetName().Name).ToArray();
            Assert.That(assemblies.Distinct().Count(), Is.EqualTo(10));
            Assert.That(assemblies, Is.EquivalentTo(Allowed.Keys.Select(m => "PokerEngine." + m)));
        }
        [Test] public void DependencyDirectionIsEnforcedInCompiledAssemblies()
        {
            foreach (var assembly in Roots.Select(t => t.Assembly))
            {
                var module = assembly.GetName().Name.Substring("PokerEngine.".Length);
                var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
                Assert.That(references.Where(r => r.StartsWith("PokerEngine.")).All(r => Allowed[module].Contains(r.Substring("PokerEngine.".Length))), Is.True, module);
                Assert.That(references.Any(r => r.StartsWith("UnityEngine") || r.StartsWith("UnityEditor")), Is.False, module);
            }
        }
        [Test] public void RunSnapshotsKeepConcreteEncounterTypesWithoutModeSwitching()
        {
            Assert.That(typeof(RunState<DrawState>).GetProperty("Encounter").PropertyType, Is.EqualTo(typeof(DrawState)));
            Assert.That(typeof(RunState<HoldemEncounterState>).GetProperty("Encounter").PropertyType, Is.EqualTo(typeof(HoldemEncounterState)));
            var runAssembly = typeof(RunEngine<,,>).Assembly;
            Assert.That(runAssembly.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                .Any(f => f.FieldType == typeof(MatchState) || f.FieldType == typeof(DrawState)), Is.False);
        }
        [Test] public void SharedBattleSessionAndPresentationContractRemainInPureComposition()
        {
            var contracts = new[] { typeof(BattleSession), typeof(BattleSessionResult), typeof(BattleSessionStep),
                typeof(IBattleSessionPresentation), typeof(BattleSessionPlayback), typeof(BattleHost) };
            var composition = typeof(HoldemRunAdapter).Assembly;
            foreach (var contract in contracts)
            {
                Assert.That(contract.Assembly, Is.SameAs(composition), contract.FullName);
                var methods = contract.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                var exposed = methods.Select(method => method.ReturnType)
                    .Concat(methods.SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType))
                    .Concat(contract.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
                foreach (var type in exposed.SelectMany(ContractTypes).Distinct())
                {
                    string assembly = type.Assembly.GetName().Name;
                    Assert.That(IsPureEngineDependency(assembly), Is.True,
                        contract.FullName + " exposes a frontend type: " + type.FullName + " (" + assembly + ")");
                }
            }
            Assert.That(composition.GetReferencedAssemblies().Select(reference => reference.Name)
                .Where(name => !IsPureEngineDependency(name)), Is.Empty);
        }

        private static bool IsPureEngineDependency(string name) => name.StartsWith("PokerEngine.") ||
            name == typeof(object).Assembly.GetName().Name || name == "netstandard" ||
            name == "System" || name.StartsWith("System.");

        private static IEnumerable<Type> ContractTypes(Type type)
        {
            yield return type;
            if (type.HasElementType)
                foreach (var element in ContractTypes(type.GetElementType())) yield return element;
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments().SelectMany(ContractTypes)) yield return argument;
        }
    }
}
