using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace InterCoreBench.Linux
{
    [SupportedOSPlatform("linux")]
    public class LinuxLogicalCoreInfo : ILogicalCoreInfo
    {
        public List<int> GetPhysicalCoreIndex()
        {
            var physicalCores = new List<int>();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "lscpu",
                ArgumentList = { "--online", "--parse=CPU,Core" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new Exception($"lscpu exited with code {process.ExitCode}.");
            }

            var reader = process.StandardOutput;
            var addedCores = new HashSet<int>();
            for (string line; !string.IsNullOrWhiteSpace(line = reader.ReadLine());)
            {
                line = line.Trim();
                if (line.StartsWith("#"))
                {
                    continue;
                }

                var coreInfo = line.Split(",");
                if (coreInfo.Length != 2)
                {
                    throw new FormatException($"Failed to read output from lscpu: invalid line '{line}'");
                }

                if (!int.TryParse(coreInfo[0], out var cpu) || !int.TryParse(coreInfo[1], out var core))
                {
                    throw new FormatException($"Failed to parse output from lscpu: invalid line '{line}'");
                }

                if (addedCores.Contains(core))
                {
                    continue;
                }

                physicalCores.Add(cpu);
                addedCores.Add(core);
            }

            return physicalCores;
        }
    }
}