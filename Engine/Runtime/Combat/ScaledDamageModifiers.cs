using System;

namespace PokerEngine
{
    /// <summary>Pure attack-side numeric rule, captured from authoring at battle creation.</summary>
    public sealed class ScaledAttackDamageModifier : IAttackDamageModifier
    {
        public string Version { get; }
        public decimal Multiplier { get; }
        public long FlatAdjustment { get; }
        public ScaledAttackDamageModifier(decimal multiplier = 1, long flatAdjustment = 0, string version = "attack.scale.1")
        {
            Version = Guard.Text(version, nameof(version));
            if (multiplier < 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
            Multiplier = multiplier; FlatAdjustment = flatAdjustment;
        }
        public DamageModification Modify(DamageModifierContext context) =>
            DamageScaling.Resolve(context, Multiplier, FlatAdjustment, Version, "攻击修正");
    }

    /// <summary>Pure target-side numeric rule, applied before block and health settlement.</summary>
    public sealed class ScaledIncomingDamageModifier : IIncomingDamageModifier
    {
        public string Version { get; }
        public decimal Multiplier { get; }
        public long FlatAdjustment { get; }
        public ScaledIncomingDamageModifier(decimal multiplier = 1, long flatAdjustment = 0, string version = "incoming.scale.1")
        {
            Version = Guard.Text(version, nameof(version));
            if (multiplier < 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
            Multiplier = multiplier; FlatAdjustment = flatAdjustment;
        }
        public DamageModification Modify(DamageModifierContext context) =>
            DamageScaling.Resolve(context, Multiplier, FlatAdjustment, Version, "承伤修正");
    }

    internal static class DamageScaling
    {
        internal static DamageModification Resolve(DamageModifierContext context, decimal multiplier, long flatAdjustment,
            string version, string stage)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            decimal adjusted = checked(context.Amount * multiplier + flatAdjustment);
            long output = checked((long)decimal.Floor(Math.Max(0m, adjusted)));
            return new DamageModification(version, stage + "：伤害 × " + multiplier + " + (" + flatAdjustment + ")，最低 0，向下取整",
                context.Amount, output);
        }
    }
}
