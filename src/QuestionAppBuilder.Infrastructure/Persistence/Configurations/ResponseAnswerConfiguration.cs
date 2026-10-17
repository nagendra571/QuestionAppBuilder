using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class ResponseAnswerConfiguration : IEntityTypeConfiguration<ResponseAnswer>
{
    public void Configure(EntityTypeBuilder<ResponseAnswer> builder)
    {
        builder.ToTable("ResponseAnswers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TextValue).HasMaxLength(ValidationLimits.AnswerTextMaxLength);
        builder.Property(x => x.NumberValue).HasPrecision(18, 4);
        builder.HasIndex(x => new { x.ResponseDraftId, x.QuestionId }).IsUnique();
        builder.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.SelectedOptions).WithOne().HasForeignKey(x => x.ResponseAnswerId).OnDelete(DeleteBehavior.Cascade);
    }
}
