namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Cubism model part opacity.
    /// Each part has a string ID, a numeric index into the native part arrays,
    /// and a current opacity value (0 = hidden, 1 = fully visible).
    /// 
    /// Parts are created during <see cref="CubismModelNode.SetupParts"/>
    /// and exposed via <see cref="CubismModelNode.PartOpacities"/>.
    /// External code can set <see cref="Value"/> and call
    /// <see cref="ApplyToModel"/> to push the change to CubismCore.
    /// </summary>
    public class CubismPartOpacity
    {
        public CubismNativeModel Model { get; }

        public CubismPartOpacity(CubismNativeModel model)
        {
            Model = model;
        }
        
        /// <summary>Cubism part ID string (e.g. "PartArmL", "PartHairBack").</summary>
        public string Id { get; set; }

        /// <summary>Zero-based index into the native part arrays.</summary>
        public int Index { get; set; }

        /// <summary>
        /// Current opacity value for this part. 0 = hidden, 1 = fully visible.
        /// Written to native model when <see cref="ApplyToModel"/> is called.
        /// </summary>
        public float Value { get; set; }

        /// <summary>
        /// Writes <see cref="Value"/> directly to the native model's part opacities array.
        /// </summary>
        public unsafe void ApplyToModel()
        {
            var opacities = Model.GetPartOpacities();
            opacities[Index] = Value;
        }
    }
}
