using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlotFacing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Facing",
                table: "plot",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Facing",
                table: "plot");
        }
    }
}
