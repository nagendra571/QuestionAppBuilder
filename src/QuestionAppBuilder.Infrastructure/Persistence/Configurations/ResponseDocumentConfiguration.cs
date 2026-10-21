using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class ResponseDocumentConfiguration : IEntityTypeConfiguration<ResponseDocument>
{
    public void Configure(EntityTypeBuilder<ResponseDocument> builder)
    {
        builder.ToTable("ResponseDocuments");
        builder.HasKey(x => new { x.ResponseDraftId, x.QuestionId });
        builder.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.HasOne<ResponseDraft>().WithMany().HasForeignKey(x => x.ResponseDraftId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}
