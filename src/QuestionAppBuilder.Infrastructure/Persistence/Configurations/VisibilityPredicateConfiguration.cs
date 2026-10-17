using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class VisibilityPredicateConfiguration : IEntityTypeConfiguration<VisibilityPredicate>
{
    public void Configure(EntityTypeBuilder<VisibilityPredicate> builder)
    {
        builder.ToTable("VisibilityPredicates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Operator).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.NumberValue).HasPrecision(18, 4);
        builder.HasOne(x => x.SourceQuestion).WithMany().HasForeignKey(x => x.SourceQuestionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Options).WithOne().HasForeignKey(x => x.VisibilityPredicateId).OnDelete(DeleteBehavior.Cascade);
    }
}
