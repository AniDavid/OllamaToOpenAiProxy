using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy;

// shortcut: NVIDIA only (nvidia-smi) and only the proxy's own machine; AMD/Intel/Apple need a different source.
internal static class GpuStats
{
    const string Query = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits";
    static volatile bool _notInstalled;

    // Null when there is no NVIDIA GPU or nvidia-smi isn't installed or fails.
    public static JsonArray? Read()
    {
        if (_notInstalled) return null;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", Query)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false, // never read; redirecting it could fill the pipe and stall nvidia-smi
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? Parse(output.Result) : null;
        }
        catch (Win32Exception)
        {
            _notInstalled = true; // not on PATH: don't try again every few seconds
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static JsonArray Parse(string csv)
    {
        var gpus = new JsonArray();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 5) continue;
            gpus.Add(new JsonObject
            {
                ["name"] = f[0],
                ["utilizationPercent"] = Number(f[1]),
                ["memoryUsedMiB"] = Number(f[2]),
                ["memoryTotalMiB"] = Number(f[3]),
                ["temperatureC"] = Number(f[4]),
            });
        }
        return gpus;
    }

    // nvidia-smi prints "[N/A]" or "[Not Supported]" for values a GPU doesn't report.
    static double? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
