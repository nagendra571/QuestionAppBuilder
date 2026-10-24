using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubmittedApplicationVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmittedApplicationVisibility",
                table: "ApplicationVersions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "WithoutScores");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubmittedApplicationVisibility",
                table: "ApplicationVersions");
        }
    }
}
