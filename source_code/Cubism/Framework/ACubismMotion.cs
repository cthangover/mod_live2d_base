using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Abstract base for all Live2D Cubism motions (animations and expressions).
    /// Порт ACubismMotion из Cubism SDK for Native 5-r.5: владеет временами фейда,
    /// весом, цикличностью и колбэками; очередь (<see cref="CubismMotionQueueManager"/>)
    /// вызывает <see cref="UpdateParameters"/> раз в кадр, тот в свою очередь —
    /// <see cref="SetupMotionQueueEntry"/> (при старте), <see cref="UpdateFadeWeight"/>
    /// и виртуальный <see cref="DoUpdateParameters"/>.
    ///
    /// Завершение выставляется по EndTime: сам моушен ставит IsFinished в
    /// DoUpdateParameters (незацикленный), а обёртка <see cref="UpdateParameters"/>
    /// после применения параметров закрывает запись, когда текущее время прошло EndTime
    /// (например, после запущенного через очередь fade-out). Колбэки при этом не
    /// дублируются: OnFinishedMotion вызывается только самим моушеном.
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

        /// <summary>Сработавшие события (UserData); наполняется в <see cref="GetFiredEvent"/>.</summary>
        protected readonly List<string> _firedEventValues = new();

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
        /// Автоматически вызывается самим моушеном в DoUpdateParameters по достижении
        /// длительности; очередь этот колбэк не дублирует.
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
        /// Порт ACubismMotion::UpdateParameters — кадровая обёртка, вызываемая очередью.
        /// Проверяет доступность записи, инициализирует её при старте, считает fade-вес,
        /// применяет параметры и после этого закрывает запись, если время прошло EndTime.
        /// </summary>
        public void UpdateParameters(CubismNativeModel model, CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            if (!entry.IsAvailable || entry.IsFinished)
            {
                return;
            }

            SetupMotionQueueEntry(entry, userTimeSeconds);

            float fadeWeight = UpdateFadeWeight(entry, userTimeSeconds);

            DoUpdateParameters(model, userTimeSeconds, fadeWeight, entry);

            // Пост-проверка SDK: EndTime > 0 и уже пройден — запись завершена.
            if ((entry.EndTime > 0f) && (entry.EndTime < userTimeSeconds))
            {
                entry.IsFinished = true;
            }
        }

        /// <summary>
        /// Порт ACubismMotion::SetupMotionQueueEntry: при первом кадре записывает
        /// StartTime (с учётом OffsetSeconds), FadeInStartTime и EndTime
        /// (если EndTime ещё не выставлен извне, например фейдаутом от очереди).
        /// </summary>
        public void SetupMotionQueueEntry(CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            if (!entry.IsAvailable || entry.IsFinished)
            {
                return;
            }

            if (entry.IsStarted)
            {
                return;
            }

            entry.IsStarted = true;
            entry.StartTime = userTimeSeconds - _offsetSeconds;
            entry.FadeInStartTime = userTimeSeconds;

            // EndTime мог быть выставлен заранее (SetFadeout до старта).
            if (entry.EndTime < 0f)
            {
                AdjustEndTime(entry);
            }

            OnBeganMotion?.Invoke(this);
        }

        /// <summary>
        /// Порт ACubismMotion::UpdateFadeWeight: вес = Weight x fadeIn x fadeOut
        /// с синус-эйзингом. При fadeIn/fadeOut == 0 соответствующий множитель = 1.
        /// </summary>
        public float UpdateFadeWeight(CubismMotionQueueEntry entry, float userTimeSeconds)
        {
            if (entry == null)
            {
                return -1f;
            }

            float fadeWeight = _weight;

            float fadeIn = _fadeInSeconds == 0f
                ? 1f
                : CubismMath.GetEasingSine((userTimeSeconds - entry.FadeInStartTime) / _fadeInSeconds);

            float fadeOut = (_fadeOutSeconds == 0f || entry.EndTime < 0f)
                ? 1f
                : CubismMath.GetEasingSine((entry.EndTime - userTimeSeconds) / _fadeOutSeconds);

            fadeWeight = fadeWeight * fadeIn * fadeOut;

            entry.SetState(userTimeSeconds, fadeWeight);

            return fadeWeight;
        }

        /// <summary>
        /// Порт ACubismMotion::AdjustEndTime: EndTime = StartTime + GetDuration();
        /// для цикличных моушенов (GetDuration() <= 0) EndTime = -1 (бесконечность).
        /// </summary>
        protected void AdjustEndTime(CubismMotionQueueEntry entry)
        {
            float duration = GetDuration();
            float endTime = duration <= 0f ? -1f : entry.StartTime + duration;
            entry.EndTime = endTime;
        }

        /// <summary>
        /// Порт ACubismMotion::GetFiredEvent: базовая реализация не содержит событий.
        /// <see cref="CubismMotion"/> переопределяет и наполняет список по UserData.
        /// </summary>
        public virtual List<string> GetFiredEvent(float beforeCheckTimeSeconds, float motionTimeSeconds)
        {
            return _firedEventValues;
        }

        /// <summary>
        /// Applies motion parameter values to the model for the current frame.
        /// Called every frame by <see cref="ACubismMotion.UpdateParameters"/>.
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
