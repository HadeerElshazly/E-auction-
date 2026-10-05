using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionClerk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClerkKeyEpoch",
                table: "auction",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ClerkUserId",
                table: "auction",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClerkKeyEpoch",
                table: "auction");

            migrationBuilder.DropColumn(
                name: "ClerkUserId",
                table: "auction");
        }
    }
}
