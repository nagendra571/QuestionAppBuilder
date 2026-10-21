using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptionLinkedFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentOptionId",
                table: "Elements",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Elements_ParentOptionId",
                table: "Elements",
                column: "ParentOptionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Elements_QuestionOptions_ParentOptionId",
                table: "Elements",
                column: "ParentOptionId",
                principalTable: "QuestionOptions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Elements_QuestionOptions_ParentOptionId",
                table: "Elements");

            migrationBuilder.DropIndex(
                name: "IX_Elements_ParentOptionId",
                table: "Elements");

            migrationBuilder.DropColumn(
                name: "ParentOptionId",
                table: "Elements");
        }
    }
}
