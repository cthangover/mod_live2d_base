using System;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Math utilities shared across the Cubism animation pipeline.
    /// Includes easing functions (sine), angle utilities, linear
    /// interpolation helpers, and bezier curve evaluators with both
    /// analytic (Cardano) and binary search approaches.
    /// 
    /// The bezier methods handle Cubism's segmented animation curves:
    /// <see cref="BezierEvaluate"/> calculates a value at a given time,
    /// <see cref="CardanoAlgorithmForBezier"/> solves for the exact
    /// bezier parameter t, and <see cref="BezierEvaluateBinarySearch"/>
    /// is a fallback when beziers are restricted (non-standard).
    /// </summary>
    public static class CubismMath
    {
        /// <summary>
        /// Sine-based easing function mapping [0,1] -> [0,1] with smooth
        /// acceleration and deceleration. Used for fade-in/fade-out curves
        /// in motion transitions.
        /// <c>0.5 - 0.5 * cos(pi * t)</c>, clamped to [0,1].
        /// </summary>
        public static float GetEasingSine(float t)
        {
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
        }

        /// <summary>Converts degrees to radians.</summary>
        public static float DegreesToRadian(float degrees)
        {
            return degrees * (MathF.PI / 180f);
        }

        /// <summary>Converts radians to degrees.</summary>
        public static float RadianToDegrees(float radian)
        {
            return radian * (180f / MathF.PI);
        }

        /// <summary>
        /// Computes the signed angle in radians from vector (fromX,fromY) to vector (toX,toY).
        /// Uses Atan2(det, dot) for the angle between two vectors.
        /// </summary>
        public static float DirectionToRadian(float fromX, float fromY, float toX, float toY)
        {
            var dot = fromX * toX + fromY * toY;
            var det = fromX * toY - fromY * toX;
            return MathF.Atan2(det, dot);
        }

        /// <summary>
        /// Converts a radian angle to a unit direction vector (sin, cos).
        /// </summary>
        public static void RadianToDirection(float radian, out float x, out float y)
        {
            x = MathF.Sin(radian);
            y = MathF.Cos(radian);
        }

        /// <summary>Standard linear interpolation between two float values.</summary>
        public static float LinearEvaluation(float startValue, float endValue, float time)
        {
            return startValue + (endValue - startValue) * time;
        }

        /// <summary>
        /// Linear interpolation between two <see cref="CubismMotionPoint"/> values,
        /// using their stored Time properties to compute the interpolation factor.
        /// </summary>
        public static float LinearEvaluation(ref CubismMotionPoint p0, ref CubismMotionPoint p1, float time)
        {
            var t = (time - p0.Time) / (p1.Time - p0.Time);
            return p0.Value + (p1.Value - p0.Value) * t;
        }

        /// <summary>
        /// De Casteljau cubic bezier evaluation.
        /// Given 4 control points (cp0..cp3), each with a time and value,
        /// returns the bezier value at the specified time.
        /// </summary>
        public static float BezierEvaluate(
            float cp0Time, float cp0Value,
            float cp1Time, float cp1Value,
            float cp2Time, float cp2Value,
            float cp3Time, float cp3Value,
            float time)
        {
            var t = (time - cp0Time) / (cp3Time - cp0Time);
            var t1 = 1f - t;
            var t2 = t * t;
            var t12 = t1 * t1;

            var value =
                t12 * t1 * cp0Value +
                3f * t12 * t * cp1Value +
                3f * t1 * t2 * cp2Value +
                t2 * t * cp3Value;

            return value;
        }

        /// <summary>
        /// Cardano cubic formula to solve for the exact bezier parameter t
        /// given a target time. Used when beziers are not restricted —
        /// the analytic solution is faster than binary search.
        /// 
        /// Returns the t value that, when applied to the bezier equation,
        /// produces the targetTime (or very close to it).
        /// </summary>
        public static float CardanoAlgorithmForBezier(
            float cp0Time, float cp1Time, float cp2Time, float cp3Time,
            float targetTime)
        {
            var dx = cp0Time - targetTime;
            var cx = cp1Time - cp0Time;
            var bx = cp2Time - cp1Time;
            var ax = cp3Time - cp2Time;
            var a2 = ax - cx;
            var ab = 2f * (ax - 2f * bx + cx);
            var a0 = 3f * (bx - cx);
            var ad = -cx;
            var a = a2 / dx;
            var b = ab / dx;
            var c = a0 / dx;
            var d = ad / dx;
            var r = (-2f * b * b * b + 9f * a * b * c - 27f * a * a * d) / (54f * a * a * a);
            var q = (b * b - 3f * a * c) / (9f * a * a);
            var q3 = q * q * q;
            var floorTerm = r * r - q3;
            if (MathF.Abs(floorTerm) < 1e-7f) floorTerm = 0f;
            if (floorTerm < 0f)
            {
                var theta = MathF.Acos(r / MathF.Sqrt(q3));
                var sqrtQ = MathF.Sqrt(q);
                return -2f * sqrtQ * MathF.Cos(theta / 3f) - b / (3f * a);
            }
            var sqrtTerm = MathF.Sqrt(floorTerm);
            var aTerm = MathF.Cbrt(MathF.Abs(r + sqrtTerm));
            var bTerm = r > 0f
                ? MathF.Cbrt(MathF.Abs(r - sqrtTerm))
                : -MathF.Cbrt(MathF.Abs(r - sqrtTerm));
            return aTerm - bTerm - b / (3f * a);
        }

        /// <summary>
        /// Binary search bezier evaluation — fallback for when
        /// <see cref="CubismMotionJson.AreBeziersRestricted"/> is true.
        /// Iteratively narrows t in [0,1] until the bezier time at t
        /// is within epsilon of the target time, then evaluates the value.
        /// Capped at 20 iterations with 1e-5 tolerance.
        /// </summary>
        public static float BezierEvaluateBinarySearch(
            float cp0Time, float cp1Time, float cp2Time, float cp3Time,
            float cp0Value, float cp1Value, float cp2Value, float cp3Value,
            float targetTime)
        {
            const int maxIterations = 20;
            const float epsilon = 1e-5f;
            var tMin = 0f;
            var tMax = 1f;

            for (var i = 0; i < maxIterations; i++)
            {
                var t = (tMin + tMax) * 0.5f;
                var t1 = 1f - t;
                var t2 = t * t;
                var t12 = t1 * t1;
                var timeAtT =
                    t12 * t1 * cp0Time +
                    3f * t12 * t * cp1Time +
                    3f * t1 * t2 * cp2Time +
                    t2 * t * cp3Time;

                if (MathF.Abs(timeAtT - targetTime) < epsilon)
                    return
                        t12 * t1 * cp0Value +
                        3f * t12 * t * cp1Value +
                        3f * t1 * t2 * cp2Value +
                        t2 * t * cp3Value;

                if (timeAtT < targetTime)
                    tMin = t;
                else
                    tMax = t;
            }

            var tf = (tMin + tMax) * 0.5f;
            var tf1 = 1f - tf;
            var tf2 = tf * tf;
            var tf12 = tf1 * tf1;
            return
                tf12 * tf1 * cp0Value +
                3f * tf12 * tf * cp1Value +
                3f * tf1 * tf2 * cp2Value +
                tf2 * tf * cp3Value;
        }
    }
}
