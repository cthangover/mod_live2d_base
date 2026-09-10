namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Represents one enqueued motion in the playback queue.
    /// Holds timing data (start time, end time, fade-in start time),
    /// lifecycle flags (available, finished, auto-delete), and a
    /// reference to the motion itself.
    /// 
    /// Created by <see cref="CubismMotionQueueManager.StartMotion"/>
    /// and consumed frame-by-frame in <see cref="CubismMotionQueueManager.DoUpdateMotion"/>.
    /// </summary>
    public class CubismMotionQueueEntry
    {
        /// <summary>Absolute user time when motion playback begins.</summary>
        public float StartTime;

        /// <summary>
        /// Absolute user time when playback ends. -1 for infinite (looping) motions.
        /// </summary>
        public float EndTime;

        /// <summary>Absolute user time when fade-in begins (may differ from StartTime for offset motions).</summary>
        public float FadeInStartTime;

        /// <summary>Whether this entry should be processed during updates.</summary>
        public bool IsAvailable;

        /// <summary>Flag set when playback completes. Checked by the queue manager during cleanup.</summary>
        public bool IsFinished;

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
            IsAvailable = true;
        }
    }

    /// <summary>
    /// Manages a queue of <see cref="ACubismMotion"/> instances for a single model.
    /// Supports priority reservation (higher-priority motions preempt lower ones),
    /// concurrent playback of multiple motions, and automatic cleanup of finished entries.
    /// 
    /// Two instances are used by <see cref="CubismModelNode"/>:
    /// one for character motions (animations) and one for expressions.
    /// Each is updated independently in the per-frame loop, allowing motions
    /// and expressions to blend independently with their own fade weights.
    /// 
    /// The update loop iterates entries in reverse for safe removal during
    /// traversal. Finished auto-delete entries are removed immediately;
    /// non-auto-delete entries remain in the queue with their IsFinished flag set.
    /// </summary>
    public class CubismMotionQueueManager
    {
        private readonly System.Collections.Generic.List<CubismMotionQueueEntry> _entries = new();
        private int _reservedPriority = -1;

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
        /// Creates and enqueues a motion for playback.
        /// Instantiates a <see cref="CubismMotionQueueEntry"/>, calls
        /// <see cref="ACubismMotion.SetupMotionQueueEntry"/> on it, and
        /// adds it to the queue. Returns the entry for optional tracking.
        /// </summary>
        /// <param name="motion">The motion to play.</param>
        /// <param name="autoDelete">Whether to auto-remove on finish.</param>
        /// <param name="userTimeSeconds">Current user time for start time calculation.</param>
        public CubismMotionQueueEntry StartMotion(ACubismMotion motion, bool autoDelete, float userTimeSeconds)
        {
            if (motion == null)
                return null;

            var entry = new CubismMotionQueueEntry(motion, autoDelete);
            motion.SetupMotionQueueEntry(entry, userTimeSeconds);
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
                if (!entry.IsFinished)
                    return false;
            return true;
        }

        /// <summary>
        /// Per-frame update for all queued motions.
        /// Iterates in reverse for safe removal. For each active entry:
        /// computes fade weight, invokes <see cref="ACubismMotion.DoUpdateParameters"/>,
        /// and checks for completion. Finished auto-delete entries are removed.
        /// </summary>
        public void DoUpdateMotion(CubismNativeModel model, float userTimeSeconds)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (!entry.IsAvailable || entry.IsFinished)
                {
                    if (entry.AutoDelete)
                        _entries.RemoveAt(i);
                    continue;
                }

                var weight = entry.Motion.UpdateFadeWeight(entry, userTimeSeconds);
                entry.Motion.DoUpdateParameters(model, userTimeSeconds, weight, entry);

                if (entry.Motion.GetDuration() > 0f)
                {
                    if (userTimeSeconds >= entry.EndTime && !entry.Motion.IsLoop)
                    {
                        entry.IsFinished = true;
                        entry.Motion.OnFinishedMotion?.Invoke(entry.Motion);
                    }
                }
            }
        }
    }
}
