using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VersionedDefinitionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScoringRules_QuestionId",
                table: "ScoringRules");

            migrationBuilder.DropIndex(
                name: "IX_QuestionOptions_QuestionId",
                table: "QuestionOptions");

            migrationBuilder.DropIndex(
                name: "IX_Elements_CategoryId",
                table: "Elements");

            migrationBuilder.DropIndex(
                name: "IX_Categories_ApplicationVersionId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "IsIndependent",
                table: "Categories");

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalKey",
                table: "ScoringRules",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<bool>(
                name: "MaxInclusive",
                table: "ScoringRules",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "MinInclusive",
                table: "ScoringRules",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ScoringRules",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxLength",
                table: "Questions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxSelections",
                table: "Questions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxValue",
                table: "Questions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinSelections",
                table: "Questions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinValue",
                table: "Questions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScoringMode",
                table: "Questions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Additive");

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalKey",
                table: "QuestionOptions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalKey",
                table: "Elements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<int>(
                name: "VisibilityConditionId",
                table: "Elements",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalKey",
                table: "Categories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<int>(
                name: "VisibilityConditionId",
                table: "Categories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationText",
                table: "ApplicationVersions",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "ApplicationVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE v SET DisplayName = a.Name FROM ApplicationVersions v INNER JOIN Applications a ON a.Id = v.ApplicationId");

            migrationBuilder.AddColumn<string>(
                name: "NavigationMode",
                table: "ApplicationVersions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Sequential");

            migrationBuilder.AddColumn<string>(
                name: "RespondentAccessMode",
                table: "ApplicationVersions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "HostSignIn");

            migrationBuilder.AddColumn<string>(
                name: "ResultVisibility",
                table: "ApplicationVersions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Total");

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionNonce",
                table: "ApplicationVersions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ApplicationVersions",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "SubmitLabel",
                table: "ApplicationVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AcceptNewResponses",
                table: "Applications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AcceptSubmissions",
                table: "Applications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE a SET AcceptNewResponses = 1, AcceptSubmissions = 1 FROM Applications a WHERE a.CurrentVersionId IS NOT NULL");

            migrationBuilder.CreateTable(
                name: "ApplicationAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    ApplicationVersionId = table.Column<int>(type: "int", nullable: true),
                    ActorKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VisibilityConditions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationVersionId = table.Column<int>(type: "int", nullable: false),
                    Junction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisibilityConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisibilityConditions_ApplicationVersions_ApplicationVersionId",
                        column: x => x.ApplicationVersionId,
                        principalTable: "ApplicationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VisibilityPredicates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VisibilityConditionId = table.Column<int>(type: "int", nullable: false),
                    SourceQuestionId = table.Column<int>(type: "int", nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NumberValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisibilityPredicates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisibilityPredicates_Questions_SourceQuestionId",
                        column: x => x.SourceQuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisibilityPredicates_VisibilityConditions_VisibilityConditionId",
                        column: x => x.VisibilityConditionId,
                        principalTable: "VisibilityConditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VisibilityPredicateOptions",
                columns: table => new
                {
                    VisibilityPredicateId = table.Column<int>(type: "int", nullable: false),
                    QuestionOptionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisibilityPredicateOptions", x => new { x.VisibilityPredicateId, x.QuestionOptionId });
                    table.ForeignKey(
                        name: "FK_VisibilityPredicateOptions_QuestionOptions_QuestionOptionId",
                        column: x => x.QuestionOptionId,
                        principalTable: "QuestionOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisibilityPredicateOptions_VisibilityPredicates_VisibilityPredicateId",
                        column: x => x.VisibilityPredicateId,
                        principalTable: "VisibilityPredicates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_QuestionId_LogicalKey",
                table: "ScoringRules",
                columns: new[] { "QuestionId", "LogicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionOptions_QuestionId_LogicalKey",
                table: "QuestionOptions",
                columns: new[] { "QuestionId", "LogicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Elements_CategoryId_LogicalKey",
                table: "Elements",
                columns: new[] { "CategoryId", "LogicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Elements_VisibilityConditionId",
                table: "Elements",
                column: "VisibilityConditionId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ApplicationVersionId_LogicalKey",
                table: "Categories",
                columns: new[] { "ApplicationVersionId", "LogicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_VisibilityConditionId",
                table: "Categories",
                column: "VisibilityConditionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationVersions_ApplicationId",
                table: "ApplicationVersions",
                column: "ApplicationId",
                unique: true,
                filter: "[Status] = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAuditEvents_ApplicationId_OccurredUtc",
                table: "ApplicationAuditEvents",
                columns: new[] { "ApplicationId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VisibilityConditions_ApplicationVersionId",
                table: "VisibilityConditions",
                column: "ApplicationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_VisibilityPredicateOptions_QuestionOptionId",
                table: "VisibilityPredicateOptions",
                column: "QuestionOptionId");

            migrationBuilder.CreateIndex(
                name: "IX_VisibilityPredicates_SourceQuestionId",
                table: "VisibilityPredicates",
                column: "SourceQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_VisibilityPredicates_VisibilityConditionId",
                table: "VisibilityPredicates",
                column: "VisibilityConditionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_VisibilityConditions_VisibilityConditionId",
                table: "Categories",
                column: "VisibilityConditionId",
                principalTable: "VisibilityConditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Elements_VisibilityConditions_VisibilityConditionId",
                table: "Elements",
                column: "VisibilityConditionId",
                principalTable: "VisibilityConditions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Categories_VisibilityConditions_VisibilityConditionId",
                table: "Categories");

            migrationBuilder.DropForeignKey(
                name: "FK_Elements_VisibilityConditions_VisibilityConditionId",
                table: "Elements");

            migrationBuilder.DropTable(
                name: "ApplicationAuditEvents");

            migrationBuilder.DropTable(
                name: "VisibilityPredicateOptions");

            migrationBuilder.DropTable(
                name: "VisibilityPredicates");

            migrationBuilder.DropTable(
                name: "VisibilityConditions");

            migrationBuilder.DropIndex(
                name: "IX_ScoringRules_QuestionId_LogicalKey",
                table: "ScoringRules");

            migrationBuilder.DropIndex(
                name: "IX_QuestionOptions_QuestionId_LogicalKey",
                table: "QuestionOptions");

            migrationBuilder.DropIndex(
                name: "IX_Elements_CategoryId_LogicalKey",
                table: "Elements");

            migrationBuilder.DropIndex(
                name: "IX_Elements_VisibilityConditionId",
                table: "Elements");

            migrationBuilder.DropIndex(
                name: "IX_Categories_ApplicationVersionId_LogicalKey",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_VisibilityConditionId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationVersions_ApplicationId",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "LogicalKey",
                table: "ScoringRules");

            migrationBuilder.DropColumn(
                name: "MaxInclusive",
                table: "ScoringRules");

            migrationBuilder.DropColumn(
                name: "MinInclusive",
                table: "ScoringRules");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ScoringRules");

            migrationBuilder.DropColumn(
                name: "MaxLength",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MaxSelections",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MaxValue",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MinSelections",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MinValue",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "ScoringMode",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "LogicalKey",
                table: "QuestionOptions");

            migrationBuilder.DropColumn(
                name: "LogicalKey",
                table: "Elements");

            migrationBuilder.DropColumn(
                name: "VisibilityConditionId",
                table: "Elements");

            migrationBuilder.DropColumn(
                name: "LogicalKey",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "VisibilityConditionId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "ConfirmationText",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "NavigationMode",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "RespondentAccessMode",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "ResultVisibility",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "RevisionNonce",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "SubmitLabel",
                table: "ApplicationVersions");

            migrationBuilder.DropColumn(
                name: "AcceptNewResponses",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "AcceptSubmissions",
                table: "Applications");

            migrationBuilder.AddColumn<bool>(
                name: "IsIndependent",
                table: "Categories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_QuestionId",
                table: "ScoringRules",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestionOptions_QuestionId",
                table: "QuestionOptions",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_Elements_CategoryId",
                table: "Elements",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ApplicationVersionId",
                table: "Categories",
                column: "ApplicationVersionId");
        }
    }
}

