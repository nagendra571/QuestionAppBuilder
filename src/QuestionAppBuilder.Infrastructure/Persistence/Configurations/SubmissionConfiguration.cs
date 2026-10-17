using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class SubmissionConfiguration : IEntityTypeConfiguration<Submission>
{
    public void Configure(EntityTypeBuilder<Submission> builder)
    {
        builder.ToTable("Submissions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.EngineVersion).HasMaxLength(64).HasDefaultValue("legacy-v1").IsRequired();
        builder.HasIndex(s => s.ResponseId).IsUnique().HasFilter("[ResponseId] IS NOT NULL");
        builder.HasIndex(s => new { s.ApplicationId, s.RequestId }).IsUnique().HasFilter("[RequestId] IS NOT NULL");

        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(s => s.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationVersion>()
            .WithMany()
            .HasForeignKey(s => s.ApplicationVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ResponseDraft>().WithMany().HasForeignKey(s => s.ResponseId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(s => s.Answers)
            .WithOne()
            .HasForeignKey(a => a.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.CategoryScores)
            .WithOne()
            .HasForeignKey(c => c.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.QuestionStates).WithOne().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.RuleTraces).WithOne().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
    }
}
