using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class SubmissionConfiguration : IEntityTypeConfiguration<Submission>
{
    public void Configure(EntityTypeBuilder<Submission> builder)
    {
        builder.ToTable("Submissions");
        builder.HasKey(s => s.Id);

        builder.HasOne<Application>()
            .WithMany()
            .HasForeignKey(s => s.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationVersion>()
            .WithMany()
            .HasForeignKey(s => s.ApplicationVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(s => s.Answers)
            .WithOne()
            .HasForeignKey(a => a.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.CategoryScores)
            .WithOne()
            .HasForeignKey(c => c.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
