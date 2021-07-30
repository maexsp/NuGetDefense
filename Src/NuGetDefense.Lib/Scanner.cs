using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using ByteDev.DotNet.Project;
using ByteDev.DotNet.Solution;
using NuGet.Versioning;
using NuGetDefense.Configuration;
using NuGetDefense.Core;
using Serilog;

namespace NuGetDefense
{
    public class Scanner
    {
        private const string Version = "3.0.0-pre0001";
        private static readonly string UserAgentString = @$"NuGetDefense/{Version}";

        private string _nuGetFile;
        private string _projectFileName;
        private Dictionary<string, NuGetPackage[]> _projects;
        private Settings _settings;
        public int NumberOfVulnerabilities;

        /// <summary>
        /// Scans the provided project's NuGet Dependencies for known vulnerabilities
        /// </summary>
        /// <param name="projectFile">File used to obtain the dependencies</param>
        /// <param name="tfm"> Target Framework Moniker</param>
        /// <param name="settingsFile">File to use for Settings</param>
        /// <param name="vulnDataFile">location of NVD Vulnerability File</param>
        /// <param name="warnOnly">Only Provide warnings and do not fail</param>
        /// <param name="checkTransitiveDependencies">Includes Transitive Dependencies when possible</param>
        /// <param name="checkReferencedProjects">Scans projects referenced by the current project as well</param>
        /// <param name="ignorePackages">Packages to ignore when scanning</param>
        /// <param name="ignoreCves">CVE's to ignore when scanning</param>
        /// <returns></returns>
        public int Scan(FileInfo projectFile,
            string tfm,
            FileInfo settingsFile,
            FileInfo vulnDataFile,
            bool warnOnly,
            bool checkTransitiveDependencies,
            bool checkReferencedProjects,
            IEnumerable<string> ignorePackages,
            IEnumerable<string> ignoreCves)
        {
            var ExitCode = 0;
            _settings = settingsFile == null ? Settings.LoadSettings(projectFile.DirectoryName) : Settings.LoadSettingsFile(settingsFile.FullName);
            _settings.WarnOnly = _settings.WarnOnly || warnOnly;
            _settings.CheckTransitiveDependencies = _settings.CheckTransitiveDependencies && checkTransitiveDependencies;
            _settings.CheckReferencedProjects = _settings.CheckReferencedProjects || checkReferencedProjects;
            _settings.ErrorSettings.IgnoredPackages = _settings.ErrorSettings.IgnoredPackages.Union(ignorePackages.Select(p => new NuGetPackage { Id = p })).ToArray();
            _settings.ErrorSettings.IgnoredCvEs = _settings.ErrorSettings.IgnoredCvEs.Union(ignoreCves).ToArray();
            _projectFileName = projectFile.Name;
            ConfigureLogging();
            try
            {
                Log.Logger.Verbose("Logging Configured");

                // Log.Logger.Verbose("Started NuGetDefense with arguments: {args}", args);

                var projectFullName = projectFile.FullName;
                if (projectFile.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    var projects = DotNetSolution.Load(projectFullName).Projects.Where(p => !p.Type.IsSolutionFolder).Select(p => p.Path).ToArray();
                    var specificFramework = !string.IsNullOrWhiteSpace(tfm);
                    if (specificFramework) Log.Logger.Information("Target Framework: {framework}", tfm);

                    _projects = LoadMultipleProjects(projectFullName, projects, specificFramework, tfm, true);
                }
                else if (_settings.CheckReferencedProjects)
                {
                    var projects = new List<string> { projectFullName };
                    GetProjectsReferenced(in projectFullName, in projects);
                    var specificFramework = !string.IsNullOrWhiteSpace(tfm);
                    if (specificFramework) Log.Logger.Information("Target Framework: {framework}", tfm);

                    _projects = LoadMultipleProjects(projectFullName, projects.ToArray(), specificFramework, tfm);
                }
                else
                {
                    var nugetFile = new NuGetFile(projectFullName);
                    _nuGetFile = nugetFile.Path;

                    Log.Logger.Verbose("NuGetFile Path: {nugetFilePath}", _nuGetFile);

                    Log.Logger.Information("Target Framework: {framework}", string.IsNullOrWhiteSpace(tfm) ? "Undefined" : tfm);
                    Log.Logger.Verbose("Loading Packages");
                    Log.Logger.Verbose("Transitive Dependencies Included: {CheckTransitiveDependencies}", _settings.CheckTransitiveDependencies);

                    if (_settings.CheckTransitiveDependencies && nugetFile.PackagesConfig)
                    {
                        var projects = DotNetProject.Load(projectFullName).ProjectReferences.Select(p => p.FilePath).ToArray();
                        var specificFramework = !string.IsNullOrWhiteSpace(tfm);
                        if (specificFramework) Log.Logger.Information("Target Framework: {framework}", tfm);

                        _projects = LoadMultipleProjects(projectFullName, projects, specificFramework, tfm);
                    }
                    else
                    {
                        _projects = new();
                        _projects.Add(nugetFile.Path, nugetFile.LoadPackages(tfm, _settings.CheckTransitiveDependencies).Values.ToArray());
                    }
                }

                GetNonSensitivePackages(out var nonSensitivePackages);
                if (_settings.ErrorSettings.IgnoredPackages.Length > 0)
                    foreach (var (proj, packages) in _projects.ToArray())
                    {
                        UtilityMethods.IgnorePackages(in packages, _settings.ErrorSettings.IgnoredPackages, out var projPackages);
                        _projects[proj] = projPackages;
                    }

                Log.Logger.Information("Loaded {packageCount} packages", _projects.Sum(p => p.Value.Length));

                if (_settings.ErrorSettings.BlockedPackages.Length > 0) CheckBlockedPackages();
                if (_settings.ErrorSettings.AllowedPackages.Length > 0) CheckAllowedPackages();
                Dictionary<string, Dictionary<string, Vulnerability>> vulnDict = null;
                if (_settings.OssIndex.Enabled)
                {
                    Log.Logger.Verbose("Checking with OSSIndex for Vulnerabilities");
                    vulnDict =
                        new OSSIndex.Scanner(_nuGetFile, _settings.OssIndex.BreakIfCannotRun, UserAgentString, _settings.OssIndex.Username, _settings.OssIndex.ApiToken)
                            .GetVulnerabilitiesForPackages(nonSensitivePackages.SelectMany(p => p.Value).ToArray());
                }

                if (_settings.NVD.Enabled)
                {
                    Log.Logger.Verbose("Checking the embedded NVD source for Vulnerabilities");

                    foreach (var (proj, pkgs) in _projects)
                        vulnDict =
                            new NVD.Scanner(_nuGetFile, TimeSpan.FromSeconds(_settings.NVD.TimeoutInSeconds),
                                    _settings.NVD.BreakIfCannotRun, _settings.NVD.SelfUpdate)
                                .GetVulnerabilitiesForPackages(pkgs,
                                    vulnDict);
                }

                Log.Logger.Information("ignoring {ignoredCVECount} Vulnerabilities", _settings.ErrorSettings.IgnoredCvEs.Length);
                if (_settings.ErrorSettings.IgnoredCvEs.Length > 0)
                    VulnerabilityData.IgnoreCVEs(vulnDict, _settings.ErrorSettings.IgnoredCvEs);

                ReportVulnerabilities(vulnDict);
                ExitCode = _settings.WarnOnly ? 0 : NumberOfVulnerabilities;
            }
            catch (Exception e)
            {
                var msBuildMessage = MsBuild.Log(_nuGetFile, MsBuild.Category.Error,
                    $"Encountered a fatal exception while checking for Dependencies in {_nuGetFile}. Exception: {e}");
                Console.WriteLine(msBuildMessage);
                Log.Logger.Fatal(msBuildMessage);
                ExitCode = -1;
            }

            return ExitCode;
        }

        private static void GetProjectsReferenced(in string proj, in List<string> projectList)
        {
            var dir = Path.GetDirectoryName(proj);
            foreach (var referencedProj in DotNetProject.Load(proj).ProjectReferences
                .Select(p => Path.Combine(dir!, p.FilePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))))
            {
                if (!projectList.Contains(referencedProj))
                    projectList.Add(referencedProj);
                GetProjectsReferenced(in referencedProj, in projectList);
            }
        }

        private Dictionary<string, NuGetPackage[]> LoadMultipleProjects(string TopLevelProject, string[] projects, bool specificFramework, string targetFramework,
            bool solutionFile = false)
        {
            var projectPackages = new Dictionary<string, NuGetPackage[]>();
            for (var i = 0; i < projects.Length; i++)
            {
                var pkgs = new List<NuGetPackage>();

                var project = projects[i];
                var path = Path.Combine(Path.GetDirectoryName(TopLevelProject)!, project
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar));

                var proj = DotNetProject.Load(path);
                if (!specificFramework && proj.Format == ProjectFormat.New)
                {
                    var monikersListBuilder = new StringBuilder();
                    var monikers = proj.ProjectTargets.Select(t => t.Moniker).ToArray();
                    monikersListBuilder.Append(monikers[0]);
                    for (var index = 1; index < monikers.Length; index++) monikersListBuilder.Append($", {monikers[index]}");

                    Log.Logger.Information("Target Frameworks for {project}: {frameworks}", project, monikersListBuilder.ToString());
                    var nugetFile = new NuGetFile(path);
                    pkgs.AddDistinctPackages(monikers.SelectMany(m => nugetFile.LoadPackages(m, _settings.CheckTransitiveDependencies).Values));
                }
                else
                {
                    pkgs.AddDistinctPackages(new NuGetFile(path).LoadPackages(targetFramework, _settings.CheckTransitiveDependencies).Values);
                }

                projectPackages.Add(path, pkgs.ToArray());
            }

            var nuGetFile = new NuGetFile(TopLevelProject);
            _nuGetFile = nuGetFile.Path;

            if (!solutionFile && !projectPackages.ContainsKey(TopLevelProject))
                projectPackages.Add(TopLevelProject, nuGetFile.LoadPackages(targetFramework, _settings.CheckTransitiveDependencies).Values.ToArray());

            return projectPackages;
        }

        private static void ParseSolutionForProjects(string s)
        {
            // TODO: This will parse hte solution file into a list of projects relative to the solution file.
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Escapes all characters for Regex, then replaces '*' with '.*' (Regex wild Card for 0 or more of any character)
        /// </summary>
        /// <param name="nuGetPackages"></param>
        /// <returns>a list of packages that do not match the wild card strings in SensitivePackages</returns>
        public void GetNonSensitivePackages(out Dictionary<string, NuGetPackage[]> nonSensitives)
        {
            nonSensitives = new();
            var sensitiveRegexSet = _settings.SensitivePackages.Select(sp => Regex.Escape(sp).Replace(@"\*", ".*")).ToArray();
            if (!sensitiveRegexSet.Any()) return;
            foreach (var (project, packages) in _projects) nonSensitives.Add(project, packages.Where(p => !sensitiveRegexSet.Any(x => Regex.IsMatch(p.Id, x))).ToArray());
        }

        private void CheckAllowedPackages()
        {
            Log.Logger.Verbose("Checking Allowed Packages");

            foreach (var (project, packages) in _projects)
            foreach (var pkg in packages.Where(p => !_settings.ErrorSettings.AllowedPackages.Any(b =>
                b.Id == p.Id &&
                (string.IsNullOrWhiteSpace(b.Version) || VersionRange.Parse(p.Version).Satisfies(new(b.Version))))))
            {
                Log.Logger.Error("{packageName}:{version} was included in {nugetFile} but is not in the Allowed List", pkg.Id, pkg.Version, _nuGetFile);

                var msBuildMessage = MsBuild.Log(_nuGetFile, MsBuild.Category.Error, pkg.LineNumber, pkg.LinePosition,
                    $"{pkg.Id} is not listed as an allowed package and may not be used in this project");
                Console.WriteLine(msBuildMessage);
                Log.Logger.Error(msBuildMessage);
            }
        }

        private void ReportVulnerabilities(Dictionary<string, Dictionary<string, Vulnerability>> vulnDict)
        {
            if (vulnDict == null)
            {
                Log.Logger.Information("No Vulnerabilities found in {numberOfPackages} packages", _projects.Sum(p => p.Value.Length));
            }
            else
            {
                Log.Logger.Verbose("Building report of Vulnerabilities found in {numberOfPackages} packages", vulnDict.Keys.Count);
                var vulnReporter = new VulnerabilityReporter();

                foreach (var (project, packages) in _projects)
                {
                    //TODO: Losing the right file somewhere here
                    vulnReporter.BuildVulnerabilityTextReport(vulnDict, packages, project, _settings.WarnOnly,
                        _settings.ErrorSettings.Cvss3Threshold, out NumberOfVulnerabilities);
                    if (_settings.VulnerabilityReports.OutputTextReport) Log.Logger.Information(vulnReporter.VulnerabilityTextReport);
                    foreach (var msBuildMessage in vulnReporter.MsBuildMessages)
                    {
                        Console.WriteLine(msBuildMessage);
                        Log.Logger.Debug(msBuildMessage);
                    }
                }


                if (string.IsNullOrWhiteSpace(_settings.VulnerabilityReports.JsonReportPath) && string.IsNullOrWhiteSpace(_settings.VulnerabilityReports.XmlReportPath)) return;

                vulnReporter.BuildVulnerabilityReport(vulnDict, _projects, _settings.WarnOnly);
                if (!string.IsNullOrWhiteSpace(_settings.VulnerabilityReports.JsonReportPath))
                {
                    var ops = new JsonSerializerOptions
                    {
                        IgnoreReadOnlyProperties = true,
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        WriteIndented = true
                    };

                    var contents = JsonSerializer.Serialize(vulnReporter.Report, ops);
                    File.WriteAllText(_settings.VulnerabilityReports.JsonReportPath.Replace("{project}", _projectFileName), contents
                    );
                }

                if (string.IsNullOrWhiteSpace(_settings.VulnerabilityReports.XmlReportPath)) return;
                var filename = _settings.VulnerabilityReports.XmlReportPath.Replace("{project}", _projectFileName);
                var xmlser = new XmlTextWriter(File.Create(filename), Encoding.Default);
                var xser = new XmlSerializer(typeof(VulnerabilityReport));
                xser.Serialize(xmlser, vulnReporter.Report);
            }
        }

        private void ConfigureLogging()
        {
            if (!(_settings.Logs?.Length > 0)) return;
            var loggerConfiguration = new LoggerConfiguration();
            foreach (var log in _settings.Logs)
            {
                var file = log.OutPut.Replace("{project}", _projectFileName);
                loggerConfiguration.WriteTo.File(file,
                    log.LogLevel,
                    rollingInterval: log.RollingInterval);
            }

            loggerConfiguration.WriteTo.Console();
            Log.Logger = loggerConfiguration.CreateLogger();
        }

        private void CheckBlockedPackages()
        {
            foreach (var (proj, pkgs) in _projects)
            foreach (var pkg in pkgs)
            {
                Log.Logger.Verbose("Checking to see if {packageName}:{version} is Blocked", pkg.Id, pkg.Version);

                var blockedPackage = _settings.ErrorSettings.BlockedPackages.FirstOrDefault(b =>
                    b.Package.Id == pkg.Id &&
                    (string.IsNullOrWhiteSpace(b.Package.Version) || VersionRange.Parse(pkg.Version).Satisfies(new(b.Package.Version))));
                if (blockedPackage == null)
                {
                    Log.Logger.Verbose("{packageName}:{version} is not Blocked", pkg.Id, pkg.Version);
                    continue;
                }

                var msBuildMessage = MsBuild.Log(proj, MsBuild.Category.Error, pkg.LineNumber, pkg.LinePosition,
                    $"{pkg.Id}: {(string.IsNullOrEmpty(blockedPackage.CustomErrorMessage) ? "has been blocked and may not be used in this project" : blockedPackage.CustomErrorMessage)}");
                Console.WriteLine(msBuildMessage);
                Log.Logger.Error(msBuildMessage);
            }
        }
    }
}