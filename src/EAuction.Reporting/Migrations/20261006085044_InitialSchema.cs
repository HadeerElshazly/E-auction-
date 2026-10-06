using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Reporting.Migrations
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
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NameAr = table.Column<string>(type: "text", nullable: false),
                    NameEn = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: true),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BidderVisibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduledStartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScheduledEndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpeningPriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    MinIncrementMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    DepositMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    BookletPriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    BrokerageFeePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    PlotCount = table.Column<int>(type: "integer", nullable: false),
                    TotalAreaSqm = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EffectiveEndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExtensionsUsed = table.Column<int>(type: "integer", nullable: false),
                    BidCount = table.Column<int>(type: "integer", nullable: false),
                    WinnerBidderId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalPriceMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    AwardedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ComplianceDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CascadeStep = table.Column<int>(type: "integer", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnsoldAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction", x => x.AuctionId);
                });

            migrationBuilder.CreateTable(
                name: "auction_bidder",
                columns: table => new
                {
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayNameAr = table.Column<string>(type: "text", nullable: true),
                    BookletPaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DepositPaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaymentRefusedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaymentRefusedReason = table.Column<string>(type: "text", nullable: true),
                    EligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EligibilityEndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    DisqualifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisqualificationReason = table.Column<string>(type: "text", nullable: true),
                    DepositForfeited = table.Column<bool>(type: "boolean", nullable: false),
                    DepositHeldMinorUnits = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction_bidder", x => new { x.AuctionId, x.BidderId });
                });

            migrationBuilder.CreateTable(
                name: "plot",
                columns: table => new
                {
                    PlotId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeedNumber = table.Column<string>(type: "text", nullable: false),
                    AreaSqm = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Latitude = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Longitude = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DescriptionAr = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot", x => x.PlotId);
                });

            migrationBuilder.CreateTable(
                name: "settlement",
                columns: table => new
                {
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement", x => x.Offset);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auction_ClosedAt",
                table: "auction",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_auction_Outcome",
                table: "auction",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_auction_Phase",
                table: "auction",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "IX_auction_SettledAt",
                table: "auction",
                column: "SettledAt");

            migrationBuilder.CreateIndex(
                name: "IX_auction_bidder_BidderId",
                table: "auction_bidder",
                column: "BidderId");

            migrationBuilder.CreateIndex(
                name: "IX_auction_bidder_EligibleAt",
                table: "auction_bidder",
                column: "EligibleAt");

            migrationBuilder.CreateIndex(
                name: "IX_plot_AuctionId",
                table: "plot",
                column: "AuctionId");

            migrationBuilder.CreateIndex(
                name: "IX_plot_DeedNumber",
                table: "plot",
                column: "DeedNumber");

            migrationBuilder.CreateIndex(
                name: "IX_settlement_At",
                table: "settlement",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_settlement_AuctionId_BidderId",
                table: "settlement",
                columns: new[] { "AuctionId", "BidderId" });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_Purpose_Outcome_At",
                table: "settlement",
                columns: new[] { "Purpose", "Outcome", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auction");

            migrationBuilder.DropTable(
                name: "auction_bidder");

            migrationBuilder.DropTable(
                name: "plot");

            migrationBuilder.DropTable(
                name: "settlement");
        }
    }
}
