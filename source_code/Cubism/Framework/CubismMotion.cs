using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    public delegate float MotionSegmentFunction(float[] segments, int baseIndex, float time);

    /// <summary>
    /// Версия обработки зацикленного времени — порт CubismMotion::MotionBehavior (SDK 5-r.5).
    /// V2 (по умолчанию): длительность цикла расширяется на один кадр (1/Fps), а переход
    /// между циклами интерполируется через коррекцию конечной точки (CorrectEndPoint).
    /// V1: время цикла жёстко перезаписывается без кадра-хвоста (поведение старых SDK).
    /// </summary>
    public enum MotionBehavior
    {
        V1 = 0,
        V2 = 1,
    }

    /// <summary>
    /// Parses and plays a Cubism .motion3.json animation.
    /// Manages three categories of curves:
    /// <list type="bullet">
    ///   <item><b>Model curves</b> — eye blink, lip sync, and opacity</item>
    ///   <item><b>Parameter curves</b> — arbitrary model parameter animations</item>
    ///   <item><b>Part opacity curves</b> — visibility/opacity of model parts</item>
    /// </list>
    ///
    /// Данные хранятся как в нативном CubismMotionData: один общий плоский массив точек
    /// (<see cref="CubismMotionPoint"/>) и общий массив сегментов (<see cref="MotionSegmentData"/>);
    /// каждая кривая ссылается на свой диапазон сегментов через BaseSegmentIndex/SegmentCount.
    /// Оценка кривой сначала ищет сегмент по времени ПЕРВОЙ точки следующего сегмента,
    /// а за пределами последнего сегмента возвращает значение последней точки
    /// (при зацикливании V2 — интерполирует переход конца в начало).
    ///
    /// Обновление параметров (<see cref="DoUpdateParameters"/>) — четыре прохода, порт SDK:
    /// <list type="number">
    ///   <item>Model-кривые: EyeBlink/LipSync значения и ModelOpacity</item>
    ///   <item>Parameter-кривые: модуляция на EyeBlink (* значение) и LipSync (+ значение)</item>
    ///   <item>Не анимированные параметры групп EyeBlink/LipSync: лерп с fade-весом</item>
    ///   <item>PartOpacity-кривые</item>
    /// </list>
    ///
    /// Фейд применяется по кривой: если у кривой заданы собственные FadeInTime/FadeOutTime —
    /// используется они (база — фейд самого моушена), иначе общий fade-вес моушена.
    /// </summary>
    public class CubismMotion : ACubismMotion
    {
        private const string ModelCurveIdEyeBlink = "EyeBlink";
        private const string ModelCurveIdLipSync = "LipSync";
        private const string ModelCurveIdOpacity = "Opacity";

        /// <summary>Порт SDK-константы MaxTargetSize: лимит целей EyeBlink/LipSync (ширина битовой маски).</summary>
        private const int MaxTargetSize = 64;

        /// <summary>Порт CubismMath::Epsilon — порог сравнения в алгоритме Кардано.</summary>
        private const float MathEpsilon = 0.00001f;

        private readonly float _sourceFrameRate;
        private float _lastWeight;
        private float _loopDurationSeconds = -1f;
        private MotionBehavior _motionBehavior = MotionBehavior.V2;
        private readonly CubismMotionJson _motionJson;
        private readonly bool _areBeziersRestricted;

        private readonly List<MotionCurveData> _modelCurves = new();
        private readonly List<MotionCurveData> _parameterCurves = new();
        private readonly List<MotionCurveData> _partOpacityCurves = new();

        // Общие плоские хранилища точек и сегментов (порт CubismMotionData.Points/Segments).
        private CubismMotionPoint[] _points = Array.Empty<CubismMotionPoint>();
        private MotionSegmentData[] _segments = Array.Empty<MotionSegmentData>();

        private readonly List<string> _eyeBlinkParameterIds = new();
        private readonly List<string> _lipSyncParameterIds = new();

        private readonly List<CubismMotionEvent> _events = new();

        private float _modelOpacity = 1f;

        /// <summary>
        /// Parses a .motion3.json byte buffer: метаданные (длительность, loop, fps, фейды,
        /// признак restricted-безье) и кривые/события через <see cref="ParseCurves"/>/<see cref="ParseEvents"/>.
        /// Отсутствующее или отрицательное время фейда заменяется на 1 c — порт CubismMotion::Parse.
        /// </summary>
        public CubismMotion(byte[] motion3JsonBytes)
        {
            _motionJson = new CubismMotionJson(motion3JsonBytes);
            _sourceFrameRate = _motionJson.GetFps();
            _areBeziersRestricted = _motionJson.AreBeziersRestricted();
            _isLoop = _motionJson.IsLoop();
            _loopDurationSeconds = _motionJson.GetDuration();

            var fadeIn = _motionJson.GetFadeInTime();
            var fadeOut = _motionJson.GetFadeOutTime();
            _fadeInSeconds = fadeIn < 0f ? 1f : fadeIn;
            _fadeOutSeconds = fadeOut < 0f ? 1f : fadeOut;

            ParseCurves();
            ParseEvents();
        }

        /// <summary>
        /// Configures which parameters are affected by eye blink and lip sync
        /// modulation (обычно из Groups модели в model3.json). Кривая параметра из
        /// этих групп умножается на значение EyeBlink-кривой / складывается с LipSync
        /// в проходе 2; параметры групп без собственной кривой получают лерп в проходе 3.
        /// </summary>
        public void SetEffectIds(List<string> eyeBlinkIds, List<string> lipSyncIds)
        {
            _eyeBlinkParameterIds.Clear();
            _lipSyncParameterIds.Clear();
            if (eyeBlinkIds != null) _eyeBlinkParameterIds.AddRange(eyeBlinkIds);
            if (lipSyncIds != null) _lipSyncParameterIds.AddRange(lipSyncIds);
        }

        /// <summary>Returns -1 for looping motions (they never end), иначе полную длительность.</summary>
        public override float GetDuration() => _isLoop ? -1f : _loopDurationSeconds;

        /// <summary>Полная длительность одного цикла, даже если моушен зациклен.</summary>
        public override float GetLoopDuration() => _loopDurationSeconds;

        /// <summary>Порт CubismMotion::SetMotionBehavior — версия обработки зацикленного времени.</summary>
        public void SetMotionBehavior(MotionBehavior behavior) => _motionBehavior = behavior;

        /// <summary>Порт CubismMotion::GetMotionBehavior.</summary>
        public MotionBehavior GetMotionBehavior() => _motionBehavior;

        /// <summary>
        /// true, если среди Model-кривых есть кривая "Opacity" — порт CubismMotion::IsExistModelOpacity:
        /// наличие кривой, а не текущее значение прозрачности.
        /// </summary>
        public bool IsExistModelOpacity()
        {
            foreach (var curve in _modelCurves)
                if (curve.Id == ModelCurveIdOpacity) return true;
            return false;
        }

        /// <summary>
        /// Текущая ModelOpacity из Model-кривой "Opacity" (1, если кривой нет).
        /// Рендерер должен умножать на неё прозрачность drawable'ов — SDK применяет
        /// её в CubismModel::SetModelOpacity, здесь значение отдаётся наружу.
        /// </summary>
        public float GetModelOpacity() => _modelOpacity;

        /// <summary>
        /// События (UserData), сработавшие в интервале (beforeCheckTime, motionTime].
        /// Список очищается при каждом вызове; очередь вызывает метод раз в кадр
        /// с lastCheckTime..currentTime, поэтому события не теряются между кадрами.
        /// </summary>
        public override List<string> GetFiredEvent(float beforeCheckTimeSeconds, float motionTimeSeconds)
        {
            _firedEventValues.Clear();
            foreach (var evt in _events)
                if (evt.FireTime > beforeCheckTimeSeconds && evt.FireTime <= motionTimeSeconds)
                    _firedEventValues.Add(evt.Value);
            return _firedEventValues;
        }

        /// <summary>Совместимая обёртка над <see cref="GetFiredEvent"/> с тем же списком результатов.</summary>
        public List<string> GetFiredEventValue(float beforeTime, float motionTime) =>
            GetFiredEvent(beforeTime, motionTime);

        /// <summary>
        /// Four-pass parameter update. Called by <see cref="CubismMotionQueueManager"/>
        /// every frame (через <see cref="ACubismMotion.UpdateParameters"/>).
        /// </summary>
        public override unsafe void DoUpdateParameters(
            CubismNativeModel model, float userTimeSeconds, float fadeWeight, CubismMotionQueueEntry entry)
        {
            if (model == null || entry == null) return;

            // Порт CubismMotion::DoUpdateParameters: пересчёт EndTime при смене loop-состояния (V2).
            if (_motionBehavior == MotionBehavior.V2 && _previousLoopState != _isLoop)
            {
                AdjustEndTime(entry);
                _previousLoopState = _isLoop;
            }

            float timeOffsetSeconds = userTimeSeconds - entry.StartTime;
            if (timeOffsetSeconds < 0f) timeOffsetSeconds = 0f; // защита от старта в "будущем"

            float lipSyncValue = float.MaxValue;  // FLT_MAX — сигнатура "кривой нет"
            float eyeBlinkValue = float.MaxValue;
            ulong lipSyncFlags = 0UL;
            ulong eyeBlinkFlags = 0UL;

            // Базовые (моушен-уровень) фейды для кривых без собственных fade-настроек.
            float tmpFadeIn = _fadeInSeconds <= 0f
                ? 1f
                : CubismMath.GetEasingSine((userTimeSeconds - entry.FadeInStartTime) / _fadeInSeconds);

            float tmpFadeOut = (_fadeOutSeconds <= 0f || entry.EndTime < 0f)
                ? 1f
                : CubismMath.GetEasingSine((entry.EndTime - userTimeSeconds) / _fadeOutSeconds);

            // 'Repeat' time as necessary.
            float time = timeOffsetSeconds;
            float duration = _loopDurationSeconds;
            bool isCorrection = _motionBehavior == MotionBehavior.V2 && _isLoop;

            if (_isLoop)
            {
                if (_motionBehavior == MotionBehavior.V2)
                    duration += 1f / _sourceFrameRate; // кадр-хвост для интерполяции цикла
                while (time > duration)
                    time -= duration;
            }

            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();
            var minValues = model.GetParameterMinimumValues();
            var maxValues = model.GetParameterMaximumValues();

            // Индексы параметров модели: порт model->GetParameterIndex(curveId).
            var paramIndexById = new Dictionary<string, int>(model.ParameterCount);
            for (int i = 0; i < model.ParameterCount; i++)
                paramIndexById[CubismNativeModel.ReadStringFromPtrArray(idsPtr, i)] = i;

            // Pass 1 — model curves (EyeBlink / LipSync / Opacity).
            foreach (var curve in _modelCurves)
            {
                float value = EvaluateCurveInternal(curve, _points, _segments, time, isCorrection, duration, _areBeziersRestricted);

                if (curve.Id == ModelCurveIdEyeBlink) eyeBlinkValue = value;
                else if (curve.Id == ModelCurveIdLipSync) lipSyncValue = value;
                else if (curve.Id == ModelCurveIdOpacity)
                {
                    // Порт: _modelOpacity = value; model->SetModelOpacity(...).
                    // Значение отдаётся наружу через GetModelOpacity().
                    _modelOpacity = value;
                }
            }

            // Pass 2 — parameter curves с модуляцией EyeBlink/LipSync и пер-кривым фейдом.
            foreach (var curve in _parameterCurves)
            {
                if (!paramIndexById.TryGetValue(curve.Id, out int parameterIndex)) continue;

                float sourceValue = values[parameterIndex];
                float value = EvaluateCurveInternal(curve, _points, _segments, time, isCorrection, duration, _areBeziersRestricted);

                if (eyeBlinkValue != float.MaxValue)
                {
                    for (int i = 0; i < _eyeBlinkParameterIds.Count && i < MaxTargetSize; i++)
                    {
                        if (_eyeBlinkParameterIds[i] == curve.Id)
                        {
                            value *= eyeBlinkValue;
                            eyeBlinkFlags |= 1UL << i;
                            break;
                        }
                    }
                }

                if (lipSyncValue != float.MaxValue)
                {
                    for (int i = 0; i < _lipSyncParameterIds.Count && i < MaxTargetSize; i++)
                    {
                        if (_lipSyncParameterIds[i] == curve.Id)
                        {
                            value += lipSyncValue;
                            lipSyncFlags |= 1UL << i;
                            break;
                        }
                    }
                }

                float v;
                if (curve.FadeInTime < 0f && curve.FadeOutTime < 0f)
                {
                    // Собственных фейдов у кривой нет — применяется общий fade-вес моушена.
                    v = sourceValue + (value - sourceValue) * fadeWeight;
                }
                else
                {
                    // Порт: база — фейд самого моушена (tmpFadeIn/tmpFadeOut), не 1.
                    float fin = curve.FadeInTime < 0f
                        ? tmpFadeIn
                        : curve.FadeInTime == 0f
                            ? 1f
                            : CubismMath.GetEasingSine((userTimeSeconds - entry.FadeInStartTime) / curve.FadeInTime);

                    float fout = curve.FadeOutTime < 0f
                        ? tmpFadeOut
                        : (curve.FadeOutTime == 0f || entry.EndTime < 0f)
                            ? 1f
                            : CubismMath.GetEasingSine((entry.EndTime - userTimeSeconds) / curve.FadeOutTime);

                    float paramWeight = _weight * fin * fout;
                    v = sourceValue + (value - sourceValue) * paramWeight;
                }

                // Порт model->SetParameterValue: значение зажимается в [min, max] параметра.
                values[parameterIndex] = ClampParameterValue(v, minValues[parameterIndex], maxValues[parameterIndex]);
            }

            // Pass 3 — EyeBlink/LipSync для параметров, не анимированных кривыми: лерп с fade-весом.
            if (eyeBlinkValue != float.MaxValue)
            {
                for (int i = 0; i < _eyeBlinkParameterIds.Count && i < MaxTargetSize; i++)
                {
                    // Моушен уже записал этот параметр в проходе 2 — моргание не применяем.
                    if ((eyeBlinkFlags >> i & 1UL) != 0UL) continue;
                    if (!paramIndexById.TryGetValue(_eyeBlinkParameterIds[i], out int pi)) continue;

                    float sourceValue = values[pi];
                    float v = sourceValue + (eyeBlinkValue - sourceValue) * fadeWeight;
                    values[pi] = ClampParameterValue(v, minValues[pi], maxValues[pi]);
                }
            }

            if (lipSyncValue != float.MaxValue)
            {
                for (int i = 0; i < _lipSyncParameterIds.Count && i < MaxTargetSize; i++)
                {
                    if ((lipSyncFlags >> i & 1UL) != 0UL) continue;
                    if (!paramIndexById.TryGetValue(_lipSyncParameterIds[i], out int pi)) continue;

                    float sourceValue = values[pi];
                    float v = sourceValue + (lipSyncValue - sourceValue) * fadeWeight;
                    values[pi] = ClampParameterValue(v, minValues[pi], maxValues[pi]);
                }
            }

            // Pass 4 — part opacity curves (пишутся напрямую в массив прозрачностей частей).
            var partOpacities = model.GetPartOpacities();
            var partIdsPtr = model.GetPartIds();
            int partCount = model.GetPartCount();
            var partIndexById = new Dictionary<string, int>(partCount);
            for (int i = 0; i < partCount; i++)
                partIndexById[CubismNativeModel.ReadStringFromPtrArray(partIdsPtr, i)] = i;

            foreach (var curve in _partOpacityCurves)
            {
                if (!partIndexById.TryGetValue(curve.Id, out int pi)) continue;
                partOpacities[pi] = EvaluateCurveInternal(curve, _points, _segments, time, isCorrection, duration, _areBeziersRestricted);
            }

            // Завершение/перезапуск: моушен сам выставляет IsFinished и зовёт колбэк
            // (очередь только удаляет finished-записи — порт CubismMotionQueueManager).
            if (timeOffsetSeconds >= duration)
            {
                if (_isLoop)
                    UpdateForNextLoop(entry, userTimeSeconds, time);
                else
                {
                    OnFinishedMotion?.Invoke(this);
                    entry.IsFinished = true;
                }
            }

            _lastWeight = fadeWeight;
        }

        /// <summary>
        /// Порт CubismMotion::UpdateForNextLoop: сдвиг StartTime назад на время внутри цикла;
        /// фейд-ин перезапускается только при включённом _isLoopFadeIn (поведение V2).
        /// </summary>
        private void UpdateForNextLoop(CubismMotionQueueEntry entry, float userTimeSeconds, float time)
        {
            switch (_motionBehavior)
            {
                case MotionBehavior.V2:
                default:
                    entry.StartTime = userTimeSeconds - time;
                    if (_isLoopFadeIn)
                        entry.FadeInStartTime = userTimeSeconds - time;
                    OnFinishedMotion?.Invoke(this);
                    break;
                case MotionBehavior.V1:
                    entry.StartTime = userTimeSeconds;
                    if (_isLoopFadeIn)
                        entry.FadeInStartTime = userTimeSeconds;
                    break;
            }
        }

        /// <summary>
        /// Разбор кривых: порт CubismMotion::Parse. Все точки и сегменты складываются
        /// в общие плоские списки; кривая хранит BaseSegmentIndex и количество сегментов.
        /// </summary>
        private void ParseCurves()
        {
            var points = new List<CubismMotionPoint>();
            var segments = new List<MotionSegmentData>();

            for (int ci = 0; ci < _motionJson.GetCurveCount(); ci++)
            {
                var target = ParseTarget(_motionJson.GetCurveTarget(ci));

                var curveData = new MotionCurveData
                {
                    Type = target,
                    Id = _motionJson.GetCurveId(ci),
                    BaseSegmentIndex = segments.Count,
                    // -1 = время фейда кривой не задано в json (как в SDK).
                    FadeInTime = _motionJson.GetCurveFadeInTime(ci),
                    FadeOutTime = _motionJson.GetCurveFadeOutTime(ci),
                };

                _motionJson.GetCurveSegments(ci, out var rawSegments);
                curveData.SegmentCount = ParseSegmentsInternal(rawSegments, points, segments);

                switch (target)
                {
                    case CubismMotionCurveTarget.Model: _modelCurves.Add(curveData); break;
                    case CubismMotionCurveTarget.Parameter: _parameterCurves.Add(curveData); break;
                    case CubismMotionCurveTarget.PartOpacity: _partOpacityCurves.Add(curveData); break;
                }
            }

            _points = points.ToArray();
            _segments = segments.ToArray();
        }

        /// <summary>
        /// Разбор массива Segments одной кривой — порт 1:1 цикла CubismMotion::Parse.
        /// Формат motion3.json: [t0, v0, type, t, v, type, t, v, v, v(bezier)...]:
        /// первые два элемента — стартовая точка кривой, дальше для каждого сегмента
        /// маркер типа и его точки (Linear/Stepped/InverseStepped — 1 точка, Bezier — 3).
        /// Возвращает число сегментов кривой; точки и сегменты дописываются в общие списки.
        /// </summary>
        internal static int ParseSegmentsInternal(float[] raw, List<CubismMotionPoint> points, List<MotionSegmentData> segments)
        {
            int count = 0;
            int segmentPosition = 0;
            while (segmentPosition < raw.Length)
            {
                int basePointIndex;
                if (segmentPosition == 0)
                {
                    // Первая точка кривой: время и значение — не маркер типа.
                    basePointIndex = points.Count;
                    points.Add(new CubismMotionPoint { Time = raw[segmentPosition], Value = raw[segmentPosition + 1] });
                    segmentPosition += 2;
                }
                else
                {
                    // Сегменты продолжаются от последней точки предыдущего сегмента.
                    basePointIndex = points.Count - 1;
                }

                var segmentType = (CubismMotionSegmentType)(int)raw[segmentPosition];
                switch (segmentType)
                {
                    case CubismMotionSegmentType.Linear:
                    case CubismMotionSegmentType.Stepped:
                    case CubismMotionSegmentType.InverseStepped:
                        points.Add(new CubismMotionPoint { Time = raw[segmentPosition + 1], Value = raw[segmentPosition + 2] });
                        segmentPosition += 3;
                        break;

                    case CubismMotionSegmentType.Bezier:
                        points.Add(new CubismMotionPoint { Time = raw[segmentPosition + 1], Value = raw[segmentPosition + 2] });
                        points.Add(new CubismMotionPoint { Time = raw[segmentPosition + 3], Value = raw[segmentPosition + 4] });
                        points.Add(new CubismMotionPoint { Time = raw[segmentPosition + 5], Value = raw[segmentPosition + 6] });
                        segmentPosition += 7;
                        break;

                    default:
                        // SDK: CSM_ASSERT(0). Неизвестный тип — останавливаем разбор кривой,
                        // чтобы не зациклиться на некорректных данных.
                        return count;
                }

                segments.Add(new MotionSegmentData { BasePointIndex = basePointIndex, SegmentType = segmentType });
                count++;
            }

            return count;
        }

        /// <summary>
        /// Порт CubismMotion::EvaluateCurve (анонимный namespace CubismMotion.cpp).
        /// Сегмент ищется по времени ПЕРВОЙ точки следующего сегмента; если время за
        /// пределами всех сегментов — значение последней точки, а при зацикленном V2
        /// (isCorrection) — интерполяция конца кривой к её началу (CorrectEndPoint).
        /// </summary>
        internal static float EvaluateCurveInternal(
            MotionCurveData curve,
            CubismMotionPoint[] points,
            MotionSegmentData[] segments,
            float time,
            bool isCorrection,
            float endTime,
            bool areBeziersRestricted)
        {
            int target = -1;
            int totalSegmentCount = curve.BaseSegmentIndex + curve.SegmentCount;
            int pointPosition = 0;

            for (int i = curve.BaseSegmentIndex; i < totalSegmentCount; i++)
            {
                // Первая точка следующего сегмента = конечная точка текущего.
                pointPosition = segments[i].BasePointIndex
                    + (segments[i].SegmentType == CubismMotionSegmentType.Bezier ? 3 : 1);

                if (points[pointPosition].Time > time)
                {
                    target = i;
                    break;
                }
            }

            if (target == -1)
            {
                if (isCorrection && time < endTime)
                {
                    // Переход конец -> начало при зацикливании (поведение V2).
                    return CorrectEndPointInternal(
                        segments[totalSegmentCount - 1].SegmentType,
                        points,
                        segments[curve.BaseSegmentIndex].BasePointIndex,
                        pointPosition,
                        time,
                        endTime);
                }

                return points[pointPosition].Value;
            }

            return EvaluateSegmentInternal(segments[target].SegmentType, points, segments[target].BasePointIndex, time, areBeziersRestricted);
        }

        /// <summary>
        /// Порт CorrectEndPoint: лерп от конечной точки кривой к начальной,
        /// чьё время искусственно продлевается до endTime (границы цикла).
        /// </summary>
        internal static float CorrectEndPointInternal(
            CubismMotionSegmentType lastSegmentType,
            CubismMotionPoint[] points,
            int beginIndex,
            int endIndex,
            float time,
            float endTime)
        {
            CubismMotionPoint end = points[endIndex];
            CubismMotionPoint begin = points[beginIndex];
            begin.Time = endTime;

            switch (lastSegmentType)
            {
                case CubismMotionSegmentType.Stepped:
                    return end.Value;
                case CubismMotionSegmentType.InverseStepped:
                    return begin.Value;
                default: // Linear / Bezier / прочее — линейная интерполяция
                    return LinearEvaluatePoints(end, begin, time);
            }
        }

        /// <summary>Диспетчер оценки одного сегмента — порт таблички SegmentType -> Evaluate.</summary>
        internal static float EvaluateSegmentInternal(
            CubismMotionSegmentType segmentType,
            CubismMotionPoint[] points,
            int basePointIndex,
            float time,
            bool areBeziersRestricted)
        {
            switch (segmentType)
            {
                case CubismMotionSegmentType.Linear:
                    return LinearEvaluateInternal(points, basePointIndex, time);

                case CubismMotionSegmentType.Bezier:
                    return areBeziersRestricted
                        ? BezierEvaluateBinarySearchInternal(points, basePointIndex, time)
                        : BezierEvaluateCardanoInternal(points, basePointIndex, time);

                case CubismMotionSegmentType.Stepped:
                    return points[basePointIndex].Value;

                case CubismMotionSegmentType.InverseStepped:
                    return points[basePointIndex + 1].Value;

                default:
                    return LinearEvaluateInternal(points, basePointIndex, time);
            }
        }

        /// <summary>Порт LerpPoints: лерп времени и значения пары контрольных точек.</summary>
        private static CubismMotionPoint LerpPoints(CubismMotionPoint a, CubismMotionPoint b, float t)
        {
            return new CubismMotionPoint
            {
                Time = a.Time + (b.Time - a.Time) * t,
                Value = a.Value + (b.Value - a.Value) * t,
            };
        }

        /// <summary>Порт LinearEvaluate: лерп пары точек, t не опускается ниже 0.</summary>
        internal static float LinearEvaluatePoints(CubismMotionPoint p0, CubismMotionPoint p1, float time)
        {
            float t = (time - p0.Time) / (p1.Time - p0.Time);
            if (t < 0f) t = 0f;
            return p0.Value + (p1.Value - p0.Value) * t;
        }

        internal static float LinearEvaluateInternal(CubismMotionPoint[] points, int baseIndex, float time) =>
            LinearEvaluatePoints(points[baseIndex], points[baseIndex + 1], time);

        /// <summary>Порт BezierEvaluate (restricted/старые безье): де Кастельо по параметру t из времени.</summary>
        internal static float BezierEvaluateInternal(CubismMotionPoint[] points, int baseIndex, float time)
        {
            float t = (time - points[baseIndex].Time) / (points[baseIndex + 3].Time - points[baseIndex].Time);
            if (t < 0f) t = 0f;

            CubismMotionPoint p01 = LerpPoints(points[baseIndex], points[baseIndex + 1], t);
            CubismMotionPoint p12 = LerpPoints(points[baseIndex + 1], points[baseIndex + 2], t);
            CubismMotionPoint p23 = LerpPoints(points[baseIndex + 2], points[baseIndex + 3], t);

            CubismMotionPoint p012 = LerpPoints(p01, p12, t);
            CubismMotionPoint p123 = LerpPoints(p12, p23, t);

            return LerpPoints(p012, p123, t).Value;
        }

        /// <summary>Порт BezierEvaluateBinarySearch: деление отрезка параметра пополам (x_error = 0.01, 20 итераций).</summary>
        internal static float BezierEvaluateBinarySearchInternal(CubismMotionPoint[] points, int baseIndex, float time)
        {
            const float xError = 0.01f;

            float x = time;
            float x1 = points[baseIndex].Time;
            float x2 = points[baseIndex + 3].Time;
            float cx1 = points[baseIndex + 1].Time;
            float cx2 = points[baseIndex + 2].Time;

            float ta = 0f;
            float tb = 1f;
            float t = 0f;
            int i = 0;

            for (; i < 20; i++)
            {
                if (x < x1 + xError)
                {
                    t = ta;
                    break;
                }

                if (x2 - xError < x)
                {
                    t = tb;
                    break;
                }

                float centerx = (cx1 + cx2) * 0.5f;
                cx1 = (x1 + cx1) * 0.5f;
                cx2 = (x2 + cx2) * 0.5f;
                float ctrlx12 = (cx1 + centerx) * 0.5f;
                float ctrlx21 = (cx2 + centerx) * 0.5f;
                centerx = (ctrlx12 + ctrlx21) * 0.5f;

                if (x < centerx)
                {
                    tb = (ta + tb) * 0.5f;
                    if (centerx - xError < x)
                    {
                        t = tb;
                        break;
                    }

                    x2 = centerx;
                    cx2 = ctrlx12;
                }
                else
                {
                    ta = (ta + tb) * 0.5f;
                    if (x < centerx + xError)
                    {
                        t = ta;
                        break;
                    }

                    x1 = centerx;
                    cx1 = ctrlx21;
                }
            }

            if (i == 20)
                t = (ta + tb) * 0.5f;

            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            CubismMotionPoint p01 = LerpPoints(points[baseIndex], points[baseIndex + 1], t);
            CubismMotionPoint p12 = LerpPoints(points[baseIndex + 1], points[baseIndex + 2], t);
            CubismMotionPoint p23 = LerpPoints(points[baseIndex + 2], points[baseIndex + 3], t);

            CubismMotionPoint p012 = LerpPoints(p01, p12, t);
            CubismMotionPoint p123 = LerpPoints(p12, p23, t);

            return LerpPoints(p012, p123, t).Value;
        }

        /// <summary>
        /// Порт BezierEvaluateCardanoInterpretation: параметр t ищется решением
        /// кубического уравнения по времени (алгоритм Кардано), затем де Кастельо.
        /// </summary>
        internal static float BezierEvaluateCardanoInternal(CubismMotionPoint[] points, int baseIndex, float time)
        {
            float x1 = points[baseIndex].Time;
            float x2 = points[baseIndex + 3].Time;
            float cx1 = points[baseIndex + 1].Time;
            float cx2 = points[baseIndex + 2].Time;

            float a = x2 - 3f * cx2 + 3f * cx1 - x1;
            float b = 3f * cx2 - 6f * cx1 + 3f * x1;
            float c = 3f * cx1 - 3f * x1;
            float d = x1 - time;

            float t = CardanoAlgorithmForBezierInternal(a, b, c, d);

            CubismMotionPoint p01 = LerpPoints(points[baseIndex], points[baseIndex + 1], t);
            CubismMotionPoint p12 = LerpPoints(points[baseIndex + 1], points[baseIndex + 2], t);
            CubismMotionPoint p23 = LerpPoints(points[baseIndex + 2], points[baseIndex + 3], t);

            CubismMotionPoint p012 = LerpPoints(p01, p12, t);
            CubismMotionPoint p123 = LerpPoints(p12, p23, t);

            return LerpPoints(p012, p123, t).Value;
        }

        /// <summary>
        /// Порт CubismMath::CardanoAlgorithmForBezier — решение кубического уравнения
        /// по формулам Кардано с выбором корня, ближайшего к 0.5, и зажимом в [0,1].
        /// </summary>
        internal static float CardanoAlgorithmForBezierInternal(float a, float b, float c, float d)
        {
            if (MathF.Abs(a) < MathEpsilon)
                return RangeFInternal(QuadraticEquationInternal(b, c, d), 0f, 1f);

            float ba = b / a;
            float ca = c / a;
            float da = d / a;

            float p = (3f * ca - ba * ba) / 3f;
            float p3 = p / 3f;
            float q = (2f * ba * ba * ba - 9f * ba * ca + 27f * da) / 27f;
            float q2 = q / 2f;
            float discriminant = q2 * q2 + p3 * p3 * p3;

            const float center = 0.5f;
            const float threshold = center + 0.01f;

            if (discriminant < 0f)
            {
                float mp3 = -p / 3f;
                float mp33 = mp3 * mp3 * mp3;
                float r = MathF.Sqrt(mp33);
                float t = -q / (2f * r);
                float cosphi = RangeFInternal(t, -1f, 1f);
                float phi = MathF.Acos(cosphi);
                float crtr = MathF.Cbrt(r);
                float t1 = 2f * crtr;

                float root1 = t1 * MathF.Cos(phi / 3f) - ba / 3f;
                if (MathF.Abs(root1 - center) < threshold)
                    return RangeFInternal(root1, 0f, 1f);

                float root2 = t1 * MathF.Cos((phi + 2f * MathF.PI) / 3f) - ba / 3f;
                if (MathF.Abs(root2 - center) < threshold)
                    return RangeFInternal(root2, 0f, 1f);

                float root3 = t1 * MathF.Cos((phi + 4f * MathF.PI) / 3f) - ba / 3f;
                return RangeFInternal(root3, 0f, 1f);
            }

            if (discriminant == 0f)
            {
                float u1 = q2 < 0f ? MathF.Cbrt(-q2) : -MathF.Cbrt(q2);

                float root1 = 2f * u1 - ba / 3f;
                if (MathF.Abs(root1 - center) < threshold)
                    return RangeFInternal(root1, 0f, 1f);

                float root2 = -u1 - ba / 3f;
                return RangeFInternal(root2, 0f, 1f);
            }

            float sd = MathF.Sqrt(discriminant);
            float uc = MathF.Cbrt(sd - q2);
            float vc = MathF.Cbrt(sd + q2);
            return RangeFInternal(uc - vc - ba / 3f, 0f, 1f);
        }

        /// <summary>Порт CubismMath::QuadraticEquation — решение вырожденного (a ≈ 0) случая.</summary>
        private static float QuadraticEquationInternal(float a, float b, float c)
        {
            if (MathF.Abs(a) < MathEpsilon)
            {
                if (MathF.Abs(b) < MathEpsilon)
                    return -c;
                return -c / b;
            }

            return -(b + MathF.Sqrt(b * b - 4f * a * c)) / (2f * a);
        }

        /// <summary>Порт CubismMath::RangeF — зажим значения в [min, max].</summary>
        private static float RangeFInternal(float value, float min, float max)
        {
            if (value < min) value = min;
            else if (value > max) value = max;
            return value;
        }

        /// <summary>Порт зажима model->SetParameterValue: значение параметра в границах [min, max].</summary>
        private static float ClampParameterValue(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static CubismMotionCurveTarget ParseTarget(string target) => target switch
        {
            "Model" => CubismMotionCurveTarget.Model,
            "Parameter" => CubismMotionCurveTarget.Parameter,
            "PartOpacity" => CubismMotionCurveTarget.PartOpacity,
            _ => CubismMotionCurveTarget.Parameter
        };

        private void ParseEvents()
        {
            for (int i = 0; i < _motionJson.GetEventCount(); i++)
            {
                _events.Add(new CubismMotionEvent
                {
                    FireTime = _motionJson.GetEventTime(i),
                    Value = _motionJson.GetEventValue(i)
                });
            }
        }
    }

    /// <summary>
    /// Internal data for a single parsed motion curve. Хранит тип цели, ID,
    /// диапазон сегментов в общем плоском массиве (BaseSegmentIndex/SegmentCount)
    /// и пер-кривые времена фейда; значения &lt; 0 означают «не задано» (как в SDK).
    /// </summary>
    internal class MotionCurveData
    {
        public CubismMotionCurveTarget Type;
        public string Id;
        public int SegmentCount;
        public int BaseSegmentIndex;
        public float FadeInTime;
        public float FadeOutTime;
    }
}
