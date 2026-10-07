using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWinnerAward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "winner_award",
                columns: table => new
                {
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AwardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WinnerBidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    BrokerageMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ComplianceDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SignedLetterDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    WinnerNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    RemainingMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    TransferStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransferCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisqualifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_winner_award", x => x.AuctionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_winner_award_WinnerBidderId",
                table: "winner_award",
                column: "WinnerBidderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "winner_award");
        }
    }
}
