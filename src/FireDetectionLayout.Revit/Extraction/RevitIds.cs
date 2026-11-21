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
    }
}
