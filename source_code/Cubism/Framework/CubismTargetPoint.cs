using System;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Smooth target-point tracking for face/eye movement parameters.
    /// Drives the ParamAngleX/Y and ParamEyeBallX/Y model parameters in
    /// <see cref="CubismModelNode"/>.
    ///
    /// Bit-exact port of Cubism SDK for Native 5-r.5 Math/CubismTargetPoint.cpp:
    /// a velocity-based follower that accelerates toward the target with a
    /// capped acceleration (reaching top speed in 0.15 s), then smoothly
    /// decelerates so the configured acceleration can still brake before the
    /// target. All time is expressed in internal 30 fps frame units.
    /// </summary>
    public class CubismTargetPoint
    {
        /// <summary>Internal reference frame rate (SDK FrameRate).</summary>
        private const int FrameRate = 30;

        /// <summary>Threshold below which no movement is applied (SDK Epsilon).</summary>
        private const float Epsilon = 0.01f;

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
        /// Advances the smoothing simulation by one frame (SDK CubismTargetPoint::Update).
        /// The very first call only anchors the internal clock and performs no
        /// integration, matching the native implementation.
        /// </summary>
        public void Update(float deltaTimeSeconds)
        {
            // Add the delta time.
            _userTimeSeconds += deltaTimeSeconds;

            // The average speed of swinging the head from center to side is about 1 second.
            // Considering acceleration/deceleration, twice that is the maximum speed.
            // The face direction is 0.0 at center and +-1.0 at the sides.
            const float FaceParamMaxV = 40f / 10f;                          // Moves 40 in 7.5 seconds (5.3/sc)
            float MaxV = FaceParamMaxV * 1f / FrameRate;                    // Upper speed limit per frame

            // First call only anchors the clock; no integration (SDK behavior).
            if (_lastTimeSeconds == 0f)
            {
                _lastTimeSeconds = _userTimeSeconds;
                return;
            }

            float deltaTimeWeight = (_userTimeSeconds - _lastTimeSeconds) * FrameRate;
            _lastTimeSeconds = _userTimeSeconds;

            // Time until maximum speed, expressed in internal frames.
            const float TimeToMaxSpeed = 0.15f;
            const float FrameToMaxSpeed = TimeToMaxSpeed * FrameRate;       // sec * frame/sec
            float MaxA = deltaTimeWeight * MaxV / FrameToMaxSpeed;          // Acceleration per frame

            // The target direction is the vector (dx, dy).
            float dx = _faceTargetX - _faceX;
            float dy = _faceTargetY - _faceY;

            if (MathF.Abs(dx) <= Epsilon && MathF.Abs(dy) <= Epsilon)
            {
                return; // No change.
            }

            // Slow down when the distance requires more than the maximum speed.
            float d = MathF.Sqrt((dx * dx) + (dy * dy));

            // Maximum speed vector toward the target.
            float vx = MaxV * dx / d;
            float vy = MaxV * dy / d;

            // Acceleration needed to change from the current velocity to the new one.
            float ax = vx - _faceVX;
            float ay = vy - _faceVY;

            float a = MathF.Sqrt((ax * ax) + (ay * ay));

            // While accelerating.
            if (a < -MaxA || a > MaxA)
            {
                ax *= MaxA / a;
                ay *= MaxA / a;
            }

            // Add the acceleration to the original velocity to get the new velocity.
            _faceVX += ax;
            _faceVY += ay;

            // When approaching the target direction, decelerate smoothly:
            // from the relation between acceleration, speed and distance, compute
            // the maximum speed that can still be stopped with the configured
            // acceleration, and clamp the current speed to it.
            {
                // Relation between acceleration, velocity and distance.
                //            2  6           2               3
                //      sqrt(a  t  + 16 a h t  - 8 a h) - a t
                // v = --------------------------------------
                //                    2
                //                 4 t  - 2
                // (t=1)
                float maxV = 0.5f * (MathF.Sqrt((MaxA * MaxA) + 16f * MaxA * d - 8f * MaxA * d) - MaxA);
                float curV = MathF.Sqrt((_faceVX * _faceVX) + (_faceVY * _faceVY));

                if (curV > maxV)
                {
                    // Current speed > maximum speed: decelerate to the maximum.
                    _faceVX *= maxV / curV;
                    _faceVY *= maxV / curV;
                }
            }

            _faceX += _faceVX;
            _faceY += _faceVY;
        }
    }
}
