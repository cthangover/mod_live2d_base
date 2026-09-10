using System;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Smooth target-point tracking for face/eye movement parameters.
    /// Drives the ParamAngleX/Y and ParamEyeBallX/Y model parameters in
    /// <see cref="CubismModelNode"/>.
    /// 
    /// Uses a velocity-based smoothing approach: each update computes a
    /// desired velocity toward the target, applies acceleration limits,
    /// enforces a maximum safe approach speed to prevent overshoot near
    /// the target, and integrates velocity into position.
    /// 
    /// The tracking is frame-rate independent — delta time is scaled by
    /// a factor of 30 (matching Cubism's internal 30fps reference).
    /// Acceleration and max velocity are clamped per Cubism SDK conventions
    /// (FaceParamMaxV = 40, FrameToMaxSpeed = 4.5).
    /// </summary>
    public class CubismTargetPoint
    {
        private const float FaceParamMaxV = 40f;
        private const float FrameToMaxSpeed = 4.5f;

        private float _faceTargetX, _faceTargetY;
        private float _faceX, _faceY;
        private float _faceVX, _faceVY;
        private float _lastTimeSeconds;
        private float _userTimeSeconds;

        /// <summary>Current smoothed X position.</summary>
        public float X => _faceX;

        /// <summary>Current smoothed Y position.</summary>
        public float Y => _faceY;

        /// <summary>
        /// Sets the target point immediately. The actual position
        /// will smoothly approach this target over subsequent calls to <see cref="Update"/>.
        /// </summary>
        public void Set(float x, float y)
        {
            _faceTargetX = x;
            _faceTargetY = y;
        }

        /// <summary>
        /// Advances the smoothing simulation by one frame.
        /// Computes delta-time-weighted velocity toward target, applies
        /// acceleration clamping and safe approach speed limiting, then
        /// integrates position.
        /// 
        /// Called each frame from <see cref="CubismModelNode.OnUpdate"/>
        /// before injecting face-tracking parameters.
        /// </summary>
        public void Update(float deltaTimeSeconds)
        {
            _userTimeSeconds += deltaTimeSeconds;
            var deltaTimeWeight = (_userTimeSeconds - _lastTimeSeconds) * 30f;
            _lastTimeSeconds = _userTimeSeconds;

            if (deltaTimeWeight <= 0f) return;

            var maxV = FaceParamMaxV / 30f;
            var maxA = deltaTimeWeight * maxV / FrameToMaxSpeed;

            var dx = _faceTargetX - _faceX;
            var dy = _faceTargetY - _faceY;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance < 0.01f) return;

            var vx = maxV * dx / distance;
            var vy = maxV * dy / distance;

            var ax = vx - _faceVX;
            var ay = vy - _faceVY;
            var aLen = MathF.Sqrt(ax * ax + ay * ay);
            if (aLen > 0f)
            {
                var clampedA = MathF.Min(aLen, maxA);
                ax = ax * clampedA / aLen;
                ay = ay * clampedA / aLen;
            }

            var maxSafeV = 0.5f * (MathF.Sqrt(maxA * maxA + 8f * maxA * distance) - maxA);
            var vLen = MathF.Sqrt(_faceVX * _faceVX + _faceVY * _faceVY);
            if (vLen > maxSafeV && vLen > 0f)
            {
                var scale = maxSafeV / vLen;
                _faceVX *= scale;
                _faceVY *= scale;
            }

            _faceVX += ax;
            _faceVY += ay;
            _faceX += _faceVX;
            _faceY += _faceVY;
        }
    }
}
