using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EAuction.Audit.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClientBidId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_event", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_system_event_AuctionId_At",
                table: "system_event",
                columns: new[] { "AuctionId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_system_event_ClientBidId",
                table: "system_event",
                column: "ClientBidId");

            migrationBuilder.CreateIndex(
                name: "IX_system_event_Kind_At",
                table: "system_event",
                columns: new[] { "Kind", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_system_event_Topic_Key_Offset",
                table: "system_event",
                columns: new[] { "Topic", "Key", "Offset" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_event");
        }
    }
}
