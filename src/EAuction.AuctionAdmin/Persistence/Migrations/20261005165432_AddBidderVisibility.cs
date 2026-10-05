using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBidderVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BidderVisibility",
                table: "auction",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BidderVisibility",
                table: "auction");
        }
    }
}
