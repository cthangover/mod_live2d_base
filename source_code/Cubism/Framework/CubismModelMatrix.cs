using System;
using System.Collections.Generic;
using Godot;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Manages the 2D transformation (scale, translation, alignment) for a
    /// Cubism model rendered inside a Godot <see cref="Node2D"/>.
    /// 
    /// Works with two coordinate systems:
    /// <list type="bullet">
    ///   <item>Cubism canvas coordinates (the model's native coordinate space)</item>
    ///   <item>Godot node local coordinates (where the model is placed)</item>
    /// </list>
    /// 
    /// The scale is computed relative to the canvas dimensions stored at construction.
    /// <see cref="SetWidth"/> and <see cref="SetHeight"/> set a uniform scale based
    /// on the ratio of target size to canvas size. <see cref="SetupFromLayout"/> reads
    /// positioning keys from a Cubism .model3.json "Layout" section.
    /// <see cref="ApplyToNode"/> writes the final transform to a Godot Node2D.
    /// </summary>
    public class CubismModelMatrix
    {
        private float _width;
        private float _height;
        private float _scaleX = 1f;
        private float _scaleY = 1f;
        private float _translateX;
        private float _translateY;

        /// <summary>Creates an identity matrix with no known canvas size.</summary>
        public CubismModelMatrix() { }

        /// <summary>
        /// Creates a matrix referenced to the given canvas pixel dimensions.
        /// Subsequent calls to <see cref="SetWidth"/> and <see cref="SetHeight"/>
        /// will compute scale as target / canvas.
        /// </summary>
        public CubismModelMatrix(float width, float height)
        {
            _width = width;
            _height = height;
        }

        /// <summary>Current uniform scale on the X axis.</summary>
        public float ScaleX => _scaleX;

        /// <summary>Current uniform scale on the Y axis.</summary>
        public float ScaleY => _scaleY;

        /// <summary>Current X translation offset in Godot local coordinates.</summary>
        public float TranslateX => _translateX;

        /// <summary>Current Y translation offset in Godot local coordinates.</summary>
        public float TranslateY => _translateY;

        /// <summary>
        /// Sets uniform scale to match the given width. Scale = targetWidth / canvasWidth.
        /// Does nothing if canvas width is zero or unknown.
        /// </summary>
        public void SetWidth(float w)
        {
            if (_width > 0f)
            {
                var scale = w / _width;
                _scaleX = scale;
                _scaleY = scale;
            }
        }

        /// <summary>
        /// Sets uniform scale to match the given height. Scale = targetHeight / canvasHeight.
        /// Does nothing if canvas height is zero or unknown.
        /// </summary>
        public void SetHeight(float h)
        {
            if (_height > 0f)
            {
                var scale = h / _height;
                _scaleX = scale;
                _scaleY = scale;
            }
        }

        /// <summary>Sets absolute translation position.</summary>
        public void SetPosition(float x, float y)
        {
            _translateX = x;
            _translateY = y;
        }

        /// <summary>Sets absolute X translation.</summary>
        public void SetX(float x) => _translateX = x;

        /// <summary>Sets absolute Y translation.</summary>
        public void SetY(float y) => _translateY = y;

        /// <summary>
        /// Centers the model horizontally and vertically at the given point.
        /// Equivalent to calling <see cref="CenterX"/> then <see cref="CenterY"/>.
        /// </summary>
        public void SetCenterPosition(float x, float y)
        {
            CenterX(x);
            CenterY(y);
        }

        /// <summary>
        /// Centers horizontally: translateX = x - (width * scaleX) / 2.
        /// </summary>
        public void CenterX(float x) => _translateX = x - (_width * _scaleX) / 2f;

        /// <summary>
        /// Centers vertically: translateY = y - (height * scaleY) / 2.
        /// </summary>
        public void CenterY(float y) => _translateY = y - (_height * _scaleY) / 2f;

        /// <summary>Aligns the top edge of the model to y.</summary>
        public void Top(float y) => _translateY = y;

        /// <summary>Aligns the bottom edge of the model to y.</summary>
        public void Bottom(float y) => _translateY = y - _height * _scaleY;

        /// <summary>Aligns the left edge of the model to x.</summary>
        public void Left(float x) => _translateX = x;

        /// <summary>Aligns the right edge of the model to x.</summary>
        public void Right(float x) => _translateX = x - _width * _scaleX;

        /// <summary>
        /// Configures scale and position from a layout dictionary.
        /// Supported keys: "width", "height", "x", "y", "center_x", "center_y",
        /// "top", "bottom", "left", "right".
        /// Width/height are applied first (since they affect scale),
        /// then position keys are applied.
        /// </summary>
        public void SetupFromLayout(Dictionary<string, float> layout)
        {
            foreach (var kvp in layout)
            {
                switch (kvp.Key)
                {
                    case "width": SetWidth(kvp.Value); break;
                    case "height": SetHeight(kvp.Value); break;
                }
            }

            foreach (var kvp in layout)
            {
                switch (kvp.Key)
                {
                    case "x": SetX(kvp.Value); break;
                    case "y": SetY(kvp.Value); break;
                    case "center_x": CenterX(kvp.Value); break;
                    case "center_y": CenterY(kvp.Value); break;
                    case "top": Top(kvp.Value); break;
                    case "bottom": Bottom(kvp.Value); break;
                    case "left": Left(kvp.Value); break;
                    case "right": Right(kvp.Value); break;
                }
            }
        }

        /// <summary>
        /// Writes the current scale and translation to a Godot <see cref="Node2D"/>.
        /// Sets both <c>Scale</c> and <c>Position</c> properties.
        /// </summary>
        public void ApplyToNode(Node2D node)
        {
            node.Scale = new Vector2(_scaleX, _scaleY);
            node.Position = new Vector2(_translateX, _translateY);
        }
    }
}
