using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    internal enum AntonAwakeningAdmissionStatus
    {
        NotApplicable = 0,
        Allowed = 1,
        MissingPrerequisites = 2,
    }

    internal sealed class AntonAwakeningAdmissionDecision
    {
        internal AntonAwakeningAdmissionDecision(
            AntonAwakeningAdmissionStatus status,
            IReadOnlyList<int> missingDungeonIds = null)
        {
            Status = status;
            MissingDungeonIds = missingDungeonIds ?? Array.Empty<int>();
        }

        internal AntonAwakeningAdmissionStatus Status { get; }
        internal IReadOnlyList<int> MissingDungeonIds { get; }
        internal bool Allowed =>
            Status == AntonAwakeningAdmissionStatus.NotApplicable
            || Status == AntonAwakeningAdmissionStatus.Allowed;
    }

    internal sealed class AntonAwakeningDailyProgressService
    {
        internal const int ConfigKey = 41;
        internal const int FirstDungeonId = 243;
        internal const int FinalDungeonId = 247;
        internal const int RequiredRouteMask = 0x0F;

        internal static bool IsTrackedDungeon(int dungeonId)
            => dungeonId >= FirstDungeonId && dungeonId <= FinalDungeonId;

        private static readonly int[] PrerequisiteDungeonIds =
            { 243, 244, 245, 246 };

        private readonly AntonAwakeningDailyProgressRepository _repository;
        private readonly Func<DateTime> _utcNow;

        internal AntonAwakeningDailyProgressService(
            AntonAwakeningDailyProgressRepository repository,
            Func<DateTime> utcNow = null)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal bool TryRestore(
            int characterId,
            int configKey,
            out AntonNormalSyncState state)
        {
            state = null;
            if (characterId <= 0
                || configKey != ConfigKey
                || !AntonNormalConquest.TryGetSequenceByKey(
                    ConfigKey,
                    out var sequence))
            {
                return false;
            }

            var permissions = _repository.EnsureCurrentDayAndLoad(
                characterId,
                _utcNow());
            state = BuildState(sequence, permissions);
            return true;
        }

        internal bool TryApplyClear(
            int characterId,
            int dungeonId,
            out AntonNormalClearApplicationResult result)
        {
            result = null;
            if (characterId <= 0
                || !IsTrackedDungeon(dungeonId)
                || !AntonNormalConquest.TryGetSequenceByKey(
                    ConfigKey,
                    out var sequence)
                || !AntonNormalConquest.TryResolveClearPlan(
                    sequence,
                    dungeonId,
                    out var plan))
            {
                return false;
            }

            var updates = new List<DungeonPermissionEntrySnapshot>();
            AddPermissionUpdate(
                updates,
                dungeonId,
                sequence.Difficulty,
                completed: true);
            AddPermissionUpdate(
                updates,
                plan.NextDungeonId,
                sequence.Difficulty,
                completed: false);
            AddPreviewPermissionUpdate(
                updates,
                plan.PreviewDungeonId,
                sequence.Difficulty);

            var permissions = _repository.RecordClearAndLoad(
                characterId,
                updates,
                _utcNow(),
                out var changes);
            var state = BuildState(sequence, permissions);
            result = new AntonNormalClearApplicationResult(state, changes);
            FileLogger.Log(
                $"[AntonAwakeningProgress] clear persisted: " +
                $"cid={characterId} dungeon={dungeonId} " +
                $"key={ConfigKey} progress={state.ProgressIndex} " +
                $"routeMask=0x{state.RouteMask:X2}");
            return true;
        }

        internal AntonAwakeningAdmissionDecision EvaluateAdmission(
            int characterId,
            int dungeonId)
        {
            if (dungeonId != FinalDungeonId)
            {
                return new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.NotApplicable);
            }
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (!TryRestore(characterId, ConfigKey, out var state))
            {
                throw new InvalidOperationException(
                    "Anton Awakening key 41 is unavailable.");
            }

            var missing = new List<int>();
            for (var index = 0; index < PrerequisiteDungeonIds.Length; index++)
            {
                if ((state.RouteMask & (1 << index)) == 0)
                    missing.Add(PrerequisiteDungeonIds[index]);
            }
            return missing.Count == 0
                ? new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.Allowed)
                : new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.MissingPrerequisites,
                    missing);
        }

        internal void EnsureCurrentDay(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            _repository.EnsureCurrentDayAndLoad(characterId, _utcNow());
        }

        private static AntonNormalSyncState BuildState(
            AntonNormalSequence sequence,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions)
        {
            var clearStates = (permissions ?? Array.Empty<DungeonPermissionEntrySnapshot>())
                .GroupBy(entry => (int)entry.DungeonId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(entry => entry.ClearState));
            var routeMask = 0;
            var progressIndex = 0;
            for (var index = 0; index < sequence.DungeonIds.Count; index++)
            {
                var dungeonId = sequence.DungeonIds[index];
                if (!AntonNormalConquest.TryResolveCompletedState(
                        dungeonId,
                        sequence.Difficulty,
                        out var completedState)
                    || !clearStates.TryGetValue(dungeonId, out var persistedState)
                    || persistedState < completedState)
                {
                    continue;
                }

                progressIndex = Math.Max(progressIndex, index + 1);
                if (index < PrerequisiteDungeonIds.Length)
                    routeMask |= 1 << index;
            }

            var entries = clearStates
                .OrderBy(pair => sequence.IndexOf(pair.Key))
                .Where(pair => sequence.IndexOf(pair.Key) >= 0 && pair.Value > 0)
                .Select(pair => new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)pair.Key,
                    ClearState = pair.Value,
                })
                .ToList();
            return new AntonNormalSyncState(
                sequence,
                (byte)Math.Min(progressIndex, byte.MaxValue),
                entries,
                routeMask);
        }

        private static void AddPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty,
            bool completed)
        {
            if (dungeonId <= 0)
                return;
            var resolved = completed
                ? AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    difficulty,
                    out var clearState)
                : AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out clearState);
            if (!resolved)
                return;
            updates.Add(new DungeonPermissionEntrySnapshot
            {
                DungeonId = (ushort)dungeonId,
                ClearState = clearState,
            });
        }

        private static void AddPreviewPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty)
        {
            if (dungeonId <= 0
                || !AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out var unlockedState))
            {
                return;
            }
            updates.Add(new DungeonPermissionEntrySnapshot
            {
                DungeonId = (ushort)dungeonId,
                ClearState = (byte)Math.Max(1, unlockedState - 1),
            });
        }
    }
}
