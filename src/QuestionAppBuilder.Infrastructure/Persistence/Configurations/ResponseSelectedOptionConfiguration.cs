using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class ResponseSelectedOptionConfiguration : IEntityTypeConfiguration<ResponseSelectedOption>
{
    public void Configure(EntityTypeBuilder<ResponseSelectedOption> builder)
    {
        builder.ToTable("ResponseSelectedOptions");
        builder.HasKey(x => new { x.ResponseAnswerId, x.QuestionOptionId });
        builder.HasOne<QuestionOption>().WithMany().HasForeignKey(x => x.QuestionOptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
