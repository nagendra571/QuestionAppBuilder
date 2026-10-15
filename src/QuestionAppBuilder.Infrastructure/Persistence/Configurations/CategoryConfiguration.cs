using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(ValidationLimits.NameMaxLength);

        builder.HasOne<ApplicationVersion>()
            .WithMany(v => v.Categories)
            .HasForeignKey(c => c.ApplicationVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
