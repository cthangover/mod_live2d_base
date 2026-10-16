using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Represents one enqueued motion in the playback queue.
    /// Holds timing data (start time, end time, fade-in start time),
    /// lifecycle flags (available, started, finished, auto-delete), and a
    /// reference to the motion itself.
    ///
    /// Created by <see cref="CubismMotionQueueManager.StartMotion"/>
    /// and consumed frame-by-frame in <see cref="CubismMotionQueueManager.DoUpdateMotion"/>.
    /// </summary>
    public class CubismMotionQueueEntry
    {
        /// <summary>Absolute user time when motion playback begins. -1 until the first update.</summary>
        public float StartTime = -1f;

        /// <summary>
        /// Absolute user time when playback ends. -1 for infinite (looping) motions.
        /// </summary>
        public float EndTime = -1f;

        /// <summary>Absolute user time when fade-in begins (may differ from StartTime for offset motions).</summary>
        public float FadeInStartTime;

        /// <summary>Whether this entry should be processed during updates.</summary>
        public bool IsAvailable = true;

        /// <summary>Flag set when playback completes. Checked by the queue manager during cleanup.</summary>
        public bool IsFinished;

        /// <summary>Flag set on the first update — порт CubismMotionQueueEntry::_started.</summary>
        public bool IsStarted;

        /// <summary>Время последнего кадра (состояние), порт CubismMotionQueueEntry::SetState.</summary>
        public float StateTime;

        /// <summary>Вес последнего кадра (состояние), порт CubismMotionQueueEntry::SetState.</summary>
        public float StateWeight;

        /// <summary>Абсолютное время последней проверки событий UserData (сдвигается каждый кадр).</summary>
        public float LastCheckEventTime;

        /// <summary>Длительность фейда-аута, назначенного при вытеснении новым моушеном.</summary>
        public float FadeOutSeconds;

        /// <summary>Флаг вытеснения: фейд-аут начат, EndTime будет назначен при следующем обновлении.</summary>
        public bool IsTriggeredFadeOut;

        /// <summary>The motion instance driving parameter updates.</summary>
        public ACubismMotion Motion;

        /// <summary>If true, the entry is automatically removed from the queue when finished.</summary>
        public bool AutoDelete;

        /// <summary>Arbitrary user data attached to this queue entry.</summary>
        public object CustomData;

        /// <summary>
        /// Creates a new queue entry for the given motion.
        /// AutoDelete controls whether the entry is removed on finish.
        /// </summary>
        public CubismMotionQueueEntry(ACubismMotion motion, bool autoDelete)
        {
            Motion = motion;
            AutoDelete = autoDelete;
        }

        /// <summary>
        /// Порт CubismMotionQueueEntry::SetFadeout: помечает запись для фейда-аута
        /// (вызывается очередью, когда стартует новый моушен).
        /// </summary>
        public void SetFadeout(float fadeOutSeconds)
        {
            FadeOutSeconds = fadeOutSeconds;
            IsTriggeredFadeOut = true;
        }

        /// <summary>
        /// Порт CubismMotionQueueEntry::StartFadeout: назначает EndTime, сокращая его,
        /// если фейд-аут уже был запущен раньше и раньше должен закончиться.
        /// </summary>
        public void StartFadeout(float fadeOutSeconds, float userTimeSeconds)
        {
            float newEndTimeSeconds = userTimeSeconds + fadeOutSeconds;
            IsTriggeredFadeOut = true;

            if (EndTime < 0f || newEndTimeSeconds < EndTime)
            {
                EndTime = newEndTimeSeconds;
            }
        }

        /// <summary>Порт CubismMotionQueueEntry::SetState — сохраняет время и вес последнего кадра.</summary>
        public void SetState(float timeSeconds, float weight)
        {
            StateTime = timeSeconds;
            StateWeight = weight;
        }
    }

    /// <summary>
    /// Manages a queue of <see cref="ACubismMotion"/> instances for a single model.
    /// Поддержка вытеснения по приоритету (ReserveMotion), параллельного проигрывания
    /// и автосборки завершённых записей. Порт CubismMotionQueueManager из SDK 5-r.5:
    ///
    /// - StartMotion назначает всем прежним записям фейд-аут;
    /// - каждый кадр DoUpdateMotion вызывает <see cref="ACubismMotion.UpdateParameters"/>,
    ///   затем проверяет события UserData (<see cref="ACubismMotion.GetFiredEvent"/>) и
    ///   удаляет finished-записи;
    /// - колбэком завершения владеет сам моушен: очередь его НЕ дублирует.
    /// </summary>
    public class CubismMotionQueueManager
    {
        private readonly List<CubismMotionQueueEntry> _entries = new();
        private readonly List<string> _firedEventValues = new();
        private int _reservedPriority = -1;

        /// <summary>
        /// Событие, вызываемое для каждого сработавшего UserData-события за кадр
        /// (порт _eventCallback из CubismMotionQueueManager).
        /// </summary>
        public event Action<ACubismMotion, string> EventFired;

        /// <summary>
        /// События, собранные за последний кадр DoUpdateMotion по всем записям очереди.
        /// Список очищается в начале каждого обновления — читайте его сразу после вызова.
        /// </summary>
        public IReadOnlyList<string> FiredEventValues => _firedEventValues;

        /// <summary>
        /// Sets the reserved priority threshold.
        /// Motions with priority below this are rejected by <see cref="ReserveMotion"/>.
        /// </summary>
        public void SetReservePriority(int priority) => _reservedPriority = priority;

        /// <summary>
        /// Attempts to reserve the given priority level.
        /// Returns true if the priority is accepted (higher than current reserved),
        /// false if a higher-priority motion is already reserved.
        /// </summary>
        public bool ReserveMotion(int priority)
        {
            if (_reservedPriority >= 0 && _reservedPriority > priority)
                return false;

            _reservedPriority = priority;
            return true;
        }

        /// <summary>
        /// Enqueues a motion for playback. Прежним записям назначается фейд-аут
        /// (SetFadeout), после чего они плавно завершаются; сама запись инициализируется
        /// при первом кадре <see cref="ACubismMotion.UpdateParameters"/>.
        /// Параметр userTimeSeconds оставлен для совместимости вызовов.
        /// </summary>
        /// <param name="motion">The motion to play.</param>
        /// <param name="autoDelete">Whether to auto-remove on finish.</param>
        /// <param name="userTimeSeconds">Current user time (не используется, порт сигнатуры).</param>
        public CubismMotionQueueEntry StartMotion(ACubismMotion motion, bool autoDelete, float userTimeSeconds)
        {
            if (motion == null)
                return null;

            // Порт CubismMotionQueueManager::StartMotion: прежние моушены вытесняются фейд-аутом.
            foreach (var existing in _entries)
            {
                if (existing == null) continue;
                existing.SetFadeout(existing.Motion?.FadeOutSeconds ?? 0f);
            }

            var entry = new CubismMotionQueueEntry(motion, autoDelete);
            _entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// Stops all motions and clears the queue. Resets priority reservation.
        /// </summary>
        public void StopAllMotions()
        {
            _entries.Clear();
            _reservedPriority = -1;
        }

        /// <summary>
        /// Returns true if all entries in the queue are finished.
        /// </summary>
        public bool IsFinished()
        {
            foreach (var entry in _entries)
                if (entry != null && !entry.IsFinished)
                    return false;
            return true;
        }

        /// <summary>
        /// Per-frame update — порт CubismMotionQueueManager::DoUpdateMotion.
        /// Для каждой записи: UpdateParameters (фейд + применение значений + финализация
        /// IsFinished самим моушеном), сборUserData-событий за кадр, затем удаление
        /// finished-записей и старт отложенного фейда-аута вытесненных.
        /// Обход прямой, как в SDK: моушены применяются от старых к новым.
        /// </summary>
        public void DoUpdateMotion(CubismNativeModel model, float userTimeSeconds)
        {
            _firedEventValues.Clear();

            int i = 0;
            while (i < _entries.Count)
            {
                CubismMotionQueueEntry entry = _entries[i];

                if (entry == null)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                ACubismMotion motion = entry.Motion;
                if (motion == null)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                // ------ Применение значений (финализация — внутри моушена/обёртки) ------
                motion.UpdateParameters(model, entry, userTimeSeconds);

                // ------ Проверка UserData-событий за кадр ------
                var fired = motion.GetFiredEvent(
                    entry.LastCheckEventTime - entry.StartTime,
                    userTimeSeconds - entry.StartTime);

                for (int e = 0; e < fired.Count; e++)
                    EventFired?.Invoke(motion, fired[e]);

                _firedEventValues.AddRange(fired);
                entry.LastCheckEventTime = userTimeSeconds;

                // ------ Удаление завершённых записей ------
                if (entry.IsFinished)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                // Фейд-аут, запланированный при вытеснении, стартует после обновления.
                if (entry.IsTriggeredFadeOut)
                {
                    entry.StartFadeout(entry.FadeOutSeconds, userTimeSeconds);
                }

                i++;
            }
        }
    }
}
