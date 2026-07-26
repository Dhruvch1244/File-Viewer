namespace FileViewer.Core.Extensibility;

/// <summary>
/// Phase 2 extension point for remote storage (Amazon S3) search/upload/download. No
/// implementation exists yet, and the core app takes no AWS SDK dependency until Phase 2 lands
/// behind this interface (PRS §6.8, §10).
/// </summary>
public interface IStorageProvider
{
    Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<Stream> DownloadAsync(string key, CancellationToken cancellationToken = default);
    Task UploadAsync(string key, Stream content, CancellationToken cancellationToken = default);
}
