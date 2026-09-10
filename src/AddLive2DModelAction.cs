using Cthangover.Core.Actions;
using Cthangover.Core.Scenes;
using Cthangover.Core.Utils;
using Godot;
using Cthangover.Live2D.Cubism.Services;

namespace Mods.Live2D
{
    public class AddLive2DModelAction : IScenarioAction
    {
        public string Name => "add_live2d_model";

        public void Run(IActionContext ctx)
        {
            var modelPath = ctx.GetParam("model_path");
            var modId = ctx.GetParam("mod_id");
            var nodeName = ctx.GetParam("node_name") ?? "Live2DModel";

            if (string.IsNullOrEmpty(modelPath))
            {
                ctx.LogWarning("LIVE2D", "AddLive2DModelAction: missing 'model_path'");
                return;
            }

            var viewBox = SceneContextNode.FindNode<Control>("ViewBox");
            if (viewBox == null)
            {
                ctx.LogWarning("LIVE2D", "AddLive2DModelAction: ViewBox not found");
                return;
            }

            var existing = viewBox.GetNodeOrNull<Node>(nodeName);
            if (existing != null)
            {
                ctx.Log("LIVE2D", $"AddLive2DModelAction: '{nodeName}' already exists, skipping");
                return;
            }

            var config = new Live2DModelConfig
            {
                ModelPath = modelPath,
                ModId = modId,
                NodeName = nodeName
            };

            var xStr = ctx.GetParam("x");
            var yStr = ctx.GetParam("y");
            if (!string.IsNullOrEmpty(xStr) && !string.IsNullOrEmpty(yStr) &&
                float.TryParse(xStr, out var x) && float.TryParse(yStr, out var y))
            {
                config.Position = new Vector2(x, y);
            }

            var scaleStr = ctx.GetParam("scale");
            if (!string.IsNullOrEmpty(scaleStr) && float.TryParse(scaleStr, out var s))
                config.Scale = new Vector2(s, s);

            Live2DService.CreateModel(viewBox, config);
            ctx.Log("LIVE2D", $"AddLive2DModelAction: loaded '{modelPath}' from mod '{modId}' as '{nodeName}'");
        }
    }
}
