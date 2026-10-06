using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentSettlementState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BookletRequestedAt",
                table: "subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DepositRequestedAt",
                table: "subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentFailedAt",
                table: "subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentFailurePurpose",
                table: "subscription",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentFailureReason",
                table: "subscription",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BookletRequestedAt",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "DepositRequestedAt",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "PaymentFailedAt",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "PaymentFailurePurpose",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "PaymentFailureReason",
                table: "subscription");
        }
    }
}
