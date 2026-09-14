using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class SequentialDungeonDefinitionCatalogSelfTest
    {
        private const string OverlappingConfig = @"
[sequential dungeon]
28
[dungeon index check]
225 243 244 245 246 247
[/dungeon index check]
[monster index check]
56639
[/monster index check]
[/sequential dungeon]
[sequential dungeon]
41
[dungeon index check]
243 244 245 246 247
[/dungeon index check]
[monster index check]
56675
56678
[/monster index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
247
[/entrance except dungeon]
[rewardable dungeon index]
247
[/rewardable dungeon index]
[clear reward item]
915 10157831 0
10 10157832 1
[/clear reward item]
[always visible dungeon]
247
[/always visible dungeon]
[/sequential dungeon]";

        public static int Run()
        {
            Console.WriteLine(
                "=== SEQUENTIAL_DUNGEON_DEFINITION_CATALOG selftest ===");
            var failures = 0;

            VerifyEtcProjectionAndIndexes(ref failures);
            VerifyImmutableModelValidation(ref failures);
            VerifyAmbiguousCapabilitiesFailClosed(ref failures);
            VerifyMalformedDefinitionsAreNotPublished(ref failures);
            VerifyCurrentPvfAndInstanceFreeze(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "SEQUENTIAL_DUNGEON_DEFINITION_CATALOG selftest passed."
                    : "SEQUENTIAL_DUNGEON_DEFINITION_CATALOG selftest failed: "
                        + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyImmutableModelValidation(ref int failures)
        {
            var sourceDungeonIds = new List<int> { 10, 11 };
            var definition = new SequentialDungeonDefinition(
                1,
                0,
                sourceDungeonIds,
                Array.Empty<int>(),
                showIndividualProcess: false,
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<SequentialDungeonRewardGroup>());
            sourceDungeonIds[0] = 99;
            Check(
                "definition copies caller-owned collections",
                definition.DungeonIds.SequenceEqual(new[] { 10, 11 }),
                ref failures);
            Check(
                "definition rejects a non-positive group key",
                ThrowsArgument(() => new SequentialDungeonDefinition(
                    0,
                    0,
                    new[] { 10 },
                    Array.Empty<int>(),
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<SequentialDungeonRewardGroup>())),
                ref failures);
            Check(
                "definition rejects duplicate dungeon identifiers",
                ThrowsArgument(() => new SequentialDungeonDefinition(
                    1,
                    0,
                    new[] { 10, 10 },
                    Array.Empty<int>(),
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<SequentialDungeonRewardGroup>())),
                ref failures);
            Check(
                "definition rejects route masks wider than 31 prerequisites",
                ThrowsArgument(() => new SequentialDungeonDefinition(
                    1,
                    0,
                    Enumerable.Range(1, 32),
                    Array.Empty<int>(),
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<SequentialDungeonRewardGroup>())),
                ref failures);
            Check(
                "reward group rejects invalid values",
                ThrowsArgument(() =>
                    new SequentialDungeonRewardGroup(0, 7001, 0))
                && ThrowsArgument(() =>
                    new SequentialDungeonRewardGroup(1, 0, 0))
                && ThrowsArgument(() =>
                    new SequentialDungeonRewardGroup(1, 7001, -1)),
                ref failures);
        }

        private static void VerifyEtcProjectionAndIndexes(ref int failures)
        {
            IReadOnlyList<int> resolvedDungeonIds = null;
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                OverlappingConfig,
                dungeonIds =>
                {
                    resolvedDungeonIds = dungeonIds.ToArray();
                    return (byte)2;
                });
            var foundByKey = catalog.TryGetByGroupKey(41, out var byKey);

            Check(
                "key lookup keeps ETC order",
                foundByKey
                && byKey.DungeonIds.SequenceEqual(
                    new[] { 243, 244, 245, 246, 247 })
                && byKey.PrerequisiteDungeonIds.SequenceEqual(
                    new[] { 243, 244, 245, 246 }),
                ref failures);
            Check(
                "difficulty resolver receives ETC dungeon order",
                resolvedDungeonIds != null
                && resolvedDungeonIds.SequenceEqual(
                    new[] { 243, 244, 245, 246, 247 })
                && byKey != null
                && byKey.Difficulty == 2,
                ref failures);
            Check(
                "show-individual definition resolves overlap",
                catalog.TryResolvePrimaryByDungeonId(243, out var primary)
                && primary.GroupKey == 41,
                ref failures);
            Check(
                "entrance and reward capabilities resolve uniquely",
                catalog.TryResolveEntranceByDungeonId(247, out var entrance)
                && byKey != null
                && ReferenceEquals(entrance, byKey)
                && catalog.TryResolveRewardableByDungeonId(
                    247,
                    out var rewardable)
                && ReferenceEquals(rewardable, byKey),
                ref failures);
            Check(
                "monster membership uses ETC union",
                catalog.ContainsConfiguredMonster(243, 56639)
                && catalog.ContainsConfiguredMonster(243, 56675)
                && catalog.ContainsConfiguredMonster(243, 56678)
                && !catalog.ContainsConfiguredMonster(243, 99999),
                ref failures);
            Check(
                "reward tuple preserves group and card state",
                byKey != null
                && byKey.ClearRewardGroups.Count == 2
                && byKey.ClearRewardGroups[0].Weight == 915
                && byKey.ClearRewardGroups[0].RewardGroupItemId == 10157831
                && byKey.ClearRewardGroups[0].CardState == 0
                && byKey.ClearRewardGroups[1].Weight == 10
                && byKey.ClearRewardGroups[1].RewardGroupItemId == 10157832
                && byKey.ClearRewardGroups[1].CardState == 1,
                ref failures);
            Check(
                "definition collections are read-only snapshots",
                byKey != null
                && byKey.DungeonIds is IList<int> dungeonIds
                && dungeonIds.IsReadOnly
                && byKey.PrerequisiteDungeonIds is IList<int> prerequisites
                && prerequisites.IsReadOnly
                && byKey.ClearRewardGroups
                    is IList<SequentialDungeonRewardGroup> rewards
                && rewards.IsReadOnly,
                ref failures);
        }

        private static void VerifyAmbiguousCapabilitiesFailClosed(
            ref int failures)
        {
            const string ambiguousConfig = @"
[sequential dungeon]
80
[dungeon index check]
300 301
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
301
[/entrance except dungeon]
[rewardable dungeon index]
301
[/rewardable dungeon index]
[/sequential dungeon]
[sequential dungeon]
81
[dungeon index check]
300 301
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
301
[/entrance except dungeon]
[rewardable dungeon index]
301
[/rewardable dungeon index]
[/sequential dungeon]";
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                ambiguousConfig,
                _ => (byte)1);

            Check(
                "ambiguous primary capability fails closed",
                !catalog.TryResolvePrimaryByDungeonId(300, out _),
                ref failures);
            Check(
                "ambiguous entrance capability fails closed",
                !catalog.TryResolveEntranceByDungeonId(301, out _),
                ref failures);
            Check(
                "ambiguous reward capability fails closed",
                !catalog.TryResolveRewardableByDungeonId(301, out _),
                ref failures);
        }

        private static void VerifyMalformedDefinitionsAreNotPublished(
            ref int failures)
        {
            const string malformedConfig = @"
[sequential dungeon]
91
[dungeon index check]
400 400
[/dungeon index check]
[/sequential dungeon]
[sequential dungeon]
92
[dungeon index check]
401 402
[/dungeon index check]
[clear reward item]
1 7001 -1
[/clear reward item]
[/sequential dungeon]
[sequential dungeon]
93
[dungeon index check]
403 404
[/dungeon index check]
[/sequential dungeon]";
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                malformedConfig,
                _ => (byte)1);

            Check(
                "malformed groups are omitted without hiding valid groups",
                !catalog.TryGetByGroupKey(91, out _)
                && !catalog.TryGetByGroupKey(92, out _)
                && catalog.TryGetByGroupKey(93, out _)
                && catalog.Definitions.Count == 1,
                ref failures);
        }

        private static void VerifyCurrentPvfAndInstanceFreeze(ref int failures)
        {
            var foundCurrent = SequentialDungeonDefinitionCatalog.Current
                .TryGetByGroupKey(41, out var current);
            Check(
                "current PVF publishes key 41",
                foundCurrent
                && current.RewardableDungeonIds.Contains(247)
                && current.MonsterIds.Count == 19
                && current.ClearRewardGroups.Count == 4,
                ref failures);
            Check(
                "Anton conquest consumes the primary ETC definition",
                AntonNormalConquest.TryGetSequence(243, out var sequence)
                && current != null
                && sequence.ConfigKey == current.GroupKey
                && sequence.DungeonIds.SequenceEqual(current.DungeonIds),
                ref failures);

            var instance = new DungeonInstance(247, 0);
            Check(
                "dungeon instance freezes primary sequential definition",
                ReferenceEquals(instance.SequentialDefinition, current),
                ref failures);
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool ThrowsArgument(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}
