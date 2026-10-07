using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenamePlotNumberAddStreetAndFrontage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DeedNumber",
                table: "plot",
                newName: "PlotNumber");

            migrationBuilder.RenameIndex(
                name: "IX_plot_AuctionId_DeedNumber",
                table: "plot",
                newName: "IX_plot_AuctionId_PlotNumber");

            migrationBuilder.AddColumn<decimal>(
                name: "FrontageMeters",
                table: "plot",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StreetWidthMeters",
                table: "plot",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FrontageMeters",
                table: "plot");

            migrationBuilder.DropColumn(
                name: "StreetWidthMeters",
                table: "plot");

            migrationBuilder.RenameColumn(
                name: "PlotNumber",
                table: "plot",
                newName: "DeedNumber");

            migrationBuilder.RenameIndex(
                name: "IX_plot_AuctionId_PlotNumber",
                table: "plot",
                newName: "IX_plot_AuctionId_DeedNumber");
        }
    }
}
