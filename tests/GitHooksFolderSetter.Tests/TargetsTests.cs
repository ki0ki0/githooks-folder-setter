using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace GitHooksFolderSetter.Tests;

/// <summary>
/// Integration tests that build an isolated consumer project and assert on
/// log output, marker file content, and git config state.
///
/// Each test creates its own temp git repo via BuildFixture so tests are
/// independent and can run in parallel.
/// </summary>
public sealed class TargetsTests
{
    // --------------------------------------------------------------------- 1
    [Fact]
    public void FirstBuild_SetsHookPathAndWritesMarker()
    {
        using var fix = new BuildFixture();

        // Use normal verbosity so the Exec command text is visible in the log.
        var result = fix.Build(verbosity: "normal");

        Assert.True(result.Succeeded, $"Build failed:\n{result.Log}");

        // git config must be set
        Assert.Equal("hooks", fix.ReadGitHooksPath());

        // marker must exist and contain the HooksPath value
        Assert.Equal("hooks", fix.ReadMarker());

        // slow path ran — rev-parse must appear in the log
        Assert.True(result.Contains("rev-parse"),
            $"Expected rev-parse in log:\n{result.Log}");
    }

    // --------------------------------------------------------------------- 2
    [Fact]
    public void SecondBuild_FastPath_NoGitExec()
    {
        using var fix = new BuildFixture();

        // First build — sets marker
        var first = fix.Build();
        Assert.True(first.Succeeded, $"First build failed:\n{first.Log}");

        // Second build with diagnostic verbosity
        var second = fix.Build(verbosity: "d");

        Assert.True(second.Succeeded, $"Second build failed:\n{second.Log}");

        // Target must not appear in the log at all (skipped by Condition before scheduling)
        Assert.True(second.Lacks("SetHooksFolderTarget"),
            $"SetHooksFolderTarget should be absent from fast-path log:\n{second.Log}");

        // No git process should have been invoked
        Assert.True(second.Lacks("rev-parse"),
            $"rev-parse should not appear in fast-path log:\n{second.Log}");

        Assert.True(second.Lacks("core.hooksPath"),
            $"core.hooksPath should not appear in fast-path log:\n{second.Log}");
    }

    // --------------------------------------------------------------------- 3
    [Fact]
    public void MarkerDeleted_SlowPathRunsAgain()
    {
        using var fix = new BuildFixture();

        var first = fix.Build();
        Assert.True(first.Succeeded, $"First build failed:\n{first.Log}");
        Assert.Equal("hooks", fix.ReadMarker());

        // Delete the marker to force slow path
        fix.DeleteMarker();
        Assert.Null(fix.ReadMarker());

        var second = fix.Build(verbosity: "d");
        Assert.True(second.Succeeded, $"Second build (after delete) failed:\n{second.Log}");

        // Slow path must have run
        Assert.True(second.Contains("rev-parse"),
            $"Expected rev-parse after marker delete:\n{second.Log}");

        // Marker and config must be restored
        Assert.Equal("hooks", fix.ReadMarker());
        Assert.Equal("hooks", fix.ReadGitHooksPath());
    }

    // --------------------------------------------------------------------- 4
    [Fact]
    public void HooksPathChanged_SlowPathUpdatesMarkerAndConfig()
    {
        using var fix = new BuildFixture(hooksPath: "hooks");

        var first = fix.Build();
        Assert.True(first.Succeeded, $"First build failed:\n{first.Log}");
        Assert.Equal("hooks", fix.ReadMarker());

        // Rewrite the csproj with a different HooksPath into the same repo
        fix.WriteConsumerCsproj(hooksPath: "custom_hooks");

        var second = fix.Build(verbosity: "d");
        Assert.True(second.Succeeded, $"Second build (changed path) failed:\n{second.Log}");

        // Slow path must have run because marker content != new HooksPath
        Assert.True(second.Contains("rev-parse"),
            $"Expected rev-parse after HooksPath change:\n{second.Log}");

        Assert.Equal("custom_hooks", fix.ReadMarker());
        Assert.Equal("custom_hooks", fix.ReadGitHooksPath());
    }

    // --------------------------------------------------------------------- 5
    [Fact]
    public void CIEnvSet_TargetSkipped_BuildSucceeds()
    {
        using var fix = new BuildFixture();

        var result = fix.Build(
            verbosity: "d",
            env: new Dictionary<string, string> { ["CI"] = "true" });

        Assert.True(result.Succeeded, $"Build failed:\n{result.Log}");

        // Target must be entirely absent
        Assert.True(result.Lacks("SetHooksFolderTarget"),
            $"SetHooksFolderTarget should be absent when CI is set:\n{result.Log}");

        Assert.True(result.Lacks("rev-parse"),
            $"rev-parse should not run when CI is set:\n{result.Log}");

        // Marker must not have been written
        Assert.Null(fix.ReadMarker());
    }

    // --------------------------------------------------------------------- 6
    [Fact]
    public void NoGitOnPath_BuildSucceedsWithWarning()
    {
        using var fix = new BuildFixture();

        // Pre-restore with the full PATH so NuGet can reach its cache.
        var restore = fix.Build(verbosity: "quiet");
        Assert.True(restore.Succeeded, $"Pre-restore failed:\n{restore.Log}");

        // Delete the marker so the target runs on the next build.
        if (File.Exists(fix.MarkerPath)) fix.DeleteMarker();

        // PATH points only to a temp dir with no git binary plus dotnet.
        string emptyBinDir = Path.Combine(
            Path.GetTempPath(), "empty-bin-" + Path.GetRandomFileName());
        Directory.CreateDirectory(emptyBinDir);

        var (_, dotnetPath) = RunWhich("dotnet");
        string dotnetDir = Path.GetDirectoryName(dotnetPath.Trim())!;

        string strippedPath = $"{emptyBinDir}{Path.PathSeparator}{dotnetDir}{Path.PathSeparator}/bin";

        BuildResult result;
        try
        {
            // --no-restore: skip restore so NuGet doesn't need network/cache.
            result = fix.Build(
                verbosity: "normal",
                pathOverride: strippedPath,
                extraArgs: new[] { "--no-restore" });
        }
        finally
        {
            try { Directory.Delete(emptyBinDir); } catch { }
        }

        // Build must succeed despite missing git
        Assert.True(result.Succeeded, $"Build should succeed without git:\n{result.Log}");

        // A warning from our targets must be emitted
        Assert.True(result.Contains("GitHooksFolderSetter"),
            $"Expected a GitHooksFolderSetter warning:\n{result.Log}");

        // No marker written because git failed
        Assert.Null(fix.ReadMarker());
    }

    // --------------------------------------------------------------------- 7
    [Fact]
    public void DisableProperty_TargetSkipped_BuildSucceeds()
    {
        using var fix = new BuildFixture();

        var result = fix.Build(
            verbosity: "d",
            properties: new Dictionary<string, string>
            {
                ["GitHooksFolderSetterDisable"] = "true"
            });

        Assert.True(result.Succeeded, $"Build failed:\n{result.Log}");

        Assert.True(result.Lacks("SetHooksFolderTarget"),
            $"SetHooksFolderTarget should be absent when disable property is set:\n{result.Log}");

        Assert.True(result.Lacks("rev-parse"),
            $"rev-parse should not run when disabled:\n{result.Log}");

        Assert.Null(fix.ReadMarker());
    }

    // ---------------------------------------------------------------- helpers

    private static (int ExitCode, string Output) RunWhich(string name)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "which",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        proc.StartInfo.ArgumentList.Add(name);
        proc.Start();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5_000);
        return (proc.ExitCode, output);
    }
}
