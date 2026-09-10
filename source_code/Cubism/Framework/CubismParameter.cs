namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Cubism model parameter.
    /// Each parameter has a string ID, a numeric index into the native
    /// parameter arrays, current value, and bounds (min, max, default).
    /// 
    /// Parameters are created during <see cref="CubismModelNode.SetupParameters"/>
    /// and exposed via <see cref="CubismModelNode.Parameters"/>. External code
    /// can read/write <see cref="Value"/> and call <see cref="ApplyToModel"/>
    /// to push changes back to the CubismCore native arrays.
    /// </summary>
    public class CubismParameter
    {
        public CubismNativeModel Model { get; }

        public CubismParameter(CubismNativeModel model)
        {
            Model = model;
        }
        
        /// <summary>Cubism parameter ID string (e.g. "ParamAngleX", "ParamMouthOpenY").</summary>
        public string Id { get; set; }

        /// <summary>Zero-based index into the native parameter arrays.</summary>
        public int Index { get; set; }

        /// <summary>Current parameter value. Writes to native model when <see cref="ApplyToModel"/> is called.</summary>
        public float Value { get; set; }

        /// <summary>Minimum allowed value for this parameter.</summary>
        public float MinimumValue { get; set; }

        /// <summary>Maximum allowed value for this parameter.</summary>
        public float MaximumValue { get; set; }

        /// <summary>Default (rest) value for this parameter from the moc3.</summary>
        public float DefaultValue { get; set; }

        /// <summary>
        /// Writes <see cref="Value"/> directly to the native model's parameter array.
        /// No clamping is performed — the caller should enforce bounds if needed.
        /// </summary>
        public unsafe void ApplyToModel()
        {
            var values = Model.GetParameterValues();
            values[Index] = Value;
        }
    }
}
