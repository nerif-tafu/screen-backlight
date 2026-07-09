using System.Text.Json;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public static class SettingsManager
{
    private static readonly object Gate = new();

    public static event Action? SettingsChanged;

    public static void ApplyAndSave(Action<AppSettings> mutate)
    {
        lock (Gate)
        {
            mutate(App.Settings);
            SettingsStore.Save(App.Settings);
        }

        SettingsChanged?.Invoke();
    }

    public static bool TryApplyPatch(JsonElement patch, out string? error)
    {
        lock (Gate)
        {
            if (!SettingsCatalog.TryApplyPatch(App.Settings, patch, out error))
                return false;

            SettingsStore.Save(App.Settings);
        }

        SettingsChanged?.Invoke();
        error = null;
        return true;
    }

    public static T WithLock<T>(Func<AppSettings, T> action)
    {
        lock (Gate)
            return action(App.Settings);
    }

    public static void WithLock(Action<AppSettings> action)
    {
        lock (Gate)
            action(App.Settings);
    }
}
