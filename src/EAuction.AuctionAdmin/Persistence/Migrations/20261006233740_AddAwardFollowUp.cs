using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAwardFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TransferCompletedAt",
                table: "award",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TransferDocumentId",
                table: "award",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransferReference",
                table: "award",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransferStatus",
                table: "award",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TransferUpdatedAt",
                table: "award",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipts",
                table: "award",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransferCompletedAt",
                table: "award");

            migrationBuilder.DropColumn(
                name: "TransferDocumentId",
                table: "award");

            migrationBuilder.DropColumn(
                name: "TransferReference",
                table: "award");

            migrationBuilder.DropColumn(
                name: "TransferStatus",
                table: "award");

            migrationBuilder.DropColumn(
                name: "TransferUpdatedAt",
                table: "award");

            migrationBuilder.DropColumn(
                name: "receipts",
                table: "award");
        }
    }
}
