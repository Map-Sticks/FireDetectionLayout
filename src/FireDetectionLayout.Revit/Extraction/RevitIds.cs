using Autodesk.Revit.DB;

namespace FireDetectionLayout.Revit.Extraction
{
    internal static class RevitIds
    {
        public static long GetValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static string GetKey(ElementId id) => GetValue(id).ToString();

        public static ElementId FromValue(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }

        /// <summary>Element id for a snapshot key, or InvalidElementId when the key is not numeric.</summary>
        public static ElementId FromKey(string? key) =>
            long.TryParse(key, out long value) ? FromValue(value) : ElementId.InvalidElementId;
    }
}
