using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Phase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NameAr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OpeningPriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    ReservePriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    MinIncrementMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    DepositMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    BrokerageFeePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    BookletPriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    QuietPeriodSeconds = table.Column<int>(type: "integer", nullable: true),
                    MaxExtensions = table.Column<int>(type: "integer", nullable: false),
                    BlindRoundEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    BlindDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    MaxBlindRounds = table.Column<int>(type: "integer", nullable: true),
                    BookletDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoverImageDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PendingCandidateBidderId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingCandidateAmountMinorUnits = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregatetype = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregateid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    relayed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "award",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    CascadeStep = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComplianceDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LetterDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SignedLetterDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    WinnerNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisqualifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisqualificationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DepositForfeited = table.Column<bool>(type: "boolean", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_award", x => x.Id);
                    table.ForeignKey(
                        name: "FK_award_auction_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "auction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeedNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AreaSqm = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Latitude = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Longitude = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DescriptionAr = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DescriptionEn = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plot_auction_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "auction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auction_Phase",
                table: "auction",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "IX_auction_Status",
                table: "auction",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_award_AuctionId_CascadeStep",
                table: "award",
                columns: new[] { "AuctionId", "CascadeStep" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_relayed_at_created_at",
                table: "outbox",
                columns: new[] { "relayed_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_plot_AuctionId_DeedNumber",
                table: "plot",
                columns: new[] { "AuctionId", "DeedNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "award");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "plot");

            migrationBuilder.DropTable(
                name: "auction");
        }
    }
}
