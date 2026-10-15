using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ApplicationTypeConfiguration : IEntityTypeConfiguration<ApplicationType>
{
    public void Configure(EntityTypeBuilder<ApplicationType> builder)
    {
        builder.ToTable("ApplicationTypes");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(ValidationLimits.ApplicationTypeNameMaxLength);

        builder.HasData(
            new ApplicationType { Id = 1, Name = "Request For Application (RFA)", SortOrder = 1 },
            new ApplicationType { Id = 2, Name = "Request For Information (RFI)", SortOrder = 2 });
    }
}
