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
    /// Формулы применения — точный порт CubismExpressionMotion::DoUpdateParameters
    /// через Set/Add/MultiplyParameterValue из CubismModel (SDK 5-r.5), weight — fade-вес:
    /// <list type="bullet">
    ///   <item><see cref="ExpressionBlendType.Additive"/> — <c>value + expValue * weight</c></item>
    ///   <item><see cref="ExpressionBlendType.Multiply"/> — <c>value * (1 + (expValue - 1) * weight)</c></item>
    ///   <item><see cref="ExpressionBlendType.Overwrite"/> — <c>value * (1 - weight) + expValue * weight</c></item>
    /// </list>
    ///
    /// Как и в SDK, результат зажимается в [min, max] параметра (порт
    /// CubismModel::SetParameterValue), а параметры применяются в порядке их
    /// перечисления в exp3.json: при нескольких записях с одним Id побеждает последняя.
    /// Fade-время по умолчанию — 1 c (DefaultFadeTime), если в файле не задано.
    ///
    /// Загружается и запускается через <see cref="CubismModelNode.StartExpression"/>.
    /// </summary>
    public class CubismExpressionMotion : ACubismMotion
    {
        /// <summary>Порт DefaultFadeTime из CubismExpressionMotion.cpp.</summary>
        private const float DefaultFadeTime = 1f;

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

            // Порт CubismExpressionMotion::Parse: root[FadeInTime].ToFloat(DefaultFadeTime).
            _fadeInSeconds = root.TryGetProperty("FadeInTime", out var fi) && fi.ValueKind == JsonValueKind.Number ? fi.GetSingle() : DefaultFadeTime;
            _fadeOutSeconds = root.TryGetProperty("FadeOutTime", out var fo) && fo.ValueKind == JsonValueKind.Number ? fo.GetSingle() : DefaultFadeTime;

            if (root.TryGetProperty("Parameters", out var paramsArr))
            {
                foreach (var p in paramsArr.EnumerateArray())
                {
                    // "Blend": null or absent -> Additive (SDK ToBlendFunction default).
                    var blend = p.TryGetProperty("Blend", out var b) && b.ValueKind == JsonValueKind.String
                        ? ParseBlendType(b.GetString())
                        : ExpressionBlendType.Additive;

                    _parameters.Add(new ExpressionParameter
                    {
                        ParameterId = p.GetProperty("Id").GetString(),
                        Value = p.TryGetProperty("Value", out var val) && val.ValueKind == JsonValueKind.Number ? val.GetSingle() : 0f,
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

            var indexById = new Dictionary<string, int>(model.ParameterCount);
            for (int i = 0; i < model.ParameterCount; i++)
                indexById[CubismNativeModel.ReadStringFromPtrArray(idsPtr, i)] = i;

            // Порт: цикл по параметрам выражения (в порядке exp3.json).
            foreach (var ep in _parameters)
            {
                if (!indexById.TryGetValue(ep.ParameterId, out int pi)) continue;

                switch (ep.BlendType)
                {
                    case ExpressionBlendType.Additive:
                        // AddParameterValue: value + (expValue * weight)
                        values[pi] = ClampParameterValue(values[pi] + ep.Value * weight, minValues[pi], maxValues[pi]);
                        break;
                    case ExpressionBlendType.Multiply:
                        // MultiplyParameterValue: value * (1 + (expValue - 1) * weight)
                        values[pi] = ClampParameterValue(values[pi] * (1f + (ep.Value - 1f) * weight), minValues[pi], maxValues[pi]);
                        break;
                    case ExpressionBlendType.Overwrite:
                        // SetParameterValue(id, value, weight): expValue зажимается, затем лерп
                        values[pi] = values[pi] * (1f - weight)
                            + ClampParameterValue(ep.Value, minValues[pi], maxValues[pi]) * weight;
                        break;
                }
            }
        }

        /// <summary>Порт GetParameterClampValue — зажим значения параметра в [min, max].</summary>
        private static float ClampParameterValue(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
