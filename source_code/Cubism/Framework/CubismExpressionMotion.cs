using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Blend mode for expression parameters.
    /// Determines how the expression value is combined with the existing
    /// parameter value during animation.
    /// </summary>
    public enum ExpressionBlendType { Additive = 0, Multiply = 1, Overwrite = 2 }

    /// <summary>
    /// A single parameter entry in an expression motion.
    /// Targets a model parameter by ID with a specific blend type and value.
    /// </summary>
    public struct ExpressionParameter
    {
        /// <summary>Target model parameter ID.</summary>
        public string ParameterId;

        /// <summary>How to combine this value with existing parameter data.</summary>
        public ExpressionBlendType BlendType;

        /// <summary>The target value (interpretation depends on <see cref="BlendType"/>).</summary>
        public float Value;
    }

    /// <summary>
    /// Expression motion, parsed from a Cubism .exp3.json file.
    /// Inherits fade and weight management from <see cref="ACubismMotion"/>.
    /// 
    /// Expressions are single-frame poses that transition between values using
    /// one of three blend modes per parameter:
    /// <list type="bullet">
    ///   <item><see cref="ExpressionBlendType.Additive"/> — adds <c>value * weight</c></item>
    ///   <item><see cref="ExpressionBlendType.Multiply"/> — multiplies by <c>1 + value * weight</c></item>
    ///   <item><see cref="ExpressionBlendType.Overwrite"/> — lerps from current to target using weight</item>
    /// </list>
    /// 
    /// Loaded and started by <see cref="CubismModelNode.StartExpression"/>.
    /// </summary>
    public class CubismExpressionMotion : ACubismMotion
    {
        private readonly List<ExpressionParameter> _parameters = new();

        /// <summary>
        /// Parses an .exp3.json byte buffer into an expression motion.
        /// Reads fade-in/out times and the list of target parameters with
        /// their blend types and values.
        /// </summary>
        public CubismExpressionMotion(byte[] jsonBytes)
        {
            var json = JsonDocument.Parse(jsonBytes);
            var root = json.RootElement;

            if (root.TryGetProperty("FadeInTime", out var fi))
                _fadeInSeconds = fi.GetSingle();
            if (root.TryGetProperty("FadeOutTime", out var fo))
                _fadeOutSeconds = fo.GetSingle();

            if (root.TryGetProperty("Parameters", out var paramsArr))
            {
                foreach (var p in paramsArr.EnumerateArray())
                {
                    var blend = p.TryGetProperty("Blend", out var b)
                        ? ParseBlendType(b.GetString())
                        : ExpressionBlendType.Additive;

                    _parameters.Add(new ExpressionParameter
                    {
                        ParameterId = p.GetProperty("Id").GetString(),
                        Value = p.GetProperty("Value").GetSingle(),
                        BlendType = blend
                    });
                }
            }
        }

        private static ExpressionBlendType ParseBlendType(string value) => value switch
        {
            "Multiply" => ExpressionBlendType.Multiply,
            "Overwrite" => ExpressionBlendType.Overwrite,
            _ => ExpressionBlendType.Additive
        };

        /// <summary>
        /// Applies all expression parameters to the model using their
        /// respective blend modes. Called every frame by the queue manager.
        /// </summary>
        public override unsafe void DoUpdateParameters(
            CubismNativeModel model, float userTimeSeconds, float weight, CubismMotionQueueEntry entry)
        {
            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();
            var minValues = model.GetParameterMinimumValues();
            var maxValues = model.GetParameterMaximumValues();
            var defaultValues = model.GetParameterDefaultValues();

            for (int pi = 0; pi < model.ParameterCount; pi++)
            {
                var pid = CubismNativeModel.ReadStringFromPtrArray(idsPtr, pi);
                foreach (var ep in _parameters)
                {
                    if (ep.ParameterId != pid) continue;

                    switch (ep.BlendType)
                    {
                        case ExpressionBlendType.Additive:
                            values[pi] = values[pi] + ep.Value * weight;
                            break;
                        case ExpressionBlendType.Multiply:
                            values[pi] = values[pi] * (1f + ep.Value * weight);
                            break;
                        case ExpressionBlendType.Overwrite:
                            values[pi] = values[pi] * (1f - weight) + ep.Value * weight;
                            break;
                    }
                    break;
                }
            }
        }
    }
}
