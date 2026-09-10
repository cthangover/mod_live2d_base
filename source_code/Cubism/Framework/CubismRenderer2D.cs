using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Cthangover.Core.Utils;
using Godot;
using Cthangover.Live2D.Cubism.Core;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Builds and updates 2D mesh instances for rendering a Cubism model in Godot.
    /// 
    /// Handles the full rendering pipeline:
    /// <list type="number">
    ///   <item><b>BuildModel</b> — Creates <see cref="ArrayMesh"/> instances from
    ///       native vertex/index data, applies textures, resolves shaders based
    ///       on drawable blend flags and mask configuration</item>
    ///   <item><b>UpdateModel</b> — Updates visibility, opacity, and vertex
    ///       positions each frame by reading CubismCore dynamic flags</item>
    ///   <item><b>Mask support</b> — Creates SubViewport-based mask textures
    ///       for drawables that have mask counts. Masked drawables use
    ///       specialized shaders (2d_cubism_mask_*) that composite the mask
    ///       texture with the main texture</item>
    /// </list>
    /// 
    /// Shader resolution delegates to <see cref="Cthangover.Core.Mods.ModManager.ResolveShader"/>.
    /// Shader names follow the pattern: 2d_cubism_[norm|mask]_[mix|add|mul][_inv]?
    /// where "norm" = no mask, "mask" = masked, "mix/add/mul" = blend mode,
    /// "_inv" = inverted mask.
    /// </summary>
    public class CubismRenderer2D
    {
        private MeshInstance2D[] _meshInstances;
        private ShaderMaterial[] _materials;
        private readonly Node2D _parentNode;
        private CubismNativeModel _model;

        private struct MaskGroupData
        {
            public SubViewport Viewport;
            public ViewportTexture Texture;
            public int[] MaskDrawableIndices;
            public MeshInstance2D[] MaskMeshes;
            public ArrayMesh[] MaskArrayMeshes;
            public int ViewportSize;
            public float OffsetX;
            public float OffsetY;
        }

        private readonly List<MaskGroupData> _maskGroups = new();
        private int[] _drawableMaskGroupIndex;
        private Color[] _drawableMaskChannel;

        /// <summary>
        /// Creates a renderer that attaches mesh instances as children of
        /// the given parent node.
        /// </summary>
        public CubismRenderer2D(Node2D parentNode)
        {
            _parentNode = parentNode;
        }

        /// <summary>
        /// Builds all mesh instances for the given model.
        /// Clears any previously built meshes. Creates mask SubViewports first,
        /// then iterates drawables in render order, building ArrayMesh instances
        /// with the appropriate shader and texture.
        /// </summary>
        public unsafe void BuildModel(CubismNativeModel model)
        {
            _model = model;
            ClearMeshes();

            var dc = model.DrawableCount;
            _meshInstances = new MeshInstance2D[dc];
            _materials = new ShaderMaterial[dc];
            GameLogger.Log("LIVE2D", $"BuildModel ENTER: drawables={dc} textures={model.Textures?.Length ?? 0} ppu={model.PixelsPerUnit} canvas={model.CanvasWidth}x{model.CanvasHeight}");

            var renderOrders = model.GetRenderOrders();
            GameLogger.Log("LIVE2D", $"BuildModel S1: renderOrders ptr ok");
            var constantFlags = model.GetConstantFlags();
            var texIndices = model.GetTextureIndices();
            var vertexCounts = model.GetVertexCounts();
            var vertexPositions = model.GetVertexPositions();
            var vertexUvs = model.GetVertexUvs();
            var indexCounts = model.GetIndexCounts();
            var indices = model.GetIndices();
            var opacities = model.GetOpacities();
            var maskCounts = model.GetMaskCounts();
            GameLogger.Log("LIVE2D", $"BuildModel S2: all native ptrs fetched");

            // Log first 10 renderOrders to understand mapping
            var roSample = new System.Text.StringBuilder("renderOrders: ");
            for (int i = 0; i < Math.Min(dc, 10); i++)
                roSample.Append($"[{i}]={renderOrders[i]} ");
            GameLogger.Log("LIVE2D", roSample.ToString());

            // Sanity: check renderOrders contain valid drawable indices
            for (int i = 0; i < dc; i++)
            {
                var ro = renderOrders[i];
                if (ro < 0 || ro >= dc)
                {
                    GameLogger.Log("LIVE2D", $"BuildModel BAD_RO: renderOrders[{i}]={ro} — OUT OF RANGE [0,{dc - 1}]");
                }
            }
            GameLogger.Log("LIVE2D", $"BuildModel S3: renderOrders validated");

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int di = 0; di < dc; di++)
            {
                var vc = vertexCounts[di];
                if (vc <= 0 || vc > 100000) { GameLogger.Log("LIVE2D", $"BuildModel BOUNDS_DIAG: di={di} vc={vc} SKIP"); continue; }
                var vp = vertexPositions[di];
                if (di == 0)
                    GameLogger.Log("LIVE2D", $"BuildModel: drawable[0] vc={vc} vp0=({vp[0].X},{vp[0].Y})");
                for (int v = 0; v < vc; v++)
                {
                    if (vp[v].X < minX) minX = vp[v].X;
                    if (vp[v].Y < minY) minY = vp[v].Y;
                    if (vp[v].X > maxX) maxX = vp[v].X;
                    if (vp[v].Y > maxY) maxY = vp[v].Y;
                }
            }
            GameLogger.Log("LIVE2D", $"BuildModel: vertexBounds=({minX:F3},{minY:F3})-({maxX:F3},{maxY:F3}) ppu={model.PixelsPerUnit}");

            for (int di = 0; di < dc; di++)
            {
                var vc = vertexCounts[di];
                if (vc <= 0 || vc > 100000) continue;
                var uv = vertexUvs[di];
                float uMin = 2, uMax = -1, vMin = 2, vMax = -1;
                for (int i = 0; i < vc; i++)
                {
                    if (uv[i].X < uMin) uMin = uv[i].X;
                    if (uv[i].Y < vMin) vMin = uv[i].Y;
                    if (uv[i].X > uMax) uMax = uv[i].X;
                    if (uv[i].Y > vMax) vMax = uv[i].Y;
                }
                GameLogger.Log("LIVE2D", $"BuildModel: drawable[{di}] uvBounds=({uMin:F3},{vMin:F3})-({uMax:F3},{vMax:F3})");
            }

            // Build mask data
            BuildMaskGroups(model, maskCounts);

            SetupMaskViewports(model);

            // Second pass: create drawable meshes — iterate by drawable index (matching C++ original)
            GameLogger.Log("LIVE2D", $"BuildModel S4: entering mesh loop, dc={dc}");
            for (int di = 0; di < dc; di++)
            {
                int vCount = vertexCounts[di];
                int iCount = indexCounts[di];
                if (vCount <= 0 || vCount > 100000 || iCount <= 0 || iCount > 500000) continue;

                var mesh = new ArrayMesh();
                var surfaceArray = new Godot.Collections.Array();
                surfaceArray.Resize((int)Mesh.ArrayType.Max);

                var verts = new Vector2[vCount];
                var uvs = new Vector2[vCount];
                var idxs = new int[iCount];

                var srcVerts = vertexPositions[di];
                var srcUvs = vertexUvs[di];
                var srcIndices = indices[di];

                for (int v = 0; v < vCount; v++)
                {
                    verts[v] = new Vector2(srcVerts[v].X * _model.PixelsPerUnit, -srcVerts[v].Y * _model.PixelsPerUnit);
                    uvs[v] = new Vector2(srcUvs[v].X, 1f - srcUvs[v].Y);
                }

                for (int t = 0; t < iCount; t++)
                    idxs[t] = srcIndices[t];

                surfaceArray[(int)Mesh.ArrayType.Vertex] = verts;
                surfaceArray[(int)Mesh.ArrayType.TexUV] = uvs;
                surfaceArray[(int)Mesh.ArrayType.Index] = idxs;

                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArray);

                var mi = new MeshInstance2D { Mesh = mesh, ZIndex = renderOrders[di], ZAsRelative = false };
                var texIndex = texIndices[di];
                var texture = texIndex >= 0 && texIndex < model.Textures.Length ? model.Textures[texIndex] : null;
                var opacity = opacities[di];
                GameLogger.Log("LIVE2D", $"BuildModel: drawable[{di}] opacity={opacity:F3}");

                var shader = ResolveShaderForDrawable(di, constantFlags, maskCounts);

                ShaderMaterial mat = null;
                if (shader != null)
                {
                    mat = new ShaderMaterial { Shader = shader };
                    mat.SetShaderParameter("color_base", new Color(1f, 1f, 1f, 1f));
                    mat.SetShaderParameter("color_multiply", new Color(1f, 1f, 1f, 1f));
                    mat.SetShaderParameter("color_screen", new Color(0f, 0f, 0f, 0f));
                    mat.SetShaderParameter("channel", new Color(0f, 0f, 0f, 0f));
                    mat.SetShaderParameter("mask_scale", 1f);
                    mat.SetShaderParameter("mesh_offset", Vector2.Zero);

                    if (texture != null)
                        mat.SetShaderParameter("tex_main", texture);

                    if (maskCounts[di] > 0)
                    {
                        var groupIdx = _drawableMaskGroupIndex[di];
                        if (groupIdx >= 0 && groupIdx < _maskGroups.Count)
                        {
                            var group = _maskGroups[groupIdx];
                            if (group.Texture != null)
                            {
                                mat.SetShaderParameter("tex_mask", group.Texture);
                                mat.SetShaderParameter("channel", _drawableMaskChannel[di]);
                                mat.SetShaderParameter("mask_scale", 1f);
                                mat.SetShaderParameter("mesh_offset", new Vector2(group.OffsetX, group.OffsetY));
                            }
                        }
                    }

                    mi.Material = mat;
                }

                if (texture != null)
                {
                    mat.SetShaderParameter("tex_main", texture);
                    GameLogger.Log("LIVE2D", $"BuildModel: drawable[{di}] texture Ok");
                }
                else
                {
                    GameLogger.Log("LIVE2D", $"BuildModel: drawable[{di}] texture MISSING (texIndex={texIndex})");
                }

                mi.Visible = true;
                _parentNode.AddChild(mi);

                GameLogger.Log("LIVE2D", $"BuildModel: drawable[{di}] materialSet={mi.Material != null} visible={mi.Visible} parent={mi.GetParent()?.Name}");
                _meshInstances[di] = mi;
                _materials[di] = mat;
            }
            GameLogger.Log("LIVE2D", $"BuildModel DONE: meshes created");
        }

        /// <summary>
        /// Returns the union AABB of all drawable meshes in local pixel space.
        /// Valid after <see cref="BuildModel"/> has completed.
        /// </summary>
        public Aabb GetContentBounds()
        {
            if (_meshInstances == null) return new Aabb();

            Aabb aabb = default;
            bool first = true;
            foreach (var mi in _meshInstances)
            {
                if (mi?.Mesh == null) continue;
                var meshAabb = ((ArrayMesh)mi.Mesh).GetAabb();
                if (first) { aabb = meshAabb; first = false; }
                else aabb = aabb.Merge(meshAabb);
            }
            return aabb;
        }

        private unsafe void BuildMaskGroups(CubismNativeModel model, int* maskCounts)
        {
            _maskGroups.Clear();
            _drawableMaskGroupIndex = new int[model.DrawableCount];
            _drawableMaskChannel = new Color[model.DrawableCount];
            for (int i = 0; i < model.DrawableCount; i++)
                _drawableMaskGroupIndex[i] = -1;

            var masks = model.GetMasks();
            var dc = model.DrawableCount;
            var maskSetToGroup = new Dictionary<string, int>();

            for (int di = 0; di < dc; di++)
            {
                var count = maskCounts[di];
                if (count == 0) continue;

                var maskIndices = masks[di];
                var sorted = new int[count];
                for (int mi = 0; mi < count; mi++)
                    sorted[mi] = maskIndices[mi];
                System.Array.Sort(sorted);

                var keyBuilder = new System.Text.StringBuilder();
                for (int mi = 0; mi < count; mi++)
                {
                    if (mi > 0) keyBuilder.Append(',');
                    keyBuilder.Append(sorted[mi]);
                }
                var key = keyBuilder.ToString();

                if (!maskSetToGroup.TryGetValue(key, out var groupIdx))
                {
                    groupIdx = _maskGroups.Count;
                    maskSetToGroup[key] = groupIdx;

                    var uniqueMaskDrawables = new System.Collections.Generic.List<int>();
                    for (int mi = 0; mi < count; mi++)
                        if (!uniqueMaskDrawables.Contains(sorted[mi]))
                            uniqueMaskDrawables.Add(sorted[mi]);

                    _maskGroups.Add(new MaskGroupData
                    {
                        MaskDrawableIndices = uniqueMaskDrawables.ToArray()
                    });
                }

                _drawableMaskGroupIndex[di] = groupIdx;

                var maskSet = _maskGroups[groupIdx].MaskDrawableIndices;
                float cr = 0f, cg = 0f, cb = 0f, ca = 0f;
                for (int mi = 0; mi < count; mi++)
                {
                    int slot = System.Array.IndexOf(maskSet, sorted[mi]);
                    if (slot >= 0)
                    {
                        switch (slot)
                        {
                            case 0: cr = 1f; break;
                            case 1: cg = 1f; break;
                            case 2: cb = 1f; break;
                            case 3: ca = 1f; break;
                        }
                    }
                }
                _drawableMaskChannel[di] = new Color(cr, cg, cb, ca);
            }
        }

        private unsafe void SetupMaskViewports(CubismNativeModel model)
        {
            for (int gi = 0; gi < _maskGroups.Count; gi++)
            {
                var group = _maskGroups[gi];
                var maskIndices = group.MaskDrawableIndices;
                var channelCount = maskIndices.Length;

                var ppu = model.PixelsPerUnit;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int mi = 0; mi < channelCount; mi++)
                {
                    var di = maskIndices[mi];
                    var vCount = model.GetVertexCounts()[di];
                    var vp = model.GetVertexPositions()[di];
                    for (int v = 0; v < vCount; v++)
                    {
                        float wx = vp[v].X * ppu;
                        float wy = -vp[v].Y * ppu;
                        if (wx < minX) minX = wx;
                        if (wy < minY) minY = wy;
                        if (wx > maxX) maxX = wx;
                        if (wy > maxY) maxY = wy;
                    }
                }

                float pad = 4f;
                float vpSize = Math.Max(maxX - minX + pad * 2f, maxY - minY + pad * 2f);
                vpSize = Math.Max(vpSize, 64f);
                int vpSizeInt = (int)Mathf.Ceil(vpSize);
                vpSizeInt = NextPowerOfTwo(vpSizeInt);

                var viewport = new SubViewport
                {
                    Size = new Vector2I(vpSizeInt, vpSizeInt),
                    TransparentBg = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always
                };
                _parentNode.AddChild(viewport);

                group.Viewport = viewport;
                group.Texture = viewport.GetTexture();
                group.ViewportSize = vpSizeInt;
                group.OffsetX = minX - pad;
                group.OffsetY = minY - pad;
                group.MaskMeshes = new MeshInstance2D[channelCount];
                group.MaskArrayMeshes = new ArrayMesh[channelCount];

                for (int mi = 0; mi < channelCount; mi++)
                {
                    var di = maskIndices[mi];
                    var mi2d = new MeshInstance2D();
                    viewport.AddChild(mi2d);
                    group.MaskMeshes[mi] = mi2d;
                    group.MaskArrayMeshes[mi] = BuildMaskArrayMesh(di, model, mi2d, mi, group.OffsetX, group.OffsetY);
                }

                _maskGroups[gi] = group;
            }
        }

        private unsafe ArrayMesh BuildMaskArrayMesh(int drawableIndex, CubismNativeModel model, MeshInstance2D mi2d, int channelSlot, float offsetX, float offsetY)
        {
            var vCount = model.GetVertexCounts()[drawableIndex];
            var iCount = model.GetIndexCounts()[drawableIndex];
            if (vCount == 0 || iCount == 0) return null;

            var mesh = new ArrayMesh();
            var surfaceArray = new Godot.Collections.Array();
            surfaceArray.Resize((int)Mesh.ArrayType.Max);

            var verts = new Vector2[vCount];
            var uvs = new Vector2[vCount];
            var idxs = new int[iCount];

            var srcVerts = model.GetVertexPositions()[drawableIndex];
            var srcUvs = model.GetVertexUvs()[drawableIndex];
            var srcIndices = model.GetIndices()[drawableIndex];
            var ppu = model.PixelsPerUnit;

            for (int v = 0; v < vCount; v++)
            {
                verts[v] = new Vector2(srcVerts[v].X * ppu - offsetX, -srcVerts[v].Y * ppu - offsetY);
                uvs[v] = new Vector2(srcUvs[v].X, 1f - srcUvs[v].Y);
            }

            for (int t = 0; t < iCount; t++)
                idxs[t] = srcIndices[t];

            surfaceArray[(int)Mesh.ArrayType.Vertex] = verts;
            surfaceArray[(int)Mesh.ArrayType.TexUV] = uvs;
            surfaceArray[(int)Mesh.ArrayType.Index] = idxs;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArray);

            var maskTexIndex = model.GetTextureIndices()[drawableIndex];
            var texture = maskTexIndex >= 0 && maskTexIndex < model.Textures.Length ? model.Textures[maskTexIndex] : null;

            var shader = ResolveShader("2d_cubism_mask");
            if (shader == null) return mesh;

            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("channel", ChannelFromSlot(channelSlot));
            if (texture != null)
                mat.SetShaderParameter("tex_main", texture);

            mi2d.Mesh = mesh;
            mi2d.Material = mat;
            return mesh;
        }

        private static Color ChannelFromSlot(int slot) => slot switch
        {
            0 => new Color(1f, 0f, 0f, 0f),
            1 => new Color(0f, 1f, 0f, 0f),
            2 => new Color(0f, 0f, 1f, 0f),
            _ => new Color(0f, 0f, 0f, 1f)
        };

        /// <summary>
        /// Per-frame render update. Resets dynamic flags, then iterates all
        /// mesh instances in render order, applying visibility, opacity, and
        /// vertex position changes based on CubismCore dynamic flags.
        /// </summary>
        public unsafe void UpdateModel()
        {
            if (_model == null || _meshInstances == null) return;

            var dynamicFlags = _model.GetDynamicFlags();
            var opacities = _model.GetOpacities();
            var renderOrders = _model.GetRenderOrders();
            var multiplyColors = _model.GetMultiplyColors();
            var screenColors = _model.GetScreenColors();
            var dc = _model.DrawableCount;

            for (int di = 0; di < dc; di++)
            {
                var mi = _meshInstances[di];
                if (mi == null) continue;
                var mat = _materials[di];

                var flags = dynamicFlags[di];

                if ((flags & CubismCoreBindings.VisibilityDidChange) != 0)
                    mi.Visible = (flags & CubismCoreBindings.IsVisible) != 0;

                mi.ZIndex = renderOrders[di];

                var mc = multiplyColors[di];
                mat.SetShaderParameter("color_multiply", new Color(mc.X, mc.Y, mc.Z, mc.W));

                var sc = screenColors[di];
                mat.SetShaderParameter("color_screen", new Color(sc.X, sc.Y, sc.Z, sc.W));

                if ((flags & CubismCoreBindings.OpacityDidChange) != 0)
                    mat.SetShaderParameter("color_base", new Color(1f, 1f, 1f, opacities[di]));

                if ((flags & CubismCoreBindings.VertexPositionsDidChange) != 0)
                {
                    UpdateMeshVertices(di, mi.Mesh as ArrayMesh);
                }
            }

            for (int gi = 0; gi < _maskGroups.Count; gi++)
            {
                var group = _maskGroups[gi];
                if (group.MaskDrawableIndices == null) continue;
                for (int mi = 0; mi < group.MaskDrawableIndices.Length; mi++)
                {
                    var di = group.MaskDrawableIndices[mi];
                    if (di < 0 || di >= dc) continue;
                    var mFlags = dynamicFlags[di];
                    if ((mFlags & CubismCoreBindings.VertexPositionsDidChange) == 0) continue;
                    UpdateMaskMeshVertices(di, group.MaskArrayMeshes[mi], gi);
                }
            }

            _model.ResetDynamicFlags();
        }

        private unsafe void UpdateMeshVertices(int drawableIndex, ArrayMesh mesh)
        {
            if (mesh == null) return;

            var vCount = _model.GetVertexCounts()[drawableIndex];
            var srcVerts = _model.GetVertexPositions()[drawableIndex];
            var ppu = _model.PixelsPerUnit;

            int stride = 8;
            byte[] data = new byte[vCount * stride];

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int v = 0; v < vCount; v++)
            {
                float x = srcVerts[v].X * ppu;
                float y = -srcVerts[v].Y * ppu;
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(v * stride), x);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(v * stride + 4), y);

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }

            mesh.SurfaceUpdateVertexRegion(0, 0, data);

            mesh.CustomAabb = new Aabb(
                new Vector3(minX, minY, 0),
                new Vector3(maxX - minX, maxY - minY, 0));
        }

        private unsafe void UpdateMaskMeshVertices(int drawableIndex, ArrayMesh mesh, int groupIndex)
        {
            if (mesh == null || groupIndex < 0 || groupIndex >= _maskGroups.Count) return;

            var group = _maskGroups[groupIndex];
            float offsetX = group.OffsetX;
            float offsetY = group.OffsetY;
            var ppu = _model.PixelsPerUnit;

            var vCount = _model.GetVertexCounts()[drawableIndex];
            var srcVerts = _model.GetVertexPositions()[drawableIndex];

            int stride = 8;
            byte[] data = new byte[vCount * stride];

            float vMinX = float.MaxValue, vMinY = float.MaxValue, vMaxX = float.MinValue, vMaxY = float.MinValue;
            for (int v = 0; v < vCount; v++)
            {
                float x = srcVerts[v].X * ppu - offsetX;
                float y = -srcVerts[v].Y * ppu - offsetY;
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(v * stride), x);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(v * stride + 4), y);

                if (x < vMinX) vMinX = x;
                if (y < vMinY) vMinY = y;
                if (x > vMaxX) vMaxX = x;
                if (y > vMaxY) vMaxY = y;
            }

            mesh.SurfaceUpdateVertexRegion(0, 0, data);

            mesh.CustomAabb = new Aabb(
                new Vector3(vMinX, vMinY, 0),
                new Vector3(vMaxX - vMinX, vMaxY - vMinY, 0));
        }

        private void ClearMeshes()
        {
            if (_meshInstances != null)
            {
                for (int i = 0; i < _meshInstances.Length; i++)
                {
                    var mi = _meshInstances[i];
                    if (mi != null) mi.QueueFree();
                }
            }
            _meshInstances = null;
            _materials = null;
            _drawableMaskGroupIndex = null;
            _drawableMaskChannel = null;

            foreach (var group in _maskGroups)
                group.Viewport.QueueFree();
            _maskGroups.Clear();
        }

        private unsafe Shader ResolveShaderForDrawable(int drawableIndex, byte* constantFlags, int* maskCounts)
        {
            var flags = constantFlags[drawableIndex];
            var hasMask = maskCounts[drawableIndex] > 0;
            var isInverted = (flags & CubismCoreBindings.IsInvertedMask) != 0;
            var isAdditive = (flags & CubismCoreBindings.BlendAdditive) != 0;
            var isMultiplicative = (flags & CubismCoreBindings.BlendMultiplicative) != 0;

            string name;

            if (!hasMask)
            {
                name = isAdditive ? "2d_cubism_norm_add" :
                       isMultiplicative ? "2d_cubism_norm_mul" : "2d_cubism_norm_mix";
            }
            else if (!isInverted)
            {
                name = isAdditive ? "2d_cubism_mask_add" :
                       isMultiplicative ? "2d_cubism_mask_mul" : "2d_cubism_mask_mix";
            }
            else
            {
                name = isAdditive ? "2d_cubism_mask_add_inv" :
                       isMultiplicative ? "2d_cubism_mask_mul_inv" : "2d_cubism_mask_mix_inv";
            }

            return ResolveShader(name);
        }

        private static Shader ResolveShader(string name)
        {
            try
            {
                var shader = Cthangover.Core.Mods.ModManager.Instance?.ResolveShader(name);
                if (shader != null)
                {
                    GameLogger.Log("LIVE2D", $"ResolveShader: '{name}' OK");
                    return shader;
                }
            }
            catch (Exception ex)
            {
                GameLogger.Log("LIVE2D", $"ResolveShader: '{name}' threw {ex.Message}", LogLevel.Error);
            }
            GameLogger.Log("LIVE2D", $"ResolveShader: '{name}' FAILED", LogLevel.Error);
            return null;
        }

        private static int NextPowerOfTwo(int v)
        {
            if (v <= 0) return 1;
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }
    }
}
