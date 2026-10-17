using RevitAi.Abstractions.Update;
using RevitAi.Abstractions.Update.Models;

namespace RevitAi.Addin.Services;

// The external SkillManager requires an IUpdateService implementation. The lean
// build keeps only the interface bridge and exposes no update or hash logic.
internal sealed class DisabledUpdateService : IUpdateService
{
    public Task<UpdateInfo?> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<UpdateInfo?>(null);

    public Task<string> DownloadUpdateAsync(
        UpdateInfo updateInfo,
        Action<int>? progressCallback = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public bool VerifyUpdatePackage(string filePath, string expectedChecksum) =>
        false;

    public Task PrepareUpdateAsync(
        string packagePath,
        UpdateInfo updateInfo,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public bool HasPendingUpdate() => false;

    public UpdateInfo? GetPendingUpdateInfo() => null;

    public string GetCurrentVersion() => "0.0.0-lean";

    public Task<string?> DownloadTextAsync(
        string url,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<LatestVersionInfo?> GetVersionInfoAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LatestVersionInfo?>(null);
}

