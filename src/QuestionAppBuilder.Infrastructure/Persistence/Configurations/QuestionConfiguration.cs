using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

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
