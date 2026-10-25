using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class PreviewScenarioConfiguration : IEntityTypeConfiguration<PreviewScenario>
{
    public void Configure(EntityTypeBuilder<PreviewScenario> builder)
    {
        builder.ToTable("PreviewScenarios");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PayloadJson).IsRequired();
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.ApplicationId, x.Name });
        builder.HasOne<ApplicationEntity>().WithMany().HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
