# GitHooksFolderSetter

Runs `git config core.hooksPath <path>` once per repository so your team's git
hooks folder is configured automatically on first build.

For background on git hooks see the
[Pro Git book](https://git-scm.com/book/en/v2/Customizing-Git-Git-Hooks).

## Installation

```sh
dotnet add package GitHooksFolderSetter
```

The hooks folder is configured on the first build after package installation.
Subsequent builds skip the target entirely with no process spawned (~1.6 ms
property evaluation vs ~430 ms for a git process).

## How it works

On every build the package evaluates a property that searches for the marker
file `git\git-hook-folder.set` in directories above the project using
`GetDirectoryNameOfFileAbove`. If the file exists and its content matches the
configured `HooksPath` the target is skipped immediately — no git process runs.

When the marker is absent or the path has changed (slow path), the package
runs `git rev-parse --show-toplevel` and `git config core.hooksPath`, then
writes the marker with the current `HooksPath` as its content. If git is
missing or the command fails the build continues with a warning; it never
fails.

## Resetting

Delete `.git/git-hook-folder.set` to force the package to reconfigure on the
next build (for example after changing `HooksPath`).

## Configuration

### HooksPath

Default: `hooks` (relative to the repository root, alongside `.git`).

```
repo/
  .git/
  hooks/
    commit-msg
  src/
    MyProject/
      MyProject.csproj
```

Override in your project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <HooksPath>custom_hooks</HooksPath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="GitHooksFolderSetter" Version="1.1.0" IncludeAssets="build" />
  </ItemGroup>
</Project>
```

### Disabling

Set `GitHooksFolderSetterDisable=true` in your project or on the command line
to opt out without removing the package:

```xml
<PropertyGroup>
  <GitHooksFolderSetterDisable>true</GitHooksFolderSetterDisable>
</PropertyGroup>
```

The target also skips automatically when any of the following environment
variables or properties are set:

| Variable / Property          | Typical source                     |
|------------------------------|------------------------------------|
| `CI`                         | Most CI providers                  |
| `TF_BUILD`                   | Azure Pipelines                    |
| `DOTNET_RUNNING_IN_CONTAINER`| .NET container images              |
| `DesignTimeBuild=true`       | IDE background design-time builds  |

## Known limitation: worktrees and submodules

In a git worktree or submodule `.git` is a file, not a directory.
`GetDirectoryNameOfFileAbove` searches for `git\git-hook-folder.set` and
cannot find it through a `.git` file, so the fast path never matches and the
slow path (one `git rev-parse` process) runs on every build.

## Acknowledgments

- <https://github.com/Nagarian/dotnethooks>
- <https://github.com/MrLuje/dotnet-format-hook>
