using System.Runtime.InteropServices;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Specifies which part of the model a motion curve affects.
    /// </summary>
    public enum CubismMotionCurveTarget { Model, Parameter, PartOpacity }

    /// <summary>
    /// Segment interpolation type within a motion curve.
    /// <list type="bullet">
    ///   <item><see cref="Linear"/> — straight line between two points</item>
    ///   <item><see cref="Bezier"/> — cubic bezier (4 control points per segment)</item>
    ///   <item><see cref="Stepped"/> — hold previous value until the segment ends</item>
    ///   <item><see cref="InverseStepped"/> — jump to next value at segment start</item>
    /// </list>
    /// </summary>
    public enum CubismMotionSegmentType { Linear = 0, Bezier = 1, Stepped = 2, InverseStepped = 3 }

    /// <summary>
    /// A time-value pair representing a keyframe or control point on a motion curve.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CubismMotionPoint
    {
        /// <summary>Time in seconds from the start of the motion.</summary>
        public float Time;

        /// <summary>Value at this point. For bezier control points, this is an intermediate value.</summary>
        public float Value;
    }

    /// <summary>
    /// Describes a single curve within a motion. Contains the target type,
    /// parameter/part ID, segment count, offset into the flat segment data,
    /// and per-curve fade timing.
    /// </summary>
    public struct CubismMotionCurve
    {
        public CubismMotionCurveTarget Type;
        public string Id;
        public int SegmentCount;
        public int BaseSegmentIndex;
        public float FadeInTime;
        public float FadeOutTime;
    }

    /// <summary>
    /// A named event that fires at a specific time during motion playback.
    /// Events are defined in the UserData section of the .motion3.json.
    /// Accessed via <see cref="CubismMotion.GetFiredEventValue"/>.
    /// </summary>
    public struct CubismMotionEvent
    {
        /// <summary>Time offset in seconds when this event triggers.</summary>
        public float FireTime;

        /// <summary>String value associated with the event.</summary>
        public string Value;
    }
}
