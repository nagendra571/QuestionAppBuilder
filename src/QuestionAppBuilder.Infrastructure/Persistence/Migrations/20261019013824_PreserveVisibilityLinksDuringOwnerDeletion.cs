using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveVisibilityLinksDuringOwnerDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server maps both Restrict and ClientNoAction to ON DELETE NO ACTION.
            // Only EF's tracked deletion behavior changes; keep both existing FKs in place.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The SQL Server foreign keys are identical in both model versions.
        }
    }
}
