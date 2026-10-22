using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PointsPerUnitScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AwardMethod",
                table: "ScoringRules",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Fixed");

            migrationBuilder.AddColumn<int>(
                name: "MaximumPoints",
                table: "ScoringRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PointsPerUnit",
                table: "ScoringRules",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AwardMethod",
                table: "ScoringRules");

            migrationBuilder.DropColumn(
                name: "MaximumPoints",
                table: "ScoringRules");

            migrationBuilder.DropColumn(
                name: "PointsPerUnit",
                table: "ScoringRules");
        }
    }
}
