using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Participant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionNameAndStage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NameAr",
                table: "auction_terms",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Stage",
                table: "auction_terms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_auction_terms_Stage",
                table: "auction_terms",
                column: "Stage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_auction_terms_Stage",
                table: "auction_terms");

            migrationBuilder.DropColumn(
                name: "NameAr",
                table: "auction_terms");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "auction_terms");
        }
    }
}
