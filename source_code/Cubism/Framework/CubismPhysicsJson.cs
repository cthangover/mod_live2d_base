using System.Collections.Generic;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Physics input/output dimensionality.
    /// </summary>
    public enum CubismPhysicsSource { X, Y, Angle }

    /// <summary>
    /// Normalization range for mapping raw parameter values to normalized physics inputs.
    /// </summary>
    public struct CubismPhysicsNormalization { public float Minimum; public float Maximum; public float Default; }

    /// <summary>
    /// A single particle in a physics chain. Holds position, velocity, force accumulators,
    /// and per-particle properties (mobility, delay, acceleration, radius) from the
    /// .physics3.json file. Mirrors SDK CubismPhysicsParticle.
    /// </summary>
    public struct CubismPhysicsParticle
    {
        public float InitialX, InitialY;
        public float Mobility;
        public float Delay;
        public float Acceleration;
        public float Radius;
        public float PositionX, PositionY;
        public float LastPositionX, LastPositionY;
        public float LastGravityX, LastGravityY;
        public float ForceX, ForceY;
        public float VelocityX, VelocityY;
    }

    /// <summary>
    /// A sub-rig groups a contiguous range of inputs, outputs, and particles
    /// that form one independent physics chain. Each sub-rig has its own
    /// normalization settings for position and angle inputs.
    /// </summary>
    public struct CubismPhysicsSubRig
    {
        public int InputCount, OutputCount, ParticleCount;
        public int BaseInputIndex, BaseOutputIndex, BaseParticleIndex;
        public CubismPhysicsNormalization NormalizationPosition;
        public CubismPhysicsNormalization NormalizationAngle;
    }

    /// <summary>
    /// Maps a model parameter to a physics chain input (X, Y, or angle)
    /// with a configurable weight and optional reflection.
    /// </summary>
    public struct CubismPhysicsInput
    {
        public string ParameterId;
        public int SourceParameterIndex;
        public float Weight;
        public CubismPhysicsSource Type;
        public bool Reflect;
    }

    /// <summary>
    /// Maps a physics chain output back to a model parameter.
    /// <see cref="VertexIndex"/> is the pendulum vertex index relative to the
    /// owning sub-rig's base particle (SDK semantics). The translation scale is
    /// deliberately never loaded: native CubismPhysicsJson reads only the
    /// single "Scale" value into <see cref="AngleScale"/>, so X/Y-typed outputs
    /// scale by zero exactly like the native implementation.
    /// </summary>
    public struct CubismPhysicsOutput
    {
        public string ParameterId;
        public int DestinationParameterIndex;
        public int VertexIndex;
        public float TranslationScaleX, TranslationScaleY;
        public float AngleScale;
        public float Weight;
        public CubismPhysicsSource Type;
        public bool Reflect;
        public float ValueBelowMinimum;
        public float ValueExceededMaximum;
    }

    /// <summary>
    /// Complete physics rig data structure. Holds all sub-rigs, flattened
    /// input/output/particle arrays, and global gravity/wind/fps settings.
    /// Used by <see cref="CubismPhysics"/> as its internal state.
    /// </summary>
    public class CubismPhysicsRig
    {
        public List<CubismPhysicsSubRig> SubRigs = new();
        public List<CubismPhysicsInput> Inputs = new();
        public List<CubismPhysicsOutput> Outputs = new();
        public CubismPhysicsParticle[] Particles = System.Array.Empty<CubismPhysicsParticle>();
        public float GravityX, GravityY;
        public float WindX, WindY;
        public float Fps;
    }

    /// <summary>
    /// JSON parser for the Cubism .physics3.json format. Field-by-field port of
    /// Cubism SDK for Native 5-r.5 Physics/CubismPhysicsJson.cpp: every getter
    /// reads the same key at the same JSON path. Missing numeric values yield
    /// 0 / false like the native error-value accessors; in particular a
    /// missing Meta.Fps yields 0 (the simulation then steps per display frame).
    /// Note: native does not read InitialPosition/InitialVelocity for vertices —
    /// those keys are ignored here as well; particles are laid out by
    /// CubismPhysics.Initialize().
    /// </summary>
    public class CubismPhysicsJson
    {
        private readonly JsonDocument _json;

        /// <summary>Parses the given .physics3.json bytes.</summary>
        public CubismPhysicsJson(byte[] jsonBytes)
        {
            _json = JsonDocument.Parse(jsonBytes);
        }

        // ------------------------------------------------------------------
        // Meta
        // ------------------------------------------------------------------

        /// <summary>Gravity vector from Meta.EffectiveForces.Gravity (missing values read as 0).</summary>
        public void GetGravity(out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (_json.RootElement.TryGetProperty("Meta", out var meta) &&
                meta.TryGetProperty("EffectiveForces", out var effectiveForces) &&
                effectiveForces.TryGetProperty("Gravity", out var gravity))
            {
                x = GetFloat(gravity, "X");
                y = GetFloat(gravity, "Y");
            }
        }

        /// <summary>Wind vector from Meta.EffectiveForces.Wind (missing values read as 0).</summary>
        public void GetWind(out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (_json.RootElement.TryGetProperty("Meta", out var meta) &&
                meta.TryGetProperty("EffectiveForces", out var effectiveForces) &&
                effectiveForces.TryGetProperty("Wind", out var wind))
            {
                x = GetFloat(wind, "X");
                y = GetFloat(wind, "Y");
            }
        }

        /// <summary>Simulation FPS from Meta.Fps; 0 when absent (native behavior).</summary>
        public float GetFps()
        {
            return _json.RootElement.TryGetProperty("Meta", out var meta) ? GetFloat(meta, "Fps") : 0f;
        }

        /// <summary>Sub-rig count from Meta.PhysicsSettingCount.</summary>
        public int GetSubRigCount()
        {
            if (_json.RootElement.TryGetProperty("Meta", out var meta) &&
                meta.TryGetProperty("PhysicsSettingCount", out var count))
            {
                return count.GetInt32();
            }

            // Defensive fallback: use the actual settings array length
            // (native would return 0 when the key is absent).
            return _json.RootElement.TryGetProperty("PhysicsSettings", out var settings)
                ? settings.GetArrayLength()
                : 0;
        }

        /// <summary>Total input count from Meta.TotalInputCount.</summary>
        public int GetTotalInputCount() => GetMetaInt("TotalInputCount");

        /// <summary>Total output count from Meta.TotalOutputCount.</summary>
        public int GetTotalOutputCount() => GetMetaInt("TotalOutputCount");

        /// <summary>Total vertex/particle count from Meta.VertexCount.</summary>
        public int GetVertexCount() => GetMetaInt("VertexCount");

        // ------------------------------------------------------------------
        // Normalization
        // ------------------------------------------------------------------

        /// <summary>Position normalization (Minimum/Maximum/Default) for a sub-rig; missing values read as 0.</summary>
        public void GetNormalizationPosition(int subRigIndex, out CubismPhysicsNormalization norm)
        {
            norm = default;
            if (GetSubRig(subRigIndex).TryGetProperty("Normalization", out var n) &&
                n.TryGetProperty("Position", out var pos))
            {
                norm.Minimum = GetFloat(pos, "Minimum");
                norm.Maximum = GetFloat(pos, "Maximum");
                norm.Default = GetFloat(pos, "Default");
            }
        }

        /// <summary>Angle normalization (Minimum/Maximum/Default) for a sub-rig; missing values read as 0.</summary>
        public void GetNormalizationAngle(int subRigIndex, out CubismPhysicsNormalization norm)
        {
            norm = default;
            if (GetSubRig(subRigIndex).TryGetProperty("Normalization", out var n) &&
                n.TryGetProperty("Angle", out var angle))
            {
                norm.Minimum = GetFloat(angle, "Minimum");
                norm.Maximum = GetFloat(angle, "Maximum");
                norm.Default = GetFloat(angle, "Default");
            }
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------

        /// <summary>Number of input entries in the given sub-rig.</summary>
        public int GetInputCount(int subRigIndex) => GetEntriesLength(subRigIndex, "Input");

        /// <summary>Input weight (Input[i].Weight).</summary>
        public float GetInputWeight(int subRigIndex, int inputIndex) =>
            GetFloat(GetInputEntry(subRigIndex, inputIndex), "Weight");

        /// <summary>Input reflect flag (Input[i].Reflect).</summary>
        public bool GetInputReflect(int subRigIndex, int inputIndex) =>
            GetBool(GetInputEntry(subRigIndex, inputIndex), "Reflect");

        /// <summary>Input type tag (Input[i].Type): "X", "Y" or "Angle".</summary>
        public string GetInputType(int subRigIndex, int inputIndex) =>
            GetString(GetInputEntry(subRigIndex, inputIndex), "Type");

        /// <summary>Source parameter id (Input[i].Source.Id).</summary>
        public string GetInputSourceId(int subRigIndex, int inputIndex)
        {
            var entry = GetInputEntry(subRigIndex, inputIndex);
            return entry.TryGetProperty("Source", out var source) ? GetString(source, "Id") : null;
        }

        // ------------------------------------------------------------------
        // Output
        // ------------------------------------------------------------------

        /// <summary>Number of output entries in the given sub-rig.</summary>
        public int GetOutputCount(int subRigIndex) => GetEntriesLength(subRigIndex, "Output");

        /// <summary>Pendulum vertex index (Output[i].VertexIndex), relative to the sub-rig base particle.</summary>
        public int GetOutputVertexIndex(int subRigIndex, int outputIndex) =>
            GetInt(GetOutputEntry(subRigIndex, outputIndex), "VertexIndex");

        /// <summary>Angle scale (Output[i].Scale). Native maps the single "Scale" value to the angle scale only.</summary>
        public float GetOutputAngleScale(int subRigIndex, int outputIndex) =>
            GetFloat(GetOutputEntry(subRigIndex, outputIndex), "Scale");

        /// <summary>Output weight (Output[i].Weight).</summary>
        public float GetOutputWeight(int subRigIndex, int outputIndex) =>
            GetFloat(GetOutputEntry(subRigIndex, outputIndex), "Weight");

        /// <summary>Destination parameter id (Output[i].Destination.Id).</summary>
        public string GetOutputsDestinationId(int subRigIndex, int outputIndex)
        {
            var entry = GetOutputEntry(subRigIndex, outputIndex);
            return entry.TryGetProperty("Destination", out var destination) ? GetString(destination, "Id") : null;
        }

        /// <summary>Output type tag (Output[i].Type): "X", "Y" or "Angle".</summary>
        public string GetOutputType(int subRigIndex, int outputIndex) =>
            GetString(GetOutputEntry(subRigIndex, outputIndex), "Type");

        /// <summary>Output reflect flag (Output[i].Reflect).</summary>
        public bool GetOutputReflect(int subRigIndex, int outputIndex) =>
            GetBool(GetOutputEntry(subRigIndex, outputIndex), "Reflect");

        // ------------------------------------------------------------------
        // Particle (Vertices)
        // ------------------------------------------------------------------

        /// <summary>Number of vertex/particle entries in the given sub-rig.</summary>
        public int GetParticleCount(int subRigIndex) => GetEntriesLength(subRigIndex, "Vertices");

        /// <summary>Particle mobility (Vertices[i].Mobility).</summary>
        public float GetParticleMobility(int subRigIndex, int vertexIndex) =>
            GetFloat(GetVertexEntry(subRigIndex, vertexIndex), "Mobility");

        /// <summary>Particle delay (Vertices[i].Delay).</summary>
        public float GetParticleDelay(int subRigIndex, int vertexIndex) =>
            GetFloat(GetVertexEntry(subRigIndex, vertexIndex), "Delay");

        /// <summary>Particle acceleration (Vertices[i].Acceleration).</summary>
        public float GetParticleAcceleration(int subRigIndex, int vertexIndex) =>
            GetFloat(GetVertexEntry(subRigIndex, vertexIndex), "Acceleration");

        /// <summary>Particle radius (Vertices[i].Radius).</summary>
        public float GetParticleRadius(int subRigIndex, int vertexIndex) =>
            GetFloat(GetVertexEntry(subRigIndex, vertexIndex), "Radius");

        /// <summary>Particle position (Vertices[i].Position.X/Y).</summary>
        public void GetParticlePosition(int subRigIndex, int vertexIndex, out float x, out float y)
        {
            var entry = GetVertexEntry(subRigIndex, vertexIndex);
            x = 0f;
            y = 0f;
            if (entry.TryGetProperty("Position", out var position))
            {
                x = GetFloat(position, "X");
                y = GetFloat(position, "Y");
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private JsonElement GetSubRig(int index) =>
            _json.RootElement.GetProperty("PhysicsSettings")[index];

        private JsonElement GetInputEntry(int subRigIndex, int inputIndex) =>
            GetSubRig(subRigIndex).GetProperty("Input")[inputIndex];

        private JsonElement GetOutputEntry(int subRigIndex, int outputIndex) =>
            GetSubRig(subRigIndex).GetProperty("Output")[outputIndex];

        private JsonElement GetVertexEntry(int subRigIndex, int vertexIndex) =>
            GetSubRig(subRigIndex).GetProperty("Vertices")[vertexIndex];

        private int GetEntriesLength(int subRigIndex, string name)
        {
            var subRig = GetSubRig(subRigIndex);
            return subRig.TryGetProperty(name, out var entries) ? entries.GetArrayLength() : 0;
        }

        private int GetMetaInt(string name) =>
            _json.RootElement.TryGetProperty("Meta", out var meta) ? GetInt(meta, name) : 0;

        private static float GetFloat(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetSingle() : 0f;

        private static int GetInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetInt32() : 0;

        private static bool GetBool(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static string GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : string.Empty;
    }
}
