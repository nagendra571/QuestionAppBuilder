using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuestionAppBuilder.Infrastructure.Persistence;

public class QuestionAppBuilderDbContextFactory : IDesignTimeDbContextFactory<QuestionAppBuilderDbContext>
{
    public QuestionAppBuilderDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
            .UseSqlServer("Server=(localdb)\\QuestionAppBuilderDesignTime;Database=QuestionAppBuilder")
            .Options;

        return new QuestionAppBuilderDbContext(options);
    }
}
