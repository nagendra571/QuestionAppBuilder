using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class AnswerConfiguration : IEntityTypeConfiguration<Answer>
{
    public void Configure(EntityTypeBuilder<Answer> builder)
    {
        builder.ToTable("Answers");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.TextValue).HasMaxLength(ValidationLimits.AnswerTextMaxLength);
        builder.Property(a => a.NumberValue).HasPrecision(18, 4);

        builder.HasOne<Question>()
            .WithMany()
            .HasForeignKey(a => a.QuestionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.SelectedOptions)
            .WithOne()
            .HasForeignKey(o => o.AnswerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
