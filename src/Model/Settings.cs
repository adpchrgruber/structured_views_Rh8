using System;
using Rhino;

namespace StructuredViews
{
    public enum ViewMode { Thumbnails, List }

    public enum SortMode { Custom, NameAscending, NameDescending }

    public enum ThumbnailSize { Small, Medium, Large }

    public enum AnimationMode { ConstantTime, ConstantSpeed }

    /// <summary>
    /// Panel options, kept in the plug-in's persistent settings. Without a plug-in (a test host) they live in
    /// memory only.
    /// </summary>
    public static class Settings
    {
        static PersistentSettings s_store;
        static readonly System.Collections.Generic.Dictionary<string, object> s_memory = new System.Collections.Generic.Dictionary<string, object>();

        /// <summary>Raised after any option changes, so every open panel can follow.</summary>
        public static event Action Changed;

        public static void Attach(PersistentSettings store)
        {
            s_store = store;
        }

        public static ViewMode ViewMode { get => (ViewMode)Int("ViewMode", 0); set => Set("ViewMode", (int)value); }
        public static SortMode Sort { get => (SortMode)Int("Sort", 0); set => Set("Sort", (int)value); }
        public static ThumbnailSize ThumbnailSize { get => (ThumbnailSize)Int("ThumbnailSize", 1); set => Set("ThumbnailSize", (int)value); }

        public static bool Animate { get => Bool("Animate", true); set => Set("Animate", value); }
        public static AnimationMode AnimationMode { get => (AnimationMode)Int("AnimationMode", 0); set => Set("AnimationMode", (int)value); }
        /// <summary>Frames between two views (constant time).</summary>
        public static int AnimationFrames { get => Math.Max(1, Int("AnimationFrames", 20)); set => Set("AnimationFrames", value); }
        /// <summary>Model units per frame (constant speed).</summary>
        public static double AnimationSpeed { get => Math.Max(1e-6, Get("AnimationSpeed", 1.0)); set => Set("AnimationSpeed", value); }
        public static int AnimationDelay { get => Math.Max(0, Int("AnimationDelay", 10)); set => Set("AnimationDelay", value); }

        public static bool RestoreAspectRatio { get => Bool("RestoreAspectRatio", false); set => Set("RestoreAspectRatio", value); }
        public static bool AutoUpdateThumbnails { get => Bool("AutoUpdateThumbnails", true); set => Set("AutoUpdateThumbnails", value); }

        static int Int(string key, int fallback) => s_store?.GetInteger(key, fallback) ?? Get(key, fallback);

        static bool Bool(string key, bool fallback) => s_store?.GetBool(key, fallback) ?? Get(key, fallback);

        static T Get<T>(string key, T fallback)
        {
            if (s_store != null && typeof(T) == typeof(double))
                return (T)(object)s_store.GetDouble(key, (double)(object)fallback);
            return s_memory.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
        }

        static void Set(string key, int value)
        {
            if (s_store != null) s_store.SetInteger(key, value); else s_memory[key] = value;
            Changed?.Invoke();
        }

        static void Set(string key, bool value)
        {
            if (s_store != null) s_store.SetBool(key, value); else s_memory[key] = value;
            Changed?.Invoke();
        }

        static void Set(string key, double value)
        {
            if (s_store != null) s_store.SetDouble(key, value); else s_memory[key] = value;
            Changed?.Invoke();
        }
    }
}
