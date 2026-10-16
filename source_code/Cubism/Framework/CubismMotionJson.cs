using System.Collections.Generic;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// JSON parser for the Cubism .motion3.json format.
    /// Provides typed access to the Meta section (duration, loop, fps, fade times,
    /// curve statistics, beziers-restricted flag) and the Curves/UserData sections.
    /// 
    /// Parsed curve segments are returned as flat float arrays (interleaved
    /// segment-type markers and point data), decoded by CubismMotion.ParseSegmentsInternal.
    /// </summary>
    public class CubismMotionJson
    {
        private readonly JsonDocument _json;
        private readonly JsonElement _meta;
        private readonly JsonElement _curves;
        private readonly JsonElement _userData;
        private readonly bool _hasUserData;
        private readonly bool _isLoop;
        private readonly float _duration;
        private readonly float _fps;
        private readonly int _curveCount;
        private readonly int _totalSegmentCount;
        private readonly int _totalPointCount;
        private readonly bool _areBeziersRestricted;

        /// <summary>
        /// Parses the given .motion3.json bytes and pre-extracts all Meta fields
        /// into readonly fields for fast repeated access.
        /// </summary>
        public CubismMotionJson(byte[] jsonBytes)
        {
            _json = JsonDocument.Parse(jsonBytes);
            var root = _json.RootElement;
            _meta = root.GetProperty("Meta");
            _curves = root.GetProperty("Curves");
            _hasUserData = root.TryGetProperty("UserData", out _userData);

            _duration = _meta.GetProperty("Duration").GetSingle();
            _isLoop = _meta.TryGetProperty("Loop", out var loop) && loop.GetBoolean();
            _fps = _meta.TryGetProperty("Fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number ? fpsEl.GetSingle() : 30f;
            _curveCount = _meta.TryGetProperty("CurveCount", out var ccEl) ? ccEl.GetInt32() : 0;
            _totalSegmentCount = _meta.TryGetProperty("TotalSegmentCount", out var tscEl) ? tscEl.GetInt32() : 0;
            _totalPointCount = _meta.TryGetProperty("TotalPointCount", out var tpcEl) ? tpcEl.GetInt32() : 0;
            _areBeziersRestricted = _meta.TryGetProperty("AreBeziersRestricted", out var abrEl) && abrEl.GetBoolean();
        }

        /// <summary>Total motion duration in seconds from the Meta section.</summary>
        public float GetDuration() => _duration;

        /// <summary>Whether the motion loops.</summary>
        public bool IsLoop() => _isLoop;

        /// <summary>Frames per second, default 30.</summary>
        public float GetFps() => _fps;

        /// <summary>Number of curves in this motion.</summary>
        public int GetCurveCount() => _curveCount;

        /// <summary>Total segment count across all curves.</summary>
        public int GetTotalSegmentCount() => _totalSegmentCount;

        /// <summary>Total point count across all curves.</summary>
        public int GetTotalPointCount() => _totalPointCount;

        /// <summary>
        /// If true, bezier curves use binary search instead of the Cardano formula
        /// (non-standard bezier parameterization).
        /// </summary>
        public bool AreBeziersRestricted() => _areBeziersRestricted;

        /// <summary>
        /// Motion-level fade-in time in seconds, or -1 if not specified.
        /// </summary>
        public float GetFadeInTime() =>
            _meta.TryGetProperty("FadeInTime", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : -1f;

        /// <summary>
        /// Motion-level fade-out time in seconds, or -1 if not specified.
        /// </summary>
        public float GetFadeOutTime() =>
            _meta.TryGetProperty("FadeOutTime", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : -1f;

        /// <summary>
        /// Returns the target string for the curve at the given index
        /// ("Model", "Parameter", or "PartOpacity").
        /// </summary>
        public string GetCurveTarget(int index) =>
            _curves[index].GetProperty("Target").GetString();

        /// <summary>
        /// Returns the parameter/part/model ID string for the curve at the given index.
        /// </summary>
        public string GetCurveId(int index) =>
            _curves[index].GetProperty("Id").GetString();

        /// <summary>
        /// Per-curve fade-in time in seconds, or -1 if not specified.
        /// </summary>
        public float GetCurveFadeInTime(int index) =>
            _curves[index].TryGetProperty("FadeInTime", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : -1f;

        /// <summary>
        /// Per-curve fade-out time in seconds, or -1 if not specified.
        /// </summary>
        public float GetCurveFadeOutTime(int index) =>
            _curves[index].TryGetProperty("FadeOutTime", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : -1f;

        /// <summary>
        /// Reads the raw float array of segment data for a curve.
        /// Формат Segments из motion3.json: [t0, v0, type, t, v, ...] — первые ДВА
        /// элемента это стартовая точка кривой (время, значение), далее для каждого
        /// сегмента маркер типа и его точки (Linear/Stepped/InverseStepped — 1 точка,
        /// Bezier — 3). Разбор: <see cref="CubismMotion.ParseSegmentsInternal"/>.
        /// </summary>
        public void GetCurveSegments(int index, out float[] segments)
        {
            var segArr = _curves[index].GetProperty("Segments");
            segments = new float[segArr.GetArrayLength()];
            int si = 0;
            foreach (var s in segArr.EnumerateArray())
                segments[si++] = s.GetSingle();
        }

        /// <summary>Number of user-data events in the motion.</summary>
        public int GetEventCount() => _hasUserData ? _userData.GetArrayLength() : 0;

        /// <summary>Fire time of the event at the given index, in seconds.</summary>
        public float GetEventTime(int index) =>
            _hasUserData && index < _userData.GetArrayLength()
                ? _userData[index].GetProperty("Time").GetSingle() : 0f;

        /// <summary>String value of the event at the given index.</summary>
        public string GetEventValue(int index) =>
            _hasUserData && index < _userData.GetArrayLength()
                ? _userData[index].GetProperty("Value").GetString() : null;
    }
}
