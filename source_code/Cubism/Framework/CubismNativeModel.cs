using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cthangover.Core.Utils;
using Godot;
using Cthangover.Live2D.Cubism.Core;
using Cthangover.Core.Mods;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Wraps a Live2D CubismCore native model instance.
    /// Handles .moc3 loading with the required 64-byte alignment, exposes
    /// model data through direct pointers into CubismCore arrays, and manages
    /// texture loading from external file providers.
    /// 
    /// Memory layout: .moc3 data and model instance memory are pinned via
    /// GCHandle to satisfy CubismCore's alignment requirements (64-byte and
    /// 16-byte respectively). The native model handle (_model) is an opaque
    /// pointer passed to all CubismCore P/Invoke calls.
    /// 
    /// Implements <see cref="IDisposable"/> to free pinned memory when the
    /// model is no longer needed.
    /// </summary>
    public class CubismNativeModel : IDisposable
    {
        private IntPtr _model;
        private GCHandle _mocAlignedHandle;
        private GCHandle _modelAlignedHandle;

        // The Core stores the raw function pointer, so the delegate must live
        // for the whole process — a collected delegate would crash native code.
        private static readonly CubismCoreBindings.CsmLogDelegate NativeLogDelegate = OnNativeLog;
        private static bool _nativeLogWired;

        static CubismNativeModel()
        {
            WireNativeLog();
        }

        /// <summary>
        /// Routes native Core log output (the only channel for native-side errors:
        /// invalid moc3, unsupported version, alignment failures) into GameLogger.
        /// </summary>
        private static void WireNativeLog()
        {
            if (_nativeLogWired) return;
            _nativeLogWired = true;
            CubismCoreBindings.csmSetLogFunction(NativeLogDelegate);
        }

        private static void OnNativeLog(IntPtr messagePtr)
        {
            if (messagePtr == IntPtr.Zero) return;
            string message = Marshal.PtrToStringAnsi(messagePtr) ?? string.Empty;
            GameLogger.Log("LIVE2D-CORE", message);
        }

        private int _drawableCount;
        private int _parameterCount;

        /// <summary>
        /// Array of Godot <see cref="ImageTexture"/> instances loaded from the
        /// model's texture references. Indexed by drawable texture index.
        /// </summary>
        public ImageTexture[] Textures { get; private set; }

        /// <summary>Number of drawables (renderable mesh parts) in the model.</summary>
        public int DrawableCount => _drawableCount;

        /// <summary>Number of parameters in the model.</summary>
        public int ParameterCount => _parameterCount;

        /// <summary>Canvas pixel width from the moc3. Available after <see cref="LoadMoc"/>.</summary>
        public float CanvasWidth { get; private set; }

        /// <summary>Canvas pixel height from the moc3. Available after <see cref="LoadMoc"/>.</summary>
        public float CanvasHeight { get; private set; }

        /// <summary>Pixels-per-unit scale for converting Cubism coordinates to pixel space.</summary>
        public float PixelsPerUnit { get; private set; }

        /// <summary>Whether the native model has been successfully initialized.</summary>
        public bool IsLoaded => _model != IntPtr.Zero;

        /// <summary>Raw native model handle passed to CubismCore functions.</summary>
        public IntPtr ModelPtr => _model;

        /// <summary>
        /// Loads raw .moc3 bytes into CubismCore.
        /// Allocates a 64-byte aligned buffer, copies moc data, revives the moc,
        /// allocates a 16-byte aligned model buffer, and initializes the model.
        /// Throws <see cref="InvalidOperationException"/> on failure.
        /// </summary>
        public unsafe void LoadMoc(byte[] mocBytes)
        {
            if (IsLoaded)
            {
                // Guard against a silent native-memory leak: handles are pinned and the
                // previous moc/model are never freed, so a second LoadMoc on the same
                // instance is always a bug in the caller.
                throw new InvalidOperationException(
                    "LoadMoc: a moc is already loaded in this CubismNativeModel instance. " +
                    "Create a new instance to load another model.");
            }

            int alignedMocSize = (mocBytes.Length + CubismCoreBindings.AlignofMoc - 1)
                / CubismCoreBindings.AlignofMoc * CubismCoreBindings.AlignofMoc;
            byte[] mocBuffer = new byte[alignedMocSize + CubismCoreBindings.AlignofMoc];
            _mocAlignedHandle = GCHandle.Alloc(mocBuffer, GCHandleType.Pinned);
            try
            {
                IntPtr mocPtr = _mocAlignedHandle.AddrOfPinnedObject();
                long alignedAddr = (mocPtr.ToInt64() + CubismCoreBindings.AlignofMoc - 1)
                    / CubismCoreBindings.AlignofMoc * CubismCoreBindings.AlignofMoc;
                var alignedMocPtr = new IntPtr(alignedAddr);
                Marshal.Copy(mocBytes, 0, alignedMocPtr, mocBytes.Length);

            // csmReviveMocInPlace (verified by disassembly of the shipped DLL) hard-requires
            // size % 64 == 0 and returns failure with '"size" is invalid.' otherwise. Cubism
            // Editor always exports moc3 files whose length is a multiple of 64, but we do not
            // rely on that: the buffer is already zero-initialized and 64-byte padded, so we
            // pass the padded size — the Core reads only the real content, zero padding is safe.
                uint mocBufferSize = (uint)alignedMocSize;

            // Validate the moc3 buffer against the loaded Core before reviving it.
            // Both checks use Core functions that were previously declared but never
            // called — this gives diagnosable errors instead of a bare
            // "csmReviveMocInPlace failed" for wrong-version or corrupted files.
                uint mocVersion = CubismCoreBindings.csmGetMocVersion((void*)alignedMocPtr, mocBufferSize);
                uint coreVersion = CubismCoreBindings.csmGetLatestMocVersion();
                if (mocVersion == 0)
                {
                    GameLogger.Log("LIVE2D", "LoadMoc: buffer is not a recognized .moc3 file (csmGetMocVersion returned 0)", LogLevel.Error);
                    throw new InvalidOperationException("The .moc3 buffer is not a recognized moc3 file");
                }
                if (mocVersion > coreVersion)
                {
                    GameLogger.Log("LIVE2D",
                        $"LoadMoc: .moc3 version {mocVersion} is newer than Cubism Core {coreVersion} — ship a newer Live2DCubismCore.dll",
                        LogLevel.Error);
                    throw new InvalidOperationException(
                        $".moc3 version {mocVersion} is not supported by Cubism Core {coreVersion}");
                }

                if (CubismCoreBindings.csmHasMocConsistency((void*)alignedMocPtr, mocBufferSize) != 1)
                {
                    GameLogger.Log("LIVE2D", "LoadMoc: .moc3 failed the consistency check (csmHasMocConsistency != 1) — data is corrupted", LogLevel.Error);
                    throw new InvalidOperationException(".moc3 data is inconsistent (csmHasMocConsistency failed)");
                }

                IntPtr moc = CubismCoreBindings.csmReviveMocInPlace(
                    (void*)alignedMocPtr, mocBufferSize);
                if (moc == IntPtr.Zero)
                {
                    GameLogger.Log("LIVE2D", "LoadMoc: csmReviveMocInPlace failed — .moc3 data is invalid or corrupted", LogLevel.Error);
                    throw new InvalidOperationException("csmReviveMocInPlace failed");
                }

                uint modelSize = CubismCoreBindings.csmGetSizeofModel(moc);
                byte[] modelBuffer = new byte[modelSize + CubismCoreBindings.AlignofModel];
                _modelAlignedHandle = GCHandle.Alloc(modelBuffer, GCHandleType.Pinned);
                IntPtr modelPtr = _modelAlignedHandle.AddrOfPinnedObject();
                long modelAlignedAddr = (modelPtr.ToInt64() + CubismCoreBindings.AlignofModel - 1)
                    / CubismCoreBindings.AlignofModel * CubismCoreBindings.AlignofModel;
                var alignedModelPtr = new IntPtr(modelAlignedAddr);

                _model = CubismCoreBindings.csmInitializeModelInPlace(
                    moc, (void*)alignedModelPtr, modelSize);
                if (_model == IntPtr.Zero)
                {
                    GameLogger.Log("LIVE2D", "LoadMoc: csmInitializeModelInPlace failed — model memory allocation failed", LogLevel.Error);
                    throw new InvalidOperationException("csmInitializeModelInPlace failed");
                }
            }
            catch
            {
                // Any failure after the Alloc calls must release the pinned buffers —
                // otherwise the GCHandles (and the native memory they pin) leak for the
                // rest of the process, because LoadMoc throws before Dispose is reachable.
                if (_mocAlignedHandle.IsAllocated) _mocAlignedHandle.Free();
                if (_modelAlignedHandle.IsAllocated) _modelAlignedHandle.Free();
                _mocAlignedHandle = default;
                _modelAlignedHandle = default;
                throw;
            }

            var sizeInPixels = default(CubismCoreBindings.csmVector2);
            var originInPixels = default(CubismCoreBindings.csmVector2);
            float ppu = 0;
            CubismCoreBindings.csmReadCanvasInfo(_model, &sizeInPixels, &originInPixels, &ppu);
            CanvasWidth = sizeInPixels.X;
            CanvasHeight = sizeInPixels.Y;
            PixelsPerUnit = ppu;

            _drawableCount = CubismCoreBindings.csmGetDrawableCount(_model);
            _parameterCount = CubismCoreBindings.csmGetParameterCount(_model);
        }

        /// <summary>
        /// Loads a model from .model3.json bytes.
        /// Parses the FileReferences section to find the .moc3 file,
        /// calls <see cref="LoadMoc"/>, then loads all referenced textures
        /// through the given file provider.
        /// </summary>
        public void LoadFromProvider(byte[] model3JsonBytes, string modelDir, IModFileProvider provider)
        {
            var json = JsonDocument.Parse(model3JsonBytes);
            var fileRefs = json.RootElement.GetProperty("FileReferences");

            string mocRelPath = fileRefs.GetProperty("Moc").GetString();
            var mocBytes = ReadFile(modelDir, mocRelPath, provider);
            if (mocBytes == null)
            {
                GameLogger.Log("LIVE2D", $"LoadFromProvider: .moc3 not found '{mocRelPath}'", LogLevel.Error);
                return;
            }
            LoadMoc(mocBytes);

            var texturePaths = new List<string>();
            if (fileRefs.TryGetProperty("Textures", out var texArr))
            {
                foreach (var t in texArr.EnumerateArray())
                    texturePaths.Add(t.GetString());
            }
            LoadTextures(texturePaths, modelDir, provider);
        }

        private static byte[] ReadFile(string baseDir, string relativePath, IModFileProvider provider)
        {
            var normalized = relativePath.Replace('\\', '/');
            var combined = string.IsNullOrEmpty(baseDir) ? normalized : $"{baseDir}/{normalized}";
            var result = provider.ReadFileBinary(combined);
            if (result == null)
                GameLogger.Log("LIVE2D", $"ReadFile: provider returned null for '{combined}'", LogLevel.Warning);
            return result;
        }

        private void LoadTextures(List<string> paths, string modelDir, IModFileProvider provider)
        {
            Textures = new ImageTexture[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                var texBytes = ReadFile(modelDir, paths[i], provider);
                if (texBytes == null || texBytes.Length == 0)
                {
                    GameLogger.Log("LIVE2D", $"LoadTextures: failed to read texture '{paths[i]}'", LogLevel.Warning);
                    continue;
                }
                var img = new Image();
                Error err;
                if (texBytes[0] == 0x89 && texBytes[1] == 0x50)
                    err = img.LoadPngFromBuffer(texBytes);
                else
                    err = img.LoadJpgFromBuffer(texBytes);

                if (err != Error.Ok)
                {
                    GameLogger.Log("LIVE2D", $"LoadTextures: failed to decode '{paths[i]}' — error={err}", LogLevel.Warning);
                    continue;
                }

                if (img.GetFormat() != Image.Format.Rgba8)
                    img.Convert(Image.Format.Rgba8);

                img.GenerateMipmaps();

                var tex = ImageTexture.CreateFromImage(img);
                Textures[i] = tex;
                var w = img.GetWidth();
                var h = img.GetHeight();
                img.Dispose();
                GameLogger.Log("LIVE2D", $"LoadTextures: loaded '{paths[i]}' ({texBytes.Length}b, {w}x{h})");
            }
        }

        /// <summary>
        /// Calls <c>csmUpdateModel</c> on the native model.
        /// Must be called after modifying parameters/parts/opacities and before
        /// reading vertex positions for rendering.
        /// </summary>
        public unsafe void UpdateModel()
        {
            if (_model == IntPtr.Zero) return;
            CubismCoreBindings.csmUpdateModel(_model);
        }

        /// <summary>
        /// Resets the per-frame dynamic flags for all drawables.
        /// Called at the start of each render update cycle.
        /// </summary>
        public unsafe void ResetDynamicFlags()
        {
            if (_model == IntPtr.Zero) return;
            CubismCoreBindings.csmResetDrawableDynamicFlags(_model);
        }

        /// <summary>Returns the drawable count from the native model.</summary>
        public unsafe int GetDrawableCount() => _drawableCount;

        /// <summary>Returns a pointer to the dynamic flags byte array (one byte per drawable).</summary>
        public unsafe byte* GetDynamicFlags() =>
            CubismCoreBindings.csmGetDrawableDynamicFlags(_model);

        /// <summary>Returns a pointer to the constant flags byte array (blend mode, double-sided, etc.).</summary>
        public unsafe byte* GetConstantFlags() =>
            CubismCoreBindings.csmGetDrawableConstantFlags(_model);

        /// <summary>Returns a pointer to the texture index array (one int per drawable).</summary>
        public unsafe int* GetTextureIndices() =>
            CubismCoreBindings.csmGetDrawableTextureIndices(_model);

        /// <summary>Returns a pointer to the vertex count array (one int per drawable).</summary>
        public unsafe int* GetVertexCounts() =>
            CubismCoreBindings.csmGetDrawableVertexCounts(_model);

        /// <summary>Returns a pointer array of csmVector2 arrays — vertex positions per drawable.</summary>
        public unsafe CubismCoreBindings.csmVector2** GetVertexPositions() =>
            CubismCoreBindings.csmGetDrawableVertexPositions(_model);

        /// <summary>Returns a pointer array of csmVector2 arrays — UV coordinates per drawable.</summary>
        public unsafe CubismCoreBindings.csmVector2** GetVertexUvs() =>
            CubismCoreBindings.csmGetDrawableVertexUvs(_model);

        /// <summary>Returns a pointer to the index count array (one int per drawable).</summary>
        public unsafe int* GetIndexCounts() =>
            CubismCoreBindings.csmGetDrawableIndexCounts(_model);

        /// <summary>Returns a pointer array of ushort arrays — triangle indices per drawable.</summary>
        public unsafe ushort** GetIndices() =>
            CubismCoreBindings.csmGetDrawableIndices(_model);

        /// <summary>Returns a pointer to the per-drawable opacity array (one float per drawable).</summary>
        public unsafe float* GetOpacities() =>
            CubismCoreBindings.csmGetDrawableOpacities(_model);

        /// <summary>Returns a pointer to the draw order indices array.</summary>
        public unsafe int* GetDrawOrders() =>
            CubismCoreBindings.csmGetDrawableDrawOrders(_model);

        /// <summary>Returns a pointer to the render order indices array.</summary>
        public unsafe int* GetRenderOrders() =>
            CubismCoreBindings.csmGetRenderOrders(_model);

        /// <summary>Returns a pointer to the mask count array (one int per drawable).</summary>
        public unsafe int* GetMaskCounts() =>
            CubismCoreBindings.csmGetDrawableMaskCounts(_model);

        /// <summary>Returns a pointer array of int arrays — mask drawable indices per drawable.</summary>
        public unsafe int** GetMasks() =>
            CubismCoreBindings.csmGetDrawableMasks(_model);

        /// <summary>Returns a pointer to the per-drawable multiply color array.</summary>
        public unsafe CubismCoreBindings.csmVector4* GetMultiplyColors() =>
            CubismCoreBindings.csmGetDrawableMultiplyColors(_model);

        /// <summary>Returns a pointer to the per-drawable screen color array.</summary>
        public unsafe CubismCoreBindings.csmVector4* GetScreenColors() =>
            CubismCoreBindings.csmGetDrawableScreenColors(_model);

        /// <summary>Returns a pointer to the current parameter values array (one float per parameter).</summary>
        public unsafe float* GetParameterValues() =>
            CubismCoreBindings.csmGetParameterValues(_model);

        /// <summary>Returns a pointer to the parameter minimum bounds array.</summary>
        public unsafe float* GetParameterMinimumValues() =>
            CubismCoreBindings.csmGetParameterMinimumValues(_model);

        /// <summary>Returns a pointer to the parameter maximum bounds array.</summary>
        public unsafe float* GetParameterMaximumValues() =>
            CubismCoreBindings.csmGetParameterMaximumValues(_model);

        /// <summary>Returns a pointer to the parameter default values array.</summary>
        public unsafe float* GetParameterDefaultValues() =>
            CubismCoreBindings.csmGetParameterDefaultValues(_model);

        /// <summary>
        /// Returns a pointer array of null-terminated ANSI strings — parameter IDs.
        /// Use <see cref="ReadStringFromPtrArray"/> to read individual strings.
        /// </summary>
        public unsafe IntPtr GetParameterIds() =>
            CubismCoreBindings.csmGetParameterIds(_model);

        /// <summary>
        /// Reads a null-terminated ANSI string from a pointer array at the given index.
        /// Each element is a byte pointer; reads until null terminator.
        /// </summary>
        public static unsafe string ReadStringFromPtrArray(IntPtr ptrArray, int index)
        {
            var strPtr = *(byte**)IntPtr.Add(ptrArray, index * IntPtr.Size);
            return Marshal.PtrToStringAnsi((IntPtr)strPtr);
        }

        /// <summary>Returns the number of parts from the native model.</summary>
        public unsafe int GetPartCount() =>
            CubismCoreBindings.csmGetPartCount(_model);

        /// <summary>Returns a pointer to the per-part opacity array (one float per part).</summary>
        public unsafe float* GetPartOpacities() =>
            CubismCoreBindings.csmGetPartOpacities(_model);

        /// <summary>Returns a pointer array of part ID strings.</summary>
        public unsafe IntPtr GetPartIds() =>
            CubismCoreBindings.csmGetPartIds(_model);

        // Snapshot of the parameter values for the Load/Save bracket
        // (port of CubismModel.cpp:878-910).
        private float[] _savedParameterValues;

        /// <summary>
        /// Порт CubismModel::SaveParameters: сохраняет текущие значения параметров в снимок.
        /// Вызывается сразу после фазы моушена, чтобы аддитивные слои (выражения, дыхание,
        /// look-инъекция) каждый кадр стартовали от одного и того же базового состояния
        /// и не накапливались кадр-за-кадром.
        /// </summary>
        public unsafe void SaveParameters()
        {
            if (_model == IntPtr.Zero) return;
            var values = GetParameterValues();
            if (_savedParameterValues == null || _savedParameterValues.Length != _parameterCount)
                _savedParameterValues = new float[_parameterCount];
            for (int i = 0; i < _parameterCount; i++)
                _savedParameterValues[i] = values[i];
        }

        /// <summary>
        /// Порт CubismModel::LoadParameters: восстанавливает значения параметров из снимка.
        /// До первого SaveParameters ничего не делает (как в SDK).
        /// </summary>
        public unsafe void LoadParameters()
        {
            if (_model == IntPtr.Zero || _savedParameterValues == null) return;
            var values = GetParameterValues();
            var count = System.Math.Min(_parameterCount, _savedParameterValues.Length);
            for (int i = 0; i < count; i++)
                values[i] = _savedParameterValues[i];
        }

        /// <summary>
        /// Frees pinned memory handles. The native model handle is set to zero;
        /// the underlying CubismCore memory was allocated inline and lives in
        /// the pinned buffers — freeing those handles releases all model memory.
        /// </summary>
        public void Dispose()
        {
            if (_mocAlignedHandle.IsAllocated) _mocAlignedHandle.Free();
            if (_modelAlignedHandle.IsAllocated) _modelAlignedHandle.Free();
            _model = IntPtr.Zero;
        }
    }
}
