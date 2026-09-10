using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Drives a state-machine based eye blink animation on target model parameters.
    /// Cycles through five states: First -> Interval -> Closing -> Closed -> Opening,
    /// modulating the specified parameter values from 1 (open) to 0 (closed) and back.
    /// 
    /// Blink timing is randomized per cycle: each interval between blinks is a random
    /// value in <c>(0, 2 * BlinkIntervalSeconds)</c>. The actual blink consists of
    /// configurable closing, closed, and opening phase durations.
    /// 
    /// Used by <see cref="CubismModelNode"/> when <see cref="CubismModelNode.EnableEyeBlink"/>
    /// is true and the model3.json defines EyeBlink parameter group.
    /// </summary>
    public class CubismEyeBlink
    {
        private enum EyeState { First, Interval, Closing, Closed, Opening }

        private EyeState _state = EyeState.First;
        private float _nextBlinkTime;
        private float _stateStartTime;
        private float _userTimeSeconds;

        private readonly List<string> _parameterIds = new();

        /// <summary>
        /// Average interval between successive blinks, in seconds.
        /// Actual interval is randomized in range (0, 2 * this value).
        /// Default is 4 seconds.
        /// </summary>
        public float BlinkIntervalSeconds { get; set; } = 4f;

        /// <summary>
        /// Duration of the eyelid closing transition, in seconds. Default 0.1.
        /// </summary>
        public float ClosingSeconds { get; set; } = 0.1f;

        /// <summary>
        /// Duration the eyes stay fully closed, in seconds. Default 0.05.
        /// </summary>
        public float ClosedSeconds { get; set; } = 0.05f;

        /// <summary>
        /// Duration of the eyelid opening transition, in seconds. Default 0.15.
        /// </summary>
        public float OpeningSeconds { get; set; } = 0.15f;

        /// <summary>
        /// Sets the parameter IDs that represent eye open/close.
        /// Typically sourced from the model3.json EyeBlink group.
        /// Clears any previously set IDs.
        /// </summary>
        public void SetParameterIds(IEnumerable<string> ids)
        {
            _parameterIds.Clear();
            _parameterIds.AddRange(ids);
        }

        /// <summary>
        /// Advances the blink state machine and writes eye openness to model parameters.
        /// 1.0 = fully open, 0.0 = fully closed. Linear interpolation between phases.
        /// Call every frame from <see cref="CubismModelNode.OnUpdate"/>.
        /// </summary>
        public void UpdateParameters(CubismNativeModel model, float deltaTimeSeconds)
        {
            _userTimeSeconds += deltaTimeSeconds;

            switch (_state)
            {
                case EyeState.First:
                    _state = EyeState.Interval;
                    _nextBlinkTime = _userTimeSeconds + Random.Shared.NextSingle() * (2f * BlinkIntervalSeconds - 1f);
                    break;

                case EyeState.Interval:
                    SetParameterValues(model, 1f);
                    if (_userTimeSeconds >= _nextBlinkTime)
                    {
                        _state = EyeState.Closing;
                        _stateStartTime = _userTimeSeconds;
                    }
                    break;

                case EyeState.Closing:
                {
                    var elapsed = _userTimeSeconds - _stateStartTime;
                    var value = 1f - Math.Clamp(elapsed / ClosingSeconds, 0f, 1f);
                    SetParameterValues(model, value);
                    if (elapsed >= ClosingSeconds)
                    {
                        _state = EyeState.Closed;
                        _stateStartTime = _userTimeSeconds;
                    }
                    break;
                }

                case EyeState.Closed:
                    SetParameterValues(model, 0f);
                    if (_userTimeSeconds - _stateStartTime >= ClosedSeconds)
                    {
                        _state = EyeState.Opening;
                        _stateStartTime = _userTimeSeconds;
                    }
                    break;

                case EyeState.Opening:
                {
                    var elapsed = _userTimeSeconds - _stateStartTime;
                    var value = Math.Clamp(elapsed / OpeningSeconds, 0f, 1f);
                    SetParameterValues(model, value);
                    if (elapsed >= OpeningSeconds)
                    {
                        _state = EyeState.Interval;
                        _nextBlinkTime = _userTimeSeconds + Random.Shared.NextSingle() * (2f * BlinkIntervalSeconds - 1f);
                    }
                    break;
                }
            }
        }

        private unsafe void SetParameterValues(CubismNativeModel model, float value)
        {
            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();
            for (int i = 0; i < model.ParameterCount; i++)
            {
                var id = CubismNativeModel.ReadStringFromPtrArray(idsPtr, i);
                if (_parameterIds.Contains(id))
                    values[i] = value;
            }
        }
    }
}
