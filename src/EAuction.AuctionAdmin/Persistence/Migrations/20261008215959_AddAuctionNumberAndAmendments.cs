using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EAuction.AuctionAdmin.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionNumberAndAmendments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AmendedAt",
                table: "auction",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Amendment",
                table: "auction",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // رقم المزاد. An identity column, not a default: PostgreSQL refuses a column
            // that is both, and the identity is what numbers the rows already there.
            migrationBuilder.AddColumn<long>(
                name: "Number",
                table: "auction",
                type: "bigint",
                nullable: false)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmittedAt",
                table: "auction",
                type: "timestamp with time zone",
                nullable: true);

            // The identity numbers existing rows in whatever order the table holds
            // them. Renumbered by creation, so the first auction prepared is number 1
            // and the sequence carries on from the last — before the unique index,
            // which a renumbering could trip part-way through.
            migrationBuilder.Sql("""
                UPDATE auction AS a
                SET "Number" = numbered.n
                FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS n
                    FROM auction
                ) AS numbered
                WHERE numbered."Id" = a."Id";

                SELECT setval(
                    pg_get_serial_sequence('auction', 'Number'),
                    COALESCE((SELECT MAX("Number") FROM auction), 0) + 1,
                    false);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_auction_Number",
                table: "auction",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_auction_Number",
                table: "auction");

            migrationBuilder.DropColumn(
                name: "AmendedAt",
                table: "auction");

            migrationBuilder.DropColumn(
                name: "Amendment",
                table: "auction");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "auction");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                table: "auction");
        }
    }
}
