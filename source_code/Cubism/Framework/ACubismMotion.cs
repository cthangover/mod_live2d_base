using System;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Abstract base for all Live2D Cubism motions (animations and expressions).
    /// Provides fade-in/fade-out timing, looping, weight blending, and event
    /// callbacks shared across <see cref="CubismMotion"/> and
    /// <see cref="CubismExpressionMotion"/>.
    /// 
    /// Motions are managed by <see cref="CubismMotionQueueManager"/>, which
    /// calls <see cref="SetupMotionQueueEntry"/> on start, then
    /// <see cref="UpdateFadeWeight"/> and <see cref="DoUpdateParameters"/>
    /// every frame.
    /// </summary>
    public abstract class ACubismMotion
    {
        /// <summary>
        /// Callback invoked when the motion finishes (non-looping only).
        /// The finished motion is passed as the argument.
        /// </summary>
        public delegate void FinishedMotionCallback(ACubismMotion motion);

        /// <summary>
        /// Callback invoked when the motion begins playback.
        /// Called from <see cref="SetupMotionQueueEntry"/> right after
        /// the entry's times are configured.
        /// </summary>
        public delegate void BeganMotionCallback(ACubismMotion motion);

        protected float _fadeInSeconds = -1f;
        protected float _fadeOutSeconds = -1f;
        protected float _weight = 1f;
        protected float _offsetSeconds;
        protected bool _isLoop;
        protected bool _isLoopFadeIn = true;
        protected bool _previousLoopState;

        /// <summary>
        /// Duration of the fade-in transition in seconds.
        /// A negative value means the default (1 second) will be used
        /// by <see cref="SetupMotionQueueEntry"/> and <see cref="UpdateFadeWeight"/>.
        /// </summary>
        public float FadeInSeconds { get => _fadeInSeconds; set => _fadeInSeconds = value; }

        /// <summary>
        /// Duration of the fade-out transition in seconds.
        /// A negative value means the default (1 second) will be used.
        /// </summary>
        public float FadeOutSeconds { get => _fadeOutSeconds; set => _fadeOutSeconds = value; }

        /// <summary>
        /// Overall motion weight (0..1). Multiplied with the fade-in/fade-out
        /// factors in <see cref="UpdateFadeWeight"/> to produce the final blend weight.
        /// </summary>
        public float Weight { get => _weight; set => _weight = value; }

        /// <summary>
        /// Time offset in seconds applied to the motion's start time.
        /// Allows starting playback from an arbitrary point.
        /// </summary>
        public float OffsetSeconds { get => _offsetSeconds; set => _offsetSeconds = value; }

        /// <summary>
        /// Whether the motion loops. When enabled, <see cref="CubismMotion"/>
        /// wraps timeOffset back to the beginning after the duration elapses.
        /// </summary>
        public bool IsLoop { get => _isLoop; set => _isLoop = value; }

        /// <summary>
        /// When true and looping, the motion performs a fade-in at each loop
        /// restart. When false, the fade-in is skipped on subsequent loops.
        /// </summary>
        public bool IsLoopFadeIn { get => _isLoopFadeIn; set => _isLoopFadeIn = value; }

        /// <summary>
        /// Invoked when the motion finishes (non-looping only).
        /// Automatically called by <see cref="CubismMotion.DoUpdateParameters"/>
        /// when timeOffset exceeds duration.
        /// </summary>
        public FinishedMotionCallback OnFinishedMotion { get; set; }

        /// <summary>
        /// Invoked when the motion begins. Fires at the start of
        /// <see cref="SetupMotionQueueEntry"/> after the entry is initialized.
        /// </summary>
        public BeganMotionCallback OnBeganMotion { get; set; }

        /// <summary>
        /// Returns the total motion duration in seconds.
        /// Override in <see cref="CubismMotion"/> to return the motion3.json
        /// duration, or -1 for looping motions.
        /// </summary>
        public virtual float GetDuration() => -1f;

        /// <summary>
        /// Returns the loop cycle duration in seconds.
        /// Override in <see cref="CubismMotion"/> to return the full duration
        /// even when looping is active.
        /// </summary>
        public virtual float GetLoopDuration() => -1f;

        /// <summary>
        /// Initializes a queue entry with start/end times based on current
        /// <see cref="FadeInSeconds"/>, <see cref="OffsetSeconds"/>,
        /// and <see cref="GetDuration"/>. Fires <see cref="OnBeganMotion"/>.
        /// </summary>
        public void SetupMotionQueueEntry(CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            var fadeIn = _fadeInSeconds >= 0f ? _fadeInSeconds : 1f;
            entry.StartTime = userTimeSeconds - _offsetSeconds;
            entry.FadeInStartTime = userTimeSeconds;
            var duration = GetDuration();
            UpdateEndTime(entry, duration);
            OnBeganMotion?.Invoke(this);
        }

        /// <summary>
        /// Computes the final frame weight = <see cref="Weight"/> x fadeIn x fadeOut.
        /// Fade curves use the sine easing function from <see cref="CubismMath.GetEasingSine"/>.
        /// </summary>
        public float UpdateFadeWeight(CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            var fadeIn = GetFadeIn(entry, userTimeSeconds);
            var fadeOut = GetFadeOut(entry, userTimeSeconds);
            return _weight * fadeIn * fadeOut;
        }

        private float GetFadeIn(CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            var fadeInTime = _fadeInSeconds >= 0f ? _fadeInSeconds : 1f;
            if (fadeInTime <= 0f) return 1f;
            var t = (userTimeSeconds - entry.FadeInStartTime) / fadeInTime;
            return CubismMath.GetEasingSine(t);
        }

        private float GetFadeOut(CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            var fadeOutTime = _fadeOutSeconds >= 0f ? _fadeOutSeconds : 1f;
            if (fadeOutTime <= 0f) return 1f;
            var t = (entry.EndTime - userTimeSeconds) / fadeOutTime;
            return CubismMath.GetEasingSine(t);
        }

        /// <summary>
        /// Sets the queue entry's EndTime based on loop state and duration.
        /// Looping motions get EndTime = -1 (infinite).
        /// </summary>
        protected void UpdateEndTime(CubismMotionQueueEntry entry, float duration)
        {
            entry.EndTime = _isLoop ? -1f : entry.StartTime + duration;
        }

        /// <summary>
        /// Recalculates EndTime when <see cref="IsLoop"/> changes mid-playback.
        /// Called by <see cref="CubismMotion.DoUpdateParameters"/> every frame
        /// to detect loop state transitions.
        /// </summary>
        protected void AdjustEndTime(CubismMotionQueueEntry entry, float duration)
        {
            if (_isLoop != _previousLoopState)
            {
                _previousLoopState = _isLoop;
                UpdateEndTime(entry, duration);
            }
        }

        /// <summary>
        /// Applies motion parameter values to the model for the current frame.
        /// Called every frame by <see cref="CubismMotionQueueManager.DoUpdateMotion"/>.
        /// </summary>
        /// <param name="model">The native Cubism model to modify.</param>
        /// <param name="userTimeSeconds">Elapsed user time in seconds.</param>
        /// <param name="weight">Blend weight already multiplied by fade factors.</param>
        /// <param name="entry">The queue entry containing start/end timing.</param>
        public abstract void DoUpdateParameters(
            CubismNativeModel model,
            float userTimeSeconds,
            float weight,
            CubismMotionQueueEntry entry);
    }
}
