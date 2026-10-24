using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GDiag
{
    // Stored as JSON in %APPDATA%\GDiag, or ~/Library/Application Support/GDiag on a Mac.
    public class AppSettings
    {
        const int RecentMaxCount = 8;

        public static string SettingsFolder => OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "GDiag")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GDiag");
        static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

        // "j2534": a J2534 adapter such as the Tactrix OpenPort 2.0 on D-CAN; "enet": an ENET cable
        public string Connection { get; set; } = "j2534";
        // J2534 driver library picked last (its file path)
        public string LastJ2534Driver { get; set; } = "";
        // J2534 driver libraries added by hand (macOS and Linux have no registry that lists them)
        public List<string> J2534DriverFiles { get; set; } = new List<string>();

        // gateway addresses typed in by hand or found by discovery, newest first
        public List<string> RecentGateways { get; set; } = new List<string>();
        public string LastFaFile { get; set; } = "";
        // optional "code;text" list for fault code descriptions (BMW codes are manufacturer specific)
        public string FaultTextFile { get; set; } = "";

        public bool TranslateToEnglish { get; set; } = true;
        public bool AdvancedMode { get; set; } = false;
        public bool UsUnits { get; set; } = false;
        public float FontSize { get; set; } = 18f;
        public float LiveInterval { get; set; } = 1f;
        // scan every address 0x00-0x7F instead of only the known G-series module addresses
        public bool FullScan { get; set; } = false;
        // live data values the user pinned: "address|did|name|scale|offset|unit|bytes|signed"
        public List<string> CustomDids { get; set; } = new List<string>();
        // live value ids shown as gauges and as charts on the Live data tab
        public List<string> LiveGauges { get; set; }
        public List<string> LiveCharts { get; set; } = new List<string>();

        public static AppSettings Current { get; private set; } = Load();

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Settings could not be read, using defaults: {ex.Message}");
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Settings could not be saved: {ex.Message}");
            }
        }

        public void AddRecentGateway(string host)
        {
            RecentGateways.Remove(host);
            RecentGateways.Insert(0, host);
            if (RecentGateways.Count > RecentMaxCount)
            {
                RecentGateways.RemoveRange(RecentMaxCount, RecentGateways.Count - RecentMaxCount);
            }
            Save();
        }
    }
}
