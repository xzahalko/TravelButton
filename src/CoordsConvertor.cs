using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using UnityEngine;

public static class CoordsConvertor
{
    /// <summary>
    /// Best-effort converter that tries to produce a Vector3 from a heterogeneous coords value.
    /// Returns true and sets 'result' when conversion succeeded; returns false otherwise.
    ///
    /// Supported inputs:
    ///  - UnityEngine.Vector3
    ///  - UnityEngine.Vector2 (z = 0)
    ///  - float[] or double[] with length >= 3
    ///  - any IList (eg. object[]/List&lt;float&gt;) with Count >= 3
    ///  - an object that exposes numeric properties or fields x,y,z (case-insensitive)
    ///  - strings formatted like "x,y,z" (uses invariant culture)
    /// </summary>
    public static bool TryConvertToVector3(object coordsVal, out Vector3 result)
    {
        result = default;

        if (coordsVal == null) return false;

        // Direct Vector3
        if (coordsVal is Vector3 v3)
        {
            result = v3;
            return true;
        }

        // Vector2 -> Vector3 with z = 0
        if (coordsVal is Vector2 v2)
        {
            result = new Vector3(v2.x, v2.y, 0f);
            return true;
        }

        // float[] -> Vector3
        if (coordsVal is float[] fa && fa.Length >= 3)
        {
            result = new Vector3(fa[0], fa[1], fa[2]);
            return true;
        }

        // double[] -> Vector3
        if (coordsVal is double[] da && da.Length >= 3)
        {
            result = new Vector3((float)da[0], (float)da[1], (float)da[2]);
            return true;
        }

        // IList (object[], List<float>, etc.)
        if (coordsVal is IList list && list.Count >= 3)
        {
            try
            {
                float x = Convert.ToSingle(list[0], CultureInfo.InvariantCulture);
                float y = Convert.ToSingle(list[1], CultureInfo.InvariantCulture);
                float z = Convert.ToSingle(list[2], CultureInfo.InvariantCulture);
                result = new Vector3(x, y, z);
                return true;
            }
            catch
            {
                // ignore and fall through
            }
        }

        // If it's a string like "x,y,z"
        if (coordsVal is string s)
        {
            var parsed = TryParseVectorFromString(s);
            if (parsed.HasValue)
            {
                result = parsed.Value;
                return true;
            }
        }

        // Last-resort: reflection - try properties x,y,z or fields x,y,z (case-insensitive)
        var cvType = coordsVal.GetType();
        try
        {
            // Try property access first
            PropertyInfo px = cvType.GetProperty("x", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? cvType.GetProperty("X", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo py = cvType.GetProperty("y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? cvType.GetProperty("Y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo pz = cvType.GetProperty("z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? cvType.GetProperty("Z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (px != null && py != null && pz != null)
            {
                float x = Convert.ToSingle(px.GetValue(coordsVal), CultureInfo.InvariantCulture);
                float y = Convert.ToSingle(py.GetValue(coordsVal), CultureInfo.InvariantCulture);
                float z = Convert.ToSingle(pz.GetValue(coordsVal), CultureInfo.InvariantCulture);
                result = new Vector3(x, y, z);
                return true;
            }

            // Try fields
            FieldInfo fx = cvType.GetField("x", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? cvType.GetField("X", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo fy = cvType.GetField("y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? cvType.GetField("Y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo fz = cvType.GetField("z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? cvType.GetField("Z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (fx != null && fy != null && fz != null)
            {
                float x = Convert.ToSingle(fx.GetValue(coordsVal), CultureInfo.InvariantCulture);
                float y = Convert.ToSingle(fy.GetValue(coordsVal), CultureInfo.InvariantCulture);
                float z = Convert.ToSingle(fz.GetValue(coordsVal), CultureInfo.InvariantCulture);
                result = new Vector3(x, y, z);
                return true;
            }
        }
        catch
        {
            // swallow exceptions - best-effort conversion only
        }

        // Failed
        TBLog.Warn("CoordsConvertor: Could not convert coordsVal to Vector3 (expected Vector3, Vector2, float[]/double[]/IList with 3 elements, or object with x/y/z).");
        return false;
    }

    private static Vector3? TryParseVectorFromString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (float.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out float z))
        {
            return new Vector3(x, y, z);
        }
        return null;
    }
}