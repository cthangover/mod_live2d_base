using System;
using System.Runtime.InteropServices;

namespace Cthangover.Live2D.Cubism.Core
{
    /// <summary>
    /// P/Invoke bindings for the Live2D CubismCore native library (Live2DCubismCore.dll).
    /// Provides direct access to model loading, updating, and data querying at the C level.
    /// 
    /// All methods are unsafe and return raw pointers into CubismCore's internal arrays.
    /// <see cref="CubismNativeModel"/> wraps this layer into a managed API with proper
    /// memory alignment and lifetime management.
    /// </summary>
    public static class CubismCoreBindings
    {
        /// <summary>Name of the native DLL loaded at runtime.</summary>
        public const string DllName = "Live2DCubismCore";

        /// <summary>Required byte alignment for .moc3 data buffers before revival.</summary>
        public const int AlignofMoc = 64;

        /// <summary>Required byte alignment for model instance memory.</summary>
        public const int AlignofModel = 16;

        /// <summary>Flag: drawable uses additive blending.</summary>
        public const byte BlendAdditive = 1 << 0;

        /// <summary>Flag: drawable uses multiplicative blending.</summary>
        public const byte BlendMultiplicative = 1 << 1;

        /// <summary>Flag: drawable is double-sided (no back-face culling).</summary>
        public const byte IsDoubleSided = 1 << 2;

        /// <summary>Flag: drawable's mask is inverted.</summary>
        public const byte IsInvertedMask = 1 << 3;

        /// <summary>Dynamic flag: drawable is currently visible.</summary>
        public const byte IsVisible = 1 << 0;

        /// <summary>Dynamic flag: drawable visibility changed this frame.</summary>
        public const byte VisibilityDidChange = 1 << 1;

        /// <summary>Dynamic flag: drawable opacity changed this frame.</summary>
        public const byte OpacityDidChange = 1 << 2;

        /// <summary>Dynamic flag: drawable draw order changed this frame.</summary>
        public const byte DrawOrderDidChange = 1 << 3;

        /// <summary>Dynamic flag: drawable render order changed this frame.</summary>
        public const byte RenderOrderDidChange = 1 << 4;

        /// <summary>Dynamic flag: vertex positions changed this frame.</summary>
        public const byte VertexPositionsDidChange = 1 << 5;

        /// <summary>Dynamic flag: blend color changed this frame.</summary>
        public const byte BlendColorDidChange = 1 << 6;

        /// <summary>2D vector used in CubismCore struct layouts.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct csmVector2 { public float X; public float Y; }

        /// <summary>4D vector used in CubismCore color struct layouts.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct csmVector4 { public float X; public float Y; public float Z; public float W; }

        /// <summary>Returns the CubismCore SDK version number.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern uint csmGetVersion();

        /// <summary>Revives a .moc3 binary from an aligned memory buffer. Returns a moc handle or IntPtr.Zero.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe IntPtr csmReviveMocInPlace(void* address, uint size);

        /// <summary>Native log callback: receives a pointer to a NUL-terminated csmChar* message.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void CsmLogDelegate(IntPtr messagePtr);

        /// <summary>
        /// Installs the Core log handler. Without it every native-side failure
        /// (invalid moc3, unsupported version, alignment errors) is invisible.
        /// Keep the delegate in a static field for the process lifetime — the
        /// Core stores the raw function pointer.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern void csmSetLogFunction(CsmLogDelegate logFunction);

        /// <summary>Queries the memory size needed to initialize a model from the given moc.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe uint csmGetSizeofModel(IntPtr moc);

        /// <summary>Creates a model instance inside the provided aligned buffer. Returns model handle or IntPtr.Zero.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe IntPtr csmInitializeModelInPlace(IntPtr moc, void* address, uint size);

        /// <summary>Advance the model by one frame — updates physics, parameter-to-vertex mapping, etc.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe void csmUpdateModel(IntPtr model);

        /// <summary>Reads the model's canvas dimensions and pixels-per-unit scale.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe void csmReadCanvasInfo(IntPtr model, csmVector2* outSizeInPixels, csmVector2* outOriginInPixels, float* outPixelsPerUnit);

        /// <summary>Returns the number of parameters in the model.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int csmGetParameterCount(IntPtr model);

        /// <summary>Returns a pointer array of null-terminated parameter ID strings.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe IntPtr csmGetParameterIds(IntPtr model);

        /// <summary>Returns a float array of current parameter values.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe float* csmGetParameterValues(IntPtr model);

        /// <summary>Returns a float array of parameter minimum bounds.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe float* csmGetParameterMinimumValues(IntPtr model);

        /// <summary>Returns a float array of parameter maximum bounds.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe float* csmGetParameterMaximumValues(IntPtr model);

        /// <summary>Returns a float array of parameter default values.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe float* csmGetParameterDefaultValues(IntPtr model);

        /// <summary>Returns the number of drawables (renderable mesh parts) in the model.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int csmGetDrawableCount(IntPtr model);

        /// <summary>Returns byte flags array for constant (immutable) drawable properties.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe byte* csmGetDrawableConstantFlags(IntPtr model);

        /// <summary>Returns byte flags array for per-frame dynamic drawable state.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe byte* csmGetDrawableDynamicFlags(IntPtr model);

        /// <summary>Returns an int array mapping each drawable to a texture index.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int* csmGetDrawableTextureIndices(IntPtr model);

        /// <summary>Returns an int array of vertex counts per drawable.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int* csmGetDrawableVertexCounts(IntPtr model);

        /// <summary>Returns a pointer array of csmVector2 arrays (vertex positions per drawable).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe csmVector2** csmGetDrawableVertexPositions(IntPtr model);

        /// <summary>Returns a pointer array of csmVector2 arrays (UVs per drawable).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe csmVector2** csmGetDrawableVertexUvs(IntPtr model);

        /// <summary>Returns an int array of index counts per drawable.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int* csmGetDrawableIndexCounts(IntPtr model);

        /// <summary>Returns a pointer array of ushort index arrays per drawable.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe ushort** csmGetDrawableIndices(IntPtr model);

        /// <summary>Returns a float array of per-drawable opacities.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe float* csmGetDrawableOpacities(IntPtr model);

        /// <summary>Returns draw order indices — maps render order slot to drawable index.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int* csmGetDrawableDrawOrders(IntPtr model);

        /// <summary>Returns render order indices (newer Cubism format).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int* csmGetRenderOrders(IntPtr model);

        /// <summary>Resets dynamic flags for all drawables, preparing for the next frame.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe void csmResetDrawableDynamicFlags(IntPtr model);

        /// <summary>Returns an int array of mask counts per drawable.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int* csmGetDrawableMaskCounts(IntPtr model);

        /// <summary>Returns a pointer array of int arrays (mask drawable indices per drawable).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int** csmGetDrawableMasks(IntPtr model);

        /// <summary>Returns a csmVector4 array of per-drawable multiply colors.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe csmVector4* csmGetDrawableMultiplyColors(IntPtr model);

        /// <summary>Returns a csmVector4 array of per-drawable screen colors.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe csmVector4* csmGetDrawableScreenColors(IntPtr model);

        /// <summary>Returns the number of parts in the model.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int csmGetPartCount(IntPtr model);

        /// <summary>Returns a pointer array of part ID strings.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe IntPtr csmGetPartIds(IntPtr model);

        /// <summary>Returns a float array of per-part opacities.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe float* csmGetPartOpacities(IntPtr model);

        /// <summary>Checks if the given .moc3 data passes consistency validation.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe int csmHasMocConsistency(void* address, uint size);

        /// <summary>Returns the latest .moc3 format version supported by the DLL.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe uint csmGetLatestMocVersion();

        /// <summary>Returns the .moc3 format version of the given binary data.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern unsafe uint csmGetMocVersion(void* address, uint size);
    }
}
