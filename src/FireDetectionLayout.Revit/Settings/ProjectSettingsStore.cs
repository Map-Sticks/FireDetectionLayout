using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Storage;

namespace FireDetectionLayout.Revit.Settings
{
    /// <summary>
    /// Everything the tool remembers for one Revit project: the rule profile, which family type
    /// each device kind maps to, and tile grid origins the user has picked.
    /// </summary>
    public sealed class ProjectSettings
    {
        public RuleProfile Profile { get; set; } = CreateDefaultProfile();

        /// <summary>"Family : Type" display name mapped to each device kind.</summary>
        public string SmokeDetectorFamily { get; set; } = string.Empty;
        public string HeatDetectorFamily { get; set; } = string.Empty;
        public string SpeakerFamily { get; set; } = string.Empty;

        public bool ReplaceToolOwnedDevices { get; set; } = true;
        public bool CaptureViewSnapshot { get; set; } = true;

        /// <summary>Picked tile grid origins (model mm) keyed by "SourceModel|CeilingElementId".</summary>
        public Dictionary<string, Point2D> TileGridOrigins { get; set; } = new Dictionary<string, Point2D>();

        /// <summary>The combined detector + speaker layout is the usual deliverable, so it is the default.</summary>
        public static RuleProfile CreateDefaultProfile()
        {
            var profile = RuleProfile.CreateAs1670Default();
            profile.Layout.PlaceSpeakers = true;
            return profile;
        }
    }

    /// <summary>Loads and saves <see cref="ProjectSettings"/> per Revit project under %APPDATA%.</summary>
    public static class ProjectSettingsStore
    {
        private static string SettingsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FireDetectionLayout",
            "Projects");

        public static ProjectSettings Load(Document doc)
        {
            string path = GetPath(doc);
            if (!File.Exists(path))
                return new ProjectSettings();

            try
            {
                return SnapshotSerializer.LoadFromFile<ProjectSettings>(path);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                                          error is Newtonsoft.Json.JsonException)
            {
                // An unreadable settings file must never block the tool; start again from defaults.
                return new ProjectSettings();
            }
        }

        public static void Save(Document doc, ProjectSettings settings)
        {
            try
            {
                SnapshotSerializer.SaveToFile(settings, GetPath(doc));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // Settings are a convenience; failing to persist them is not worth interrupting the user.
            }
        }

        public static string GetPath(Document doc)
        {
            // Workshared and cloud models report a stable central path; unsaved models fall back to the title.
            string identity = doc.PathName;
            try
            {
                if (doc.IsWorkshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null)
                        identity = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Keep the local path.
            }

            if (string.IsNullOrWhiteSpace(identity))
                identity = doc.Title;

            string safeTitle = string.Concat(doc.Title.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(SettingsFolder, $"{safeTitle}_{ShortHash(identity)}.json");
        }

        private static string ShortHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
                var builder = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                    builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
