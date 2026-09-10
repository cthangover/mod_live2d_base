using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Defines a sinusoidal breathing parameter. Each breath parameter
    /// oscillates a model parameter value around an offset with a given
    /// peak amplitude, cycle period, and weight.
    /// </summary>
    public struct BreathParameterData
    {
        /// <summary>Model parameter ID to apply the breath to.</summary>
        public string ParameterId;

        /// <summary>Base offset added to the sinusoidal value.</summary>
        public float Offset;

        /// <summary>Amplitude of the sine wave.</summary>
        public float Peak;

        /// <summary>Full cycle duration in seconds.</summary>
        public float Cycle;

        /// <summary>Multiplier applied to the final breath value.</summary>
        public float Weight;
    }

    /// <summary>
    /// Applies sinusoidal breathing animation to model parameters.
    /// Operates additively on top of existing parameter values —
    /// the breath oscillation is multiplied by <see cref="BreathParameterData.Weight"/>
    /// and added to the current parameter value every frame.
    /// 
    /// Configured with a list of <see cref="BreathParameterData"/> entries,
    /// each targeting a specific parameter ID. Called from
    /// <see cref="CubismModelNode.OnUpdate"/> alongside other effect layers
    /// (eye blink, pose, physics).
    /// </summary>
    public class CubismBreath
    {
        private readonly List<BreathParameterData> _parameters = new();
        private float _currentTime;

        /// <summary>
        /// Replaces the current breath parameter set.
        /// Previously configured parameters are cleared.
        /// </summary>
        public void SetParameters(List<BreathParameterData> parameters)
        {
            _parameters.Clear();
            _parameters.AddRange(parameters);
        }

        /// <summary>
        /// Applies the breath oscillation to matching model parameters.
        /// Accumulates internal time and evaluates a sine wave per parameter:
        /// <c>offset + peak * sin(2 * pi * time / cycle)</c>, then adds
        /// the weighted result to the native parameter value.
        /// </summary>
        public unsafe void UpdateParameters(CubismNativeModel model, float deltaTimeSeconds)
        {
            _currentTime += deltaTimeSeconds;

            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();

            for (int i = 0; i < model.ParameterCount; i++)
            {
                var id = CubismNativeModel.ReadStringFromPtrArray(idsPtr, i);
                foreach (var bp in _parameters)
                {
                    if (bp.ParameterId != id) continue;
                    var value = bp.Offset + bp.Peak * MathF.Sin(2f * MathF.PI * _currentTime / bp.Cycle);
                    values[i] += bp.Weight * value;
                    break;
                }
            }
        }
    }
}
