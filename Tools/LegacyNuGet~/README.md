# Legacy NuGet snapshot

The original NuGetForUnity editor plugin and its installed DLLs are retained here
for recovery. This folder is outside Assets and Packages, so Unity does not import
these duplicate assemblies. Do not copy them back into an active project together
with the `org.nuget` packages declared by SSFramework.

The project now resolves the existing SSFramework dependency declarations through
OpenUPM, including Google.Protobuf 3.36.1. The old packages.config remains in
Packages/nuget-packages as historical build input; it is not an active restore
source. No SSFramework source or submodule revision was changed.
