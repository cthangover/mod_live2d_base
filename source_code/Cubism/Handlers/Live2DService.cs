using Godot;
using Cthangover.Live2D.Cubism.Framework;

namespace Cthangover.Live2D.Cubism.Services
{
    /// <summary>
    /// Configuration for <see cref="Live2DService.CreateModel(Node, Live2DModelConfig)"/>.
    /// When <see cref="Position"/> is set, <see cref="ScaleFit"/> defaults to
    /// <see cref="FitMode.None"/> to disable automatic layout, giving manual
    /// coordinates full control. Otherwise <see cref="FitMode.FitAuto"/> is used.
    /// </summary>
    public class Live2DModelConfig
    {
        public string ModelPath;
        public string ModId;
        public string NodeName;
        public Vector2? Position;
        public Vector2? Scale;
        public float AnchorX = 0.5f;
        public float AnchorY = 0.5f;
        public FitMode ScaleFit = FitMode.FitAuto;

        /// <summary>
        /// Enables the automatic eye-blink effect. Disable for pixel-deterministic
        /// rendering (screenshot/visual tests): the blink fires on a random timer
        /// and would otherwise change pixels between captures.
        /// </summary>
        public bool EnableEyeBlink = true;
    }

    /// <summary>
    /// High-level factory for creating Live2D model nodes and attaching them
    /// to the scene tree. Encapsulates the boilerplate of instantiating
    /// <see cref="CubismModelNode"/>, adding to a parent, and calling
    /// <see cref="CubismModelNode.LoadModelFromMod"/>.
    /// </summary>
    public static class Live2DService
    {
        /// <summary>
        /// Creates a <see cref="CubismModelNode"/>, adds it to <paramref name="parent"/>,
        /// and loads the model from the specified mod.
        /// </summary>
        /// <param name="parent">Parent node to attach the model to.</param>
        /// <param name="modelPath">Path to the .model3.json file, relative to the mod root.</param>
        /// <param name="modId">Registered mod ID containing the model assets.</param>
        /// <param name="nodeName">Godot node name for the model instance.</param>
        /// <returns>The created and loaded model node.</returns>
        public static CubismModelNode CreateModel(                           
            Node parent,
            string modelPath,
            string nodeName,
            string modId = null)
        {
            return CreateModel(parent, new Live2DModelConfig
            {
                ModelPath = modelPath,
                ModId = modId,
                NodeName = nodeName
            });
        }
               
        /// <summary>
        /// Creates a <see cref="CubismModelNode"/> using full configuration.
        /// When <see cref="Live2DModelConfig.Position"/> is provided,
        /// <see cref="Live2DModelConfig.ScaleFit"/> is forced to
        /// <see cref="FitMode.None"/> — manual coordinates take priority over
        /// the automatic <see cref="CubismModelNode.DeferredApplyLayout"/>.
        /// </summary>
        public static CubismModelNode CreateModel(Node parent, Live2DModelConfig config)
        {
            var fitMode = config.Position.HasValue ? FitMode.None : config.ScaleFit;

            var model = new CubismModelNode
            {
                Name = config.NodeName,
                AnchorX = config.AnchorX,
                AnchorY = config.AnchorY,
                ScaleFit = fitMode,
                EnableEyeBlink = config.EnableEyeBlink
            };

            parent.AddChild(model);

            if (config.Position.HasValue)
                model.Position = config.Position.Value;
            if (config.Scale.HasValue)
                model.Scale = config.Scale.Value;

            model.LoadModelFromMod(config.ModelPath, config.ModId);
            return model;
        }
    }
}
