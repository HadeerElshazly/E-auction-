using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepositClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DepositAppliedToPurchase",
                table: "subscription",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DepositClosedAt",
                table: "subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepositClosedByUserId",
                table: "subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DepositClosureReference",
                table: "subscription",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepositAppliedToPurchase",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "DepositClosedAt",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "DepositClosedByUserId",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "DepositClosureReference",
                table: "subscription");
        }
    }
}
