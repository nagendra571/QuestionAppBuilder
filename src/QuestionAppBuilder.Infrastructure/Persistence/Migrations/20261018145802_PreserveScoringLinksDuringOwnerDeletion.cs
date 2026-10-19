using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveScoringLinksDuringOwnerDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server maps both Restrict and ClientNoAction to ON DELETE NO ACTION.
            // Only EF's tracked deletion behavior changes; retain the existing FK to
            // avoid an unnecessary schema lock and constraint revalidation.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The SQL Server foreign key is identical in both model versions.
        }
    }
}
