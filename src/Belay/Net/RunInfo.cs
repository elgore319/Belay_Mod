using System;
using System.Collections.Generic;
using System.Linq;

namespace Belay.Net
{
    /// <summary>
    /// The run the host is currently playing, stored in lobby data so late joiners can follow it too.
    /// Format: "runId;seed;gamemodeName;setting1,setting2".
    /// </summary>
    public struct RunInfo
    {
        public int RunId;
        public int Seed;
        public string Gamemode;
        public List<string> Settings;

        public bool IsValid => RunId > 0 && !string.IsNullOrEmpty(Gamemode);

        public string Serialize()
        {
            return $"{RunId};{Seed};{Gamemode};{string.Join(",", Settings ?? new List<string>())}";
        }

        public static RunInfo Parse(string raw)
        {
            var run = new RunInfo { Settings = new List<string>() };
            if (string.IsNullOrEmpty(raw))
            {
                return run;
            }
            string[] parts = raw.Split(new[] { ';' }, 4);
            if (parts.Length < 3)
            {
                return run;
            }
            int.TryParse(parts[0], out run.RunId);
            int.TryParse(parts[1], out run.Seed);
            run.Gamemode = parts[2];
            if (parts.Length > 3)
            {
                run.Settings = parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            return run;
        }
    }
}
