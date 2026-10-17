using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class VisibilityPredicateOptionConfiguration : IEntityTypeConfiguration<VisibilityPredicateOption>
{
    public void Configure(EntityTypeBuilder<VisibilityPredicateOption> builder)
    {
        builder.ToTable("VisibilityPredicateOptions");
        builder.HasKey(x => new { x.VisibilityPredicateId, x.QuestionOptionId });
        builder.HasOne<QuestionOption>().WithMany().HasForeignKey(x => x.QuestionOptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
