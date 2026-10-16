using System;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Cubism physics simulation engine. Drives secondary motion (hair, cloth,
    /// accessories) using a chain of particles influenced by parameter inputs
    /// (X, Y, angle from model parameters), gravity, and wind.
    ///
    /// Bit-exact port of Cubism SDK for Native 5-r.5 Physics/CubismPhysics.cpp.
    /// Architecture (mirroring the native rig structures):
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
    /// The simulation runs at the rig's configured FPS (from .physics3.json,
    /// Meta.Fps; 0 means "step once per display frame"). Pendulum results are
    /// kept as current/previous rig outputs and the value written to the model
    /// parameters is interpolated between them by the remaining sub-frame time.
    /// The pure math core (normalization, particle update, output value update)
    /// lives in internal static methods operating on plain data for testability.
    /// </summary>
    public class CubismPhysics
    {
        // ------------------------------------------------------------------
        // Physics constants (SDK CubismPhysics.cpp anonymous namespace).
        // ------------------------------------------------------------------
        private const float AirResistance = 5f;
        private const float MaximumWeight = 100f;
        private const float MovementThreshold = 0.001f;
        private const float MaxDeltaTime = 5f;

        /// <summary>SDK CubismMath::Pi (identical to MathF.PI as a csmFloat32).</summary>
        private const float Pi = MathF.PI;

        private readonly CubismPhysicsRig _rig = new();

        // Runtime options (SDK _options). Native defaults: gravity (0, -1), wind (0, 0).
        private float _optionsGravityX;
        private float _optionsGravityY = -1f;
        private float _optionsWindX;
        private float _optionsWindY;

        // Parameter value caches used for sub-frame input interpolation
        // (SDK _parameterCaches / _parameterInputCaches).
        private float[] _parameterCaches = Array.Empty<float>();
        private float[] _parameterInputCaches = Array.Empty<float>();

        // Pendulum output history (SDK _currentRigOutputs / _previousRigOutputs,
        // flattened to one slot per absolute output index).
        private float[] _currentRigOutputs = Array.Empty<float>();
        private float[] _previousRigOutputs = Array.Empty<float>();

        private float _currentRemainTime;

        /// <summary>
        /// Parses a .physics3.json byte buffer and builds the full physics rig
        /// with sub-rigs, inputs, outputs, and particles (SDK Parse + Initialize).
        /// </summary>
        public CubismPhysics(byte[] physics3JsonBytes)
        {
            var json = new CubismPhysicsJson(physics3JsonBytes);

            json.GetGravity(out _rig.GravityX, out _rig.GravityY);
            json.GetWind(out _rig.WindX, out _rig.WindY);
            _rig.Fps = json.GetFps();

            int subRigCount = json.GetSubRigCount();
            var particles = new List<CubismPhysicsParticle>(json.GetVertexCount());
            _rig.Inputs.Capacity = json.GetTotalInputCount();
            _rig.Outputs.Capacity = json.GetTotalOutputCount();

            int inputIndex = 0, outputIndex = 0, particleIndex = 0;
            for (int si = 0; si < subRigCount; si++)
            {
                var subRig = new CubismPhysicsSubRig
                {
                    BaseInputIndex = inputIndex,
                    BaseOutputIndex = outputIndex,
                    BaseParticleIndex = particleIndex,
                };
                json.GetNormalizationPosition(si, out subRig.NormalizationPosition);
                json.GetNormalizationAngle(si, out subRig.NormalizationAngle);

                // Input
                subRig.InputCount = json.GetInputCount(si);
                for (int j = 0; j < subRig.InputCount; j++)
                {
                    _rig.Inputs.Add(new CubismPhysicsInput
                    {
                        SourceParameterIndex = -1,
                        Weight = json.GetInputWeight(si, j),
                        Reflect = json.GetInputReflect(si, j),
                        Type = ParseSource(json.GetInputType(si, j)),
                        ParameterId = json.GetInputSourceId(si, j),
                    });
                }
                inputIndex += subRig.InputCount;

                // Output
                subRig.OutputCount = json.GetOutputCount(si);
                for (int j = 0; j < subRig.OutputCount; j++)
                {
                    _rig.Outputs.Add(new CubismPhysicsOutput
                    {
                        DestinationParameterIndex = -1,
                        VertexIndex = json.GetOutputVertexIndex(si, j),
                        AngleScale = json.GetOutputAngleScale(si, j),
                        Weight = json.GetOutputWeight(si, j),
                        ParameterId = json.GetOutputsDestinationId(si, j),
                        Type = ParseSource(json.GetOutputType(si, j)),
                        Reflect = json.GetOutputReflect(si, j),
                        // TranslationScale stays (0, 0): native never loads a
                        // translation scale from physics3.json, so X/Y outputs
                        // scale by zero exactly like the native implementation.
                    });
                }
                outputIndex += subRig.OutputCount;

                // Particle
                subRig.ParticleCount = json.GetParticleCount(si);
                for (int j = 0; j < subRig.ParticleCount; j++)
                {
                    json.GetParticlePosition(si, j, out var px, out var py);
                    particles.Add(new CubismPhysicsParticle
                    {
                        InitialX = px,
                        InitialY = py,
                        Mobility = json.GetParticleMobility(si, j),
                        Delay = json.GetParticleDelay(si, j),
                        Acceleration = json.GetParticleAcceleration(si, j),
                        Radius = json.GetParticleRadius(si, j),
                        PositionX = px,
                        PositionY = py,
                    });
                }
                particleIndex += subRig.ParticleCount;

                _rig.SubRigs.Add(subRig);
            }

            _rig.Particles = particles.ToArray();

            _currentRigOutputs = new float[_rig.Outputs.Count];
            _previousRigOutputs = new float[_rig.Outputs.Count];

            Initialize();

            // SDK CubismPhysics::Create zeroes the rig gravity Y after parsing.
            _rig.GravityY = 0f;
        }

        /// <summary>
        /// Lays out the particle chains at their rest positions
        /// (SDK CubismPhysics::Initialize).
        /// </summary>
        private void Initialize()
        {
            foreach (var currentSetting in _rig.SubRigs)
            {
                int baseParticleIndex = currentSetting.BaseParticleIndex;

                // Initialize the top of the strand.
                _rig.Particles[baseParticleIndex].InitialX = 0f;
                _rig.Particles[baseParticleIndex].InitialY = 0f;
                _rig.Particles[baseParticleIndex].LastPositionX = 0f;
                _rig.Particles[baseParticleIndex].LastPositionY = 0f;
                _rig.Particles[baseParticleIndex].LastGravityX = 0f;
                _rig.Particles[baseParticleIndex].LastGravityY = 1f;    // (0, -1) with Y negated
                _rig.Particles[baseParticleIndex].VelocityX = 0f;
                _rig.Particles[baseParticleIndex].VelocityY = 0f;
                _rig.Particles[baseParticleIndex].ForceX = 0f;
                _rig.Particles[baseParticleIndex].ForceY = 0f;

                // Initialize the remaining particles along +Y by radius.
                for (int i = 1; i < currentSetting.ParticleCount; ++i)
                {
                    int idx = baseParticleIndex + i;
                    _rig.Particles[idx].InitialX = _rig.Particles[idx - 1].InitialX;
                    _rig.Particles[idx].InitialY = _rig.Particles[idx - 1].InitialY + _rig.Particles[idx].Radius;
                    _rig.Particles[idx].PositionX = _rig.Particles[idx].InitialX;
                    _rig.Particles[idx].PositionY = _rig.Particles[idx].InitialY;
                    _rig.Particles[idx].LastPositionX = _rig.Particles[idx].InitialX;
                    _rig.Particles[idx].LastPositionY = _rig.Particles[idx].InitialY;
                    _rig.Particles[idx].LastGravityX = 0f;
                    _rig.Particles[idx].LastGravityY = 1f;              // (0, -1) with Y negated
                    _rig.Particles[idx].VelocityX = 0f;
                    _rig.Particles[idx].VelocityY = 0f;
                    _rig.Particles[idx].ForceX = 0f;
                    _rig.Particles[idx].ForceY = 0f;
                }
            }
        }

        /// <summary>
        /// Overrides gravity and wind at runtime (SDK SetOptions; these feed the
        /// wind force during simulation and the parent gravity of angle outputs).
        /// </summary>
        public void SetOptions(float gravityX, float gravityY, float windX, float windY)
        {
            _optionsGravityX = gravityX;
            _optionsGravityY = gravityY;
            _optionsWindX = windX;
            _optionsWindY = windY;
        }

        /// <summary>
        /// Forces the physics to stabilize immediately (SDK CubismPhysics::Stabilization).
        /// Particles snap to their rest positions along the current gravity
        /// direction and the resolved outputs are written into the model.
        /// </summary>
        public unsafe void Stabilization(CubismNativeModel model)
        {
            int parameterCount = model.ParameterCount;
            float* parameterValues = model.GetParameterValues();
            float* parameterMaximumValues = model.GetParameterMaximumValues();
            float* parameterMinimumValues = model.GetParameterMinimumValues();
            float* parameterDefaultValues = model.GetParameterDefaultValues();

            if (_parameterCaches.Length < parameterCount)
            {
                _parameterCaches = new float[parameterCount];
            }
            if (_parameterInputCaches.Length < parameterCount)
            {
                _parameterInputCaches = new float[parameterCount];
            }

            for (int j = 0; j < parameterCount; ++j)
            {
                _parameterCaches[j] = parameterValues[j];
                _parameterInputCaches[j] = parameterValues[j];
            }

            foreach (var currentSetting in _rig.SubRigs)
            {
                float totalAngle = 0f;
                float totalTranslationX = 0f, totalTranslationY = 0f;

                // Load input parameters.
                for (int i = 0; i < currentSetting.InputCount; ++i)
                {
                    int inputIndex = currentSetting.BaseInputIndex + i;
                    var input = _rig.Inputs[inputIndex];
                    float weight = input.Weight / MaximumWeight;

                    if (input.SourceParameterIndex == -1)
                    {
                        input.SourceParameterIndex = ResolveParameterIndex(model, input.ParameterId);
                    }
                    if (input.SourceParameterIndex == -1)
                    {
                        continue;
                    }
                    _rig.Inputs[inputIndex] = input;

                    int source = input.SourceParameterIndex;
                    switch (input.Type)
                    {
                        case CubismPhysicsSource.X:
                            GetInputTranslationXFromNormalizedParameterValue(
                                ref totalTranslationX, ref totalTranslationY, ref totalAngle,
                                parameterValues[source],
                                parameterMinimumValues[source],
                                parameterMaximumValues[source],
                                parameterDefaultValues[source],
                                currentSetting.NormalizationPosition,
                                currentSetting.NormalizationAngle,
                                input.Reflect, weight);
                            break;
                        case CubismPhysicsSource.Y:
                            GetInputTranslationYFromNormalizedParameterValue(
                                ref totalTranslationX, ref totalTranslationY, ref totalAngle,
                                parameterValues[source],
                                parameterMinimumValues[source],
                                parameterMaximumValues[source],
                                parameterDefaultValues[source],
                                currentSetting.NormalizationPosition,
                                currentSetting.NormalizationAngle,
                                input.Reflect, weight);
                            break;
                        case CubismPhysicsSource.Angle:
                            GetInputAngleFromNormalizedParameterValue(
                                ref totalTranslationX, ref totalTranslationY, ref totalAngle,
                                parameterValues[source],
                                parameterMinimumValues[source],
                                parameterMaximumValues[source],
                                parameterDefaultValues[source],
                                currentSetting.NormalizationPosition,
                                currentSetting.NormalizationAngle,
                                input.Reflect, weight);
                            break;
                    }

                    _parameterCaches[source] = parameterValues[source];
                }

                float radAngle = DegreesToRadian(-totalAngle);

                // NOTE: native computes Y from the already-updated X
                // (bug-compatible with CubismPhysics.cpp Stabilization).
                totalTranslationX = totalTranslationX * CosF(radAngle) - totalTranslationY * SinF(radAngle);
                totalTranslationY = totalTranslationX * SinF(radAngle) + totalTranslationY * CosF(radAngle);

                // Calculate particles position.
                UpdateParticlesForStabilization(
                    _rig.Particles, currentSetting.BaseParticleIndex, currentSetting.ParticleCount,
                    totalTranslationX, totalTranslationY, totalAngle,
                    _optionsWindX, _optionsWindY,
                    MovementThreshold * currentSetting.NormalizationPosition.Maximum);

                // Update output parameters.
                for (int i = 0; i < currentSetting.OutputCount; ++i)
                {
                    int outputIndex = currentSetting.BaseOutputIndex + i;
                    var output = _rig.Outputs[outputIndex];
                    int particleIndex = output.VertexIndex;

                    if (output.DestinationParameterIndex == -1)
                    {
                        output.DestinationParameterIndex = ResolveParameterIndex(model, output.ParameterId);
                    }

                    if (particleIndex < 1 || particleIndex >= currentSetting.ParticleCount)
                    {
                        _rig.Outputs[outputIndex] = output;
                        continue;
                    }

                    int vertex = currentSetting.BaseParticleIndex + particleIndex;
                    float translationX = _rig.Particles[vertex].PositionX - _rig.Particles[vertex - 1].PositionX;
                    float translationY = _rig.Particles[vertex].PositionY - _rig.Particles[vertex - 1].PositionY;

                    float outputValue = GetOutputValue(
                        output.Type, translationX, translationY,
                        _rig.Particles, currentSetting.BaseParticleIndex, particleIndex,
                        output.Reflect, _optionsGravityX, _optionsGravityY);

                    _currentRigOutputs[outputIndex] = outputValue;
                    _previousRigOutputs[outputIndex] = outputValue;

                    if (output.DestinationParameterIndex != -1)
                    {
                        int destination = output.DestinationParameterIndex;
                        UpdateOutputParameterValue(
                            ref parameterValues[destination],
                            parameterMinimumValues[destination],
                            parameterMaximumValues[destination],
                            outputValue, ref output);

                        _rig.Outputs[outputIndex] = output;
                        _parameterCaches[destination] = parameterValues[destination];
                    }
                }
            }
        }

        /// <summary>
        /// Evaluates physics for one display frame (SDK CubismPhysics::Evaluate).
        /// Accumulates time and runs physics steps at the rig's native FPS.
        /// Inputs are linearly interpolated between the cached and live
        /// parameter values at each step, and the output written to the model
        /// parameters is interpolated between the previous and current pendulum
        /// results by the remaining sub-frame time.
        /// Called from <see cref="CubismModelNode.OnUpdate"/> every frame.
        /// </summary>
        public unsafe void Evaluate(CubismNativeModel model, float deltaTimeSeconds)
        {
            if (0f >= deltaTimeSeconds)
            {
                return;
            }

            int parameterCount = model.ParameterCount;
            float* parameterValues = model.GetParameterValues();
            float* parameterMaximumValues = model.GetParameterMaximumValues();
            float* parameterMinimumValues = model.GetParameterMinimumValues();
            float* parameterDefaultValues = model.GetParameterDefaultValues();

            _currentRemainTime += deltaTimeSeconds;
            if (_currentRemainTime > MaxDeltaTime)
            {
                _currentRemainTime = 0f;
            }

            if (_parameterCaches.Length < parameterCount)
            {
                _parameterCaches = new float[parameterCount];
            }
            if (_parameterInputCaches.Length < parameterCount)
            {
                _parameterInputCaches = new float[parameterCount];
                for (int j = 0; j < parameterCount; ++j)
                {
                    _parameterInputCaches[j] = parameterValues[j];
                }
            }

            float physicsDeltaTime = _rig.Fps > 0f ? 1f / _rig.Fps : deltaTimeSeconds;
            if (!(physicsDeltaTime > 0f))
            {
                physicsDeltaTime = deltaTimeSeconds;
            }

            // After the MaxDeltaTime clamp the loop can consume at most
            // ceil(MaxDeltaTime / physicsDeltaTime) steps; cap it defensively so
            // pathological Fps values cannot spin (SDK relies on well-formed data).
            int maxSteps = (int)(MaxDeltaTime / physicsDeltaTime) + 2;
            int steps = 0;

            while (_currentRemainTime >= physicsDeltaTime)
            {
                if (++steps > maxSteps)
                {
                    _currentRemainTime = 0f;
                    break;
                }

                // copyRigOutputs: _currentRigOutputs -> _previousRigOutputs.
                Array.Copy(_currentRigOutputs, _previousRigOutputs, _currentRigOutputs.Length);

                // Linear interpolation between the cached input values and the
                // live parameter values at each UpdateParticles timing.
                // _parameterCaches propagates values between groups, which is
                // why it is kept separate from _parameterInputCaches.
                float inputWeight = physicsDeltaTime / _currentRemainTime;
                for (int j = 0; j < parameterCount; ++j)
                {
                    _parameterCaches[j] = _parameterInputCaches[j] * (1f - inputWeight) + parameterValues[j] * inputWeight;
                    _parameterInputCaches[j] = _parameterCaches[j];
                }

                foreach (var currentSetting in _rig.SubRigs)
                {
                    float totalAngle = 0f;
                    float totalTranslationX = 0f, totalTranslationY = 0f;

                    // Load input parameters.
                    for (int i = 0; i < currentSetting.InputCount; ++i)
                    {
                        int inputIndex = currentSetting.BaseInputIndex + i;
                        var input = _rig.Inputs[inputIndex];
                        float weight = input.Weight / MaximumWeight;

                        if (input.SourceParameterIndex == -1)
                        {
                            input.SourceParameterIndex = ResolveParameterIndex(model, input.ParameterId);
                        }
                        if (input.SourceParameterIndex == -1)
                        {
                            continue;
                        }
                        _rig.Inputs[inputIndex] = input;

                        int source = input.SourceParameterIndex;
                        switch (input.Type)
                        {
                            case CubismPhysicsSource.X:
                                GetInputTranslationXFromNormalizedParameterValue(
                                    ref totalTranslationX, ref totalTranslationY, ref totalAngle,
                                    _parameterCaches[source],
                                    parameterMinimumValues[source],
                                    parameterMaximumValues[source],
                                    parameterDefaultValues[source],
                                    currentSetting.NormalizationPosition,
                                    currentSetting.NormalizationAngle,
                                    input.Reflect, weight);
                                break;
                            case CubismPhysicsSource.Y:
                                GetInputTranslationYFromNormalizedParameterValue(
                                    ref totalTranslationX, ref totalTranslationY, ref totalAngle,
                                    _parameterCaches[source],
                                    parameterMinimumValues[source],
                                    parameterMaximumValues[source],
                                    parameterDefaultValues[source],
                                    currentSetting.NormalizationPosition,
                                    currentSetting.NormalizationAngle,
                                    input.Reflect, weight);
                                break;
                            case CubismPhysicsSource.Angle:
                                GetInputAngleFromNormalizedParameterValue(
                                    ref totalTranslationX, ref totalTranslationY, ref totalAngle,
                                    _parameterCaches[source],
                                    parameterMinimumValues[source],
                                    parameterMaximumValues[source],
                                    parameterDefaultValues[source],
                                    currentSetting.NormalizationPosition,
                                    currentSetting.NormalizationAngle,
                                    input.Reflect, weight);
                                break;
                        }
                    }

                    float radAngle = DegreesToRadian(-totalAngle);

                    // NOTE: native computes Y from the already-updated X
                    // (bug-compatible with CubismPhysics.cpp Evaluate).
                    totalTranslationX = totalTranslationX * CosF(radAngle) - totalTranslationY * SinF(radAngle);
                    totalTranslationY = totalTranslationX * SinF(radAngle) + totalTranslationY * CosF(radAngle);

                    // Calculate particles position.
                    UpdateParticles(
                        _rig.Particles, currentSetting.BaseParticleIndex, currentSetting.ParticleCount,
                        totalTranslationX, totalTranslationY, totalAngle,
                        _optionsWindX, _optionsWindY,
                        MovementThreshold * currentSetting.NormalizationPosition.Maximum,
                        physicsDeltaTime, AirResistance);

                    // Update output parameters.
                    for (int i = 0; i < currentSetting.OutputCount; ++i)
                    {
                        int outputIndex = currentSetting.BaseOutputIndex + i;
                        var output = _rig.Outputs[outputIndex];
                        int particleIndex = output.VertexIndex;

                        if (output.DestinationParameterIndex == -1)
                        {
                            output.DestinationParameterIndex = ResolveParameterIndex(model, output.ParameterId);
                        }

                        if (particleIndex < 1 || particleIndex >= currentSetting.ParticleCount)
                        {
                            _rig.Outputs[outputIndex] = output;
                            continue;
                        }

                        int vertex = currentSetting.BaseParticleIndex + particleIndex;
                        float translationX = _rig.Particles[vertex].PositionX - _rig.Particles[vertex - 1].PositionX;
                        float translationY = _rig.Particles[vertex].PositionY - _rig.Particles[vertex - 1].PositionY;

                        float outputValue = GetOutputValue(
                            output.Type, translationX, translationY,
                            _rig.Particles, currentSetting.BaseParticleIndex, particleIndex,
                            output.Reflect, _optionsGravityX, _optionsGravityY);

                        _currentRigOutputs[outputIndex] = outputValue;

                        if (output.DestinationParameterIndex != -1)
                        {
                            int destination = output.DestinationParameterIndex;
                            UpdateOutputParameterValue(
                                ref _parameterCaches[destination],
                                parameterMinimumValues[destination],
                                parameterMaximumValues[destination],
                                outputValue, ref output);
                        }

                        _rig.Outputs[outputIndex] = output;
                    }
                }

                _currentRemainTime -= physicsDeltaTime;
            }

            float alpha = _currentRemainTime / physicsDeltaTime;
            Interpolate(parameterValues, parameterMinimumValues, parameterMaximumValues, alpha);
        }

        /// <summary>
        /// Writes the interpolated pendulum results into the model parameters
        /// (SDK CubismPhysics::Interpolate). Outputs whose destination was never
        /// resolved are skipped.
        /// </summary>
        private unsafe void Interpolate(
            float* parameterValues, float* parameterMinimumValues, float* parameterMaximumValues, float weight)
        {
            foreach (var currentSetting in _rig.SubRigs)
            {
                for (int i = 0; i < currentSetting.OutputCount; ++i)
                {
                    int outputIndex = currentSetting.BaseOutputIndex + i;
                    var output = _rig.Outputs[outputIndex];

                    if (output.DestinationParameterIndex == -1)
                    {
                        continue;
                    }

                    int destination = output.DestinationParameterIndex;
                    float interpolated =
                        _previousRigOutputs[outputIndex] * (1 - weight) + _currentRigOutputs[outputIndex] * weight;

                    UpdateOutputParameterValue(
                        ref parameterValues[destination],
                        parameterMinimumValues[destination],
                        parameterMaximumValues[destination],
                        interpolated, ref output);

                    _rig.Outputs[outputIndex] = output;
                }
            }
        }

        // ==================================================================
        // Pure math core (internal static, no CubismNativeModel dependency —
        // unit-testable against the native SDK formulas).
        // ==================================================================

        /// <summary>Dispatches to the SDK value getter for an output type.</summary>
        internal static float GetOutputValue(
            CubismPhysicsSource type, float translationX, float translationY,
            CubismPhysicsParticle[] particles, int baseParticleIndex, int particleIndex,
            bool isInverted, float parentGravityX, float parentGravityY)
        {
            switch (type)
            {
                case CubismPhysicsSource.X:
                    return GetOutputTranslationX(translationX, isInverted);
                case CubismPhysicsSource.Y:
                    return GetOutputTranslationY(translationY, isInverted);
                case CubismPhysicsSource.Angle:
                    return GetOutputAngle(translationX, translationY, particles, baseParticleIndex, particleIndex,
                        isInverted, parentGravityX, parentGravityY);
                default:
                    return GetOutputTranslationX(translationX, isInverted);
            }
        }

        internal static float GetOutputTranslationX(float translationX, bool isInverted)
        {
            float outputValue = translationX;

            if (isInverted)
            {
                outputValue *= -1f;
            }

            return outputValue;
        }

        internal static float GetOutputTranslationY(float translationY, bool isInverted)
        {
            float outputValue = translationY;

            if (isInverted)
            {
                outputValue *= -1f;
            }

            return outputValue;
        }

        /// <summary>
        /// SDK GetOutputAngle: the parent gravity is the direction between the
        /// two previous particles once the pendulum has at least two segments,
        /// otherwise the negated rig gravity.
        /// </summary>
        internal static float GetOutputAngle(
            float translationX, float translationY,
            CubismPhysicsParticle[] particles, int baseParticleIndex, int particleIndex,
            bool isInverted, float parentGravityX, float parentGravityY)
        {
            float gravityX, gravityY;
            if (particleIndex >= 2)
            {
                gravityX = particles[baseParticleIndex + particleIndex - 1].PositionX
                         - particles[baseParticleIndex + particleIndex - 2].PositionX;
                gravityY = particles[baseParticleIndex + particleIndex - 1].PositionY
                         - particles[baseParticleIndex + particleIndex - 2].PositionY;
            }
            else
            {
                gravityX = -parentGravityX;
                gravityY = -parentGravityY;
            }

            float outputValue = DirectionToRadian(gravityX, gravityY, translationX, translationY);

            if (isInverted)
            {
                outputValue *= -1f;
            }

            return outputValue;
        }

        /// <summary>SDK scale getter dispatch: X/Y use the (never loaded, i.e. zero) translation scale; Angle uses AngleScale.</summary>
        internal static float GetOutputScale(CubismPhysicsSource type, float translationScaleX, float translationScaleY, float angleScale)
        {
            switch (type)
            {
                case CubismPhysicsSource.X:
                    return translationScaleX;
                case CubismPhysicsSource.Y:
                    return translationScaleY;
                default:
                    return angleScale;
            }
        }

        /// <summary>
        /// SDK UpdateOutputParameterValue: scale the translation, clamp to the
        /// parameter range (recording below-minimum/exceeded-maximum extremes),
        /// then blend into the parameter by Output.Weight / 100.
        /// </summary>
        internal static void UpdateOutputParameterValue(
            ref float parameterValue, float parameterValueMinimum, float parameterValueMaximum,
            float translation, ref CubismPhysicsOutput output)
        {
            float outputScale = GetOutputScale(output.Type, output.TranslationScaleX, output.TranslationScaleY, output.AngleScale);

            float value = translation * outputScale;

            if (value < parameterValueMinimum)
            {
                if (value < output.ValueBelowMinimum)
                {
                    output.ValueBelowMinimum = value;
                }

                value = parameterValueMinimum;
            }
            else if (value > parameterValueMaximum)
            {
                if (value > output.ValueExceededMaximum)
                {
                    output.ValueExceededMaximum = value;
                }

                value = parameterValueMaximum;
            }

            float weight = output.Weight / MaximumWeight;

            if (weight >= 1f)
            {
                parameterValue = value;
            }
            else
            {
                value = parameterValue * (1f - weight) + value * weight;
                parameterValue = value;
            }
        }

        /// <summary>SDK GetRangeValue.</summary>
        internal static float GetRangeValue(float min, float max)
        {
            float maxValue = MaxF(min, max);
            float minValue = MinF(min, max);

            return AbsF(maxValue - minValue);
        }

        /// <summary>SDK Sign.</summary>
        internal static int Sign(float value)
        {
            if (value > 0f)
            {
                return 1;
            }
            if (value < 0f)
            {
                return -1;
            }

            return 0;
        }

        /// <summary>SDK GetDefaultValue: the middle of the parameter range.</summary>
        internal static float GetDefaultValue(float min, float max)
        {
            float minValue = MinF(min, max);
            return minValue + GetRangeValue(min, max) / 2f;
        }

        /// <summary>
        /// SDK NormalizeParameterValue: clamp the value to its parameter range,
        /// map it piecewise across the normalization range relative to the
        /// default middle, and invert when the input is not reflected
        /// (native applies the sign flip to non-inverted values).
        /// </summary>
        internal static float NormalizeParameterValue(
            float value,
            float parameterMinimum,
            float parameterMaximum,
            float parameterDefault,
            float normalizedMinimum,
            float normalizedMaximum,
            float normalizedDefault,
            bool isInverted)
        {
            float result = 0f;

            float maxValue = MaxF(parameterMaximum, parameterMinimum);
            if (maxValue < value)
            {
                value = maxValue;
            }

            float minValue = MinF(parameterMaximum, parameterMinimum);
            if (minValue > value)
            {
                value = minValue;
            }

            float minNormValue = MinF(normalizedMinimum, normalizedMaximum);
            float maxNormValue = MaxF(normalizedMinimum, normalizedMaximum);
            float middleNormValue = normalizedDefault;

            float middleValue = GetDefaultValue(minValue, maxValue);
            float paramValue = value - middleValue;

            switch (Sign(paramValue))
            {
                case 1:
                {
                    float nLength = maxNormValue - middleNormValue;
                    float pLength = maxValue - middleValue;
                    if (pLength != 0f)
                    {
                        result = paramValue * (nLength / pLength);
                        result += middleNormValue;
                    }

                    break;
                }
                case -1:
                {
                    float nLength = minNormValue - middleNormValue;
                    float pLength = minValue - middleValue;
                    if (pLength != 0f)
                    {
                        result = paramValue * (nLength / pLength);
                        result += middleNormValue;
                    }

                    break;
                }
                default:
                {
                    result = middleNormValue;

                    break;
                }
            }

            return isInverted
                ? result
                : result * -1f;
        }

        /// <summary>SDK GetInputTranslationXFromNormalizedParameterValue.</summary>
        internal static void GetInputTranslationXFromNormalizedParameterValue(
            ref float targetTranslationX, ref float targetTranslationY, ref float targetAngle,
            float value, float parameterMinimumValue, float parameterMaximumValue, float parameterDefaultValue,
            CubismPhysicsNormalization normalizationPosition, CubismPhysicsNormalization normalizationAngle,
            bool isInverted, float weight)
        {
            targetTranslationX += NormalizeParameterValue(
                value,
                parameterMinimumValue,
                parameterMaximumValue,
                parameterDefaultValue,
                normalizationPosition.Minimum,
                normalizationPosition.Maximum,
                normalizationPosition.Default,
                isInverted
            ) * weight;
        }

        /// <summary>SDK GetInputTranslationYFromNormalizedParameterValue.</summary>
        internal static void GetInputTranslationYFromNormalizedParameterValue(
            ref float targetTranslationX, ref float targetTranslationY, ref float targetAngle,
            float value, float parameterMinimumValue, float parameterMaximumValue, float parameterDefaultValue,
            CubismPhysicsNormalization normalizationPosition, CubismPhysicsNormalization normalizationAngle,
            bool isInverted, float weight)
        {
            targetTranslationY += NormalizeParameterValue(
                value,
                parameterMinimumValue,
                parameterMaximumValue,
                parameterDefaultValue,
                normalizationPosition.Minimum,
                normalizationPosition.Maximum,
                normalizationPosition.Default,
                isInverted
            ) * weight;
        }

        /// <summary>SDK GetInputAngleFromNormalizedParameterValue.</summary>
        internal static void GetInputAngleFromNormalizedParameterValue(
            ref float targetTranslationX, ref float targetTranslationY, ref float targetAngle,
            float value, float parameterMinimumValue, float parameterMaximumValue, float parameterDefaultValue,
            CubismPhysicsNormalization normalizationPosition, CubismPhysicsNormalization normalizationAngle,
            bool isInverted, float weight)
        {
            targetAngle += NormalizeParameterValue(
                value,
                parameterMinimumValue,
                parameterMaximumValue,
                parameterDefaultValue,
                normalizationAngle.Minimum,
                normalizationAngle.Maximum,
                normalizationAngle.Default,
                isInverted
            ) * weight;
        }

        /// <summary>
        /// SDK UpdateParticles: one integration step of a particle strand.
        /// The root is pinned to the total translation, each particle keeps its
        /// radius distance from its predecessor, is rotated with the gravity
        /// change (divided by air resistance), integrates velocity and force,
        /// and stores velocity for the next step (only when delay != 0).
        /// </summary>
        internal static void UpdateParticles(
            CubismPhysicsParticle[] strand, int baseParticleIndex, int strandCount,
            float totalTranslationX, float totalTranslationY, float totalAngle,
            float windX, float windY, float thresholdValue, float deltaTimeSeconds, float airResistance)
        {
            strand[baseParticleIndex].PositionX = totalTranslationX;
            strand[baseParticleIndex].PositionY = totalTranslationY;

            float totalRadian = DegreesToRadian(totalAngle);
            float currentGravityX = SinF(totalRadian);
            float currentGravityY = CosF(totalRadian);
            Normalize(ref currentGravityX, ref currentGravityY);

            for (int i = 1; i < strandCount; ++i)
            {
                int idx = baseParticleIndex + i;

                strand[idx].ForceX = currentGravityX * strand[idx].Acceleration + windX;
                strand[idx].ForceY = currentGravityY * strand[idx].Acceleration + windY;

                strand[idx].LastPositionX = strand[idx].PositionX;
                strand[idx].LastPositionY = strand[idx].PositionY;

                float delay = strand[idx].Delay * deltaTimeSeconds * 30f;

                float directionX = strand[idx].PositionX - strand[idx - 1].PositionX;
                float directionY = strand[idx].PositionY - strand[idx - 1].PositionY;

                float radian = DirectionToRadian(strand[idx].LastGravityX, strand[idx].LastGravityY,
                    currentGravityX, currentGravityY) / airResistance;

                // NOTE: native computes Y from the already-updated X
                // (bug-compatible with CubismPhysics.cpp UpdateParticles).
                float newDirectionX = CosF(radian) * directionX - directionY * SinF(radian);
                directionY = SinF(radian) * newDirectionX + directionY * CosF(radian);
                directionX = newDirectionX;

                strand[idx].PositionX = strand[idx - 1].PositionX + directionX;
                strand[idx].PositionY = strand[idx - 1].PositionY + directionY;

                float velocityX = strand[idx].VelocityX * delay;
                float velocityY = strand[idx].VelocityY * delay;
                float forceX = strand[idx].ForceX * delay * delay;
                float forceY = strand[idx].ForceY * delay * delay;

                strand[idx].PositionX = strand[idx].PositionX + velocityX + forceX;
                strand[idx].PositionY = strand[idx].PositionY + velocityY + forceY;

                float newDirectionX2 = strand[idx].PositionX - strand[idx - 1].PositionX;
                float newDirectionY2 = strand[idx].PositionY - strand[idx - 1].PositionY;
                Normalize(ref newDirectionX2, ref newDirectionY2);

                strand[idx].PositionX = strand[idx - 1].PositionX + newDirectionX2 * strand[idx].Radius;
                strand[idx].PositionY = strand[idx - 1].PositionY + newDirectionY2 * strand[idx].Radius;

                if (AbsF(strand[idx].PositionX) < thresholdValue)
                {
                    strand[idx].PositionX = 0f;
                }

                if (delay != 0f)
                {
                    strand[idx].VelocityX = strand[idx].PositionX - strand[idx].LastPositionX;
                    strand[idx].VelocityY = strand[idx].PositionY - strand[idx].LastPositionY;
                    strand[idx].VelocityX /= delay;
                    strand[idx].VelocityY /= delay;
                    strand[idx].VelocityX *= strand[idx].Mobility;
                    strand[idx].VelocityY *= strand[idx].Mobility;
                }

                strand[idx].ForceX = 0f;
                strand[idx].ForceY = 0f;
                strand[idx].LastGravityX = currentGravityX;
                strand[idx].LastGravityY = currentGravityY;
            }
        }

        /// <summary>
        /// SDK UpdateParticlesForStabilization: velocity is zeroed and each
        /// particle snaps to one normalized-force radius from its predecessor.
        /// </summary>
        internal static void UpdateParticlesForStabilization(
            CubismPhysicsParticle[] strand, int baseParticleIndex, int strandCount,
            float totalTranslationX, float totalTranslationY, float totalAngle,
            float windX, float windY, float thresholdValue)
        {
            strand[baseParticleIndex].PositionX = totalTranslationX;
            strand[baseParticleIndex].PositionY = totalTranslationY;

            float totalRadian = DegreesToRadian(totalAngle);
            float currentGravityX = SinF(totalRadian);
            float currentGravityY = CosF(totalRadian);
            Normalize(ref currentGravityX, ref currentGravityY);

            for (int i = 1; i < strandCount; ++i)
            {
                int idx = baseParticleIndex + i;

                strand[idx].ForceX = currentGravityX * strand[idx].Acceleration + windX;
                strand[idx].ForceY = currentGravityY * strand[idx].Acceleration + windY;

                strand[idx].LastPositionX = strand[idx].PositionX;
                strand[idx].LastPositionY = strand[idx].PositionY;

                strand[idx].VelocityX = 0f;
                strand[idx].VelocityY = 0f;

                float forceX = strand[idx].ForceX;
                float forceY = strand[idx].ForceY;
                Normalize(ref forceX, ref forceY);

                forceX *= strand[idx].Radius;
                forceY *= strand[idx].Radius;
                strand[idx].PositionX = strand[idx - 1].PositionX + forceX;
                strand[idx].PositionY = strand[idx - 1].PositionY + forceY;

                if (AbsF(strand[idx].PositionX) < thresholdValue)
                {
                    strand[idx].PositionX = 0f;
                }

                strand[idx].ForceX = 0f;
                strand[idx].ForceY = 0f;
                strand[idx].LastGravityX = currentGravityX;
                strand[idx].LastGravityY = currentGravityY;
            }
        }

        // ==================================================================
        // SDK math equivalents (bit-exact formulas; do not "simplify").
        // ==================================================================

        /// <summary>SDK CubismMath::DegreesToRadian — note the divide-first order.</summary>
        internal static float DegreesToRadian(float degrees)
        {
            return (degrees / 180f) * Pi;
        }

        /// <summary>SDK CubismMath::DirectionToRadian — atan2 difference wrapped into [-Pi, Pi].</summary>
        internal static float DirectionToRadian(float fromX, float fromY, float toX, float toY)
        {
            float q1 = MathF.Atan2(toY, toX);
            float q2 = MathF.Atan2(fromY, fromX);

            float ret = q1 - q2;

            while (ret < -Pi)
            {
                ret += Pi * 2f;
            }

            while (ret > Pi)
            {
                ret -= Pi * 2f;
            }

            return ret;
        }

        /// <summary>SDK CubismMath::RadianToDirection — (sin, cos).</summary>
        internal static void RadianToDirection(float totalAngle, out float x, out float y)
        {
            x = SinF(totalAngle);
            y = CosF(totalAngle);
        }

        /// <summary>
        /// SDK CubismVector2::Normalize — note that native uses
        /// powf(squareLength, 0.5f), not sqrtf, and does not guard zero length.
        /// </summary>
        internal static void Normalize(ref float x, ref float y)
        {
            float length = MathF.Pow(x * x + y * y, 0.5f);

            x = x / length;
            y = y / length;
        }

        private static float SinF(float x) => MathF.Sin(x);

        private static float CosF(float x) => MathF.Cos(x);

        private static float AbsF(float x) => MathF.Abs(x);

        private static float MinF(float l, float r) => (l > r) ? r : l;

        private static float MaxF(float l, float r) => (l > r) ? l : r;

        /// <summary>Resolves a parameter id to its index; -1 when not present (native asserts instead).</summary>
        private static unsafe int ResolveParameterIndex(CubismNativeModel model, string parameterId)
        {
            var idsPtr = model.GetParameterIds();
            for (int i = 0; i < model.ParameterCount; i++)
            {
                if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, i) == parameterId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Maps a physics3.json type tag to its source enum; unknown tags behave as X (enum value 0, like native).</summary>
        private static CubismPhysicsSource ParseSource(string value)
        {
            switch (value)
            {
                case "Angle": return CubismPhysicsSource.Angle;
                case "Y": return CubismPhysicsSource.Y;
                default: return CubismPhysicsSource.X;
            }
        }
    }
}
