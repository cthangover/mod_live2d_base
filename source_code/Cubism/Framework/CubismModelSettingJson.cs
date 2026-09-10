using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Cthangover.Live2D.Cubism.Framework
{
    /// <summary>
    /// Interface for reading a Cubism model3.json setting file.
    /// All accessors return default/empty values when the corresponding
    /// data is absent from the JSON.
    /// </summary>
    public interface ICubismModelSetting
    {
        /// <summary>Returns the relative path to the .moc3 file.</summary>
        string GetModelFileName();

        /// <summary>Returns the number of texture references in the model.</summary>
        int GetTextureCount();

        /// <summary>Returns the relative path to the texture at the given index.</summary>
        string GetTextureFileName(int index);

        /// <summary>Returns the relative path to the physics3.json file, or null.</summary>
        string GetPhysicsFileName();

        /// <summary>Returns the relative path to the pose3.json file, or null.</summary>
        string GetPoseFileName();

        /// <summary>Returns the number of expressions defined in the model.</summary>
        int GetExpressionCount();

        /// <summary>Returns the display name of the expression at the given index.</summary>
        string GetExpressionName(int index);

        /// <summary>Returns the relative path to the .exp3.json file for the expression at the given index.</summary>
        string GetExpressionFileName(int index);

        /// <summary>Returns the number of motion groups.</summary>
        int GetMotionGroupCount();

        /// <summary>Returns the name of the motion group at the given index.</summary>
        string GetMotionGroupName(int index);

        /// <summary>Returns the number of motions within the named group.</summary>
        int GetMotionCount(string groupName);

        /// <summary>Returns the relative path to the motion3.json file for the given group and index.</summary>
        string GetMotionFileName(string groupName, int index);

        /// <summary>Returns the relative path to the associated sound file, or null.</summary>
        string GetMotionSoundFileName(string groupName, int index);

        /// <summary>Returns the fade-in time in seconds for the motion, or -1 if not specified.</summary>
        float GetMotionFadeInTimeValue(string groupName, int index);

        /// <summary>Returns the fade-out time in seconds for the motion, or -1 if not specified.</summary>
        float GetMotionFadeOutTimeValue(string groupName, int index);

        /// <summary>Returns the number of parameters in the EyeBlink group.</summary>
        int GetEyeBlinkParameterCount();

        /// <summary>Returns the parameter ID at the given index within the EyeBlink group.</summary>
        string GetEyeBlinkParameterId(int index);

        /// <summary>Returns the number of parameters in the LipSync group.</summary>
        int GetLipSyncParameterCount();

        /// <summary>Returns the parameter ID at the given index within the LipSync group.</summary>
        string GetLipSyncParameterId(int index);

        /// <summary>Returns the number of hit area definitions.</summary>
        int GetHitAreasCount();

        /// <summary>Returns the hit area ID at the given index.</summary>
        string GetHitAreaId(int index);

        /// <summary>Returns the hit area display name at the given index.</summary>
        string GetHitAreaName(int index);

        /// <summary>
        /// Returns the Layout section as a dictionary of lowercased key -> float value.
        /// Supported keys: width, height, x, y, center_x, center_y, top, bottom, left, right.
        /// </summary>
        Dictionary<string, float> GetLayoutMap();
    }

    /// <summary>
    /// JSON parser for the Cubism .model3.json format (Live2D model settings).
    /// Implements <see cref="ICubismModelSetting"/>.
    /// 
    /// Provides typed access to all sections: FileReferences (moc, textures,
    /// expressions, motions), Groups (EyeBlink, LipSync), HitAreas, and Layout.
    /// All string path values are returned as-is (relative to the model directory);
    /// it is the caller's responsibility to resolve them through a file provider.
    /// 
    /// Instances hold a <see cref="JsonDocument"/> and should be disposed
    /// via <see cref="Dispose"/> when no longer needed.
    /// </summary>
    public class CubismModelSettingJson : ICubismModelSetting
    {
        private readonly JsonDocument _json;
        private readonly JsonElement _root;
        private readonly JsonElement? _fileRefs;
        private readonly JsonElement? _groups;
        private readonly JsonElement? _hitAreas;
        private readonly JsonElement? _layout;

        /// <summary>
        /// Parses the given .model3.json bytes.
        /// </summary>
        public CubismModelSettingJson(byte[] jsonBytes)
        {
            _json = JsonDocument.Parse(jsonBytes);
            _root = _json.RootElement;
            if (_root.TryGetProperty("FileReferences", out var fr))
                _fileRefs = fr;
            if (_root.TryGetProperty("Groups", out var gr))
                _groups = gr;
            if (_root.TryGetProperty("HitAreas", out var ha))
                _hitAreas = ha;
            if (_root.TryGetProperty("Layout", out var lo))
                _layout = lo;
        }

        public string GetModelFileName() =>
            _fileRefs?.GetProperty("Moc").GetString();

        public int GetTextureCount() =>
            _fileRefs?.TryGetProperty("Textures", out var arr) == true ? arr.GetArrayLength() : 0;

        public string GetTextureFileName(int index) =>
            _fileRefs?.GetProperty("Textures")[index].GetString();

        public string GetPhysicsFileName() =>
            TryGetString(_fileRefs, "Physics");

        public string GetPoseFileName() =>
            TryGetString(_fileRefs, "Pose");

        public int GetExpressionCount() =>
            _fileRefs?.TryGetProperty("Expressions", out var arr) == true ? arr.GetArrayLength() : 0;

        public string GetExpressionName(int index) =>
            _fileRefs?.GetProperty("Expressions")[index].GetProperty("Name").GetString();

        public string GetExpressionFileName(int index) =>
            _fileRefs?.GetProperty("Expressions")[index].GetProperty("File").GetString();

        public int GetMotionGroupCount() =>
            _fileRefs?.TryGetProperty("Motions", out var motions) == true ? motions.EnumerateObject().Count() : 0;

        public string GetMotionGroupName(int index)
        {
            int i = 0;
            foreach (var prop in _fileRefs?.GetProperty("Motions").EnumerateObject() ?? default)
                if (i++ == index) return prop.Name;
            return null;
        }

        public int GetMotionCount(string groupName) =>
            _fileRefs?.TryGetProperty("Motions", out var m) == true &&
            m.TryGetProperty(groupName, out var grp) ? grp.GetArrayLength() : 0;

        public string GetMotionFileName(string groupName, int index) =>
            _fileRefs?.GetProperty("Motions").GetProperty(groupName)[index].GetProperty("File").GetString();

        public string GetMotionSoundFileName(string groupName, int index) =>
            TryGetString(_fileRefs?.GetProperty("Motions").GetProperty(groupName)[index], "Sound");

        public float GetMotionFadeInTimeValue(string groupName, int index) =>
            TryGetFloat(_fileRefs?.GetProperty("Motions").GetProperty(groupName)[index], "FadeInTime");

        public float GetMotionFadeOutTimeValue(string groupName, int index) =>
            TryGetFloat(_fileRefs?.GetProperty("Motions").GetProperty(groupName)[index], "FadeOutTime");

        public int GetEyeBlinkParameterCount()
        {
            foreach (var group in _groups?.EnumerateArray() ?? default)
                if (group.GetProperty("Name").GetString() == "EyeBlink")
                    return group.GetProperty("Ids").GetArrayLength();
            return 0;
        }

        public string GetEyeBlinkParameterId(int index)
        {
            foreach (var group in _groups?.EnumerateArray() ?? default)
                if (group.GetProperty("Name").GetString() == "EyeBlink")
                    return group.GetProperty("Ids")[index].GetString();
            return null;
        }

        public int GetLipSyncParameterCount()
        {
            foreach (var group in _groups?.EnumerateArray() ?? default)
                if (group.GetProperty("Name").GetString() == "LipSync")
                    return group.GetProperty("Ids").GetArrayLength();
            return 0;
        }

        public string GetLipSyncParameterId(int index)
        {
            foreach (var group in _groups?.EnumerateArray() ?? default)
                if (group.GetProperty("Name").GetString() == "LipSync")
                    return group.GetProperty("Ids")[index].GetString();
            return null;
        }

        public int GetHitAreasCount() =>
            _hitAreas?.GetArrayLength() ?? 0;

        public string GetHitAreaId(int index) =>
            _hitAreas?[index].GetProperty("Id").GetString();

        public string GetHitAreaName(int index) =>
            _hitAreas?[index].GetProperty("Name").GetString();

        public Dictionary<string, float> GetLayoutMap()
        {
            var map = new Dictionary<string, float>();
            if (_layout == null) return map;
            foreach (var prop in _layout.Value.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    map[prop.Name.ToLowerInvariant()] = prop.Value.GetSingle();
            return map;
        }

        private static string TryGetString(JsonElement? parent, string key) =>
            parent?.TryGetProperty(key, out var val) == true ? val.GetString() : null;

        private static float TryGetFloat(JsonElement? parent, string key) =>
            parent?.TryGetProperty(key, out var val) == true && val.ValueKind == JsonValueKind.Number
                ? val.GetSingle() : -1f;

        /// <summary>
        /// Releases the underlying <see cref="JsonDocument"/>.
        /// </summary>
        public void Dispose() => _json?.Dispose();
    }
}
