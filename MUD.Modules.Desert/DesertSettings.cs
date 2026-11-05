using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MUD.Modules.Desert
{
    /// <summary>
    /// The desert module's own settings (module-private configuration, not world data, MODULES section 7.1). They are read from the optional
    /// file desert-settings.json next to the program; a missing file gives the defaults, and with no start rooms no storm ever spawns.
    ///
    ///   { "startRooms": ["desert.dune_3"], "spawnIntervalSeconds": 1800, "stormDurationSeconds": 240, "stepSeconds": 10, "maxSteps": 12, "blindSeconds": 30 }
    ///
    /// The numbers are the implementer's defaults taken from the worked example; the owner has not set them. A room's own behavior entry
    /// Config may override stepSeconds ("step_s") and blindSeconds ("blind_s").
    /// </summary>
    public sealed class DesertSettings
    {
        public const string FileName = "desert-settings.json";

        /// <summary>Rooms where a new storm may appear (full room ids). Empty: no storm spawns.</summary>
        public List<string> StartRooms { get; set; } = new List<string>();

        public int SpawnIntervalSeconds { get; set; } = 1800;
        public int StormDurationSeconds { get; set; } = 240;
        public int StepSeconds { get; set; } = 10;
        public int MaxSteps { get; set; } = 12;
        public int BlindSeconds { get; set; } = 30;

        /// <summary>Reads the settings file in the directory. A missing file gives the defaults; a broken file is reported through the problem text and gives the defaults.</summary>
        public static DesertSettings Load(string directory, out string problem)
        {
            problem = null;
            string path = Path.Combine(directory ?? string.Empty, FileName);
            if (!File.Exists(path)) { return new DesertSettings(); }
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                DesertSettings loaded = JsonSerializer.Deserialize<DesertSettings>(File.ReadAllText(path), options);
                return loaded ?? new DesertSettings();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                problem = "The desert settings file '" + path + "' could not be read (" + ex.Message + "); the defaults are used.";
                return new DesertSettings();
            }
        }
    }
}
