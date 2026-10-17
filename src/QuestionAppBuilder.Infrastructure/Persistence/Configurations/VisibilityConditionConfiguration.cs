using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class VisibilityConditionConfiguration : IEntityTypeConfiguration<VisibilityCondition>
{
    public void Configure(EntityTypeBuilder<VisibilityCondition> builder)
    {
        builder.ToTable("VisibilityConditions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Junction).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasOne<ApplicationVersion>().WithMany().HasForeignKey(x => x.ApplicationVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Predicates).WithOne().HasForeignKey(x => x.VisibilityConditionId).OnDelete(DeleteBehavior.Cascade);
    }
}
