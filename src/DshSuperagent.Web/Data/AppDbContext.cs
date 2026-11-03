using System.Text.Json;
using DshSuperagent.Domain.Authorization;
using DshSuperagent.Domain.Channels;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Domain.Threads;
using DshSuperagent.Domain.Work;
using DshSuperagent.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Thread = DshSuperagent.Domain.Threads.Thread;

namespace DshSuperagent.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ISecretProtector protector) : DbContext(options)
{
    private readonly ISecretProtector _protector = protector;

    // The domain is persisted directly; configuration tables hold deployment adapters, not domain state.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<SourcePolicy> SourcePolicies => Set<SourcePolicy>();
    public DbSet<Thread> Threads => Set<Thread>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Trigger> Triggers => Set<Trigger>();
    public DbSet<Eval> Evals => Set<Eval>();
    public DbSet<Disposition> Dispositions => Set<Disposition>();
    public DbSet<Correlation> Correlations => Set<Correlation>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Input> TicketInputs => Set<Input>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();

    public DbSet<RepositoryEntity> Repositories => Set<RepositoryEntity>();
    public DbSet<LlmProfileEntity> LlmProfiles => Set<LlmProfileEntity>();
    public DbSet<PullRequestProfileEntity> PullRequestProfiles => Set<PullRequestProfileEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<StreamReplyEntity> StreamReplies => Set<StreamReplyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConfiguration(modelBuilder);
        ConfigureDomain(modelBuilder);
    }

    public override int SaveChanges()
    {
        TrackNewAggregateChildren();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TrackNewAggregateChildren();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void TrackNewAggregateChildren()
    {
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in ChangeTracker.Entries<Thread>())
            {
                foreach (var subscription in entry.Entity.Subscriptions)
                {
                    if (Entry(subscription).State == EntityState.Detached)
                    {
                        Entry(subscription).State = EntityState.Added;
                    }
                }
            }

            foreach (var entry in ChangeTracker.Entries<Ticket>())
            {
                foreach (var input in entry.Entity.Inputs)
                {
                    if (Entry(input).State == EntityState.Detached)
                    {
                        Entry(input).State = EntityState.Added;
                    }
                }

                foreach (var assignment in entry.Entity.Assignments)
                {
                    if (Entry(assignment).State == EntityState.Detached)
                    {
                        Entry(assignment).State = EntityState.Added;
                    }
                }

                foreach (var report in entry.Entity.Reports)
                {
                    if (Entry(report).State == EntityState.Detached)
                    {
                        Entry(report).State = EntityState.Added;
                    }
                }
            }

            foreach (var entry in ChangeTracker.Entries<Policy>())
            {
                foreach (var rule in entry.Entity.Rules)
                {
                    if (Entry(rule).State == EntityState.Detached)
                    {
                        Entry(rule).State = EntityState.Added;
                    }
                }

                foreach (var sourcePolicy in entry.Entity.SourcePolicies)
                {
                    if (Entry(sourcePolicy).State == EntityState.Detached)
                    {
                        Entry(sourcePolicy).State = EntityState.Added;
                    }
                }
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset; store it as a sortable binary value for all entities.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    private static void ConfigureConfiguration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RepositoryEntity>(entity => entity.HasIndex(repository => repository.Slug).IsUnique());
        modelBuilder.Entity<LlmProfileEntity>(entity => entity.HasIndex(profile => profile.Name).IsUnique());
        modelBuilder.Entity<PullRequestProfileEntity>(entity => entity.HasIndex(profile => profile.Name).IsUnique());
        modelBuilder.Entity<SettingsEntity>(entity => entity.HasKey(settings => settings.Id));
        modelBuilder.Entity<AuditEventEntity>(entity => entity.HasIndex(audit => audit.At));
        modelBuilder.Entity<StreamReplyEntity>(entity =>
        {
            entity.HasKey(reply => reply.StreamId);
            entity.HasIndex(reply => reply.UpdatedAt);
        });
    }

    private void ConfigureDomain(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Source>(entity =>
        {
            entity.ToTable("Sources");
            entity.HasKey(source => source.Id);
            entity.Property(source => source.ProviderId).IsRequired();
            entity.Property(source => source.Name).IsRequired();
            entity.HasIndex(source => source.Name).IsUnique();
            entity.Property(source => source.Configuration)
                .HasConversion(SourceConfigurationConverter())
                .Metadata.SetValueComparer(JsonComparer<IReadOnlyDictionary<string, string>>());
        });

        modelBuilder.Entity<Policy>(entity =>
        {
            entity.ToTable("Policies");
            entity.HasKey(policy => policy.Id);
            entity.Property(policy => policy.AcceptMode).HasConversion<string>();
            entity.HasMany(policy => policy.Rules)
                .WithOne()
                .HasForeignKey("PolicyId")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(policy => policy.SourcePolicies)
                .WithOne()
                .HasForeignKey("PolicyId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PolicyRule>(entity =>
        {
            entity.ToTable("PolicyRules");
            entity.HasKey(rule => rule.Id);
            entity.Property(rule => rule.Name).IsRequired();
        });

        modelBuilder.Entity<SourcePolicy>(entity =>
        {
            entity.ToTable("PolicySourceParts");
            entity.HasKey("PolicyId", nameof(SourcePolicy.SourceId));
            entity.Property(part => part.AllowedUsers)
                .HasConversion(JsonConverter<List<AllowedUser>>())
                .Metadata.SetValueComparer(JsonComparer<List<AllowedUser>>());
            entity.Property(part => part.BotIds)
                .HasConversion(JsonConverter<List<string>>())
                .Metadata.SetValueComparer(JsonComparer<List<string>>());
            entity.Property(part => part.Admission)
                .HasConversion(JsonConverter<AdmissionTable>())
                .Metadata.SetValueComparer(JsonComparer<AdmissionTable>());
        });

        modelBuilder.Entity<Thread>(entity =>
        {
            entity.ToTable("Threads");
            entity.HasKey(thread => thread.Id);
            entity.Property(thread => thread.ExternalKey).IsRequired();
            entity.HasIndex(thread => new { thread.SourceId, thread.ExternalKey }).IsUnique();
            entity.HasIndex(thread => thread.LastActivityAt);
            entity.Property(thread => thread.PendingTriggerIds)
                .HasConversion(ReadOnlyGuidListConverter())
                .Metadata.SetValueComparer(ReadOnlyGuidListComparer());
            entity.HasMany(thread => thread.Subscriptions)
                .WithOne()
                .HasForeignKey(subscription => subscription.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(thread => thread.Subscriptions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("Subscriptions");
            entity.HasKey(subscription => subscription.Id);
            entity.HasIndex(subscription => subscription.TicketId);
        });

        modelBuilder.Entity<Trigger>(entity =>
        {
            entity.ToTable("Triggers");
            entity.HasKey(trigger => trigger.Id);
            entity.Property(trigger => trigger.ExternalId).IsRequired();
            entity.HasIndex(trigger => new { trigger.SourceId, trigger.ExternalId }).IsUnique();
            entity.HasIndex(trigger => trigger.ThreadId);
            entity.HasIndex(trigger => trigger.ReceivedAt);
            entity.Property(trigger => trigger.Strength).HasConversion<string>();
            entity.OwnsOne(trigger => trigger.Actor, actor =>
            {
                actor.Property(value => value.Id).HasColumnName("ActorId");
                actor.Property(value => value.DisplayName).HasColumnName("ActorDisplayName");
                actor.Property(value => value.Kind).HasColumnName("ActorKind").HasConversion<string>();
            });
        });

        modelBuilder.Entity<Eval>(entity =>
        {
            entity.ToTable("Evals");
            entity.HasKey(eval => eval.Id);
            entity.HasIndex(eval => new { eval.ThreadId, eval.Status });
            entity.Property(eval => eval.Status).HasConversion<string>();
            entity.Property(eval => eval.TriggerIds)
                .HasConversion(ReadOnlyGuidListConverter())
                .Metadata.SetValueComparer(ReadOnlyGuidListComparer());
            entity.OwnsOne(eval => eval.Decision, decision =>
            {
                decision.Property(value => value.Kind).HasConversion<string>();
                decision.Property(value => value.Strength).HasConversion<string>();
                decision.Property(value => value.MatchedRuleIds)
                    .HasConversion(ReadOnlyGuidListConverter())
                    .Metadata.SetValueComparer(ReadOnlyGuidListComparer());
            });
        });

        modelBuilder.Entity<Disposition>(entity =>
        {
            entity.ToTable("Dispositions");
            entity.HasKey(disposition => disposition.Id);
            entity.Property(disposition => disposition.Kind).HasConversion<string>();
            entity.HasIndex(disposition => disposition.TriggerId);
        });

        modelBuilder.Entity<Correlation>(entity =>
        {
            entity.ToTable("Correlations");
            entity.HasKey(correlation => correlation.Id);
            entity.HasIndex(correlation => correlation.TriggerId);
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.ToTable("Tickets");
            entity.HasKey(ticket => ticket.Id);
            entity.Property(ticket => ticket.Status).HasConversion<string>();
            entity.Property(ticket => ticket.Outcome).HasConversion<string>();
            entity.Property(ticket => ticket.Stage).HasConversion<string>();
            entity.Property(ticket => ticket.WorkKind).HasConversion<string>();
            entity.HasIndex(ticket => ticket.Stage);
            entity.HasIndex(ticket => ticket.CreatedAt);
            entity.OwnsOne(ticket => ticket.Mandate, mandate =>
            {
                mandate.Property(value => value.Id).HasColumnName("MandateId");
                mandate.Property(value => value.Strength).HasConversion<string>();
                mandate.Property(value => value.Basis).HasConversion<string>();
                mandate.Property(value => value.MatchedRuleIds)
                    .HasConversion(JsonConverter<List<Guid>>())
                    .Metadata.SetValueComparer(JsonComparer<List<Guid>>());
                mandate.OwnsOne(value => value.Requester, requester =>
                {
                    requester.Property(actor => actor.Id).HasColumnName("RequesterId");
                    requester.Property(actor => actor.DisplayName).HasColumnName("RequesterDisplayName");
                    requester.Property(actor => actor.Kind).HasColumnName("RequesterKind").HasConversion<string>();
                });
            });
            entity.HasMany(ticket => ticket.Inputs)
                .WithOne()
                .HasForeignKey(input => input.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(ticket => ticket.Inputs).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasMany(ticket => ticket.Assignments)
                .WithOne()
                .HasForeignKey(assignment => assignment.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(ticket => ticket.Assignments).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasMany(ticket => ticket.Reports)
                .WithOne()
                .HasForeignKey(report => report.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(ticket => ticket.Reports).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Input>(entity =>
        {
            entity.ToTable("TicketInputs");
            entity.HasKey(input => input.Id);
            entity.Property(input => input.Kind).HasConversion<string>();
            entity.HasIndex(input => input.TicketId);
        });

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.ToTable("Assignments");
            entity.HasKey(assignment => assignment.Id);
            entity.Property(assignment => assignment.Status).HasConversion<string>();
            entity.HasIndex(assignment => assignment.TicketId);
        });

        modelBuilder.Entity<Report>(entity =>
        {
            entity.ToTable("Reports");
            entity.HasKey(report => report.Id);
            entity.Property(report => report.Kind).HasConversion<string>();
            entity.HasIndex(report => report.TicketId);
        });

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.ToTable("Channels");
            entity.HasKey(channel => channel.Id);
            entity.Property(channel => channel.Purpose).HasConversion<string>();
            entity.HasIndex(channel => channel.ThreadId);
        });

        modelBuilder.Entity<ChannelMessage>(entity =>
        {
            entity.ToTable("ChannelMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Status).HasConversion<string>();
            entity.Property(message => message.Purpose).HasConversion<string>();
            entity.HasIndex(message => new { message.Status, message.NextAttemptAt });
            entity.HasIndex(message => message.TicketId);
        });
    }

    private ValueConverter<IReadOnlyDictionary<string, string>, string> SourceConfigurationConverter() =>
        new(
            value => _protector.Protect(JsonSerializer.Serialize(value, JsonOptions)),
            value => JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(value), JsonOptions)
                     ?? new Dictionary<string, string>());

    private static ValueConverter<T, string> JsonConverter<T>() =>
        new(
            value => JsonSerializer.Serialize(value, JsonOptions),
            value => JsonSerializer.Deserialize<T>(value, JsonOptions)!);

    private static ValueComparer<T> JsonComparer<T>() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions),
            value => JsonSerializer.Serialize(value, JsonOptions).GetHashCode(),
            value => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!);

    private static ValueConverter<IReadOnlyList<Guid>, string> ReadOnlyGuidListConverter() =>
        new(
            value => JsonSerializer.Serialize(value, JsonOptions),
            value => JsonSerializer.Deserialize<List<Guid>>(value, JsonOptions) ?? new List<Guid>());

    private static ValueComparer<IReadOnlyList<Guid>> ReadOnlyGuidListComparer() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions),
            value => JsonSerializer.Serialize(value, JsonOptions).GetHashCode(),
            value => JsonSerializer.Deserialize<List<Guid>>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!);
}
