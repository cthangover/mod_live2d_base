using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    public delegate float MotionSegmentFunction(float[] segments, int baseIndex, float time);

    /// <summary>
    /// Parses and plays a Cubism .motion3.json animation.
    /// Manages three categories of curves:
    /// <list type="bullet">
    ///   <item><b>Model curves</b> — eye blink, lip sync, and opacity</item>
    ///   <item><b>Parameter curves</b> — arbitrary model parameter animations</item>
    ///   <item><b>Part opacity curves</b> — visibility/opacity of model parts</item>
    /// </list>
    /// 
    /// Each curve is a sequence of segments (linear, stepped, bezier) stored in
    /// flat arrays for performance. Evaluation uses a segment-finding pass
    /// followed by per-segment interpolation — linear, stepped, or bezier
    /// (analytic via Cardano formula or binary search for restricted beziers).
    /// 
    /// The four-pass update cycle (<see cref="DoUpdateParameters"/>):
    /// <list type="number">
    ///   <item>Evaluate model curves (eye blink, lip sync, opacity)</item>
    ///   <item>Evaluate parameter curves, applying eye blink/lip sync modulation</item>
    ///   <item>Apply eye blink/lip sync to remaining (non-animated) parameters</item>
    ///   <item>Evaluate part opacity curves</item>
    /// </list>
    /// 
    /// Parameter fade-in/out is handled per-curve (overriding the motion-level
    /// fade settings). Curve targets and IDs come from the motion3.json metadata.
    /// </summary>
    public class CubismMotion : ACubismMotion
    {
        private const string ModelCurveIdEyeBlink = "EyeBlink";
        private const string ModelCurveIdLipSync = "LipSync";
        private const string ModelCurveIdOpacity = "Opacity";

        private readonly float _sourceFrameRate;
        private float _lastWeight;
        private CubismMotionJson _motionJson;

        private readonly List<MotionCurveData> _modelCurves = new();
        private readonly List<MotionCurveData> _parameterCurves = new();
        private readonly List<MotionCurveData> _partOpacityCurves = new();

        private readonly List<string> _eyeBlinkParameterIds = new();
        private readonly List<string> _lipSyncParameterIds = new();

        private readonly List<CubismMotionEvent> _events = new();
        private readonly List<string> _firedEventValues = new();

        private float _modelOpacity = 1f;
        private bool _areBeziersRestricted;

        /// <summary>
        /// Parses a .motion3.json byte buffer.
        /// Reads the meta section (duration, loop, fps, fade times, bezier flag)
        /// and delegates curve/event parsing to private helpers.
        /// </summary>
        public CubismMotion(byte[] motion3JsonBytes)
        {
            _motionJson = new CubismMotionJson(motion3JsonBytes);
            _sourceFrameRate = _motionJson.GetFps();
            _areBeziersRestricted = _motionJson.AreBeziersRestricted();
            _isLoop = _motionJson.IsLoop();

            if (_motionJson.GetFadeInTime() >= 0f) _fadeInSeconds = _motionJson.GetFadeInTime();
            if (_motionJson.GetFadeOutTime() >= 0f) _fadeOutSeconds = _motionJson.GetFadeOutTime();

            ParseCurves();
            ParseEvents();
        }

        /// <summary>
        /// Configures which parameters are affected by eye blink and lip sync
        /// modulation. These lists typically come from the model3.json Groups.
        /// When a parameter curve targets one of these IDs, its value is
        /// multiplied by the eye blink curve value (for blink) or added to
        /// the lip sync curve value (for lip sync) in Pass 2.
        /// </summary>
        public void SetEffectIds(List<string> eyeBlinkIds, List<string> lipSyncIds)
        {
            _eyeBlinkParameterIds.Clear();
            _lipSyncParameterIds.Clear();
            if (eyeBlinkIds != null) _eyeBlinkParameterIds.AddRange(eyeBlinkIds);
            if (lipSyncIds != null) _lipSyncParameterIds.AddRange(lipSyncIds);
        }

        /// <summary>
        /// Returns the total duration from the motion3.json, or -1 for looping motions.
        /// </summary>
        public override float GetDuration() => _isLoop ? -1f : _motionJson.GetDuration();

        /// <summary>
        /// Returns the full duration even when looping is active — used for computing
        /// loop cycle length.
        /// </summary>
        public override float GetLoopDuration() => _motionJson.GetDuration();

        /// <summary>Whether the model's overall opacity is actively controlled by this motion.</summary>
        public bool IsExistModelOpacity() => _modelOpacity > 0f;

        /// <summary>Current model opacity value from the Opacity model curve, clamped [0,1].</summary>
        public float GetModelOpacity() => _modelOpacity;

        /// <summary>
        /// Collects event values that fired between two time points.
        /// Clears the internal fired-event list on each call. (Currently returns 0f;
        /// the fired values list is populated as a side effect.)
        /// </summary>
        public float GetFiredEventValue(float beforeTime, float motionTime)
        {
            _firedEventValues.Clear();
            foreach (var evt in _events)
                if (evt.FireTime > beforeTime && evt.FireTime <= motionTime)
                    _firedEventValues.Add(evt.Value);
            return 0f;
        }

        /// <summary>
        /// Four-pass parameter update. Called by <see cref="CubismMotionQueueManager"/>
        /// every frame.
        /// </summary>
        public override unsafe void DoUpdateParameters(
            CubismNativeModel model, float userTimeSeconds, float weight, CubismMotionQueueEntry entry)
        {
            if (model == null || entry == null) return;

            AdjustEndTime(entry, _motionJson.GetDuration());

            var timeOffset = userTimeSeconds - entry.StartTime;
            var duration = _motionJson.GetDuration();
            if (_isLoop && duration > 0f)
            {
                while (timeOffset > duration)
                    timeOffset -= duration;
            }

            var fadeWeight = weight;

            int paramCount = model.ParameterCount;
            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();
            var minValues = model.GetParameterMinimumValues();
            var maxValues = model.GetParameterMaximumValues();
            var defaultValues = model.GetParameterDefaultValues();

            float eyeBlinkValue = 0f;
            float lipSyncValue = 0f;
            _modelOpacity = 1f;
            var modifiedFlags = new bool[paramCount];

            // Pass 1 — model curves
            foreach (var curve in _modelCurves)
            {
                var val = EvaluateCurve(curve, timeOffset, fadeWeight);
                if (curve.Id == ModelCurveIdEyeBlink) eyeBlinkValue = val;
                else if (curve.Id == ModelCurveIdLipSync) lipSyncValue = val;
                else if (curve.Id == ModelCurveIdOpacity) _modelOpacity = Math.Clamp(val, 0f, 1f);
            }

            // Pass 2 — parameter curves
            foreach (var curve in _parameterCurves)
            {
                var curveValue = EvaluateCurve(curve, timeOffset, fadeWeight);

                for (int pi = 0; pi < paramCount; pi++)
                {
                    var pid = CubismNativeModel.ReadStringFromPtrArray(idsPtr, pi);
                    if (pid != curve.Id) continue;

                    var modulatedValue = curveValue;
                    if (_eyeBlinkParameterIds.Contains(pid))
                        modulatedValue *= eyeBlinkValue;
                    if (_lipSyncParameterIds.Contains(pid))
                        modulatedValue += lipSyncValue;

                    var paramFadeWeight = fadeWeight;
                    if (curve.ParamFadeIn >= 0f || curve.ParamFadeOut >= 0f)
                    {
                        var fi = curve.ParamFadeIn >= 0f ? curve.ParamFadeIn : 1f;
                        var fo = curve.ParamFadeOut >= 0f ? curve.ParamFadeOut : 1f;
                        var paramFadeIn = CubismMath.GetEasingSine(
                            (userTimeSeconds - entry.FadeInStartTime) / fi);
                        var paramFadeOut = CubismMath.GetEasingSine(
                            (entry.EndTime > 0f ? entry.EndTime - userTimeSeconds : 1f) / fo);
                        paramFadeWeight = _weight * paramFadeIn * paramFadeOut;
                    }

                    values[pi] = values[pi] + (modulatedValue - values[pi]) * paramFadeWeight;
                    ClampParameter(pi, values, minValues, maxValues);
                    modifiedFlags[pi] = true;
                    break;
                }
            }

            // Pass 3 — remaining eye blink / lip sync (non-modulated params)
            for (int pi = 0; pi < paramCount; pi++)
            {
                if (modifiedFlags[pi]) continue;
                var pid = CubismNativeModel.ReadStringFromPtrArray(idsPtr, pi);
                if (_eyeBlinkParameterIds.Contains(pid))
                    values[pi] = eyeBlinkValue > 0f ? values[pi] * eyeBlinkValue : values[pi];
                if (_lipSyncParameterIds.Contains(pid))
                    values[pi] += lipSyncValue;
            }

            // Pass 4 — part opacity curves
            var partOpacities = model.GetPartOpacities();
            var partIdsPtr = model.GetPartIds();
            int partCount = model.GetPartCount();
            foreach (var curve in _partOpacityCurves)
            {
                var val = EvaluateCurve(curve, timeOffset, fadeWeight);
                for (int pi = 0; pi < partCount; pi++)
                {
                    var pid = CubismNativeModel.ReadStringFromPtrArray(partIdsPtr, pi);
                    if (pid == curve.Id)
                    {
                        partOpacities[pi] = val;
                        break;
                    }
                }
            }

            // End-of-motion
            if (!_isLoop && timeOffset >= duration)
            {
                entry.IsFinished = true;
                OnFinishedMotion?.Invoke(this);
            }
            else if (_isLoop && timeOffset >= duration)
            {
                entry.StartTime = userTimeSeconds - (timeOffset - duration);
                if (!_isLoopFadeIn)
                    entry.FadeInStartTime = userTimeSeconds;
            }
        }

        private float EvaluateCurve(MotionCurveData curve, float time, float weight)
        {
            var segIdx = FindSegmentIndex(curve, time);
            if (segIdx < 0 || segIdx >= curve.SegmentCount) return 0f;
            return EvaluateSegment(curve, segIdx, time);
        }

        private int FindSegmentIndex(MotionCurveData curve, float time)
        {
            for (int i = 0; i < curve.SegmentCount; i++)
            {
                var nextIdx = curve.BaseSegmentIndex + i + 1;
                if (nextIdx < curve.Points.Length)
                {
                    var nextPoint = curve.Points[nextIdx];
                    if (time < nextPoint.Time)
                        return i;
                }
            }
            var lastPt = curve.Points[curve.BaseSegmentIndex + curve.SegmentCount];
            if (time <= lastPt.Time)
                return curve.SegmentCount - 1;
            return -1;
        }

        private float EvaluateSegment(MotionCurveData curve, int segIndex, float time)
        {
            var basePtIdx = curve.BaseSegmentIndex + segIndex;
            var p0 = curve.Points[basePtIdx];
            var p1 = curve.Points[basePtIdx + 1];

            var segType = curve.SegmentTypes != null && segIndex < curve.SegmentTypes.Length
                ? curve.SegmentTypes[segIndex] : CubismMotionSegmentType.Linear;

            switch (segType)
            {
                case CubismMotionSegmentType.Linear:
                    return CubismMath.LinearEvaluation(ref p0, ref p1, time);

                case CubismMotionSegmentType.Stepped:
                    return p0.Value;

                case CubismMotionSegmentType.InverseStepped:
                    return p1.Value;

                case CubismMotionSegmentType.Bezier:
                    var cp1 = curve.Points[basePtIdx + 1];
                    var cp2 = curve.Points[basePtIdx + 2];
                    var cp3 = curve.Points[basePtIdx + 3];

                    if (_areBeziersRestricted)
                        return CubismMath.BezierEvaluateBinarySearch(
                            p0.Time, cp1.Time, cp2.Time, cp3.Time,
                            p0.Value, cp1.Value, cp2.Value, cp3.Value, time);
                    else
                    {
                        var t = CubismMath.CardanoAlgorithmForBezier(
                            p0.Time, cp1.Time, cp2.Time, cp3.Time, time);
                        t = Math.Clamp(t, 0f, 1f);
                        var t1 = 1f - t;
                        var t2 = t * t;
                        var t12 = t1 * t1;
                        return t12 * t1 * p0.Value + 3f * t12 * t * cp1.Value +
                               3f * t1 * t2 * cp2.Value + t2 * t * cp3.Value;
                    }

                default:
                    return CubismMath.LinearEvaluation(ref p0, ref p1, time);
            }
        }

        private static unsafe void ClampParameter(int index, float* values, float* min, float* max)
        {
            if (values[index] < min[index]) values[index] = min[index];
            if (values[index] > max[index]) values[index] = max[index];
        }

        private void ParseCurves()
        {
            var segsFlat = new float[_motionJson.GetTotalSegmentCount() * 2];
            var segTypesFlat = new CubismMotionSegmentType[_motionJson.GetTotalSegmentCount()];
            var allPoints = new List<CubismMotionPoint>();
            int segOffset = 0;

            for (int ci = 0; ci < _motionJson.GetCurveCount(); ci++)
            {
                var target = _motionJson.GetCurveTarget(ci);
                var id = _motionJson.GetCurveId(ci);
                var fadeIn = _motionJson.GetCurveFadeInTime(ci);
                var fadeOut = _motionJson.GetCurveFadeOutTime(ci);
                _motionJson.GetCurveSegments(ci, out var rawSegments);

                var segmentCount = ParseSegments(rawSegments, segsFlat, segTypesFlat, segOffset, allPoints);
                var curveTarget = ParseTarget(target);

                var curveData = new MotionCurveData
                {
                    Type = curveTarget,
                    Id = id,
                    SegmentCount = segmentCount,
                    BaseSegmentIndex = segOffset,
                    ParamFadeIn = fadeIn,
                    ParamFadeOut = fadeOut,
                    Points = allPoints.ToArray(),
                    SegmentTypes = segTypesFlat
                };

                segOffset += segmentCount;

                switch (curveTarget)
                {
                    case CubismMotionCurveTarget.Model:
                        _modelCurves.Add(curveData); break;
                    case CubismMotionCurveTarget.Parameter:
                        _parameterCurves.Add(curveData); break;
                    case CubismMotionCurveTarget.PartOpacity:
                        _partOpacityCurves.Add(curveData); break;
                }
            }
        }

        private int ParseSegments(float[] raw, float[] segsFlat, CubismMotionSegmentType[] segTypesFlat,
            int segOffset, List<CubismMotionPoint> points)
        {
            int count = 0;
            int i = 0;
            while (i < raw.Length)
            {
                var segType = (CubismMotionSegmentType)(int)raw[i++];

                if (count == 0)
                {
                    // First "segment" is actually the starting point
                    points.Add(new CubismMotionPoint { Time = raw[i++], Value = raw[i++] });
                    continue;
                }

                segTypesFlat[segOffset + count - 1] = segType;

                switch (segType)
                {
                    case CubismMotionSegmentType.Linear:
                    case CubismMotionSegmentType.Stepped:
                    case CubismMotionSegmentType.InverseStepped:
                        points.Add(new CubismMotionPoint { Time = raw[i++], Value = raw[i++] });
                        break;

                    case CubismMotionSegmentType.Bezier:
                        points.Add(new CubismMotionPoint { Time = raw[i++], Value = raw[i++] });
                        points.Add(new CubismMotionPoint { Time = raw[i++], Value = raw[i++] });
                        points.Add(new CubismMotionPoint { Time = raw[i++], Value = raw[i++] });
                        break;
                }

                count++;
            }

            return count - 1;
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
    /// Internal data for a single parsed motion curve.
    /// Holds the curve type, target ID, segment layout, points array,
    /// and optional per-curve fade-in/fade-out durations.
    /// </summary>
    internal class MotionCurveData
    {
        public CubismMotionCurveTarget Type;
        public string Id;
        public int SegmentCount;
        public int BaseSegmentIndex;
        public float ParamFadeIn;
        public float ParamFadeOut;
        public CubismMotionPoint[] Points;
        public CubismMotionSegmentType[] SegmentTypes;
    }
}
