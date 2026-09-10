using System;
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
    /// .physics3.json file. Positions are computed relative to the chain anchor.
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
    /// Defines which particle vertex to read, the output type (X, Y, angle),
    /// translation scale, weight, and reflection.
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
    /// JSON parser for the Cubism .physics3.json format.
    /// Reads the Meta section (fps, gravity, wind) and the PhysicsSettings
    /// array (sub-rigs with their inputs, outputs, and vertices/particles).
    /// 
    /// All data is accessed through typed out-parameter methods that
    /// parse JSON elements directly — avoiding intermediate allocation
    /// of temporary data structures.
    /// </summary>
    public class CubismPhysicsJson
    {
        private readonly JsonDocument _json;

        /// <summary>
        /// Parses the given .physics3.json bytes.
        /// </summary>
        public CubismPhysicsJson(byte[] jsonBytes)
        {
            _json = JsonDocument.Parse(jsonBytes);
        }

        /// <summary>Physics simulation FPS from the Meta section, default 30.</summary>
        public float GetFps()
        {
            var meta = _json.RootElement.GetProperty("Meta");
            return meta.TryGetProperty("Fps", out var v) ? v.GetSingle() : 30f;
        }

        /// <summary>Number of sub-rig entries in PhysicsSettings.</summary>
        public int GetSubRigCount() =>
            _json.RootElement.TryGetProperty("PhysicsSettings", out var arr) ? arr.GetArrayLength() : 0;

        /// <summary>Reads gravity vector from Meta.EffectiveForces.Gravity. Defaults to (0, -1).</summary>
        public void GetGravity(out float x, out float y)
        {
            x = 0; y = -1;
            var meta = _json.RootElement.GetProperty("Meta");
            if (meta.TryGetProperty("EffectiveForces", out var ef) &&
                ef.TryGetProperty("Gravity", out var g))
            {
                x = g.TryGetProperty("X", out var gx) ? gx.GetSingle() : 0f;
                y = g.TryGetProperty("Y", out var gy) ? gy.GetSingle() : -1f;
            }
        }

        /// <summary>Reads wind vector from Meta.EffectiveForces.Wind. Defaults to (0, 0).</summary>
        public void GetWind(out float x, out float y)
        {
            x = 0; y = 0;
            var meta = _json.RootElement.GetProperty("Meta");
            if (meta.TryGetProperty("EffectiveForces", out var ef) &&
                ef.TryGetProperty("Wind", out var w))
            {
                x = w.TryGetProperty("X", out var wx) ? wx.GetSingle() : 0f;
                y = w.TryGetProperty("Y", out var wy) ? wy.GetSingle() : 0f;
            }
        }

        /// <summary>Number of input entries in the given sub-rig.</summary>
        public int GetInputCount(int subRigIndex) =>
            GetSubRig(subRigIndex).GetProperty("Input").GetArrayLength();

        /// <summary>Number of output entries in the given sub-rig.</summary>
        public int GetOutputCount(int subRigIndex) =>
            GetSubRig(subRigIndex).GetProperty("Output").GetArrayLength();

        /// <summary>Number of vertex/particle entries in the given sub-rig.</summary>
        public int GetVertexCount(int subRigIndex) =>
            GetSubRig(subRigIndex).GetProperty("Vertices").GetArrayLength();

        /// <summary>Reads position normalization (min, max, default) for the given sub-rig.</summary>
        public void GetNormalizationPosition(int subRigIndex, out CubismPhysicsNormalization norm)
        {
            norm = default;
            var sr = GetSubRig(subRigIndex);
            if (!sr.TryGetProperty("Normalization", out var n)) return;
            if (!n.TryGetProperty("Position", out var pos)) return;
            norm.Minimum = pos.TryGetProperty("Minimum", out var p) ? p.GetSingle() : -30f;
            norm.Maximum = pos.TryGetProperty("Maximum", out var pp) ? pp.GetSingle() : 30f;
            norm.Default = pos.TryGetProperty("Default", out var pd) ? pd.GetSingle() : 0f;
        }

        /// <summary>Reads angle normalization (min, max, default) for the given sub-rig.</summary>
        public void GetNormalizationAngle(int subRigIndex, out CubismPhysicsNormalization norm)
        {
            norm = default;
            var sr = GetSubRig(subRigIndex);
            if (!sr.TryGetProperty("Normalization", out var n)) return;
            if (!n.TryGetProperty("Angle", out var angle)) return;
            norm.Minimum = angle.TryGetProperty("Minimum", out var p) ? p.GetSingle() : -30f;
            norm.Maximum = angle.TryGetProperty("Maximum", out var pp) ? pp.GetSingle() : 30f;
            norm.Default = angle.TryGetProperty("Default", out var pd) ? pd.GetSingle() : 0f;
        }

        /// <summary>Reads a single input entry: parameter ID, weight, type, reflect flag.</summary>
        public void GetInput(int subRigIndex, int inputIndex, out string parameterId, out float weight,
            out CubismPhysicsSource type, out bool reflect)
        {
            var inp = GetSubRig(subRigIndex).GetProperty("Input")[inputIndex];
            parameterId = inp.GetProperty("Source").GetProperty("Id").GetString();
            weight = inp.TryGetProperty("Weight", out var w) ? w.GetSingle() : 1f;
            type = ParseSource(inp.TryGetProperty("Type", out var t) ? t.GetString() : "X");
            reflect = inp.TryGetProperty("Reflect", out var r) && r.GetBoolean();
        }

        /// <summary>Reads a single output entry: destination parameter ID, vertex index, scales, type, reflect.</summary>
        public void GetOutput(int subRigIndex, int outputIndex, out string parameterId, out int vertexIndex,
            out float scaleX, out float scaleY, out float weight, out CubismPhysicsSource type, out bool reflect)
        {
            var outp = GetSubRig(subRigIndex).GetProperty("Output")[outputIndex];
            parameterId = outp.GetProperty("Destination").GetProperty("Id").GetString();
            vertexIndex = outp.TryGetProperty("VertexIndex", out var v) ? v.GetInt32() : 0;
            scaleX = scaleY = outp.TryGetProperty("Scale", out var s) ? s.GetSingle() : 1f;
            weight = outp.TryGetProperty("Weight", out var w) ? w.GetSingle() : 1f;
            type = ParseSource(outp.TryGetProperty("Type", out var t) ? t.GetString() : "X");
            reflect = outp.TryGetProperty("Reflect", out var r) && r.GetBoolean();
        }

        /// <summary>Reads a single particle/vertex: mobility, delay, acceleration, radius, position.</summary>
        public void GetParticle(int subRigIndex, int vertexIndex,
            out float mobility, out float delay, out float acceleration, out float radius,
            out float posX, out float posY)
        {
            var verts = GetSubRig(subRigIndex).GetProperty("Vertices")[vertexIndex];
            mobility = verts.TryGetProperty("Mobility", out var m) ? m.GetSingle() : 1f;
            delay = verts.TryGetProperty("Delay", out var d) ? d.GetSingle() : 0f;
            acceleration = verts.TryGetProperty("Acceleration", out var a) ? a.GetSingle() : 1f;
            radius = verts.TryGetProperty("Radius", out var r) ? r.GetSingle() : 1f;
            var pos = verts.GetProperty("Position");
            posX = pos.TryGetProperty("X", out var px) ? px.GetSingle() : 0f;
            posY = pos.TryGetProperty("Y", out var py) ? py.GetSingle() : 0f;
        }

        private JsonElement GetSubRig(int index) =>
            _json.RootElement.GetProperty("PhysicsSettings")[index];

        private static CubismPhysicsSource ParseSource(string v) => v switch
        {
            "Angle" => CubismPhysicsSource.Angle,
            "Y" => CubismPhysicsSource.Y,
            _ => CubismPhysicsSource.X
        };
    }
}
