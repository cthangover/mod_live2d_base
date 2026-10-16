using Cthangover.Core.Mods;
using Cthangover.Core.UI.Event;
using Cthangover.Core.Utils;
using Godot;
using System.Collections.Generic;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Defines how the Live2D model scales to fit its parent container.
    /// </summary>
    public enum FitMode { None, FitWidth, FitHeight, FitAuto }

    /// <summary>
    /// The main Godot node that loads, updates, and renders a Live2D Cubism model.
    /// Represents a single character/avatar in the scene tree.
    /// 
    /// Orchestrates the full pipeline:
    /// <list type="number">
    ///   <item>Loads .model3.json and .moc3 via <see cref="CubismNativeModel"/></item>
    ///   <item>Sets up <see cref="CubismParameter"/> and <see cref="CubismPartOpacity"/> arrays</item>
    ///   <item>Creates optional effect layers: <see cref="CubismEyeBlink"/>, <see cref="CubismBreath"/>,
    ///       <see cref="CubismPose"/>, <see cref="CubismPhysics"/></item>
    ///   <item>Builds 2D meshes through <see cref="CubismRenderer2D"/></item>
    ///   <item>Every frame (<see cref="OnUpdate"/>): target point -> parameter injection ->
    ///       motion/expression evaluation -> effect layers -> model update -> renderer update</item>
    /// </list>
    /// 
    /// The update order matters: motion values are applied first, then expression values
    /// blend on top, and finally effect layers (eye blink, breath, pose, physics) modify
    /// the result. The target point drives face-tracking parameters (ParamAngleX/Y, ParamEyeBallX/Y).
    /// </summary>
    public partial class CubismModelNode : Node2D, IOnUpdateEvent
    {
        public int Priority => 0;
        private CubismNativeModel _nativeModel;
        private CubismRenderer2D _renderer;
        private CubismModelSettingJson _modelSetting;

        // The autoload event controller this node registered its OnUpdate with.
        // Kept so _ExitTree can unregister: the controller outlives the scene,
        // and a stale registration would keep dispatching into a freed node.
        private SceneEventController _eventController;

        private CubismEyeBlink _eyeBlink;
        private CubismBreath _breath;
        private CubismPose _pose;
        private CubismPhysics _physics;
        private CubismTargetPoint _targetPoint = new();

        private Vector2 _contentSize;
        private Vector2 _contentCenter;
        private bool _contentBoundsReady;

        private CubismMotionQueueManager _motionManager = new();
        private CubismMotionQueueManager _expressionManager = new();

        private float _userTimeSeconds;
        private string _modelDir;
        private IModFileProvider _fileProvider;
        private bool _layoutLogged;
        // Parameters claimed by SetLiveParameter with their last written values.
        // Re-applied after the Load/Save snapshot bracket every frame, so manual
        // control survives the per-frame parameter restore.
        private readonly System.Collections.Generic.Dictionary<string, float> _manualValues = new();

        // Cache of parameter index by id, built once after model load — hot loops
        // (face-track injection, SetLiveParameter) resolve by index without
        // per-frame Marshal.PtrToStringAnsi / string comparisons.
        private readonly Dictionary<string, int> _parameterIndexById = new();

        private FaceTrackEntry[] _faceTrackCache = System.Array.Empty<FaceTrackEntry>();

        private readonly List<string> _eyeBlinkIds = new();
        private readonly List<string> _lipSyncIds = new();

        /// <summary>One precomputed face-track injection: values[index] = targetAxis * scale.</summary>
        private struct FaceTrackEntry
        {
            public int Index;
            public string Id;
            public bool FromTargetX;
            public float Scale;
        }

        /// <summary>
        /// All model parameters with their IDs, bounds, and current values.
        /// Directly references the native parameter arrays — modifying
        /// <see cref="CubismParameter.Value"/> and calling
        /// <see cref="CubismParameter.ApplyToModel"/> writes back to CubismCore.
        /// </summary>
        public CubismParameter[] Parameters { get; private set; } = System.Array.Empty<CubismParameter>();

        /// <summary>
        /// All model part opacities, indexed by part index.
        /// Useful for showing/hiding parts (clothing layers, accessories, etc.).
        /// </summary>
        public CubismPartOpacity[] PartOpacities { get; private set; } = System.Array.Empty<CubismPartOpacity>();

        /// <summary>Canvas pixel width from the model's .moc3 data. Available after loading.</summary>
        public float CanvasWidth => _nativeModel?.CanvasWidth ?? 0f;

        /// <summary>Canvas pixel height from the model's .moc3 data. Available after loading.</summary>
        public float CanvasHeight => _nativeModel?.CanvasHeight ?? 0f;

        /// <summary>
        /// Horizontal anchor point for layout positioning, 0..1.
        /// 0 = left edge, 0.5 = center, 1 = right edge. Default 0.5.
        /// Serialized as a Godot export property.
        /// </summary>
        [Export] public float AnchorX { get; set; } = 0.5f;

        /// <summary>
        /// Vertical anchor point for layout positioning, 0..1.
        /// 0 = top edge, 0.5 = center, 1 = bottom edge. Default 0.5.
        /// </summary>
        [Export] public float AnchorY { get; set; } = 0.5f;

        /// <summary>
        /// How the model scales to fit its parent container. Default <see cref="FitMode.FitAuto"/>.
        /// Used by <see cref="DeferredApplyLayout"/> on the first frame with a valid parent size.
        /// </summary>
        [Export] public FitMode ScaleFit { get; set; } = FitMode.FitAuto;

        /// <summary>
        /// Enables automatic eye blink animation. Requires EyeBlink parameter groups
        /// in the model3.json. Default true.
        /// </summary>
        [Export] public bool EnableEyeBlink { get; set; } = true;

        /// <summary>
        /// Enables breathing animation. Default false.
        /// Note: breath parameters are not currently loaded from model settings —
        /// enable this only if parameters are set externally via <see cref="CubismBreath.SetParameters"/>.
        /// </summary>
        [Export] public bool EnableBreath { get; set; }

        /// <summary>
        /// Enables physics simulation (hair, cloth, etc.) from the .physics3.json file.
        /// Default true.
        /// </summary>
        [Export] public bool EnablePhysics { get; set; } = true;

        /// <summary>
        /// Enables pose-based part opacity management from the .pose3.json file.
        /// Default true.
        /// </summary>
        [Export] public bool EnablePose { get; set; } = true;

        private bool _loaded;

        /// <summary>
        /// Loads a Cubism model from within a registered mod.
        /// Reads the .model3.json file, resolves the .moc3 and textures, then
        /// calls <see cref="FinishSetup"/> to build parameters, parts, effects, and meshes.
        /// When <paramref name="modId"/> is <c>null</c>, all mods are searched in priority
        /// order for the first match.
        /// </summary>
        /// <param name="relativePath">Path to .model3.json relative to the mod root.</param>
        /// <param name="modId">Optional registered mod ID containing the model assets.</param>
        public void LoadModelFromMod(string relativePath, string modId = null)
        {
            var mods = ModManager.Instance?.Mods;
            if (mods == null)
            {
                GameLogger.Log("LIVE2D", $"LoadModelFromMod: no mods loaded", LogLevel.Error);
                return;
            }

            IModInfo modInfo = null;

            if (modId != null)
            {
                mods.TryGetValue(modId, out modInfo);
            }
            else
            {
                var orderedIds = ModRegistry.Instance.GetOrderedModIds();
                foreach (var id in orderedIds)
                {
                    if (mods.TryGetValue(id, out var candidate) && candidate.FileProvider?.FileExists(relativePath) == true)
                    {
                        modInfo = candidate;
                        break;
                    }
                }
            }

            if (modInfo == null)
            {
                GameLogger.Log("LIVE2D", $"LoadModelFromMod: file '{relativePath}' not found in any mod", LogLevel.Error);
                return;
            }

            _fileProvider = modInfo.FileProvider;
            var jsonBytes = _fileProvider.ReadFileBinary(relativePath);
            if (jsonBytes == null)
            {
                GameLogger.Log("LIVE2D", $"LoadModelFromMod: file not found '{relativePath}' in mod '{modId ?? "?"}'", LogLevel.Error);
                return;
            }

            _modelDir = relativePath.Contains('/')
                ? relativePath.Substring(0, relativePath.LastIndexOf('/'))
                : "";

            _nativeModel = new CubismNativeModel();
            _nativeModel.LoadFromProvider(jsonBytes, _modelDir, _fileProvider);
            if (!_nativeModel.IsLoaded)
            {
                // LoadFromProvider logs failures (missing moc, invalid JSON) and returns
                // without a native model — FinishSetup would P/Invoke on IntPtr.Zero.
                GameLogger.Log("LIVE2D", $"LoadModelFromMod: native model load failed for '{relativePath}'", LogLevel.Error);
                return;
            }
            FinishSetup(jsonBytes);
        }

        private void FinishSetup(byte[] model3JsonBytes)
        {
            _modelSetting = new CubismModelSettingJson(model3JsonBytes);
            SetupParameters();
            SetupParts();
            SetupEffects();

            _renderer = new CubismRenderer2D(this);
            _nativeModel.UpdateModel();
            _renderer.BuildModel(_nativeModel);

            var bounds = _renderer.GetContentBounds();
            _contentSize = new Vector2(bounds.Size.X, bounds.Size.Y);
            _contentCenter = new Vector2(bounds.GetCenter().X, bounds.GetCenter().Y);
            _contentBoundsReady = true;
            GameLogger.Log("LIVE2D", $"contentBounds: center={_contentCenter} size={_contentSize}");

            _loaded = true;

            var ec = GetNodeOrNull<SceneEventController>("/root/EventController");
            if (ec != null)
            {
                _eventController = ec;
                ec.AddUpdateEventListener(this);
            }

            GameLogger.Log("LIVE2D", $"Model loaded: {_nativeModel.ParameterCount} params, {_nativeModel.DrawableCount} drawables, canvas={_nativeModel.CanvasWidth}x{_nativeModel.CanvasHeight}");
        }

        private ulong _lastTickMsec;

        /// <summary>
        /// Per-frame update called by the SceneEventController.
        /// Execution order (SDK LAppModel::Update):
        /// <list type="number">
        ///   <item>Apply deferred layout (first valid frame only)</item>
        ///   <item>Update <see cref="_targetPoint"/> for face tracking</item>
        ///   <item>Restore the parameter snapshot (<c>LoadParameters</c>)</item>
        ///   <item>Evaluate motion queue (character animations)</item>
        ///   <item>Save the parameter snapshot (<c>SaveParameters</c>) — additive layers
        ///       (expressions, breath, face-track) start every frame from the same
        ///       baseline and never compound frame-over-frame</item>
        ///   <item>Run effect layers in SDK 5 scheduler order (ICubismUpdater.hpp):
        ///       eye blink → expression → look/face-track → breath → physics → pose</item>
        ///   <item>Call <c>csmUpdateModel</c> on the native model</item>
        ///   <item>Update the renderer (visibility, opacity, vertex positions)</item>
        /// </list>
        /// </summary>
        public void OnUpdate()
        {
            if (!_loaded) return;
            // If the node was freed while detached from the tree, _ExitTree never
            // ran and the stale registration survives: bail out before any Godot
            // API call on the disposed meshes (ObjectDisposedException).
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;

            var now = Time.GetTicksMsec();
            var dt = _lastTickMsec == 0 ? 0.016f : (now - _lastTickMsec) / 1000f;
            _lastTickMsec = now;

            DeferredApplyLayout();

            _userTimeSeconds += dt;

            _targetPoint.Update(dt);

            // LAppModel.cpp:388-398 — Load/SaveParameters bracket around the motion
            // phase. Without it, Additive/Multiply expressions and breath deltas
            // accumulate frame-over-frame and drive parameters into their clamps.
            _nativeModel.LoadParameters();

            _motionManager.DoUpdateMotion(_nativeModel, _userTimeSeconds);

            _nativeModel.SaveParameters();

            // Manual values survive the snapshot bracket: LoadParameters restores the
            // pre-motion baseline and would otherwise silently revert SetLiveParameter.
            ReapplyManualParameters();

            // SDK 5 update-scheduler order (ICubismUpdater.hpp):
            // EyeBlink(200) → Expression(300) → Look(400) → Breath(500) → Physics(600) → Pose(800).
            _eyeBlink?.UpdateParameters(_nativeModel, dt);
            _expressionManager.DoUpdateMotion(_nativeModel, _userTimeSeconds);
            InjectFaceTrackParameters();
            _breath?.UpdateParameters(_nativeModel, dt);
            _physics?.Evaluate(_nativeModel, dt);
            _pose?.UpdateParameters(_nativeModel, dt);

            _nativeModel.UpdateModel();
            _renderer.UpdateModel();
        }

        /// <summary>
        /// Frees the pinned native model memory when the node leaves the scene tree.
        /// Without this the pinned GCHandles (moc + model buffers) leak for the rest
        /// of the process — <see cref="CubismNativeModel.Dispose"/> is unreachable otherwise.
        /// </summary>
        public override void _ExitTree()
        {
            // Invalidate before anything else: the autoload controller may
            // dispatch OnUpdate between tree exit and the deferred delete pass,
            // and the meshes die together with this subtree.
            _loaded = false;
            if (_eventController != null)
            {
                _eventController.RemoveUpdateEventListener(this);
                _eventController = null;
            }
            _nativeModel?.Dispose();
            _nativeModel = null;
        }

        /// <summary>
        /// Adds the face-tracking target to the native parameter values via the
        /// precomputed index cache — no string reads or comparisons in the per-frame
        /// path. Parameters claimed through <see cref="SetLiveParameter"/> are skipped.
        /// Mirrors SDK CubismLook::UpdateParameters (AddParameterValue): AngleX/Y =
        /// target × 30, EyeBallX/Y = target — layered on top of the motion result,
        /// never replacing it. The Load/Save bracket in <see cref="OnUpdate"/>
        /// keeps the addition from accumulating across frames.
        /// </summary>
        private unsafe void InjectFaceTrackParameters()
        {
            if (_faceTrackCache.Length == 0) return;

            var values = _nativeModel.GetParameterValues();
            var tx = _targetPoint.X;
            var ty = _targetPoint.Y;

            for (int i = 0; i < _faceTrackCache.Length; i++)
            {
                var entry = _faceTrackCache[i];
                if (_manualValues.ContainsKey(entry.Id)) continue;
                values[entry.Index] += (entry.FromTargetX ? tx : ty) * entry.Scale;
            }
        }

        /// <summary>
        /// Restores values set through <see cref="SetLiveParameter"/> after
        /// <c>LoadParameters</c> resets the native parameter arrays to the snapshot.
        /// </summary>
        private unsafe void ReapplyManualParameters()
        {
            if (_manualValues.Count == 0) return;

            var values = _nativeModel.GetParameterValues();
            foreach (var kv in _manualValues)
            {
                if (!_parameterIndexById.TryGetValue(kv.Key, out var index)) continue;
                values[index] = kv.Value;
            }
        }

        private void DeferredApplyLayout()
        {
            if (_layoutLogged) return;
            if (!_contentBoundsReady) return;

            // Layout from model3.json takes precedence over the Fit logic
            // (same as LAppModel::SetupModel -> modelMatrix->SetupFromLayout).
            var layout = _modelSetting?.GetLayoutMap();
            if (layout != null && layout.Count > 0)
            {
                ApplyLayoutFromModelSetting(layout);
                return;
            }

            var parentSize = GetParentSize();
            if (parentSize.X <= 0f || parentSize.Y <= 0f) return;

            var cw = _contentSize.X;
            var ch = _contentSize.Y;
            if (cw <= 0f || ch <= 0f) return;

            float scale = 1f;
            switch (ScaleFit)
            {
                case FitMode.FitWidth:  scale = parentSize.X / cw; break;
                case FitMode.FitHeight: scale = parentSize.Y / ch; break;
                case FitMode.FitAuto:   scale = Mathf.Min(parentSize.X / cw, parentSize.Y / ch); break;
            }

            Scale = new Vector2(scale, scale);
            Position = new Vector2(
                parentSize.X * AnchorX - _contentCenter.X * scale,
                parentSize.Y * AnchorY - _contentCenter.Y * scale);

            _layoutLogged = true;
            GameLogger.Log("LIVE2D", $"ApplyLayout: parentSize={parentSize} contentSize={cw}x{ch} contentCenter={_contentCenter} scale={scale} pos={Position}");
        }

        /// <summary>
        /// Applies the "Layout" section of model3.json through
        /// <see cref="CubismModelMatrix.SetupFromLayout"/> (SDK semantics:
        /// canvas logical units, model height defaults to 2 units via the matrix
        /// constructor). The logical view scale maps 2 logical units to the
        /// parent height (like the SDK samples' view matrix); Y is flipped
        /// because Godot's Y axis points down.
        /// </summary>
        private void ApplyLayoutFromModelSetting(Dictionary<string, float> layout)
        {
            var ppu = _nativeModel.PixelsPerUnit;
            if (ppu <= 0f) ppu = 1f;

            var matrix = new CubismModelMatrix(CanvasWidth / ppu, CanvasHeight / ppu);
            matrix.SetupFromLayout(layout);

            var parentSize = GetParentSize();
            var logicalPixelsPerUnit = parentSize.Y > 0f ? parentSize.Y / 2f : ppu;

            Scale = new Vector2(
                matrix.ScaleX * logicalPixelsPerUnit / ppu,
                matrix.ScaleY * logicalPixelsPerUnit / ppu);
            Position = new Vector2(
                matrix.TranslateX * logicalPixelsPerUnit,
                -matrix.TranslateY * logicalPixelsPerUnit);

            _layoutLogged = true;
            GameLogger.Log("LIVE2D", $"ApplyModelSettingLayout: matrixScale=({matrix.ScaleX:F3},{matrix.ScaleY:F3}) translate=({matrix.TranslateX:F3},{matrix.TranslateY:F3}) nodeScale={Scale} pos={Position}");
        }

        private Vector2 GetParentSize()
        {
            var parent = GetParent();
            if (parent is Control c && c.Size.X > 0f && c.Size.Y > 0f)
                return c.Size;
            return GetViewportRect().Size;
        }

        private unsafe void SetupParameters()
        {
            var count = _nativeModel.ParameterCount;
            Parameters = new CubismParameter[count];
            var idsPtr = _nativeModel.GetParameterIds();
            var mins = _nativeModel.GetParameterMinimumValues();
            var maxs = _nativeModel.GetParameterMaximumValues();
            var defaults = _nativeModel.GetParameterDefaultValues();

            for (int i = 0; i < count; i++)
            {
                Parameters[i] = new CubismParameter(_nativeModel)
                {
                    Id = CubismNativeModel.ReadStringFromPtrArray(idsPtr, i),
                    Index = i,
                    MinimumValue = mins[i],
                    MaximumValue = maxs[i],
                    DefaultValue = defaults[i],
                    Value = defaults[i]
                };
            }

            // Parameter index cache is built once after load — hot loops
            // resolve by index only.
            _parameterIndexById.Clear();
            for (int i = 0; i < count; i++)
                _parameterIndexById[Parameters[i].Id] = i;

            _faceTrackCache = BuildFaceTrackCache();
        }

        /// <summary>
        /// Precomputes the face-track injection list once after load:
        /// ParamAngleX/Y (×30) and ParamEyeBallX/Y (×1) — LAppModel semantics.
        /// Parameters absent from the model are skipped.
        /// </summary>
        private FaceTrackEntry[] BuildFaceTrackCache()
        {
            var specs = new (string Id, bool FromTargetX, float Scale)[]
            {
                ("ParamAngleX", true, 30f),
                ("ParamAngleY", false, 30f),
                ("ParamEyeBallX", true, 1f),
                ("ParamEyeBallY", false, 1f),
            };

            var result = new List<FaceTrackEntry>(specs.Length);
            foreach (var spec in specs)
            {
                if (!_parameterIndexById.TryGetValue(spec.Item1, out var index)) continue;
                result.Add(new FaceTrackEntry
                {
                    Index = index,
                    Id = spec.Item1,
                    FromTargetX = spec.Item2,
                    Scale = spec.Item3
                });
            }
            return result.ToArray();
        }

        private unsafe void SetupParts()
        {
            var count = _nativeModel.GetPartCount();
            PartOpacities = new CubismPartOpacity[count];
            var idsPtr = _nativeModel.GetPartIds();
            var opacities = _nativeModel.GetPartOpacities();

            for (int i = 0; i < count; i++)
            {
                PartOpacities[i] = new CubismPartOpacity(_nativeModel)
                {
                    Id = CubismNativeModel.ReadStringFromPtrArray(idsPtr, i),
                    Index = i,
                    Value = opacities[i]
                };
            }
        }

        private void SetupEffects()
        {
            // Collect the Groups (EyeBlink/LipSync) parameter ids once — they are
            // wired into every motion via SetEffectIds (see StartMotion).
            _eyeBlinkIds.Clear();
            for (int i = 0; i < _modelSetting.GetEyeBlinkParameterCount(); i++)
                _eyeBlinkIds.Add(_modelSetting.GetEyeBlinkParameterId(i));

            _lipSyncIds.Clear();
            for (int i = 0; i < _modelSetting.GetLipSyncParameterCount(); i++)
                _lipSyncIds.Add(_modelSetting.GetLipSyncParameterId(i));

            if (EnableEyeBlink && _eyeBlinkIds.Count > 0)
            {
                _eyeBlink = new CubismEyeBlink();
                _eyeBlink.SetParameterIds(_eyeBlinkIds);
            }

            var physicsPath = _modelSetting.GetPhysicsFileName();
            if (EnablePhysics && !string.IsNullOrEmpty(physicsPath))
            {
                var physBytes = ReadModFile(physicsPath);
                if (physBytes != null)
                    _physics = new CubismPhysics(physBytes);
                else
                    GameLogger.Log("LIVE2D", $"SetupEffects: failed to read physics '{physicsPath}'", LogLevel.Warning);
            }

            var posePath = _modelSetting.GetPoseFileName();
            if (EnablePose && !string.IsNullOrEmpty(posePath))
            {
                var poseBytes = ReadModFile(posePath);
                if (poseBytes != null)
                    _pose = new CubismPose(poseBytes);
                else
                    GameLogger.Log("LIVE2D", $"SetupEffects: failed to read pose '{posePath}'", LogLevel.Warning);
            }
        }

        private byte[] ReadModFile(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/');
            var combined = string.IsNullOrEmpty(_modelDir) ? normalized : $"{_modelDir}/{normalized}";
            var result = _fileProvider.ReadFileBinary(combined);
            if (result == null)
                GameLogger.Log("LIVE2D", $"ReadModFile: provider returned null for '{combined}'", LogLevel.Warning);
            return result;
        }


        /// <summary>
        /// Sets the face-tracking target. Drives eye/head movement parameters
        /// (ParamAngleX/Y, ParamEyeBallX/Y) through a smooth damped approach
        /// in <see cref="CubismTargetPoint"/>.
        /// </summary>
        public void SetTargetPoint(float x, float y) => _targetPoint.Set(x, y);

        /// <summary>
        /// Finds a parameter by its Cubism ID string (e.g. "ParamAngleX").
        /// Returns null if not found.
        /// </summary>
        public CubismParameter FindParameter(string id)
        {
            foreach (var p in Parameters)
                if (p.Id == id) return p;
            return null;
        }

        /// <summary>
        /// Finds a part by its Cubism ID string. Returns null if not found.
        /// </summary>
        public CubismPartOpacity FindPart(string id)
        {
            foreach (var p in PartOpacities)
                if (p.Id == id) return p;
            return null;
        }

        /// <summary>
        /// Sets a parameter value directly in the native model, bypassing the per-frame
        /// face-tracking injection in <see cref="OnUpdate"/>. Once a parameter is set
        /// via this method, <see cref="OnUpdate"/> stops overwriting it — the caller
        /// takes full control of that parameter's value.
        /// Safe to call before the model is loaded — silently ignored.
        /// </summary>
        /// <param name="id">Cubism parameter ID (e.g. "ParamAngleX").</param>
        /// <param name="value">Raw parameter value. Caller should clamp to the parameter's bounds.</param>
        public void SetLiveParameter(string id, float value)
        {
            if (!_loaded)
            {
                GameLogger.Log("LIVE2D", $"SetLiveParameter: model not loaded, ignoring id='{id}'", LogLevel.Warning);
                return;
            }

            unsafe
            {
                if (!_parameterIndexById.TryGetValue(id, out var index))
                {
                    GameLogger.Log("LIVE2D", $"SetLiveParameter: parameter '{id}' not found", LogLevel.Warning);
                    return;
                }

                _nativeModel.GetParameterValues()[index] = value;
                _manualValues[id] = value;

                if (index < Parameters.Length)
                    Parameters[index].Value = value;
            }
        }

        /// <summary>
        /// Starts a motion animation by group name and index.
        /// Loads the motion3.json from the model directory, applies the
        /// model-specified fade-in/fade-out times, and enqueues it.
        /// Safe to call before the model is loaded — the call is silently ignored.
        /// </summary>
        /// <param name="group">Motion group name from model3.json (e.g. "Idle", "TapBody").</param>
        /// <param name="no">Zero-based index within the group.</param>
        public void StartMotion(string group, int no)
        {
            if (!_loaded) return;
            if (no < 0 || no >= _modelSetting.GetMotionCount(group)) return;

            var motionFileName = _modelSetting.GetMotionFileName(group, no);
            var motionBytes = ReadModFile(motionFileName);
            if (motionBytes == null) return;

            var motion = new CubismMotion(motionBytes);

            var fadeIn = _modelSetting.GetMotionFadeInTimeValue(group, no);
            var fadeOut = _modelSetting.GetMotionFadeOutTimeValue(group, no);
            if (fadeIn >= 0f) motion.FadeInSeconds = fadeIn;
            if (fadeOut >= 0f) motion.FadeOutSeconds = fadeOut;

            // Wire the motion's model curves (EyeBlink/LipSync) to the parameter
            // ids from model3.json Groups — same as LAppModel.StartMotion.
            motion.SetEffectIds(_eyeBlinkIds, _lipSyncIds);

            _motionManager.StartMotion(motion, true, _userTimeSeconds);
        }

        /// <summary>
        /// Starts an expression by its name (as defined in the model3.json
        /// Expressions array). Looks up the expression file, loads the
        /// .exp3.json, and enqueues it in the expression manager.
        /// Safe to call before model loaded — silently ignored.
        /// </summary>
        public void StartExpression(string expressionId)
        {
            if (!_loaded) return;

            for (int i = 0; i < _modelSetting.GetExpressionCount(); i++)
            {
                if (_modelSetting.GetExpressionName(i) == expressionId)
                {
                    var expBytes = ReadModFile(_modelSetting.GetExpressionFileName(i));
                    if (expBytes == null) return;

                    var expr = new CubismExpressionMotion(expBytes);
                    _expressionManager.StartMotion(expr, true, _userTimeSeconds);
                    return;
                }
            }
        }

        /// <summary>Stops all currently playing motion animations.</summary>
        public void StopMotion() => _motionManager.StopAllMotions();

        /// <summary>Stops all currently playing expression animations.</summary>
        public void StopExpression() => _expressionManager.StopAllMotions();
    }
}
