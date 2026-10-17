using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> builder)
    {
        builder.ToTable("Questions");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.AnswerType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(q => q.ScoringMode).HasConversion<string>().HasMaxLength(32).HasDefaultValue(ScoringMode.Additive).IsRequired();
        builder.Property(q => q.MinValue).HasPrecision(18, 4);
        builder.Property(q => q.MaxValue).HasPrecision(18, 4);

        builder.HasMany(q => q.Options)
            .WithOne()
            .HasForeignKey(o => o.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(q => q.Rules)
            .WithOne()
            .HasForeignKey(r => r.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
