using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Infrastructure.Attachments;

/// <summary>
/// The one place that turns a provider name into an <see cref="IAttachmentFileStore"/>. The
/// desktop host asks for <see cref="LocalProvider"/> today. A bucket-backed store is added by
/// registering its name here (and nothing else changes): see docs/attachment-storage.md.
/// </summary>
public static class AttachmentFileStoreFactory
{
    public const string LocalProvider = "local";

    /// <summary>The store for the live attachments of a data root.</summary>
    public static IAttachmentFileStore Create(StorageOptions options, string provider = LocalProvider) => provider switch
    {
        LocalProvider => new FileAttachmentStore(options.AttachmentsDirectory),
        _ => throw new AppException(AppErrorKind.UnsupportedCapability, $"Attachment storage \"{provider}\" is not available in this version of Chaos."),
    };

    /// <summary>The shared, content-addressed store that backups keep their attachment copies in.</summary>
    public static IAttachmentFileStore CreateBackupStore(StorageOptions options) => new FileAttachmentStore(options.BackupAttachmentsDirectory);
}
