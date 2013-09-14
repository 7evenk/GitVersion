﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GitFlowVersion;
using LibGit2Sharp;
using Mono.Cecil;

public class ModuleWeaver : IDisposable
{

    static Dictionary<string, CachedVersion> versionCacheVersions = new Dictionary<string, CachedVersion>();
    public Action<string> LogInfo;
    public Action<string> LogWarning;
    public ModuleDefinition ModuleDefinition;
    public string SolutionDirectoryPath;
    public string AddinDirectoryPath;
    public string AssemblyFilePath;
    string assemblyInfoVersion;
    Version assemblyVersion;
    bool dotGitDirExists;

    public ModuleWeaver()
    {
        LogInfo = s => { };
        LogWarning = s => { };
    }

    public void Execute()
    {
        Logger.Write = LogInfo;
        SearchPath.SetSearchPath(AddinDirectoryPath);
        var customAttributes = ModuleDefinition.Assembly.CustomAttributes;


        if (TeamCity.IsRunningInBuildAgent())
        {
            LogInfo("Executing inside a TeamCity build agent");
        }

        var gitDir = GitDirFinder.TreeWalkForGitDir(SolutionDirectoryPath);

        if (string.IsNullOrEmpty(gitDir))
        {
            if (TeamCity.IsRunningInBuildAgent()) //fail the build if we're on a TC build agent
            {
                throw new WeavingException("Failed to find .git directory on agent. Please make sure agent checkout mode is enabled for you VCS roots - http://confluence.jetbrains.com/display/TCD8/VCS+Checkout+Mode");
            }

            LogWarning(string.Format("No .git directory found in solution path '{0}'. This means the assembly may not be versioned correctly. To fix this warning either clone the repository using git or remove the `GitFlowVersion.Fody` nuget package. To temporarily work around this issue add a AssemblyInfo.cs with an appropriate `AssemblyVersionAttribute`.", SolutionDirectoryPath));

            return;
        }

        dotGitDirExists = true;

        using (var repo = RepositoryLoader.GetRepo(gitDir))
        {
            var branch = repo.Head;
            if (branch.Tip == null)
            {
                LogWarning("No Tip found. Has repo been initialize?");
                return;
            }

            VersionInformation versionInformation;
            var ticks = DirectoryDateFinder.GetLastDirectoryWrite(gitDir).Ticks;
            var key = string.Format("{0}:{1}:{2}", repo.Head.CanonicalName, repo.Head.Tip.Sha, ticks);
            CachedVersion cachedVersion;
            if (versionCacheVersions.TryGetValue(key, out cachedVersion))
            {
                LogInfo("Version read from cache.");
                if (cachedVersion.Timestamp == ticks)
                {
                    versionInformation = cachedVersion.VersionInformation;
                }
                else
                {
                    LogInfo("Change detected. flushing cache.");
                    versionInformation = cachedVersion.VersionInformation = GetSemanticVersion(repo);
                }
            }
            else
            {
                LogInfo("Version not in cache. Calculating version.");
                versionInformation = GetSemanticVersion(repo);
                versionCacheVersions[key] = new CachedVersion
                                            {
                                                VersionInformation = versionInformation,
                                                Timestamp = ticks
                                            };
            }

            SetAssemblyVersion(versionInformation);

            ModuleDefinition.Assembly.Name.Version = assemblyVersion;


            assemblyInfoVersion = versionInformation.ToLongString();


            var customAttribute = customAttributes.FirstOrDefault(x => x.AttributeType.Name == "AssemblyInformationalVersionAttribute");
            if (customAttribute == null)
            {
                var versionAttribute = GetVersionAttribute();
                var constructor = ModuleDefinition.Import(versionAttribute.Methods.First(x => x.IsConstructor));
                customAttribute = new CustomAttribute(constructor);

                customAttribute.ConstructorArguments.Add(new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, assemblyInfoVersion));
                customAttributes.Add(customAttribute);
            }
            else
            {
                customAttribute.ConstructorArguments[0] = new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, assemblyInfoVersion);
            }

            if (TeamCity.IsRunningInBuildAgent())
            {
                LogWarning(TeamCity.GenerateBuildVersion(versionInformation));
            }
        }
    }

    public virtual VersionInformation GetSemanticVersion(Repository repo)
    {
        try
        {
            var versionForRepositoryFinder = new VersionForRepositoryFinder();
            return versionForRepositoryFinder.GetVersion(repo);
        }
        catch (MissingBranchException missingBranchException)
        {
            throw new WeavingException(missingBranchException.Message);
        }
    }

    void SetAssemblyVersion(VersionInformation versionInformation)
    {
        if (ModuleDefinition.IsStrongNamed())
        {
            // for strong named we don't want to include the patch to avoid binding redirect issues
            assemblyVersion = new Version(versionInformation.Major, versionInformation.Minor, 0, 0);
        }
        else
        {
            // for non strong named we want to include the patch
            assemblyVersion = new Version(versionInformation.Major, versionInformation.Minor, versionInformation.Patch, 0);
        }
    }


    TypeDefinition GetVersionAttribute()
    {
        var msCoreLib = ModuleDefinition.AssemblyResolver.Resolve("mscorlib");
        var msCoreAttribute = msCoreLib.MainModule.Types.FirstOrDefault(x => x.Name == "AssemblyInformationalVersionAttribute");
        if (msCoreAttribute != null)
        {
            return msCoreAttribute;
        }
        var systemRuntime = ModuleDefinition.AssemblyResolver.Resolve("System.Runtime");
        return systemRuntime.MainModule.Types.First(x => x.Name == "AssemblyInformationalVersionAttribute");
    }

    public void AfterWeaving()
    {
        if (!dotGitDirExists)
        {
            return;
        }
        var verPatchPath = Path.Combine(AddinDirectoryPath, "verpatch.exe");
        var arguments = string.Format("\"{0}\" /pv \"{1}\" /high /va {2}", AssemblyFilePath, assemblyInfoVersion, assemblyVersion);
        LogInfo(string.Format("Patching version using: {0} {1}", verPatchPath, arguments));
        var startInfo = new ProcessStartInfo
                        {
                            FileName = verPatchPath,
                            Arguments = arguments,
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            WorkingDirectory = Path.GetTempPath()
                        };
        using (var process = Process.Start(startInfo))
        {
            if (!process.WaitForExit(1000))
            {
                var timeoutMessage = string.Format("Failed to apply product version to Win32 resources in 1 second.\r\nFailed command: {0} {1}", verPatchPath, arguments);
                throw new WeavingException(timeoutMessage);
            }

            if (process.ExitCode == 0)
            {
                return;
            }
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            var message = string.Format("Failed to apply product version to Win32 resources.\r\nOutput: {0}\r\nError: {1}", output, error);
            throw new WeavingException(message);
        }
    }

    public void Dispose()
    {
        Logger.Reset();
    }

}