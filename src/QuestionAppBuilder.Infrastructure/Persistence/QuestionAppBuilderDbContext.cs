using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Persistence;

public class QuestionAppBuilderDbContext : DbContext
{
    public QuestionAppBuilderDbContext(DbContextOptions<QuestionAppBuilderDbContext> options)
        : base(options)
    {
    }

    public DbSet<ApplicationType> ApplicationTypes => Set<ApplicationType>();
    public DbSet<ApplicationEntity> Applications => Set<ApplicationEntity>();
    public DbSet<ApplicationVersion> ApplicationVersions => Set<ApplicationVersion>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Element> Elements => Set<Element>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<ScoringRule> ScoringRules => Set<ScoringRule>();
    public DbSet<ScoringRuleOption> ScoringRuleOptions => Set<ScoringRuleOption>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<AnswerSelectedOption> AnswerSelectedOptions => Set<AnswerSelectedOption>();
    public DbSet<SubmissionCategoryScore> SubmissionCategoryScores => Set<SubmissionCategoryScore>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(QuestionAppBuilderDbContext).Assembly);
    }
}
