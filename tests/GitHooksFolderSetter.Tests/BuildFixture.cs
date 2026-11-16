using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GitHooksFolderSetter.Tests;

/// <summary>
/// Spins up an isolated temporary git repo with a minimal consumer project
/// that references the local GitHooksFolderSetter package, then provides
/// helpers to run dotnet-build and inspect results.
/// </summary>
public sealed class BuildFixture : IDisposable
{
    // ------------------------------------------------------------------ paths

    /// <summary>Path to the temp directory that acts as the git repo root.</summary>
    public string RepoDir { get; }

    /// <summary>Full path to the .git/git-hook-folder.set marker file.</summary>
    public string MarkerPath => Path.Combine(RepoDir, ".git", "git-hook-folder.set");

    // ----------------------------------------------------------------- setup

    public BuildFixture(string? hooksPath = null)
    {
        // Use a directory outside any git repo so the fast path doesn't
        // accidentally find the workspace .git above the temp dir.
        string tempBase = System.Environment.GetEnvironmentVariable("GHFS_PKG_DIR") is { } feedDir
            ? Path.GetDirectoryName(feedDir)!   // sibling of the package feed
            : Path.GetTempPath();

        RepoDir = Path.GetFullPath(
            Path.Combine(tempBase, "ghfs-test-" + Path.GetRandomFileName()));
        Directory.CreateDirectory(RepoDir);

        // Bare git init (no initial commit needed)
        RunProcess("git", new[] { "init", RepoDir });

        // nuget.config pointing at the local package feed
        // GetFullPath resolves /tmp -> /private/var/... on macOS so the path
        // matches what NuGet sees when it resolves the working directory.
        string pkgDir = LocalPackageDir();
        File.WriteAllText(Path.Combine(RepoDir, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="local" value="{pkgDir}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        WriteConsumerCsproj(hooksPath);
        File.WriteAllText(Path.Combine(RepoDir, "Program.cs"), "Console.WriteLine(\"hello\");");
    }

    public void WriteConsumerCsproj(string? hooksPath)
    {
        string hooksPathLine = hooksPath is null
            ? ""
            : $"    <HooksPath>{hooksPath}</HooksPath>\n";

        File.WriteAllText(Path.Combine(RepoDir, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>Exe</OutputType>
            {hooksPathLine}  </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="GitHooksFolderSetter" Version="1.1.0" IncludeAssets="build" />
              </ItemGroup>
            </Project>
            """);
    }

    // ------------------------------------------------------------------ build

    public BuildResult Build(
        string? verbosity = null,
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? properties = null,
        string? pathOverride = null,
        string[]? extraArgs = null)
    {
        var args = new List<string> { "build", "Consumer.csproj", "--nologo", "-tl:off" };
        if (verbosity is not null)
            args.AddRange(new[] { "-v", verbosity });
        if (properties is not null)
            foreach (var (k, v) in properties)
                args.Add($"-p:{k}={v}");
        if (extraArgs is not null)
            args.AddRange(extraArgs);

        // Build a modified environment: start from current, apply overrides,
        // optionally replace PATH.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            environment[(string)e.Key] = (string)(e.Value ?? "");

        if (env is not null)
            foreach (var (k, v) in env)
                environment[k] = v;

        if (pathOverride is not null)
            environment["PATH"] = pathOverride;

        // Remove CI skip variables unless the caller explicitly sets them.
        foreach (var skipVar in new[] { "CI", "TF_BUILD", "DOTNET_RUNNING_IN_CONTAINER" })
            if (env is null || !env.ContainsKey(skipVar))
                environment.Remove(skipVar);

        return RunDotnet(args, environment);
    }

    // ----------------------------------------------------------------- state

    public string? ReadMarker()
    {
        if (!File.Exists(MarkerPath)) return null;
        return File.ReadAllText(MarkerPath).Trim();
    }

    public void DeleteMarker() => File.Delete(MarkerPath);

    public string? ReadGitHooksPath()
    {
        try
        {
            var (code, output) = RunProcess("git", new[] { "-C", RepoDir, "config", "core.hooksPath" });
            return code == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- helpers

    private BuildResult RunDotnet(IEnumerable<string> args, Dictionary<string, string> env)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.StartInfo.Environment.Clear();
        foreach (var (k, v) in env) proc.StartInfo.Environment[k] = v;

        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { sb.AppendLine(e.Data); } };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { sb.AppendLine(e.Data); } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit(120_000);

        return new BuildResult(proc.ExitCode, sb.ToString());
    }

    private static (int ExitCode, string Output) RunProcess(string exe, string[] args)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, output);
    }

    /// <summary>
    /// Locates the local NuGet feed directory written by pack-and-test.sh.
    /// The GHFS_PKG_DIR environment variable is set by pack-and-test.sh to
    /// the exact directory where the .nupkg was written, so no path
    /// resolution is needed here.
    /// </summary>
    private static string LocalPackageDir()
    {
        string? fromEnv = System.Environment.GetEnvironmentVariable("GHFS_PKG_DIR");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        // Fallback for running tests directly without the script
        return "/private/tmp/ghfs-pkg";
    }

    public void Dispose()
    {
        try { Directory.Delete(RepoDir, recursive: true); } catch { /* best effort */ }
    }
}

public sealed record BuildResult(int ExitCode, string Log)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>True when the log contains the given substring (case-insensitive).</summary>
    public bool Contains(string text) =>
        Log.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when no line in the log contains the given substring.</summary>
    public bool Lacks(string text) => !Contains(text);
}
