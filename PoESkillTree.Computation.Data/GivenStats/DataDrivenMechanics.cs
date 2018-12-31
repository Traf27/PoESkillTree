﻿using System;
using System.Collections.Generic;
using System.Linq;
using EnumsNET;
using PoESkillTree.Computation.Common;
using PoESkillTree.Computation.Common.Builders;
using PoESkillTree.Computation.Common.Builders.Conditions;
using PoESkillTree.Computation.Common.Builders.Damage;
using PoESkillTree.Computation.Common.Builders.Effects;
using PoESkillTree.Computation.Common.Builders.Forms;
using PoESkillTree.Computation.Common.Builders.Modifiers;
using PoESkillTree.Computation.Common.Builders.Stats;
using PoESkillTree.Computation.Common.Builders.Values;
using PoESkillTree.Computation.Common.Data;
using PoESkillTree.Computation.Data.Base;
using PoESkillTree.Computation.Data.Collections;
using PoESkillTree.GameModel;
using PoESkillTree.Utils.Extensions;

namespace PoESkillTree.Computation.Data.GivenStats
{
    public class DataDrivenMechanics : UsesConditionBuilders, IGivenStats
    {
        private readonly IModifierBuilder _modifierBuilder;
        private readonly IMetaStatBuilders _stat;
        private readonly Lazy<IReadOnlyList<IIntermediateModifier>> _lazyGivenStats;

        public DataDrivenMechanics(
            IBuilderFactories builderFactories, IModifierBuilder modifierBuilder, IMetaStatBuilders metaStatBuilders)
            : base(builderFactories)
        {
            _modifierBuilder = modifierBuilder;
            _stat = metaStatBuilders;
            _lazyGivenStats = new Lazy<IReadOnlyList<IIntermediateModifier>>(
                () => CollectionToList(CreateCollection()));
        }

        public IReadOnlyList<Entity> AffectedEntities { get; } = Enums.GetValues<Entity>().ToList();

        public IReadOnlyList<string> GivenStatLines { get; } = new string[0];

        public IReadOnlyList<IIntermediateModifier> GivenModifiers => _lazyGivenStats.Value;

        private DataDrivenMechanicCollection CreateCollection()
            => new DataDrivenMechanicCollection(_modifierBuilder, BuilderFactories)
            {
                // skill hit damage
                // - DPS
                {
                    TotalOverride, _stat.SkillDpsWithHits,
                    _stat.AverageHitDamage.Value *
                    ValueFactory.If(Stat.HitRate.IsSet).Then(Stat.HitRate.Value)
                        .Else(_stat.CastRate.Value * _stat.SkillNumberOfHitsPerCast.Value)
                },
                // - average damage
                {
                    TotalOverride, _stat.AverageHitDamage,
                    CombineSource(_stat.AverageDamage.WithHits, CombineHandsForHitDamage)
                },
                // - average damage per source
                {
                    TotalOverride, _stat.AverageDamage.WithHits.With(AttackDamageHand.MainHand),
                    _stat.AverageDamagePerHit.With(AttackDamageHand.MainHand).Value *
                    Stat.ChanceToHit.With(AttackDamageHand.MainHand).Value.AsPercentage
                },
                {
                    TotalOverride, _stat.AverageDamage.WithHits.With(AttackDamageHand.OffHand),
                    _stat.AverageDamagePerHit.With(AttackDamageHand.OffHand).Value *
                    Stat.ChanceToHit.With(AttackDamageHand.OffHand).Value.AsPercentage
                },
                {
                    TotalOverride, _stat.AverageDamage.WithHits.With(DamageSource.Spell),
                    _stat.AverageDamagePerHit.With(DamageSource.Spell).Value
                },
                {
                    TotalOverride, _stat.AverageDamage.WithHits.With(DamageSource.Secondary),
                    _stat.AverageDamagePerHit.With(DamageSource.Secondary).Value
                },
                // - average damage of a successful hit per source
                {
                    TotalOverride, _stat.AverageDamagePerHit,
                    _stat.DamageWithNonCrits().WithHits, _stat.DamageWithCrits().WithHits, _stat.EffectiveCritChance,
                    (nonCritDamage, critDamage, critChance)
                        => nonCritDamage.Value.Average * (1 - critChance.Value) +
                           critDamage.Value.Average * critChance.Value
                },
                // - crit/non-crit damage per source and type
                {
                    TotalOverride, dt => _stat.DamageWithNonCrits(dt).WithHits,
                    dt => _stat.Damage(dt).WithHits,
                    dt => _stat.EffectiveDamageMultiplierWithNonCrits(dt).WithHits,
                    dt => _stat.Damage(dt).WithHits.ChanceToDouble,
                    (_, damage, mult, chanceToDouble)
                        => damage.Value * mult.Value * (1 + chanceToDouble.Value.AsPercentage)
                },
                {
                    TotalOverride, dt => _stat.DamageWithCrits(dt).WithHits,
                    dt => _stat.Damage(dt).WithHits,
                    dt => _stat.EffectiveDamageMultiplierWithCrits(dt).WithHits,
                    dt => _stat.Damage(dt).WithHits.ChanceToDouble,
                    (_, damage, mult, chanceToDouble)
                        => damage.Value * mult.Value * (1 + chanceToDouble.Value.AsPercentage)
                },
                // - effective crit/non-crit damage multiplier per source and type
                {
                    BaseSet, dt => _stat.EffectiveDamageMultiplierWithNonCrits(dt).WithHits,
                    dt => _stat.EnemyResistanceAgainstNonCrits(dt),
                    dt => DamageTaken(dt).WithHits.For(Enemy),
                    dt => DamageMultiplier(dt).WithHits,
                    (_, resistance, damageTaken, damageMulti)
                        => DamageTakenMultiplier(resistance, damageTaken) * damageMulti.Value.AsPercentage
                },
                {
                    BaseSet, dt => _stat.EffectiveDamageMultiplierWithCrits(dt).WithHits,
                    dt => _stat.EnemyResistanceAgainstCrits(dt),
                    dt => DamageTaken(dt).WithHits.For(Enemy),
                    dt => DamageMultiplier(dt).WithHits,
                    _ => CriticalStrike.Multiplier.WithHits,
                    (_, resistance, damageTaken, damageMulti, critMulti)
                        => DamageTakenMultiplier(resistance, damageTaken) * damageMulti.Value.AsPercentage
                                                                          * critMulti.Value.AsPercentage
                },
                // - enemy resistance against crit/non-crit hits per source and type
                {
                    TotalOverride, dt => _stat.EnemyResistanceAgainstNonCrits(dt),
                    dt => DamageTypeBuilders.From(dt).IgnoreResistanceWithNonCrits,
                    dt => DamageTypeBuilders.From(dt).PenetrationWithNonCrits,
                    (dt, ignoreResistance, penetration)
                        => ValueFactory.If(ignoreResistance.IsSet).Then(0)
                            .Else(DamageTypeBuilders.From(dt).Resistance.For(Enemy).Value - penetration.Value)
                },
                {
                    TotalOverride, dt => _stat.EnemyResistanceAgainstCrits(dt),
                    dt => DamageTypeBuilders.From(dt).IgnoreResistanceWithCrits,
                    dt => DamageTypeBuilders.From(dt).PenetrationWithCrits,
                    (dt, ignoreResistance, penetration)
                        => ValueFactory.If(ignoreResistance.Value.Eq(1)).Then(0)
                            .Else(DamageTypeBuilders.From(dt).Resistance.For(Enemy).Value - penetration.Value)
                },

                // skill damage over time
                // - DPS = average damage = non-crit damage
                { TotalOverride, _stat.SkillDpsWithDoTs, _stat.AverageDamage.WithSkills(DamageSource.OverTime).Value },
                {
                    TotalOverride, _stat.AverageDamage.WithSkills(DamageSource.OverTime),
                    _stat.DamageWithNonCrits().WithSkills(DamageSource.OverTime).Value
                },
                // - damage per type
                {
                    TotalOverride, dt => _stat.DamageWithNonCrits(dt).WithSkills(DamageSource.OverTime),
                    dt => _stat.Damage(dt).WithSkills(DamageSource.OverTime).Value *
                          _stat.EffectiveDamageMultiplierWithNonCrits(dt).WithSkills(DamageSource.OverTime).Value
                },
                // - effective damage multiplier per type
                {
                    BaseSet, dt => _stat.EffectiveDamageMultiplierWithNonCrits(dt).WithSkills(DamageSource.OverTime),
                    dt => EnemyDamageTakenMultiplier(dt, DamageTaken(dt).WithSkills(DamageSource.OverTime))
                          * DamageMultiplier(dt).WithSkills(DamageSource.OverTime).Value.AsPercentage
                },

                // ailment damage (modifiers for EffectiveDamageMultiplierWith[Non]Crits() and Damage() are added below
                // this collection initializer)
                // - DPS
                {
                    TotalOverride, _stat.AilmentDps,
                    ailment => _stat.AverageAilmentDamage(ailment).Value *
                               _stat.AilmentEffectiveInstances(ailment).Value
                },
                // - average damage
                {
                    TotalOverride, ailment => _stat.AverageAilmentDamage(ailment),
                    ailment => CombineSource(_stat.AverageDamage.With(Ailment.From(ailment)),
                        CombineHandsForAverageAilmentDamage(ailment))
                },
                // - lifetime damage of one instance
                {
                    TotalOverride, _stat.AilmentInstanceLifetimeDamage,
                    ailment => _stat.AverageAilmentDamage(ailment).Value * Ailment.From(ailment).Duration.Value
                },
                // - average damage per source
                {
                    TotalOverride, ailment => _stat.AverageDamage.With(Ailment.From(ailment)),
                    ailment => _stat.DamageWithNonCrits().With(Ailment.From(ailment)),
                    ailment => _stat.DamageWithCrits().With(Ailment.From(ailment)),
                    _ => _stat.EffectiveCritChance,
                    ailment => Ailment.From(ailment).Chance,
                    ailment => _stat.AilmentChanceWithCrits(ailment),
                    AverageAilmentDamageFromCritAndNonCrit
                },
                // - crit/non-crit damage per source and type
                {
                    TotalOverride, (a, dt) => _stat.DamageWithNonCrits(dt).With(Ailment.From(a)),
                    (a, dt) => _stat.Damage(dt).With(Ailment.From(a)),
                    (a, dt) => _stat.EffectiveDamageMultiplierWithNonCrits(dt).With(Ailment.From(a)),
                    (damage, mult) => damage.Value * mult.Value
                },
                {
                    TotalOverride, (a, dt) => _stat.DamageWithCrits(dt).With(Ailment.From(a)),
                    (a, dt) => _stat.Damage(dt).With(Ailment.From(a)),
                    (a, dt) => _stat.EffectiveDamageMultiplierWithCrits(dt).With(Ailment.From(a)),
                    (damage, mult) => damage.Value * mult.Value
                },

                // speed
                { TotalOverride, Stat.CastRate, Stat.BaseCastTime, castTime => castTime.Value.Invert },
                { TotalOverride, _stat.CastRate, CombineSourceDefaultingToSpell(Stat.CastRate, CombineHandsByAverage) },
                { TotalOverride, _stat.CastTime, _stat.CastRate.Value.Invert },
                { PercentMore, Stat.MovementSpeed, ActionSpeedValueForPercentMore },
                {
                    PercentMore, Stat.CastRate, ActionSpeedValueForPercentMore,
                    Not(Or(With(Keyword.Totem), With(Keyword.Trap), With(Keyword.Mine)))
                },
                { PercentMore, Stat.Totem.Speed, ActionSpeedValueForPercentMore },
                { PercentMore, Stat.Trap.Speed, ActionSpeedValueForPercentMore },
                { PercentMore, Stat.Mine.Speed, ActionSpeedValueForPercentMore },
                // resistances/damage reduction
                { BaseSet, _stat.ResistanceAgainstHits(DamageType.Physical), Physical.Resistance.Value },
                {
                    BaseAdd, _stat.ResistanceAgainstHits(DamageType.Physical),
                    100 * Armour.Value /
                    (Armour.Value + 10 * Physical.Damage.WithSkills.With(AttackDamageHand.MainHand).For(Enemy).Value)
                },
                { BaseSet, _stat.ResistanceAgainstHits(DamageType.Physical).Maximum, 90 },
                { TotalOverride, _stat.ResistanceAgainstHits(DamageType.Lightning), Lightning.Resistance.Value },
                { TotalOverride, _stat.ResistanceAgainstHits(DamageType.Cold), Cold.Resistance.Value },
                { TotalOverride, _stat.ResistanceAgainstHits(DamageType.Fire), Fire.Resistance.Value },
                { TotalOverride, _stat.ResistanceAgainstHits(DamageType.Chaos), Chaos.Resistance.Value },
                // damage mitigation (1 - (1 - resistance / 100) * damage taken)
                {
                    TotalOverride, _stat.MitigationAgainstHits,
                    dt => 1 - DamageTakenMultiplier(_stat.ResistanceAgainstHits(dt),
                              DamageTaken(dt).WithSkills(DamageSource.Secondary))
                },
                {
                    TotalOverride, _stat.MitigationAgainstDoTs,
                    dt => 1 - DamageTakenMultiplier(DamageTypeBuilders.From(dt).Resistance,
                              DamageTaken(dt).WithSkills(DamageSource.OverTime))
                },
                // chance to hit/evade
                {
                    BaseSet, Evasion.Chance,
                    100 - ChanceToHitValue(Stat.Accuracy.With(AttackDamageHand.MainHand).For(Enemy), Evasion,
                        Buff.Blind.IsOn(Enemy))
                },
                {
                    BaseSet, Stat.ChanceToHit.With(AttackDamageHand.MainHand),
                    ChanceToHitValue(Stat.Accuracy.With(AttackDamageHand.MainHand), Evasion.For(Enemy),
                        Buff.Blind.IsOn(Self))
                },
                {
                    BaseSet, Stat.ChanceToHit.With(AttackDamageHand.OffHand),
                    ChanceToHitValue(Stat.Accuracy.With(AttackDamageHand.OffHand), Evasion.For(Enemy),
                        Buff.Blind.IsOn(Self))
                },
                // chance to avoid
                {
                    TotalOverride, _stat.ChanceToAvoidMeleeAttacks,
                    100 - 100 * (FailureProbability(Evasion.ChanceAgainstMeleeAttacks) *
                                 FailureProbability(Stat.Dodge.AttackChance) * FailureProbability(Block.AttackChance))
                },
                {
                    TotalOverride, _stat.ChanceToAvoidProjectileAttacks,
                    100 - 100 * (FailureProbability(Evasion.ChanceAgainstProjectileAttacks) *
                                 FailureProbability(Stat.Dodge.AttackChance) * FailureProbability(Block.AttackChance))
                },
                {
                    TotalOverride, _stat.ChanceToAvoidSpells,
                    100 - 100 * (FailureProbability(Stat.Dodge.SpellChance) * FailureProbability(Block.SpellChance))
                },
                // crit
                {
                    TotalOverride, _stat.EffectiveCritChance.With(AttackDamageHand.MainHand),
                    CalculateLuckyCriticalStrikeChance(CriticalStrike.Chance.With(AttackDamageHand.MainHand)) *
                    Stat.ChanceToHit.With(AttackDamageHand.MainHand).Value.AsPercentage
                },
                {
                    TotalOverride, _stat.EffectiveCritChance.With(AttackDamageHand.OffHand),
                    CalculateLuckyCriticalStrikeChance(CriticalStrike.Chance.With(AttackDamageHand.OffHand)) *
                    Stat.ChanceToHit.With(AttackDamageHand.OffHand).Value.AsPercentage
                },
                {
                    TotalOverride, _stat.EffectiveCritChance.With(DamageSource.Spell),
                    CalculateLuckyCriticalStrikeChance(CriticalStrike.Chance.With(DamageSource.Spell))
                },
                {
                    TotalOverride, _stat.EffectiveCritChance.With(DamageSource.Secondary),
                    CalculateLuckyCriticalStrikeChance(CriticalStrike.Chance.With(DamageSource.Secondary))
                },
                // pools
                {
                    BaseAdd, p => Stat.Pool.From(p).Regen,
                    p => _stat.RegenTargetPoolValue(p) * Stat.Pool.From(p).Regen.Percent.Value.AsPercentage
                },
                { TotalOverride, _stat.EffectiveRegen, p => p.Regen.Value * p.RecoveryRate.Value },
                { TotalOverride, _stat.EffectiveRecharge, p => p.Recharge.Value * p.RecoveryRate.Value },
                { TotalOverride, _stat.RechargeStartDelay, p => 2 / p.Recharge.Start.Value },
                { TotalOverride, _stat.EffectiveLeechRate, p => p.Leech.Rate.Value * p.RecoveryRate.Value },
                {
                    TotalOverride, _stat.AbsoluteLeechRate,
                    p => _stat.LeechTargetPoolValue(p) * _stat.EffectiveLeechRate(p).Value.AsPercentage
                },
                {
                    TotalOverride, _stat.AbsoluteLeechRateLimit,
                    p => _stat.LeechTargetPoolValue(p) * Stat.Pool.From(p).Leech.RateLimit.Value.AsPercentage
                },
                {
                    TotalOverride, _stat.TimeToReachLeechRateLimit,
                    p => p.Leech.RateLimit.Value / p.Leech.Rate.Value /
                         (_stat.CastRate.Value * _stat.SkillNumberOfHitsPerCast.Value)
                },
                // flasks
                { PercentMore, Flask.LifeRecovery, Flask.Effect.Value * 100 },
                { PercentMore, Flask.ManaRecovery, Flask.Effect.Value * 100 },
                { PercentMore, Flask.LifeRecovery, Flask.RecoverySpeed.Value * 100 },
                { PercentMore, Flask.ManaRecovery, Flask.RecoverySpeed.Value * 100 },
                { PercentMore, Flask.Duration, (100 / Flask.RecoverySpeed.Value) - 100 },
                // ailments
                {
                    TotalOverride, _stat.AilmentDealtDamageType(Common.Builders.Effects.Ailment.Ignite),
                    (int) DamageType.Fire
                },
                {
                    TotalOverride, _stat.AilmentDealtDamageType(Common.Builders.Effects.Ailment.Bleed),
                    (int) DamageType.Physical
                },
                {
                    TotalOverride, _stat.AilmentDealtDamageType(Common.Builders.Effects.Ailment.Poison),
                    (int) DamageType.Chaos
                },
                {
                    TotalOverride, _stat.AilmentCombinedEffectiveChance,
                    ailment => CombineSource(_stat.AilmentEffectiveChance(ailment), CombineHandsByAverage)
                },
                {
                    TotalOverride, _stat.AilmentEffectiveChance,
                    ailment => Ailment.From(ailment).Chance,
                    ailment => _stat.AilmentChanceWithCrits(ailment),
                    _ => _stat.EffectiveCritChance,
                    (ailment, ailmentChance, ailmentChanceWithCrits, critChance)
                        => (ailmentChance.Value.AsPercentage * (1 - critChance.Value) +
                            ailmentChanceWithCrits.Value.AsPercentage * critChance.Value) *
                           (1 - Ailment.From(ailment).Avoidance.For(Enemy).Value.AsPercentage)
                },
                {
                    TotalOverride, _stat.AilmentChanceWithCrits,
                    ailment => Ailment.From(ailment).Chance,
                    (ailment, ailmentChance) => ValueFactory
                        .If(Ailment.From(ailment).CriticalStrikesAlwaysInflict.IsSet).Then(100)
                        .Else(ailmentChance.Value)
                },
                { TotalOverride, Ailment.Chill.On(Self), 1, Ailment.Freeze.IsOn(Self) },
                { PercentIncrease, Ailment.Shock.AddStat(Damage.Taken), _stat.IncreasedDamageTakenFromShocks.Value },
                { BaseSet, _stat.IncreasedDamageTakenFromShocks, 20 },
                { TotalOverride, _stat.IncreasedDamageTakenFromShocks.Maximum, 50 },
                { TotalOverride, _stat.IncreasedDamageTakenFromShocks.Minimum, 1 },
                {
                    PercentReduce, Ailment.Chill.AddStat(Stat.ActionSpeed),
                    _stat.ReducedActionSpeedFromChill.Value
                },
                { BaseSet, _stat.ReducedActionSpeedFromChill, 10 },
                { TotalOverride, _stat.ReducedActionSpeedFromChill.Maximum, 30 },
                { TotalOverride, _stat.ReducedActionSpeedFromChill.Minimum, 1 },
                // - AilmentEffectiveInstances
                {
                    TotalOverride, _stat.AilmentEffectiveInstances(Common.Builders.Effects.Ailment.Ignite),
                    Ailment.Ignite.InstancesOn(Enemy).Maximum.Value
                },
                {
                    TotalOverride, _stat.AilmentEffectiveInstances(Common.Builders.Effects.Ailment.Bleed),
                    Ailment.Bleed.InstancesOn(Enemy).Maximum.Value
                },
                {
                    TotalOverride, _stat.AilmentEffectiveInstances(Common.Builders.Effects.Ailment.Poison),
                    Ailment.Poison.Duration.Value * _stat.CastRate.Value * _stat.SkillNumberOfHitsPerCast.Value *
                    CombineSource(_stat.AilmentEffectiveChance(Common.Builders.Effects.Ailment.Poison),
                        s => CombineByWeightedAverage(
                            s.With(AttackDamageHand.MainHand).Value *
                            Stat.ChanceToHit.With(AttackDamageHand.MainHand).Value.AsPercentage,
                            SkillUsesHandAsMultiplier(AttackDamageHand.MainHand),
                            s.With(AttackDamageHand.OffHand).Value *
                            Stat.ChanceToHit.With(AttackDamageHand.OffHand).Value.AsPercentage,
                            SkillUsesHandAsMultiplier(AttackDamageHand.OffHand)))
                },
                // buffs
                {
                    PercentMore,
                    _stat.EffectiveDamageMultiplierWithNonCrits(DamageType.Physical).WithSkills,
                    Buff.Impale.Chance,
                    chance => ValueFactory.If(Buff.Impale.IsOn(Self, Enemy))
                        .Then(10 * Buff.Impale.EffectOn(Enemy).Value * Buff.Impale.StackCount.For(Enemy).Value
                              * chance.WithCondition(Hit.On).Value.AsPercentage)
                        .Else(0)
                },
                {
                    PercentMore,
                    _stat.EffectiveDamageMultiplierWithCrits(DamageType.Physical).WithSkills,
                    Buff.Impale.Chance,
                    chance => ValueFactory.If(Buff.Impale.IsOn(Self, Enemy))
                        .Then(10 * Buff.Impale.EffectOn(Enemy).Value * Buff.Impale.StackCount.For(Enemy).Value
                              * chance.WithCondition(Hit.On).Value.AsPercentage)
                        .Else(0)
                },
                { TotalOverride, Buff.Impale.Chance.WithCondition(Hit.On).Maximum, 100 },
                // stun (see https://pathofexile.gamepedia.com/Stun)
                { PercentLess, Effect.Stun.Duration, Effect.Stun.Recovery.For(Enemy).Value * 100 },
                {
                    TotalOverride, _stat.EffectiveStunThreshold,
                    Effect.Stun.Threshold, EffectiveStunThresholdValue
                },
                {
                    BaseSet, Effect.Stun.Chance,
                    _stat.AverageDamage.WithHits, _stat.EffectiveStunThreshold,
                    (damage, threshold)
                        => 200 * damage.Value / (Life.For(Enemy).ValueFor(NodeType.Subtotal) * threshold.Value)
                },
                {
                    TotalOverride, _stat.StunAvoidanceWhileCasting,
                    1 -
                    (1 - Effect.Stun.Avoidance.Value) * (1 - Effect.Stun.ChanceToAvoidInterruptionWhileCasting.Value)
                },
                // flags
                {
                    PercentMore, Damage.WithSkills(DamageSource.Attack).With(Keyword.Projectile),
                    30 * ValueFactory.LinearScale(Projectile.TravelDistance, (35, 0), (70, 1)),
                    Flag.FarShot.IsSet
                },
                // other
                { PercentMore, Stat.Radius, Stat.AreaOfEffect.Value.Select(Math.Sqrt, v => $"Sqrt({v})") },
                { PercentMore, Stat.Cooldown, 100 - 100 * Stat.CooldownRecoverySpeed.Value.Invert },
                { BaseSet, _stat.SkillNumberOfHitsPerCast, 1 },
                { BaseSet, Stat.MainSkillPart, 0 },
            };

        private static ValueBuilder AverageAilmentDamageFromCritAndNonCrit(
            IStatBuilder nonCritDamage, IStatBuilder critDamage, IStatBuilder critChance,
            IStatBuilder nonCritAilmentChance, IStatBuilder critAilmentChance)
        {
            return CombineByWeightedAverage(
                nonCritDamage.Value.Average, (1 - critChance.Value) * nonCritAilmentChance.Value.AsPercentage,
                critDamage.Value.Average, critChance.Value * critAilmentChance.Value.AsPercentage);
        }

        private ValueBuilder EnemyDamageTakenMultiplier(DamageType resistanceType, IStatBuilder damageTaken)
            => DamageTakenMultiplier(DamageTypeBuilders.From(resistanceType).Resistance.For(Enemy),
                damageTaken.For(Enemy));

        private static ValueBuilder DamageTakenMultiplier(IStatBuilder resistance, IStatBuilder damageTaken)
            => (1 - resistance.Value.AsPercentage) * damageTaken.Value;

        private IDamageRelatedStatBuilder DamageTaken(DamageType damageType)
            => DamageTypeBuilders.From(damageType).Damage.Taken;

        private IDamageRelatedStatBuilder DamageMultiplier(DamageType damageType)
            => DamageTypeBuilders.From(damageType).DamageMultiplier;

        private ValueBuilder ActionSpeedValueForPercentMore => (Stat.ActionSpeed.Value - 1) * 100;

        private ValueBuilder ChanceToHitValue(
            IStatBuilder accuracyStat, IStatBuilder evasionStat, IConditionBuilder isBlinded)
        {
            var accuracy = accuracyStat.Value;
            var evasion = evasionStat.Value;
            var blindMultiplier = ValueFactory.If(isBlinded).Then(0.5).Else(1);
            return 100 * blindMultiplier * accuracy /
                   (accuracy + (evasion / 4).Select(d => Math.Pow(d, 0.8), v => $"{v}^0.8"));
        }

        private static ValueBuilder FailureProbability(IStatBuilder percentageChanceStat)
            => 1 - percentageChanceStat.Value.AsPercentage;

        private IValueBuilder EffectiveStunThresholdValue(IStatBuilder stunThresholdStat)
        {
            // If stun threshold is less than 25%, it is scaled up.
            // See https://pathofexile.gamepedia.com/Stun#Stun_threshold
            var stunThreshold = stunThresholdStat.Value;
            return ValueFactory
                .If(stunThreshold >= 0.25).Then(stunThreshold)
                .Else(0.25 - 0.25 * (0.25 - stunThreshold) / (0.5 - stunThreshold));
        }

        private IReadOnlyList<IIntermediateModifier> CollectionToList(DataDrivenMechanicCollection collection)
        {
            AddDamageWithNonCritsModifiers(collection);
            AddDamageWithCritsModifiers(collection);
            AddAilmentEffectiveDamageMultiplierModifiers(collection);
            AddAilmentSourceDamageTypeModifiers(collection);
            return collection.ToList();
        }

        private void AddAilmentEffectiveDamageMultiplierModifiers(DataDrivenMechanicCollection collection)
        {
            var ailmentsAndTypes = new[]
            {
                (Common.Builders.Effects.Ailment.Ignite, DamageType.Fire),
                (Common.Builders.Effects.Ailment.Bleed, DamageType.Physical),
                (Common.Builders.Effects.Ailment.Poison, DamageType.Chaos),
            };
            foreach (var (ailment, damageType) in ailmentsAndTypes)
            {
                AddEffectiveDamageMultiplierWithNonCritsModifiers(collection, ailment, damageType);
                AddEffectiveDamageMultiplierWithCritsModifiers(collection, ailment, damageType);
            }
        }

        private void AddEffectiveDamageMultiplierWithNonCritsModifiers(
            DataDrivenMechanicCollection collection, Ailment ailment, DamageType damageType)
        {
            var ailmentBuilder = Ailment.From(ailment);
            collection.Add(BaseSet, dt => _stat.EffectiveDamageMultiplierWithNonCrits(dt).With(ailmentBuilder),
                _ => DamageTaken(damageType).With(ailmentBuilder),
                _ => DamageMultiplier(damageType).With(ailmentBuilder),
                (_, damageTaken, damageMulti)
                    => EnemyDamageTakenMultiplier(damageType, damageTaken) * damageMulti.Value.AsPercentage);
        }

        private void AddEffectiveDamageMultiplierWithCritsModifiers(
            DataDrivenMechanicCollection collection, Ailment ailment, DamageType damageType)
        {
            var ailmentBuilder = Ailment.From(ailment);
            collection.Add(BaseSet, dt => _stat.EffectiveDamageMultiplierWithCrits(dt).With(ailmentBuilder),
                _ => DamageTaken(damageType).With(ailmentBuilder),
                _ => CriticalStrike.Multiplier.With(ailmentBuilder),
                _ => DamageMultiplier(damageType).With(ailmentBuilder),
                (_, damageTaken, damageMulti, critMulti)
                    => EnemyDamageTakenMultiplier(damageType, damageTaken) * damageMulti.Value.AsPercentage
                                                                           * critMulti.Value.AsPercentage);
        }

        private void AddAilmentSourceDamageTypeModifiers(GivenStatCollection collection)
        {
            foreach (var ailment in Enums.GetValues<Ailment>())
            {
                var ailmentBuilder = Ailment.From(ailment);
                foreach (var damageType in Enums.GetValues<DamageType>())
                {
                    collection.Add(TotalOverride, _stat.Damage(damageType).With(ailmentBuilder), 0,
                        ailmentBuilder.Source(DamageTypeBuilders.From(damageType)).IsSet.Not);
                }
            }
        }

        private void AddDamageWithNonCritsModifiers(GivenStatCollection collection)
        {
            AddDamageWithModifiers(collection, _stat.DamageWithNonCrits(), _stat.DamageWithNonCrits);
        }

        private void AddDamageWithCritsModifiers(GivenStatCollection collection)
        {
            AddDamageWithModifiers(collection, _stat.DamageWithCrits(), _stat.DamageWithCrits);
        }

        private void AddDamageWithModifiers(GivenStatCollection collection,
            IDamageRelatedStatBuilder damage, Func<DamageType, IDamageRelatedStatBuilder> damageForType)
        {
            var form = BaseAdd;
            foreach (var type in Enums.GetValues<DamageType>().Except(DamageType.RandomElement))
            {
                var forType = damageForType(type);
                AddForSkillAndAilments(collection, form, damage.With(AttackDamageHand.MainHand),
                    forType.With(AttackDamageHand.MainHand));
                AddForSkillAndAilments(collection, form, damage.With(AttackDamageHand.OffHand),
                    forType.With(AttackDamageHand.OffHand));
                AddForSkillAndAilments(collection, form, damage.With(DamageSource.Spell),
                    forType.With(DamageSource.Spell));
                AddForSkillAndAilments(collection, form, damage.With(DamageSource.Secondary),
                    forType.With(DamageSource.Secondary));
                collection.Add(form, damage.WithSkills(DamageSource.OverTime),
                    forType.WithSkills(DamageSource.OverTime).Value);
            }
        }

        private void AddForSkillAndAilments(GivenStatCollection collection,
            IFormBuilder form, IDamageRelatedStatBuilder stat, IDamageRelatedStatBuilder valueStat)
        {
            collection.Add(form, stat.WithSkills, valueStat.WithSkills.Value);
            foreach (var ailment in Enums.GetValues<Ailment>())
            {
                var ailmentBuilder = Ailment.From(ailment);
                collection.Add(form, stat.With(ailmentBuilder), valueStat.With(ailmentBuilder).Value);
            }
        }

        private ValueBuilder CalculateLuckyCriticalStrikeChance(IStatBuilder critChance)
        {
            var critValue = critChance.Value.AsPercentage;
            return ValueFactory.If(Flag.CriticalStrikeChanceIsLucky.IsSet)
                .Then(1 - (1 - critValue) * (1 - critValue))
                .Else(critValue);
        }

        private ValueBuilder CombineSource(
            IDamageRelatedStatBuilder statToCombine, Func<IDamageRelatedStatBuilder, IValueBuilder> handCombiner)
            => ValueFactory.If(_stat.SkillHitDamageSource.Value.Eq((int) DamageSource.Attack))
                .Then(handCombiner(statToCombine))
                .ElseIf(_stat.SkillHitDamageSource.Value.Eq((int) DamageSource.Spell))
                .Then(statToCombine.With(DamageSource.Spell).Value)
                .ElseIf(_stat.SkillHitDamageSource.Value.Eq((int) DamageSource.Secondary))
                .Then(statToCombine.With(DamageSource.Secondary).Value)
                .Else(0);

        private ValueBuilder CombineSourceDefaultingToSpell(
            IDamageRelatedStatBuilder statToCombine, Func<IDamageRelatedStatBuilder, IValueBuilder> handCombiner)
            => ValueFactory.If(_stat.SkillHitDamageSource.Value.Eq((int) DamageSource.Attack))
                .Then(handCombiner(statToCombine))
                .ElseIf(_stat.SkillHitDamageSource.Value.Eq((int) DamageSource.Secondary))
                .Then(statToCombine.With(DamageSource.Secondary).Value)
                .Else(statToCombine.With(DamageSource.Spell).Value);

        private ValueBuilder CombineHandsByAverage(IDamageRelatedStatBuilder statToCombine)
        {
            var mhWeight = SkillUsesHandAsMultiplier(AttackDamageHand.MainHand);
            var ohWeight = SkillUsesHandAsMultiplier(AttackDamageHand.OffHand);
            return CombineByWeightedAverage(
                statToCombine.With(AttackDamageHand.MainHand).Value, mhWeight,
                statToCombine.With(AttackDamageHand.OffHand).Value, ohWeight);
        }

        private Func<IDamageRelatedStatBuilder, ValueBuilder> CombineHandsForAverageAilmentDamage(
            Ailment ailment)
        {
            var ailmentChance = _stat.AilmentEffectiveChance(ailment);
            var mhWeight = Stat.ChanceToHit.With(AttackDamageHand.MainHand).Value.AsPercentage *
                           ailmentChance.With(AttackDamageHand.MainHand).Value *
                           SkillUsesHandAsMultiplier(AttackDamageHand.MainHand);
            var ohWeight = Stat.ChanceToHit.With(AttackDamageHand.OffHand).Value.AsPercentage *
                           ailmentChance.With(AttackDamageHand.OffHand).Value *
                           SkillUsesHandAsMultiplier(AttackDamageHand.OffHand);
            return statToCombine => CombineByWeightedAverage(
                statToCombine.With(AttackDamageHand.MainHand).Value, mhWeight,
                statToCombine.With(AttackDamageHand.OffHand).Value, ohWeight);
        }

        private ValueBuilder CombineHandsForHitDamage(IDamageRelatedStatBuilder statToCombine)
        {
            var usesMh = SkillUsesHandAsMultiplier(AttackDamageHand.MainHand);
            var usesOh = SkillUsesHandAsMultiplier(AttackDamageHand.OffHand);
            var sumOfHands = statToCombine.With(AttackDamageHand.MainHand).Value * usesMh +
                             statToCombine.With(AttackDamageHand.MainHand).Value * usesOh;
            return ValueFactory.If(_stat.SkillDoubleHitsWhenDualWielding.IsSet)
                .Then(sumOfHands)
                .Else(sumOfHands / (usesMh + usesOh));
        }

        private static ValueBuilder CombineByWeightedAverage(
            ValueBuilder left, ValueBuilder leftWeight, ValueBuilder right, ValueBuilder rightWeight)
            => (left * leftWeight + right * rightWeight) / (leftWeight + rightWeight);

        private ValueBuilder SkillUsesHandAsMultiplier(AttackDamageHand hand)
            => ValueFactory.If(_stat.SkillUsesHand(hand).IsSet).Then(1).Else(0);
    }
}