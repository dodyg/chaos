using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chaos.Server.Data;

/// <summary>
/// The server database (PLAN2 §8.1, §8.2): accounts, logins, devices, email codes, audit events and
/// OpenIddict's applications, authorizations, scopes and tokens. Every name and constraint that
/// reaches the schema is configured explicitly below; conventions are not relied on.
/// </summary>
public sealed class ChaosDbContext(DbContextOptions<ChaosDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountLogin> AccountLogins => Set<AccountLogin>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SyncStream> SyncStreams => Set<SyncStream>();
    public DbSet<SyncEntityRow> SyncEntities => Set<SyncEntityRow>();
    public DbSet<SyncChangeRow> SyncChanges => Set<SyncChangeRow>();
    public DbSet<SpaceMember> SpaceMembers => Set<SpaceMember>();
    public DbSet<SpaceInvite> SpaceInvites => Set<SpaceInvite>();
    public DbSet<BlobRow> Blobs => Set<BlobRow>();
    public DbSet<BlobObjectRow> BlobObjects => Set<BlobObjectRow>();
    public DbSet<BlobDeletionRow> BlobDeletions => Set<BlobDeletionRow>();
    public DbSet<AttachmentBlobBinding> AttachmentBlobBindings => Set<AttachmentBlobBinding>();
    public DbSet<BlobUploadRow> BlobUploads => Set<BlobUploadRow>();
    public DbSet<BlobPartRow> BlobParts => Set<BlobPartRow>();

    /// <summary>Provider options shared by the service, the design-time factory and the tests.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString);
        builder.UseOpenIddict();
        return builder;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();

        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new AccountLoginConfiguration());
        modelBuilder.ApplyConfiguration(new DeviceConfiguration());
        modelBuilder.ApplyConfiguration(new EmailCodeConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());
        modelBuilder.ApplyConfiguration(new SyncStreamConfiguration());
        modelBuilder.ApplyConfiguration(new SyncEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SyncChangeConfiguration());
        modelBuilder.ApplyConfiguration(new SpaceMemberConfiguration());
        modelBuilder.ApplyConfiguration(new SpaceInviteConfiguration());
        modelBuilder.ApplyConfiguration(new BlobConfiguration());
        modelBuilder.ApplyConfiguration(new BlobObjectConfiguration());
        modelBuilder.ApplyConfiguration(new BlobDeletionConfiguration());
        modelBuilder.ApplyConfiguration(new AttachmentBlobBindingConfiguration());
        modelBuilder.ApplyConfiguration(new BlobUploadConfiguration());
        modelBuilder.ApplyConfiguration(new BlobPartConfiguration());
    }

    private static string InList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
    {
        public void Configure(EntityTypeBuilder<Account> e)
        {
            e.ToTable("Accounts", t =>
            {
                t.HasCheckConstraint("CK_Accounts_PrimaryEmail_Lower", "\"PrimaryEmail\" = lower(\"PrimaryEmail\")");
                t.HasCheckConstraint("CK_Accounts_StorageBytes", "\"StorageBytes\" >= 0");
            });
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.PrimaryEmail).IsRequired();
            e.Property(a => a.StorageBytes).HasDefaultValue(0L);
            e.Property(a => a.MembershipVersion).HasDefaultValue(0L);
            e.HasIndex(a => a.PrimaryEmail).IsUnique().HasDatabaseName("UX_Accounts_PrimaryEmail");
            e.HasIndex(a => a.ProfileId).IsUnique().HasFilter("\"ProfileId\" IS NOT NULL")
                .HasDatabaseName("UX_Accounts_ProfileId");

            e.HasMany(a => a.Logins).WithOne().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Devices).WithOne().HasForeignKey(d => d.AccountId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class AccountLoginConfiguration : IEntityTypeConfiguration<AccountLogin>
    {
        public void Configure(EntityTypeBuilder<AccountLogin> e)
        {
            e.ToTable("AccountLogins", t =>
                t.HasCheckConstraint("CK_AccountLogins_Provider", $"\"Provider\" IN ({InList(LoginProviders.All)})"));
            e.HasKey(l => new { l.Provider, l.Subject });
            e.HasIndex(l => l.AccountId).HasDatabaseName("IX_AccountLogins_AccountId");
        }
    }

    private sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
    {
        public void Configure(EntityTypeBuilder<Device> e)
        {
            e.ToTable("Devices");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).ValueGeneratedNever();
            e.HasIndex(d => d.AccountId).HasDatabaseName("IX_Devices_AccountId");
        }
    }

    private sealed class EmailCodeConfiguration : IEntityTypeConfiguration<EmailCode>
    {
        public void Configure(EntityTypeBuilder<EmailCode> e)
        {
            e.ToTable("EmailCodes", t =>
            {
                t.HasCheckConstraint("CK_EmailCodes_Email_Lower", "\"Email\" = lower(\"Email\")");
                t.HasCheckConstraint("CK_EmailCodes_Attempts", $"\"Attempts\" BETWEEN 0 AND {EmailCode.MaxAttempts}");
            });
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.Attempts).HasDefaultValue(0);
            e.HasIndex(c => new { c.Email, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("IX_EmailCodes_Email");
        }
    }

    private sealed class SyncStreamConfiguration : IEntityTypeConfiguration<SyncStream>
    {
        public void Configure(EntityTypeBuilder<SyncStream> e)
        {
            e.ToTable("SyncStreams", t =>
            {
                t.HasCheckConstraint("CK_SyncStreams_Identity", "(\"Kind\" = 1 AND \"AccountId\" IS NOT NULL AND \"AccountId\" = \"Id\" AND \"SpaceId\" IS NULL) OR (\"Kind\" = 2 AND \"SpaceId\" IS NOT NULL AND \"SpaceId\" = \"Id\" AND \"AccountId\" IS NULL)");
                t.HasCheckConstraint("CK_SyncStreams_NextSeq", "\"NextSeq\" >= 1");
                t.HasCheckConstraint("CK_SyncStreams_Prune", "\"ChangesPrunedThroughSeq\" >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.NextSeq).HasDefaultValue(1L);
            e.Property(x => x.ChangesPrunedThroughSeq).HasDefaultValue(0L);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class SyncEntityConfiguration : IEntityTypeConfiguration<SyncEntityRow>
    {
        public void Configure(EntityTypeBuilder<SyncEntityRow> e)
        {
            e.ToTable("SyncEntities", t => t.HasCheckConstraint("CK_SyncEntities_LastSeq", "\"LastSeq\" >= 0"));
            e.HasKey(x => new { x.AccountId, x.Kind, x.EntityKey });
            e.Property(x => x.AccountId).HasColumnName("StreamId");
            e.Property(x => x.Groups).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Kind, x.EntityKey });
            e.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class SyncChangeConfiguration : IEntityTypeConfiguration<SyncChangeRow>
    {
        public void Configure(EntityTypeBuilder<SyncChangeRow> e)
        {
            e.ToTable("SyncChanges", t =>
            {
                t.HasCheckConstraint("CK_SyncChanges_Seq", "\"Seq\" >= 1");
                t.HasCheckConstraint("CK_SyncChanges_Op", "\"Op\" IN ('upsert', 'purge', 'moved')");
            });
            e.HasKey(x => new { x.AccountId, x.Seq });
            e.Property(x => x.AccountId).HasColumnName("StreamId");
            e.Property(x => x.Seq).ValueGeneratedNever();
            e.Property(x => x.PreviousGroups).HasColumnType("jsonb");
            e.Property(x => x.Groups).HasColumnType("jsonb");
            e.HasIndex(x => new { x.AccountId, x.ChangeId }).IsUnique().HasDatabaseName("UX_SyncChanges_ChangeId");
            e.HasIndex(x => new { x.AccountId, x.CreatedAt }).HasDatabaseName("IX_SyncChanges_CreatedAt");
            e.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class BlobConfiguration : IEntityTypeConfiguration<BlobRow>
    {
        public void Configure(EntityTypeBuilder<BlobRow> e)
        {
            e.ToTable("Blobs", t =>
            {
                t.HasCheckConstraint("CK_Blobs_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_Blobs_ByteSize", "\"ByteSize\" >= 0");
                t.HasCheckConstraint("CK_Blobs_State", $"\"State\" IN ({InList(BlobStates.All)})");
            });
            e.HasKey(x => new { x.StreamId, x.Sha256 });
            e.HasIndex(x => x.UnreferencedSince).HasDatabaseName("IX_Blobs_UnreferencedSince");
            e.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BlobObjectRow>().WithMany().HasForeignKey(x => x.ObjectId).OnDelete(DeleteBehavior.Restrict);
        }

    }

    private sealed class BlobObjectConfiguration : IEntityTypeConfiguration<BlobObjectRow>
    {
        public void Configure(EntityTypeBuilder<BlobObjectRow> e)
        {
            e.ToTable("BlobObjects", t =>
            {
                t.HasCheckConstraint("CK_BlobObjects_ByteSize", "\"ByteSize\" >= 0");
                t.HasCheckConstraint("CK_BlobObjects_State", $"\"State\" IN ({InList(BlobStates.All)})");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.AccountId).HasDatabaseName("IX_BlobObjects_AccountId");
        }
    }

    private sealed class BlobDeletionConfiguration : IEntityTypeConfiguration<BlobDeletionRow>
    {
        public void Configure(EntityTypeBuilder<BlobDeletionRow> e)
        {
            e.ToTable("BlobDeletions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        }
    }

    private sealed class SpaceMemberConfiguration : IEntityTypeConfiguration<SpaceMember>
    {
        public void Configure(EntityTypeBuilder<SpaceMember> e)
        {
            e.ToTable("SpaceMembers", t =>
            {
                t.HasCheckConstraint("CK_SpaceMembers_MemberKind", "\"MemberKind\" IN (1, 2)");
                t.HasCheckConstraint("CK_SpaceMembers_Role", "\"Role\" IN (1, 2, 3, 4)");
                t.HasCheckConstraint("CK_SpaceMembers_KindRole",
                    "(\"MemberKind\" = 1 AND \"Role\" IN (1, 2, 3)) OR (\"MemberKind\" = 2 AND \"Role\" = 4)");
            });
            e.HasKey(m => new { m.SpaceId, m.MemberId });
            e.Property(m => m.AllowedContainers).HasColumnType("jsonb");
            e.HasIndex(m => m.AccountId).HasFilter("\"RemovedAt\" IS NULL")
                .HasDatabaseName("IX_SpaceMembers_AccountId_Live");
            e.HasOne<SyncStream>().WithMany().HasForeignKey(m => m.SpaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.SetNull);
        }
    }

    private sealed class SpaceInviteConfiguration : IEntityTypeConfiguration<SpaceInvite>
    {
        public void Configure(EntityTypeBuilder<SpaceInvite> e)
        {
            e.ToTable("SpaceInvites", t =>
            {
                t.HasCheckConstraint("CK_SpaceInvites_Email_Lower", "\"Email\" IS NULL OR \"Email\" = lower(\"Email\")");
                t.HasCheckConstraint("CK_SpaceInvites_Role", "\"Role\" IN (2, 3)");
                t.HasCheckConstraint("CK_SpaceInvites_Uses", "\"Uses\" >= 0");
                t.HasCheckConstraint("CK_SpaceInvites_MaxUses", "\"MaxUses\" IS NULL OR \"MaxUses\" > 0");
            });
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.Uses).HasDefaultValue(0);
            e.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("UX_SpaceInvites_TokenHash");
            e.HasIndex(i => i.SpaceId).HasDatabaseName("IX_SpaceInvites_SpaceId");
            e.HasOne<SyncStream>().WithMany().HasForeignKey(i => i.SpaceId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class AttachmentBlobBindingConfiguration : IEntityTypeConfiguration<AttachmentBlobBinding>
    {
        public void Configure(EntityTypeBuilder<AttachmentBlobBinding> e)
        {
            e.ToTable("AttachmentBlobBindings", t =>
                t.HasCheckConstraint("CK_AttachmentBlobBindings_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'"));
            e.HasKey(x => new { x.StreamId, x.AttachmentId });
            e.Property(x => x.AttachmentId).HasMaxLength(Chaos.Sync.SyncProtocol.MaxKeyLength);
            e.HasIndex(x => new { x.CustodianAccountId, x.Sha256 }).HasDatabaseName("IX_AttachmentBlobBindings_Custodian");
            e.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.CustodianAccountId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class BlobUploadConfiguration : IEntityTypeConfiguration<BlobUploadRow>
    {
        public void Configure(EntityTypeBuilder<BlobUploadRow> e)
        {
            e.ToTable("BlobUploads", t =>
            {
                t.HasCheckConstraint("CK_BlobUploads_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_BlobUploads_ByteSize", "\"ByteSize\" >= 0 AND \"PartCount\" >= 0");
            });
            e.HasKey(x => new { x.StreamId, x.Sha256 });
            e.HasIndex(x => x.UpdatedAt).HasDatabaseName("IX_BlobUploads_UpdatedAt");
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class BlobPartConfiguration : IEntityTypeConfiguration<BlobPartRow>
    {
        public void Configure(EntityTypeBuilder<BlobPartRow> e)
        {
            e.ToTable("BlobParts", t => t.HasCheckConstraint("CK_BlobParts_PartIndex", "\"PartIndex\" >= 0 AND \"ByteSize\" > 0"));
            e.HasKey(x => new { x.StreamId, x.Sha256, x.PartIndex });
            e.HasOne<BlobUploadRow>().WithMany().HasForeignKey(x => new { x.StreamId, x.Sha256 }).OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
    {
        public void Configure(EntityTypeBuilder<AuditEvent> e)
        {
            e.ToTable("AuditEvents", t =>
                t.HasCheckConstraint("CK_AuditEvents_Kind", $"\"Kind\" IN ({InList(AuditKinds.All)})"));
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).UseIdentityAlwaysColumn();
            e.HasIndex(a => new { a.AccountId, a.At }).HasDatabaseName("IX_AuditEvents_AccountId");
        }
    }
}
