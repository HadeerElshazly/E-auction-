using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTermsVersionAndGuaranteeReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedBookletDocumentId",
                table: "subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GuaranteeRejectedAt",
                table: "subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuaranteeRejectionReason",
                table: "subscription",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptedBookletDocumentId",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "GuaranteeRejectedAt",
                table: "subscription");

            migrationBuilder.DropColumn(
                name: "GuaranteeRejectionReason",
                table: "subscription");
        }
    }
}
