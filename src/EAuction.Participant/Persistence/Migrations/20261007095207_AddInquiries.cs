using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInquiries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inquiry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnsweredBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Clarification = table.Column<int>(type: "integer", nullable: false),
                    ClarificationQuestion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ClarificationAnswer = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ClarificationDraftedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ClarificationDraftedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClarificationApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ClarificationPublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inquiry", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inquiry_AuctionId_AskedAt",
                table: "inquiry",
                columns: new[] { "AuctionId", "AskedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_inquiry_BidderId",
                table: "inquiry",
                column: "BidderId");

            migrationBuilder.CreateIndex(
                name: "IX_inquiry_Status",
                table: "inquiry",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inquiry");
        }
    }
}
