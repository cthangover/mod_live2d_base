using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Cubism physics simulation engine. Drives secondary motion (hair, cloth,
    /// accessories) using a chain of particles influenced by parameter inputs
    /// (X, Y, angle from model parameters), gravity, and wind.
    /// 
    /// Architecture:
    /// <list type="bullet">
    ///   <item><see cref="CubismPhysicsRig"/> holds the complete rig structure:
    ///       sub-rigs, inputs, outputs, and particles</item>
    ///   <item>Each sub-rig is an independent chain of particles anchored to one
    ///       or more model parameters via inputs</item>
    ///   <item>Inputs map model parameters (normalized and weighted) to particle
    ///       chain root position/angle</item>
    ///   <item>Outputs map particle chain state back to model parameters after
    ///       physics integration</item>
    /// </list>
    /// 
    /// The simulation runs at the rig's configured FPS (from .physics3.json),
    /// using sub-frame accumulation when the display delta time is smaller
    /// than the physics step. Output values are interpolated between steps
    /// for smooth visual results.
    /// </summary>
    public class CubismPhysics
    {
        private const float AirResistance = 5f;
        private const float MaximumWeight = 100f;

        private readonly CubismPhysicsRig _rig = new();
        private readonly List<float> _currentOutputs = new();
        private readonly List<float> _previousOutputs = new();
        private float _currentRemainTime;
        private float _physicsDeltaTime;

        /// <summary>
        /// Parses a .physics3.json byte buffer and builds the full physics rig
        /// with sub-rigs, inputs, outputs, and particles.
        /// </summary>
        public CubismPhysics(byte[] physics3JsonBytes)
        {
            var json = new CubismPhysicsJson(physics3JsonBytes);

            json.GetGravity(out _rig.GravityX, out _rig.GravityY);
            json.GetWind(out _rig.WindX, out _rig.WindY);
            _rig.Fps = json.GetFps();
            _physicsDeltaTime = 1f / _rig.Fps;

            int inputOffset = 0, outputOffset = 0, particleOffset = 0;
            for (int si = 0; si < json.GetSubRigCount(); si++)
            {
                var subRig = new CubismPhysicsSubRig
                {
                    InputCount = json.GetInputCount(si),
                    OutputCount = json.GetOutputCount(si),
                    ParticleCount = json.GetVertexCount(si),
                    BaseInputIndex = inputOffset,
                    BaseOutputIndex = outputOffset,
                    BaseParticleIndex = particleOffset
                };
                json.GetNormalizationPosition(si, out subRig.NormalizationPosition);
                json.GetNormalizationAngle(si, out subRig.NormalizationAngle);
                _rig.SubRigs.Add(subRig);

                for (int ii = 0; ii < subRig.InputCount; ii++)
                {
                    json.GetInput(si, ii, out var pid, out var w, out var type, out var reflect);
                    _rig.Inputs.Add(new CubismPhysicsInput
                    {
                        ParameterId = pid, Weight = w, Type = type, Reflect = reflect
                    });
                }

                for (int oi = 0; oi < subRig.OutputCount; oi++)
                {
                    json.GetOutput(si, oi, out var pid, out var vi, out var sx, out var sy,
                        out var w, out var type, out var reflect);
                    _rig.Outputs.Add(new CubismPhysicsOutput
                    {
                        ParameterId = pid, VertexIndex = vi + particleOffset,
                        TranslationScaleX = sx, TranslationScaleY = sy, Weight = w,
                        Type = type, Reflect = reflect
                    });
                }

                int totalParticles = particleOffset + subRig.ParticleCount;
                var newParticles = new CubismPhysicsParticle[totalParticles];
                if (_rig.Particles.Length > 0)
                    System.Array.Copy(_rig.Particles, newParticles, _rig.Particles.Length);
                _rig.Particles = newParticles;

                for (int pi = 0; pi < subRig.ParticleCount; pi++)
                {
                    var pIdx = particleOffset + pi;
                    json.GetParticle(si, pi, out var mob, out var del, out var acc, out var rad,
                        out var px, out var py);
                    _rig.Particles[pIdx] = new CubismPhysicsParticle
                    {
                        InitialX = px, InitialY = py,
                        Mobility = mob, Delay = del, Acceleration = acc, Radius = rad,
                        PositionX = px, PositionY = py,
                        LastPositionX = px, LastPositionY = py
                    };
                }

                inputOffset += subRig.InputCount;
                outputOffset += subRig.OutputCount;
                particleOffset += subRig.ParticleCount;
            }

            _currentOutputs.Capacity = _rig.Outputs.Count;
            _previousOutputs.Capacity = _rig.Outputs.Count;
            for (int i = 0; i < _rig.Outputs.Count; i++)
            {
                _currentOutputs.Add(0f);
                _previousOutputs.Add(0f);
            }
        }

        /// <summary>
        /// Overrides gravity and wind at runtime.
        /// </summary>
        public void SetOptions(float gravityX, float gravityY, float windX, float windY)
        {
            _rig.GravityX = gravityX;
            _rig.GravityY = gravityY;
            _rig.WindX = windX;
            _rig.WindY = windY;
        }

        /// <summary>
        /// Forces the physics to stabilize immediately by running one update
        /// cycle in stabilization mode. Particles snap to their rest positions
        /// along the chain direction.
        /// </summary>
        public void Stabilization(CubismNativeModel model)
        {
            UpdatePhysics(model, true);
        }

        /// <summary>
        /// Evaluates physics for one display frame.
        /// Accumulates time and runs physics steps at the rig's native FPS.
        /// Output values are linearly interpolated between the previous and
        /// current step for smooth visual results.
        /// Called from <see cref="CubismModelNode.OnUpdate"/> every frame.
        /// </summary>
        public unsafe void Evaluate(CubismNativeModel model, float deltaTimeSeconds)
        {
            _currentRemainTime += deltaTimeSeconds;
            var physicsDeltaCalc = _physicsDeltaTime > 0f ? _physicsDeltaTime : deltaTimeSeconds;

            var prevValues = model.GetParameterValues();
            var paramCache = stackalloc float[_rig.Inputs.Count];
            for (int i = 0; i < _rig.Inputs.Count; i++)
                paramCache[i] = 0f;

            while (_currentRemainTime >= physicsDeltaCalc)
            {
                _currentRemainTime -= physicsDeltaCalc;

                for (int i = 0; i < _rig.Outputs.Count; i++)
                {
                    _previousOutputs[i] = _currentOutputs[i];
                    _currentOutputs[i] = 0f;
                }

                UpdatePhysics(model, false);

                for (int i = 0; i < _rig.Outputs.Count; i++)
                    _currentOutputs[i] = EvaluateOutputValue(i);
            }

            var alpha = physicsDeltaCalc > 0f ? _currentRemainTime / physicsDeltaCalc : 0f;
            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();
            var maxValues = model.GetParameterMaximumValues();
            var minValues = model.GetParameterMinimumValues();

            for (int oi = 0; oi < _rig.Outputs.Count; oi++)
            {
                var output = _rig.Outputs[oi];
                var interpolated = _previousOutputs[oi] * (1f - alpha) + _currentOutputs[oi] * alpha;
                for (int pi = 0; pi < model.ParameterCount; pi++)
                {
                    if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, pi) == output.ParameterId)
                    {
                        var result = interpolated * MaximumWeight;
                        if (result < minValues[pi]) result = minValues[pi];
                        if (result > maxValues[pi]) result = maxValues[pi];
                        values[pi] = values[pi] * (1f - output.Weight) + result * output.Weight;
                        break;
                    }
                }
            }
        }

        private unsafe void UpdatePhysics(CubismNativeModel model, bool stabilization)
        {
            var idsPtr = model.GetParameterIds();
            var values = model.GetParameterValues();
            var minValues = model.GetParameterMinimumValues();
            var maxValues = model.GetParameterMaximumValues();
            var defaultValues = model.GetParameterDefaultValues();

            foreach (var subRig in _rig.SubRigs)
            {
                float totalTranslationX = 0f, totalTranslationY = 0f, totalAngle = 0f;

                for (int ii = 0; ii < subRig.InputCount; ii++)
                {
                    var input = _rig.Inputs[subRig.BaseInputIndex + ii];
                    var paramValue = GetParameterValue(idsPtr, values, model.ParameterCount, input.ParameterId);

                    float tx = 0f, ty = 0f, angle = 0f;
                    switch (input.Type)
                    {
                        case CubismPhysicsSource.X:
                            tx = NormalizeParameterValue(paramValue, subRig.NormalizationPosition, input.Reflect) * input.Weight;
                            break;
                        case CubismPhysicsSource.Y:
                            ty = NormalizeParameterValue(paramValue, subRig.NormalizationPosition, input.Reflect) * input.Weight;
                            break;
                        case CubismPhysicsSource.Angle:
                            angle = NormalizeParameterValue(paramValue, subRig.NormalizationAngle, input.Reflect) * input.Weight;
                            break;
                    }

                    totalTranslationX += tx;
                    totalTranslationY += ty;
                    totalAngle += angle;
                }

                var particles = _rig.Particles;
                int baseP = subRig.BaseParticleIndex;

                float prevRootX = particles[baseP].PositionX;
                float prevRootY = particles[baseP].PositionY;
                float smoothRate = MathF.Min(1f, _physicsDeltaTime * 12f);
                particles[baseP].PositionX = prevRootX + (totalTranslationX - prevRootX) * smoothRate;
                particles[baseP].PositionY = prevRootY + (totalTranslationY - prevRootY) * smoothRate;

                for (int pi = 1; pi < subRig.ParticleCount; pi++)
                {
                    var pIdx = baseP + pi;
                    var prev = particles[pIdx - 1];

                    if (stabilization)
                    {
                        var dirX = particles[pIdx].InitialX - prev.InitialX;
                        var dirY = particles[pIdx].InitialY - prev.InitialY;
                        var dist = MathF.Sqrt(dirX * dirX + dirY * dirY);
                        if (dist > 0f)
                        {
                            dirX /= dist;
                            dirY /= dist;
                        }
                        particles[pIdx].PositionX = particles[pIdx - 1].PositionX + dirX * particles[pIdx].Radius;
                        particles[pIdx].PositionY = particles[pIdx - 1].PositionY + dirY * particles[pIdx].Radius;
                    }
                    else
                    {
                        var forceX = _rig.GravityX * particles[pIdx].Acceleration + _rig.WindX;
                        var forceY = _rig.GravityY * particles[pIdx].Acceleration + _rig.WindY;

                        var delay = particles[pIdx].Delay * _physicsDeltaTime * 30f;
                        if (delay > 0f)
                        {
                            var delay2 = delay * delay;
                            var newX = particles[pIdx].PositionX + particles[pIdx].VelocityX * delay + 0.5f * forceX * delay2;
                            var newY = particles[pIdx].PositionY + particles[pIdx].VelocityY * delay + 0.5f * forceY * delay2;

                            var radian = CubismMath.DirectionToRadian(
                                particles[pIdx].LastGravityX, particles[pIdx].LastGravityY, forceX, forceY);
                            radian /= AirResistance;

                            var dirX = newX - particles[pIdx - 1].PositionX;
                            var dirY = newY - particles[pIdx - 1].PositionY;
                            var currentDist = MathF.Sqrt(dirX * dirX + dirY * dirY);
                            if (currentDist > 0f)
                            {
                                var ndirX = dirX / currentDist;
                                var ndirY = dirY / currentDist;
                                var cosR = MathF.Cos(radian);
                                var sinR = MathF.Sin(radian);
                                dirX = ndirX * cosR - ndirY * sinR;
                                dirY = ndirX * sinR + ndirY * cosR;
                            }
                            particles[pIdx].VelocityX = (newX - particles[pIdx].LastPositionX) / delay * particles[pIdx].Mobility;
                            particles[pIdx].VelocityY = (newY - particles[pIdx].LastPositionY) / delay * particles[pIdx].Mobility;
                        }

                        particles[pIdx].LastGravityX = forceX;
                        particles[pIdx].LastGravityY = forceY;
                        particles[pIdx].ForceX = forceX;
                        particles[pIdx].ForceY = forceY;

                        var curDirX = particles[pIdx].PositionX - particles[pIdx - 1].PositionX;
                        var curDirY = particles[pIdx].PositionY - particles[pIdx - 1].PositionY;
                        var curDist = MathF.Sqrt(curDirX * curDirX + curDirY * curDirY);
                        if (curDist > 0f)
                        {
                            curDirX = curDirX / curDist * particles[pIdx].Radius;
                            curDirY = curDirY / curDist * particles[pIdx].Radius;
                        }
                        particles[pIdx].PositionX = particles[pIdx - 1].PositionX + curDirX;
                        particles[pIdx].PositionY = particles[pIdx - 1].PositionY + curDirY;
                        particles[pIdx].LastPositionX = particles[pIdx].PositionX;
                        particles[pIdx].LastPositionY = particles[pIdx].PositionY;
                    }
                }

                for (int oi = 0; oi < subRig.OutputCount; oi++)
                {
                    var outIdx = subRig.BaseOutputIndex + oi;
                    var output = _rig.Outputs[outIdx];
                    var particle = particles[output.VertexIndex];
                    var value = EvaluateOutputValueStatic(particle, particles, output, baseP, _currentOutputs[outIdx]);
                    _currentOutputs[outIdx] = value * output.Weight;
                }
            }
        }

        private float EvaluateOutputValue(int outputIndex)
        {
            return _currentOutputs[outputIndex];
        }

        private static float EvaluateOutputValueStatic(CubismPhysicsParticle particle,
            CubismPhysicsParticle[] particles, CubismPhysicsOutput output, int baseParticleIndex, float currentValue)
        {
            var pIdx = output.VertexIndex;
            var p = particles[pIdx];
            var prev = pIdx > baseParticleIndex ? particles[pIdx - 1] : particles[pIdx];

            return output.Type switch
            {
                CubismPhysicsSource.X => p.PositionX - prev.PositionX,
                CubismPhysicsSource.Y => p.PositionY - prev.PositionY,
                CubismPhysicsSource.Angle =>
                    MathF.Atan2(p.PositionY - prev.PositionY, p.PositionX - prev.PositionX) * 180f / MathF.PI,
                _ => p.PositionX
            };
        }

        private static unsafe float GetParameterValue(IntPtr idsPtr, float* values, int count, string id)
        {
            for (int i = 0; i < count; i++)
                if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, i) == id)
                    return values[i];
            return 0f;
        }

        private static float NormalizeParameterValue(float value, CubismPhysicsNormalization norm, bool reflect)
        {
            var range = norm.Maximum - norm.Minimum;
            if (MathF.Abs(range) < 1e-6f) return 0f;
            var result = (value - norm.Default) / range;
            return reflect ? -result : result;
        }
    }
}
