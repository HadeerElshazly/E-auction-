using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auction_terms",
                columns: table => new
                {
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DepositMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    BookletPriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction_terms", x => x.AuctionId);
                });

            migrationBuilder.CreateTable(
                name: "bidder",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NationalId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NameAr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NafathVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProfileCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bidder", x => x.Id);
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
                name: "subscription",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    BookletPurchasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BookletPaymentRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TermsAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DepositMethod = table.Column<int>(type: "integer", nullable: true),
                    DepositPaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DepositPaymentRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GuaranteeDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuaranteeExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GuaranteeVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GuaranteeVerifiedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    KeyEpoch = table.Column<int>(type: "integer", nullable: false),
                    EligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DepositResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DepositForfeited = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bidder_NationalId",
                table: "bidder",
                column: "NationalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_relayed_at_created_at",
                table: "outbox",
                columns: new[] { "relayed_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_subscription_AuctionId_BidderId",
                table: "subscription",
                columns: new[] { "AuctionId", "BidderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscription_Status",
                table: "subscription",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auction_terms");

            migrationBuilder.DropTable(
                name: "bidder");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "subscription");
        }
    }
}
