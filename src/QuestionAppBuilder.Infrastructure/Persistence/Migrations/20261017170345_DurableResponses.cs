using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionAppBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_ApplicationId",
                table: "Submissions");

            migrationBuilder.AddColumn<string>(
                name: "EngineVersion",
                table: "Submissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy-v1");

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "Submissions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResponseId",
                table: "Submissions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ResponseDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    ApplicationVersionId = table.Column<int>(type: "int", nullable: false),
                    OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    RevisionNonce = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResponseDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResponseDrafts_ApplicationVersions_ApplicationVersionId",
                        column: x => x.ApplicationVersionId,
                        principalTable: "ApplicationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResponseDrafts_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionQuestionStates",
                columns: table => new
                {
                    SubmissionId = table.Column<int>(type: "int", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    QuestionLogicalKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsApplicable = table.Column<bool>(type: "bit", nullable: false),
                    PointsEarned = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionQuestionStates", x => new { x.SubmissionId, x.QuestionId });
                    table.ForeignKey(
                        name: "FK_SubmissionQuestionStates_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionQuestionStates_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionRuleTraces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubmissionId = table.Column<int>(type: "int", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    Matches = table.Column<bool>(type: "bit", nullable: false),
                    Contributes = table.Column<bool>(type: "bit", nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionRuleTraces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionRuleTraces_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionRuleTraces_ScoringRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "ScoringRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionRuleTraces_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResponseAccessTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenDigest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    ApplicationVersionId = table.Column<int>(type: "int", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    VerifiedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResponseAccessTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResponseAccessTokens_ApplicationVersions_ApplicationVersionId",
                        column: x => x.ApplicationVersionId,
                        principalTable: "ApplicationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResponseAccessTokens_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResponseAccessTokens_ResponseDrafts_ResponseId",
                        column: x => x.ResponseId,
                        principalTable: "ResponseDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResponseAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResponseDraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    TextValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    NumberValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResponseAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResponseAnswers_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResponseAnswers_ResponseDrafts_ResponseDraftId",
                        column: x => x.ResponseDraftId,
                        principalTable: "ResponseDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResponseSelectedOptions",
                columns: table => new
                {
                    ResponseAnswerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionOptionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResponseSelectedOptions", x => new { x.ResponseAnswerId, x.QuestionOptionId });
                    table.ForeignKey(
                        name: "FK_ResponseSelectedOptions_QuestionOptions_QuestionOptionId",
                        column: x => x.QuestionOptionId,
                        principalTable: "QuestionOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResponseSelectedOptions_ResponseAnswers_ResponseAnswerId",
                        column: x => x.ResponseAnswerId,
                        principalTable: "ResponseAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ApplicationId_RequestId",
                table: "Submissions",
                columns: new[] { "ApplicationId", "RequestId" },
                unique: true,
                filter: "[RequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ResponseId",
                table: "Submissions",
                column: "ResponseId",
                unique: true,
                filter: "[ResponseId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseAccessTokens_ApplicationId_VerifiedEmail_CreatedUtc",
                table: "ResponseAccessTokens",
                columns: new[] { "ApplicationId", "VerifiedEmail", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResponseAccessTokens_ApplicationVersionId",
                table: "ResponseAccessTokens",
                column: "ApplicationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseAccessTokens_ResponseId",
                table: "ResponseAccessTokens",
                column: "ResponseId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseAccessTokens_TokenDigest",
                table: "ResponseAccessTokens",
                column: "TokenDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResponseAnswers_QuestionId",
                table: "ResponseAnswers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseAnswers_ResponseDraftId_QuestionId",
                table: "ResponseAnswers",
                columns: new[] { "ResponseDraftId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResponseDrafts_ApplicationId_ApplicationVersionId",
                table: "ResponseDrafts",
                columns: new[] { "ApplicationId", "ApplicationVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResponseDrafts_ApplicationVersionId",
                table: "ResponseDrafts",
                column: "ApplicationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseDrafts_OwnerKey_Status_UpdatedUtc",
                table: "ResponseDrafts",
                columns: new[] { "OwnerKey", "Status", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResponseSelectedOptions_QuestionOptionId",
                table: "ResponseSelectedOptions",
                column: "QuestionOptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionQuestionStates_QuestionId",
                table: "SubmissionQuestionStates",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionRuleTraces_QuestionId",
                table: "SubmissionRuleTraces",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionRuleTraces_RuleId",
                table: "SubmissionRuleTraces",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionRuleTraces_SubmissionId_QuestionId_RuleId",
                table: "SubmissionRuleTraces",
                columns: new[] { "SubmissionId", "QuestionId", "RuleId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_ResponseDrafts_ResponseId",
                table: "Submissions",
                column: "ResponseId",
                principalTable: "ResponseDrafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_ResponseDrafts_ResponseId",
                table: "Submissions");

            migrationBuilder.DropTable(
                name: "ResponseAccessTokens");

            migrationBuilder.DropTable(
                name: "ResponseSelectedOptions");

            migrationBuilder.DropTable(
                name: "SubmissionQuestionStates");

            migrationBuilder.DropTable(
                name: "SubmissionRuleTraces");

            migrationBuilder.DropTable(
                name: "ResponseAnswers");

            migrationBuilder.DropTable(
                name: "ResponseDrafts");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_ApplicationId_RequestId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_ResponseId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "EngineVersion",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "ResponseId",
                table: "Submissions");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ApplicationId",
                table: "Submissions",
                column: "ApplicationId");
        }
    }
}
