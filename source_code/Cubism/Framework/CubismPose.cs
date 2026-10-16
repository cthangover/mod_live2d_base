using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Manages pose-based part visibility from a Cubism .pose3.json file.
    /// Pose groups organize model parts into mutual-exclusion groups — only one
    /// part in each group is visible at a time. Commonly used for eye states
    /// (open/closed/surprised) and other multi-state part switches.
    ///
    /// This is a faithful port of CubismPose.cpp (Cubism SDK for Native 5-r.5):
    /// <list type="number">
    ///   <item>Part groups are stored as a flattened list with per-group counts
    ///       (matching the SDK layout, so index arithmetic in Reset/DoFade is identical).</item>
    ///   <item>Each group part references both a part index (for opacity) and a
    ///       parameter index (for visibility). The visible part of a group is
    ///       decided by the PARAMETER VALUE (&gt; Epsilon), not by the part opacity.
    ///       If a pose parameter/part does not exist in the model, the SDK's
    ///       "not exist" maps are emulated, and the group falls back to its
    ///       first part with opacity 1.</item>
    ///   <item>Reset (on model change): the first part of each group gets
    ///       parameter value 1 / part opacity 1, all others get 0; linked parts
    ///       resolve their indices too.</item>
    ///   <item>DoFade: the visible part fades toward 1 at rate
    ///       deltaTime / fadeTimeSeconds while all other parts are pushed toward
    ///       0 with the SDK's piecewise-linear crossing formula (Phi = 0.5) and
    ///       back-opacity threshold (0.15). If no part is visible, the group
    ///       falls back to its first part with opacity 1. If fadeTimeSeconds is
    ///       0, the visible part becomes fully opaque immediately.</item>
    ///   <item>CopyPartOpacities: parts with a "Link" list copy their current
    ///       part opacity to every linked part.</item>
    /// </list>
    /// </summary>
    public class CubismPose
    {
        private const float Epsilon = 0.001f;
        private const float DefaultFadeInSeconds = 0.5f;
        private const float Phi = 0.5f;
        private const float BackOpacityThreshold = 0.15f;

        private sealed class PartData
        {
            public string PartId;
            public int PartIndex = -1;
            public int ParameterIndex = -1;
            public readonly List<PartData> Link = new();

            /// <summary>
            /// SDK CubismPose::PartData::Initialize — resolves both indices for
            /// this part id and initializes its pose parameter to 1.
            /// </summary>
            public void Initialize(CubismNativeModel model, CubismPose owner)
            {
                ParameterIndex = owner.FindParameterIndex(model, PartId);
                PartIndex = owner.FindPartIndex(model, PartId);
                owner.SetParameterValue(model, ParameterIndex, PartId, 1f);
            }
        }

        private readonly List<PartData> _partGroups = new();
        private readonly List<int> _partGroupCounts = new();
        private float _fadeTimeSeconds = DefaultFadeInSeconds;
        private IntPtr _lastModel = IntPtr.Zero;

        // Emulation of the SDK's _notExistParameterValues / _notExistPartOpacities maps:
        // pose3.json may reference parameters/parts that do not exist in the model.
        // They are keyed by id here (the SDK keys parameter entries by synthesized
        // indices; per-id keys are equivalent for this emulation).
        private readonly Dictionary<string, float> _notExistParameterValues = new();
        private readonly Dictionary<string, float> _notExistPartOpacities = new();

        /// <summary>
        /// Parses a .pose3.json byte buffer.
        /// Extracts the fade time and all part groups (flattened, with counts per
        /// group) including their linked-part chains.
        /// </summary>
        public CubismPose(byte[] pose3JsonBytes)
        {
            if (pose3JsonBytes == null || pose3JsonBytes.Length == 0) return;

            var json = JsonDocument.Parse(pose3JsonBytes);
            var root = json.RootElement;

            if (root.TryGetProperty("FadeInTime", out var ft) && ft.ValueKind == JsonValueKind.Number)
            {
                _fadeTimeSeconds = ft.GetSingle();
                if (_fadeTimeSeconds < 0f)
                    _fadeTimeSeconds = DefaultFadeInSeconds;
            }

            if (!root.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return;

            foreach (var group in groups.EnumerateArray())
            {
                var groupCount = 0;
                foreach (var part in group.EnumerateArray())
                {
                    var pd = new PartData
                    {
                        PartId = part.GetProperty("Id").GetString()
                    };

                    if (part.TryGetProperty("Link", out var links) && links.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var link in links.EnumerateArray())
                            pd.Link.Add(new PartData { PartId = link.GetString() });
                    }

                    _partGroups.Add(pd);
                    ++groupCount;
                }
                _partGroupCounts.Add(groupCount);
            }
        }

        /// <summary>
        /// Per-frame pose update, mirroring CubismPose::UpdateParameters.
        /// Detects model changes to trigger a full reset, then fades each group
        /// and propagates opacities to linked parts.
        /// Called from <see cref="CubismModelNode.OnUpdate"/> after expressions.
        /// </summary>
        public void UpdateParameters(CubismNativeModel model, float deltaTimeSeconds)
        {
            if (model == null || model.ModelPtr == IntPtr.Zero) return;

            // 前回のモデルと同じではないときは初期化が必要
            if (_lastModel != model.ModelPtr)
                Reset(model);

            _lastModel = model.ModelPtr;

            // 設定から時間を変更すると、経過時間がマイナスになることがあるので、経過時間0として対応。
            if (deltaTimeSeconds < 0f)
                deltaTimeSeconds = 0f;

            var beginIndex = 0;
            for (int i = 0; i < _partGroupCounts.Count; i++)
            {
                var partGroupCount = _partGroupCounts[i];
                DoFade(model, deltaTimeSeconds, beginIndex, partGroupCount);
                beginIndex += partGroupCount;
            }

            CopyPartOpacities(model);
        }

        /// <summary>SDK CubismPose::Reset — re-resolves indices and restores the
        /// default state: the first part of each group is visible (parameter 1,
        /// opacity 1), all others hidden, links re-initialized.</summary>
        private unsafe void Reset(CubismNativeModel model)
        {
            var beginIndex = 0;

            for (int gi = 0; gi < _partGroupCounts.Count; gi++)
            {
                var groupCount = _partGroupCounts[gi];

                for (int j = beginIndex; j < beginIndex + groupCount; j++)
                {
                    var pd = _partGroups[j];
                    pd.Initialize(model, this);

                    if (pd.PartIndex < 0)
                        continue;

                    SetPartOpacity(model, pd.PartIndex, pd.PartId, j == beginIndex ? 1f : 0f);
                    SetParameterValue(model, pd.ParameterIndex, pd.PartId, j == beginIndex ? 1f : 0f);

                    for (int k = 0; k < pd.Link.Count; k++)
                        pd.Link[k].Initialize(model, this);
                }

                beginIndex += groupCount;
            }
        }

        /// <summary>SDK CubismPose::DoFade — visibility is driven by the group
        /// part PARAMETER values; the visible part fades in while the rest are
        /// faded out with the crossing-threshold formula.</summary>
        private unsafe void DoFade(CubismNativeModel model, float deltaTimeSeconds, int beginIndex, int partGroupCount)
        {
            var visiblePartIndex = -1;
            var newOpacity = 1f;

            // 現在、表示状態になっているパーツを取得
            for (int i = beginIndex; i < beginIndex + partGroupCount; i++)
            {
                var pd = _partGroups[i];

                if (GetParameterValue(model, pd.ParameterIndex, pd.PartId) > Epsilon)
                {
                    if (visiblePartIndex >= 0)
                        break;

                    visiblePartIndex = i;

                    if (_fadeTimeSeconds == 0f)
                    {
                        newOpacity = 1f;
                        continue;
                    }

                    newOpacity = ComputeVisibleOpacity(
                        GetPartOpacity(model, pd.PartIndex, pd.PartId),
                        deltaTimeSeconds,
                        _fadeTimeSeconds);
                }
            }

            if (visiblePartIndex < 0)
            {
                visiblePartIndex = 0;
                newOpacity = 1f;
            }

            //  表示パーツ、非表示パーツの不透明度を設定する
            for (int i = beginIndex; i < beginIndex + partGroupCount; i++)
            {
                var pd = _partGroups[i];

                if (visiblePartIndex == i)
                {
                    SetPartOpacity(model, pd.PartIndex, pd.PartId, newOpacity); // 先に設定
                }
                else
                {
                    var opacity = GetPartOpacity(model, pd.PartIndex, pd.PartId);
                    var a1 = ComputeHiddenOpacity(newOpacity);

                    if (opacity > a1)
                        opacity = a1;

                    SetPartOpacity(model, pd.PartIndex, pd.PartId, opacity);
                }
            }
        }

        /// <summary>SDK CubismPose::CopyPartOpacities — every group part with a
        /// Link list copies its current part opacity to all linked parts.</summary>
        private unsafe void CopyPartOpacities(CubismNativeModel model)
        {
            foreach (var partData in _partGroups)
            {
                if (partData.Link.Count == 0)
                    continue; // 連動するパラメータはない

                var opacity = GetPartOpacity(model, partData.PartIndex, partData.PartId);

                for (int linkIndex = 0; linkIndex < partData.Link.Count; linkIndex++)
                {
                    var linkPart = partData.Link[linkIndex];

                    if (linkPart.PartIndex < 0)
                        continue;

                    SetPartOpacity(model, linkPart.PartIndex, linkPart.PartId, opacity);
                }
            }
        }

        /// <summary>Resolves a part id to a part index, or -1 when absent.</summary>
        private unsafe int FindPartIndex(CubismNativeModel model, string partId)
        {
            var idsPtr = model.GetPartIds();
            var partCount = model.GetPartCount();
            for (int i = 0; i < partCount; i++)
            {
                if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, i) == partId)
                    return i;
            }
            return -1;
        }

        /// <summary>Resolves a parameter id to a parameter index, or -1 when absent.</summary>
        private unsafe int FindParameterIndex(CubismNativeModel model, string parameterId)
        {
            var idsPtr = model.GetParameterIds();
            var parameterCount = model.ParameterCount;
            for (int i = 0; i < parameterCount; i++)
            {
                if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, i) == parameterId)
                    return i;
            }
            return -1;
        }

        private unsafe float GetParameterValue(CubismNativeModel model, int parameterIndex, string parameterId)
        {
            if (parameterIndex < 0)
                return _notExistParameterValues.TryGetValue(parameterId, out var v) ? v : 0f;
            return model.GetParameterValues()[parameterIndex];
        }

        private unsafe void SetParameterValue(CubismNativeModel model, int parameterIndex, string parameterId, float value)
        {
            if (parameterIndex < 0)
            {
                _notExistParameterValues[parameterId] = value;
                return;
            }
            model.GetParameterValues()[parameterIndex] = value;
        }

        private unsafe float GetPartOpacity(CubismNativeModel model, int partIndex, string partId)
        {
            if (partIndex < 0)
                return _notExistPartOpacities.TryGetValue(partId, out var v) ? v : 0f;
            return model.GetPartOpacities()[partIndex];
        }

        private unsafe void SetPartOpacity(CubismNativeModel model, int partIndex, string partId, float opacity)
        {
            if (partIndex < 0)
            {
                _notExistPartOpacities[partId] = opacity;
                return;
            }
            model.GetPartOpacities()[partIndex] = opacity;
        }

        /// <summary>
        /// Pure helper (SDK DoFade, visible branch): the visible part's opacity
        /// grows by deltaTimeSeconds / fadeTimeSeconds, clamped to 1.
        /// Internal for unit tests.
        /// </summary>
        internal static float ComputeVisibleOpacity(float currentOpacity, float deltaTimeSeconds, float fadeTimeSeconds)
        {
            var newOpacity = currentOpacity + (deltaTimeSeconds / fadeTimeSeconds);
            return newOpacity > 1f ? 1f : newOpacity;
        }

        /// <summary>
        /// Pure helper (SDK DoFade, hidden branch): piecewise-linear alpha with
        /// the Phi = 0.5 crossing and the 0.15 back-opacity threshold.
        /// Internal for unit tests.
        /// </summary>
        internal static float ComputeHiddenOpacity(float newOpacity)
        {
            float a1;
            if (newOpacity < Phi)
                a1 = newOpacity * (Phi - 1f) / Phi + 1f; // (0,1),(phi,phi)を通る直線式
            else
                a1 = (1f - newOpacity) * Phi / (1f - Phi); // (1,0),(phi,phi)を通る直線式

            var backOpacity = (1f - a1) * (1f - newOpacity);
            if (backOpacity > BackOpacityThreshold)
                a1 = 1f - BackOpacityThreshold / (1f - newOpacity);

            return a1;
        }
    }
}
