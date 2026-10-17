using Cthangover.Core.UI;
using Cthangover.Core.Utils;
using Cthangover.Live2D.Cubism.Framework;
using Cthangover.Live2D.Cubism.Services;
using Godot;

using Cthangover.Mods.Interface.Base.Lists;
namespace Mods.Live2DTest
{
    /// <summary>
    /// Scene-driven replacement for the old live2d_test subscription snippet.
    /// The overlay root is added to a shell slot by <see cref="UiHost"/>; it
    /// spawns the Cubism model into the <c>Lastground</c> anchor and keeps a
    /// transparent drag handle on top so the player can steer the model's head
    /// parameters with the mouse. Created per <c>home_outside</c> visit
    /// (once=false) and destroyed together with the overlay.
    /// </summary>
    public partial class Live2DOverlay : ModWidget
    {
        private const string ModelPath = "models/runtime/ren.model3.json";
        private const string ModelNodeName = "Live2DTestModel";
        private const string ContainerNodeName = "Lastground";

        private CubismModelNode model;
        private ColorRect dragHandle;
        private bool dragging;
        private float mouseStartX;
        private float mouseStartY;
        private float baseValueX;
        private float baseValueY;

        protected override void Construct()
        {
            MouseFilter = MouseFilterEnum.Ignore;

            var container = UiHost.Instance?.FindNode(ContainerNodeName);
            if (container == null)
            {
                GameLogger.Log("LIVE2D_TEST", $"Live2DOverlay: container '{ContainerNodeName}' not found", LogLevel.Error);
                return;
            }

            model = Live2DService.CreateModel(container, ModelPath, ModelNodeName, "live2d__avatar");
            if (model == null)
            {
                GameLogger.Log("LIVE2D_TEST", "Live2DOverlay: model creation failed", LogLevel.Error);
                return;
            }

            dragHandle = new ColorRect
            {
                Name = "Live2DDragPanel",
                MouseFilter = MouseFilterEnum.Stop,
                Color = new Color(0, 1f, 0, 0f),
                Size = new Vector2(400, 400),
                AnchorLeft = 0.5f,
                AnchorRight = 0.5f,
                AnchorTop = 0.5f,
                AnchorBottom = 0.5f,
                OffsetLeft = -200f,
                OffsetRight = 200f,
                OffsetTop = -200f,
                OffsetBottom = -200f
            };
            AddChild(dragHandle);
            dragHandle.GuiInput += OnDragInput;
        }

        protected override void Destruct()
        {
            if (model != null && GodotObject.IsInstanceValid(model))
                model.QueueFree();
            model = null;
        }

        private void OnDragInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    dragging = true;
                    mouseStartX = mb.GlobalPosition.X;
                    mouseStartY = mb.GlobalPosition.Y;
                    baseValueX = model.FindParameter("ParamAngleX")?.Value ?? 0f;
                    baseValueY = model.FindParameter("ParamAngleY")?.Value ?? 0f;
                }
                else
                {
                    dragging = false;
                }
                return;
            }

            if (!dragging || @event is not InputEventMouseMotion mm || model == null)
                return;

            var targetX = model.FindParameter("ParamAngleX");
            var targetY = model.FindParameter("ParamAngleY");
            var deltaX = (mm.GlobalPosition.X - mouseStartX) / 30f;
            var deltaY = (mm.GlobalPosition.Y - mouseStartY) / 30f;

            if (targetX != null)
                model.SetLiveParameter("ParamAngleX", Mathf.Clamp(baseValueX + deltaX, targetX.MinimumValue, targetX.MaximumValue));
            if (targetY != null)
                model.SetLiveParameter("ParamAngleY", Mathf.Clamp(baseValueY + deltaY, targetY.MinimumValue, targetY.MaximumValue));
        }
    }
}
