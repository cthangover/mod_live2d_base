using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Manages pose-based part visibility from a Cubism .pose3.json file.
    /// Poses organize model parts into mutual-exclusion groups — only one
    /// part in each group is visible at a time. This is commonly used for
    /// eye states (open/closed/surprised) and other binary/multi-state
    /// part switches.
    /// 
    /// How it works:
    /// <list type="number">
    ///   <item>On model change (detected via ModelPtr comparison), all
    ///       part groups are reset: the first part in each group gets
    ///       opacity 1, others get opacity 0</item>
    ///   <item>Each frame, the currently visible part in each group
    ///       fades toward opacity 1 while all others fade toward 0</item>
    ///   <item>Linked parts (defined by the "Link" array in pose data)
    ///       copy their parent's opacity, cascading visibility changes</item>
    /// </list>
    /// 
    /// The fade uses a constant rate (1 / fadeTimeSeconds per second).
    /// A piecewise-linear alpha calculation with a crossing threshold (Phi=0.5)
    /// prevents parts from being partially visible during transitions.
    /// </summary>
    public class CubismPose
    {
        private struct PartData
        {
            public string PartId;
            public int PartIndex;
            public int ParameterIndex;
            public List<PartData> Link;
        }

        private readonly List<List<PartData>> _partGroups = new();
        private readonly float _fadeTimeSeconds;
        private IntPtr _lastModel = IntPtr.Zero;

        /// <summary>
        /// Parses a .pose3.json byte buffer.
        /// Extracts the global fade time and all part groups with their
        /// linked-part chains.
        /// </summary>
        public CubismPose(byte[] pose3JsonBytes)
        {
            if (pose3JsonBytes == null || pose3JsonBytes.Length == 0) return;

            var json = JsonDocument.Parse(pose3JsonBytes);
            var root = json.RootElement;
            _fadeTimeSeconds = root.TryGetProperty("FadeInTime", out var ft) ? ft.GetSingle() : 0.5f;

            if (!root.TryGetProperty("Groups", out var groups)) return;

            foreach (var group in groups.EnumerateArray())
            {
                var partList = new List<PartData>();
                foreach (var part in group.EnumerateArray())
                {
                    var pd = new PartData
                    {
                        PartId = part.GetProperty("Id").GetString(),
                        PartIndex = -1,
                        ParameterIndex = -1,
                        Link = new List<PartData>()
                    };

                    if (part.TryGetProperty("Link", out var links))
                    {
                        foreach (var link in links.EnumerateArray())
                            pd.Link.Add(new PartData { PartId = link.GetString(), PartIndex = -1, ParameterIndex = -1 });
                    }

                    partList.Add(pd);
                }
                _partGroups.Add(partList);
            }
        }

        /// <summary>
        /// Per-frame pose update. Detects model pointer changes to trigger
        /// a full reset, then fades parts within each group and propagates
        /// opacities to linked parts.
        /// Called from <see cref="CubismModelNode.OnUpdate"/> after expressions.
        /// </summary>
        public unsafe void UpdateParameters(CubismNativeModel model, float deltaTimeSeconds)
        {
            if (model == null || model.ModelPtr == IntPtr.Zero) return;

            if (_lastModel != model.ModelPtr)
            {
                _lastModel = model.ModelPtr;
                Reset(model);
            }

            foreach (var group in _partGroups)
                DoFade(model, group, deltaTimeSeconds);

            CopyPartOpacities(model);
        }

        private unsafe void Reset(CubismNativeModel model)
        {
            var idsPtr = model.GetPartIds();
            var opacities = model.GetPartOpacities();
            int partCount = model.GetPartCount();

            foreach (var group in _partGroups)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    var pd = group[i];
                    pd.PartIndex = -1;
                    pd.ParameterIndex = -1;

                    for (int pi = 0; pi < partCount; pi++)
                    {
                        if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, pi) == pd.PartId)
                        {
                            pd.PartIndex = pi;
                            opacities[pi] = i == 0 ? 1f : 0f;
                            break;
                        }
                    }

                    group[i] = pd;
                }
            }
        }

        private unsafe void DoFade(CubismNativeModel model, List<PartData> group, float deltaTimeSeconds)
        {
            const float Phi = 0.5f;
            const float BackOpacityThreshold = 0.15f;

            var idsPtr = model.GetPartIds();
            var opacities = model.GetPartOpacities();
            int partCount = model.GetPartCount();

            float newOpacity = 1f;
            int visibleIndex = 0;

            for (int i = 0; i < group.Count; i++)
            {
                var pd = group[i];
                if (pd.PartIndex < 0) continue;

                if (opacities[pd.PartIndex] > 0.001f)
                {
                    visibleIndex = i;
                    break;
                }
            }

            for (int i = 0; i < group.Count; i++)
            {
                var pd = group[i];
                if (pd.PartIndex < 0) continue;

                if (i == visibleIndex)
                {
                    var fadeRate = deltaTimeSeconds / _fadeTimeSeconds;
                    newOpacity = MathF.Min(opacities[pd.PartIndex] + fadeRate, 1f);
                    opacities[pd.PartIndex] = newOpacity;
                }
                else
                {
                    float a1;
                    if (newOpacity < Phi)
                        a1 = newOpacity * (Phi - 1f) / Phi + 1f;
                    else
                        a1 = (1f - newOpacity) * Phi / (1f - Phi);

                    var backOpacity = (1f - a1) * (1f - newOpacity);
                    if (backOpacity > BackOpacityThreshold)
                        a1 = 1f - BackOpacityThreshold / (1f - newOpacity);

                    opacities[pd.PartIndex] = MathF.Min(opacities[pd.PartIndex], a1);
                }
            }
        }

        private unsafe void CopyPartOpacities(CubismNativeModel model)
        {
            var idsPtr = model.GetPartIds();
            var opacities = model.GetPartOpacities();
            int partCount = model.GetPartCount();

            foreach (var group in _partGroups)
            {
                for (int gi = 0; gi < group.Count; gi++)
                {
                    var pd = group[gi];
                    if (pd.PartIndex < 0 || pd.Link == null) continue;

                    var parentOpacity = opacities[pd.PartIndex];
                    for (int li = 0; li < pd.Link.Count; li++)
                    {
                        var link = pd.Link[li];
                        for (int pi = 0; pi < partCount; pi++)
                        {
                            if (CubismNativeModel.ReadStringFromPtrArray(idsPtr, pi) == link.PartId)
                            {
                                link.PartIndex = pi;
                                opacities[pi] = parentOpacity;
                                pd.Link[li] = link;
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}
