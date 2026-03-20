using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DotRush.Common.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using FileSystemExtensions = DotRush.Common.Extensions.FileSystemExtensions;
using PathExtensions = DotRush.Common.Extensions.PathExtensions;

namespace DotRush.Roslyn.Workspaces.Extensions;

public static class AnalyzerShadowCopyExtensions {
    public static Project WithShadowCopiedAnalyzerReferences(this Project project) {
        var analyzerReferences = CreateShadowCopiedAnalyzerReferences(project.AnalyzerReferences);
        return analyzerReferences == null ? project : project.WithAnalyzerReferences(analyzerReferences);
    }
    public static Solution WithShadowCopiedAnalyzerReferences(this Solution solution) {
        var currentSolution = solution;

        foreach (var project in solution.Projects.ToArray()) {
            var analyzerReferences = CreateShadowCopiedAnalyzerReferences(project.AnalyzerReferences);
            if (analyzerReferences == null)
                continue;

            currentSolution = currentSolution.WithProjectAnalyzerReferences(project.Id, analyzerReferences);
        }

        return currentSolution;
    }

    private static List<AnalyzerReference>? CreateShadowCopiedAnalyzerReferences(IEnumerable<AnalyzerReference> analyzerReferences) {
        var result = new List<AnalyzerReference>();
        var hasChanges = false;

        foreach (var reference in analyzerReferences) {
            if (reference is not AnalyzerFileReference fileReference || string.IsNullOrEmpty(fileReference.FullPath) || !File.Exists(fileReference.FullPath)) {
                result.Add(reference);
                continue;
            }

            var shadowCopyPath = ShadowCopyDirectory.GetOrCreate(fileReference.FullPath);
            if (PathExtensions.Equals(shadowCopyPath, fileReference.FullPath)) {
                result.Add(reference);
                continue;
            }

            result.Add(new AnalyzerFileReference(shadowCopyPath, ShadowCopyAnalyzerAssemblyLoader.Instance));
            hasChanges = true;
        }

        return hasChanges ? result : null;
    }
}

internal sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader {
    private readonly Dictionary<string, string> shadowCopyPaths = new(StringComparer.OrdinalIgnoreCase);

    public static ShadowCopyAnalyzerAssemblyLoader Instance { get; } = new ShadowCopyAnalyzerAssemblyLoader();

    private ShadowCopyAnalyzerAssemblyLoader() { }

    public void AddDependencyLocation(string fullPath) {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            return;

        lock (shadowCopyPaths)
            shadowCopyPaths[fullPath] = ShadowCopyDirectory.GetOrCreate(fullPath);
    }
    public Assembly LoadFromPath(string fullPath) {
        if (string.IsNullOrEmpty(fullPath))
            throw new ArgumentException("Assembly path is null or empty", nameof(fullPath));

        lock (shadowCopyPaths) {
            if (shadowCopyPaths.TryGetValue(fullPath, out var shadowCopyPath))
                return Assembly.LoadFrom(shadowCopyPath);
        }

        return Assembly.LoadFrom(ShadowCopyDirectory.GetOrCreate(fullPath));
    }
}

internal static class ShadowCopyDirectory {
    private static readonly object SyncRoot = new();
    private static readonly string RootPath = Path.Combine(Path.GetTempPath(), "DotRush", "analyzers");
    private static readonly string SessionRootPath = Path.Combine(RootPath, $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    static ShadowCopyDirectory() {
        Directory.CreateDirectory(SessionRootPath);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FileSystemExtensions.TryDeleteDirectory(SessionRootPath);
    }

    public static string GetOrCreate(string assemblyPath) {
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            return assemblyPath;

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (PathExtensions.StartsWith(fullAssemblyPath, RootPath))
            return fullAssemblyPath;

        var sourceDirectory = Path.GetDirectoryName(fullAssemblyPath);
        if (string.IsNullOrEmpty(sourceDirectory))
            return fullAssemblyPath;

        var shadowDirectory = Path.Combine(SessionRootPath, GetDirectoryFingerprint(sourceDirectory));
        var shadowAssemblyPath = Path.Combine(shadowDirectory, Path.GetFileName(fullAssemblyPath));

        lock (SyncRoot) {
            if (!File.Exists(shadowAssemblyPath)) {
                Directory.CreateDirectory(shadowDirectory);
                foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)) {
                    var shadowFilePath = Path.Combine(shadowDirectory, Path.GetRelativePath(sourceDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(shadowFilePath)!);
                    File.Copy(file, shadowFilePath, true);
                }

                CurrentSessionLogger.Debug($"[Reflector]: Shadow copied analyzer '{fullAssemblyPath}' to '{shadowAssemblyPath}'.");
            }
        }

        return shadowAssemblyPath;
    }

    private static string GetDirectoryFingerprint(string sourceDirectory) {
        var fingerprint = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(sourceDirectory, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceDirectory}|{string.Join('\n', fingerprint)}"));
        return Convert.ToHexString(bytes);
    }
}

