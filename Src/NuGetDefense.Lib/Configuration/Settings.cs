using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using NuGetDefense.Core;

namespace NuGetDefense.Configuration;

public class Settings
{
    public bool WarnOnly { get; set; }

    public FileLogSettings? Log
    {
        get => Logs?.Length > 0 ? Logs[0] : null;
        set { Logs = new[] { value }; }
    }

    public VulnerabilityReportsSettings VulnerabilityReports { get; set; } = new();

    public FileLogSettings[]? Logs { get; set; }
    public bool CheckTransitiveDependencies { get; set; } = true;
    public bool CheckReferencedProjects { get; set; }

    public BuildErrorSettings ErrorSettings { get; set; } = new();

    public RemoteVulnerabilitySourceConfiguration OssIndex { get; set; } = new();
    public RemoteVulnerabilitySourceConfiguration GitHubAdvisoryDatabase { get; set; } = new() { BreakIfCannotRun = false };
    public string? CacheLocation { get; set; }

    public OfflineVulnerabilitySourceConfiguration NVD { get; set; } =
        new();

    public string[] SensitivePackages { get; set; } = new string[0];

    public static Settings LoadSettings(string directory)
    {
        Settings settings;

        var settingsFilePath = Path.Combine(directory, "NuGetDefense.json");
        try
        {
            if (!File.Exists(settingsFilePath) && File.Exists(Path.Combine(Directory.GetParent(directory)?.FullName ?? "", "NuGetDefense.json")))
                settingsFilePath = Path.Combine(Directory.GetParent(directory)?.FullName ?? "", "NuGetDefense.json");

            //Edit to allow it to repeatedly check if hte file exists prior to multiple instances trying to save over it.
            if (File.Exists(settingsFilePath))
            {
                settings = LoadSettingsFile(settingsFilePath);
            }
            else
            {
                settingsFilePath = Path.Combine(directory, "NuGetDefense.json");
                settings = new();
                SpinWait.SpinUntil(() =>
                {
                    try
                    {
                        if (SaveSettings(settings, settingsFilePath)) return true;
                        settings = LoadSettingsFile(settingsFilePath);

                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }, TimeSpan.FromMinutes(5));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(MsBuild.Log(settingsFilePath, MsBuild.Category.Error,
                $"NuGetDefense Settings failed to load. Default Settings were used instead. Exception: {e}"));
            settings = new();
        }

#pragma warning disable 618
        if (settings.ErrorSettings.BlacklistedPackages != null)
            settings.ErrorSettings.BlockedPackages =
                settings.ErrorSettings.BlockedPackages.Concat(settings.ErrorSettings.BlacklistedPackages).ToArray();
        if (settings.ErrorSettings.WhiteListedPackages != null)
            settings.ErrorSettings.AllowedPackages =
                settings.ErrorSettings.AllowedPackages.Concat(settings.ErrorSettings.WhiteListedPackages).ToArray();
#pragma warning restore 618

        return settings;
    }

    public static Settings LoadSettingsFile(string settingsFilePath)
    {
        Settings settings;
        var settingsFileContents = ReadSettingsFileWhenAble(settingsFilePath, TimeSpan.FromMinutes(5));

        var ops = new JsonSerializerOptions
        {
            IgnoreReadOnlyProperties = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        settings = JsonSerializer.Deserialize<Settings>(settingsFileContents, ops)!;
        return settings;
    }

    private static string ReadSettingsFileWhenAble(string settingsFile, TimeSpan timeout)
    {
        var settingsFileContents = string.Empty;
        SpinWait.SpinUntil(() =>
        {
            try
            {
                using Stream settingsStream = File.Open(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var settingsReader = new StreamReader(settingsStream);
                settingsFileContents = settingsReader.ReadToEnd();
                return true;
            }
            catch
            {
                return false;
            }
        }, timeout);

        return settingsFileContents;
    }

    private static bool SaveSettings(Settings settings, string settingsFilePath)
    {
        var ops = new JsonSerializerOptions
        {
            IgnoreReadOnlyProperties = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        try
        {
            File.WriteAllText(settingsFilePath,
                JsonSerializer.Serialize(settings, ops));
            return true;
        }
        catch
        {
            return false;
        }
    }
}