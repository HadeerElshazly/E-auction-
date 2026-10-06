using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAuction.Audit.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entry",
                columns: table => new
                {
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    ActorSubject = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorRoles = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: true),
                    Subject = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    SourceAddress = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Malformed = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PreviousHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entry", x => x.Offset);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entry_Action",
                table: "audit_entry",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entry_ActorSubject_At",
                table: "audit_entry",
                columns: new[] { "ActorSubject", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entry_At",
                table: "audit_entry",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entry_Subject_At",
                table: "audit_entry",
                columns: new[] { "Subject", "At" });

            // Append-only, enforced by the database rather than promised by the
            // service.
            //
            // The service has no endpoint that updates or deletes an entry, which
            // is most of the protection and all of it that the code can offer. This
            // covers what the code cannot: somebody at a psql prompt with the
            // service's own credentials. A hash chain already makes an alteration
            // detectable afterwards; this makes the ordinary way of performing one
            // fail at the time.
            //
            // It is not a claim that the trail cannot be altered. A superuser can
            // drop this trigger, and nothing in a database the operator controls can
            // stop the operator. What it buys is that tampering is no longer a
            // single UPDATE: it needs a privileged, deliberate, separately
            // auditable act — and the chain still shows it afterwards.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_entry_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION
                        'audit_entry is append-only; % is not permitted on it', TG_OP;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER audit_entry_append_only
                    BEFORE UPDATE OR DELETE ON audit_entry
                    FOR EACH ROW EXECUTE FUNCTION audit_entry_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_entry_append_only ON audit_entry;");

            migrationBuilder.DropTable(
                name: "audit_entry");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_entry_append_only();");
        }
    }
}
