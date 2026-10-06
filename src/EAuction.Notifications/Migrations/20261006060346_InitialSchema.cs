using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auction_name",
                columns: table => new
                {
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NameAr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction_name", x => x.AuctionId);
                });

            migrationBuilder.CreateTable(
                name: "audience",
                columns: table => new
                {
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Eligible = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audience", x => new { x.AuctionId, x.BidderId });
                });

            migrationBuilder.CreateTable(
                name: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Dedup = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TitleAr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BodyAr = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Actionable = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "topic_watermark",
                columns: table => new
                {
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_topic_watermark", x => x.Topic);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_BidderId_AuctionId_Kind_Dedup",
                table: "notification",
                columns: new[] { "BidderId", "AuctionId", "Kind", "Dedup" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_BidderId_ReadAt_CreatedAt",
                table: "notification",
                columns: new[] { "BidderId", "ReadAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auction_name");

            migrationBuilder.DropTable(
                name: "audience");

            migrationBuilder.DropTable(
                name: "notification");

            migrationBuilder.DropTable(
                name: "topic_watermark");
        }
    }
}
