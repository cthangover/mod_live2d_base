using System;
using System.Collections.Generic;
using Godot;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Port of CubismModelMatrix.cpp (Cubism SDK for Native 5-r.5).
    /// Manages the 2D transformation (scale, translation, alignment) of a Cubism
    /// model inside its logical coordinate space.
    ///
    /// SDK semantics to be aware of:
    /// <list type="bullet">
    ///   <item><see cref="SetWidth"/> and <see cref="SetHeight"/> MULTIPLY the
    ///       current scale by target / canvas-size (they do not assign it),
    ///       exactly like the C++ original.</item>
    ///   <item>The (width, height) constructor performs
    ///       <c>SetHeight(2.0f)</c> like the SDK, i.e. the model is initially
    ///       scaled to occupy 2 logical units of height.</item>
    ///   <item>Translation methods are INCREMENTAL in the SDK: SetX/SetY,
    ///       SetPosition, Top, Left (via TranslateX/Y) add to the current
    ///       translation. SetupFromLayout is unaffected because it starts from a
    ///       fresh matrix (translation 0).</item>
    ///   <item>CenterX/CenterY/Bottom/Right compute their offset from the full
    ///       scaled size (canvas size × current scale).</item>
    /// </list>
    ///
    /// <see cref="SetupFromLayout"/> reads positioning keys from a Cubism
    /// .model3.json "Layout" section; <see cref="ApplyToNode"/> writes the final
    /// transform to a Godot <see cref="Node2D"/>.
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
        /// Creates a matrix referenced to the given canvas dimensions.
        /// Like the SDK constructor, this calls <see cref="SetHeight"/>(2.0f),
        /// so the model initially occupies 2 logical units of height.
        /// </summary>
        public CubismModelMatrix(float width, float height)
        {
            _width = width;
            _height = height;

            SetHeight(2.0f);
        }

        /// <summary>Current scale on the X axis.</summary>
        public float ScaleX => _scaleX;

        /// <summary>Current scale on the Y axis.</summary>
        public float ScaleY => _scaleY;

        /// <summary>Current X translation offset.</summary>
        public float TranslateX => _translateX;

        /// <summary>Current Y translation offset.</summary>
        public float TranslateY => _translateY;

        /// <summary>SDK CubismModelMatrix::Scale — multiplies the current scale.</summary>
        public void Scale(float scaleX, float scaleY)
        {
            _scaleX *= scaleX;
            _scaleY *= scaleY;
        }

        /// <summary>
        /// SDK SetWidth: multiplies the current uniform scale by w / canvasWidth.
        /// Ignored when the canvas width is zero or unknown (the SDK would divide by zero).
        /// </summary>
        public void SetWidth(float w)
        {
            if (_width <= 0f) return;

            var scaleX = w / _width;
            var scaleY = scaleX;
            Scale(scaleX, scaleY);
        }

        /// <summary>
        /// SDK SetHeight: multiplies the current uniform scale by h / canvasHeight.
        /// Ignored when the canvas height is zero or unknown (the SDK would divide by zero).
        /// </summary>
        public void SetHeight(float h)
        {
            if (_height <= 0f) return;

            var scaleX = h / _height;
            var scaleY = scaleX;
            Scale(scaleX, scaleY);
        }

        /// <summary>SDK Translate — adds x and y to the current translation
        /// (the SDK's TranslateX/TranslateY helpers are folded in here; the
        /// TranslateX/TranslateY properties above are the read accessors).</summary>
        public void Translate(float x, float y)
        {
            _translateX += x;
            _translateY += y;
        }

        /// <summary>SDK SetPosition — incremental (adds to the current translation).</summary>
        public void SetPosition(float x, float y) => Translate(x, y);

        /// <summary>SDK SetX — incremental (adds to the current X translation).</summary>
        public void SetX(float x) => _translateX += x;

        /// <summary>SDK SetY — incremental (adds to the current Y translation).</summary>
        public void SetY(float y) => _translateY += y;

        /// <summary>
        /// Centers the model horizontally and vertically at the given point.
        /// Equivalent to calling <see cref="CenterX"/> then <see cref="CenterY"/>.
        /// </summary>
        public void SetCenterPosition(float x, float y)
        {
            CenterX(x);
            CenterY(y);
        }

        /// <summary>SDK CenterX — adds x - (width × scaleX) / 2 to the X translation.</summary>
        public void CenterX(float x)
        {
            var w = _width * _scaleX;
            _translateX += x - (w / 2f);
        }

        /// <summary>SDK CenterY — adds y - (height × scaleY) / 2 to the Y translation.</summary>
        public void CenterY(float y)
        {
            var h = _height * _scaleY;
            _translateY += y - (h / 2f);
        }

        /// <summary>SDK Top — adds y to the Y translation (via SetY).</summary>
        public void Top(float y) => SetY(y);

        /// <summary>SDK Bottom — adds y - (height × scaleY) to the Y translation.</summary>
        public void Bottom(float y)
        {
            var h = _height * _scaleY;
            _translateY += y - h;
        }

        /// <summary>SDK Left — adds x to the X translation (via SetX).</summary>
        public void Left(float x) => SetX(x);

        /// <summary>SDK Right — adds x - (width × scaleX) to the X translation.</summary>
        public void Right(float x)
        {
            var w = _width * _scaleX;
            _translateX += x - w;
        }

        /// <summary>
        /// SDK SetupFromLayout — configures scale and position from a layout
        /// dictionary (lowercased keys). Width/height are applied first (they
        /// affect scale), then the position keys: "x", "y", "center_x",
        /// "center_y", "top", "bottom", "left", "right".
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
