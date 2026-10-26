using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SpotifyMini
{
    // Settings in %APPDATA%\SpotifyMini\config.json
    public class Config
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyMini");
        static readonly string FilePath = Path.Combine(Dir, "config.json");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string ClientId { get; set; } = "";

        public static Config Load()
        {
            try
            {
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
            }
            catch
            {
                return new Config();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }

        // "Start with Windows" lives in the registry, not in the json
        public static bool StartWithWindows
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue("SpotifyMini") != null;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue("SpotifyMini", "\"" + Environment.ProcessPath + "\"");
                else key.DeleteValue("SpotifyMini", false);
            }
        }
    }
}
