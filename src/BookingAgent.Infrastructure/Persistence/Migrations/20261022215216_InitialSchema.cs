using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BookingAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "businesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    slot_granularity = table.Column<Duration>(type: "interval", nullable: false),
                    minimum_notice = table.Column<Duration>(type: "interval", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_businesses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.ForeignKey(
                        name: "fk_customers_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "professionals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    specialty = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    services = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_professionals", x => x.id);
                    table.ForeignKey(
                        name: "fk_professionals_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    duration = table.Column<Duration>(type: "interval", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_services", x => x.id);
                    table.ForeignKey(
                        name: "fk_services_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "time_off",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<Interval>(type: "tstzrange", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_off", x => x.id);
                    table.ForeignKey(
                        name: "fk_time_off_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "working_hours",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    day = table.Column<int>(type: "integer", nullable: false),
                    start = table.Column<LocalTime>(type: "time", nullable: false),
                    end = table.Column<LocalTime>(type: "time", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_working_hours", x => x.id);
                    table.ForeignKey(
                        name: "fk_working_hours_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<Interval>(type: "tstzrange", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    hold_expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    confirmation_token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    confirmed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    replaces = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bookings", x => x.id);
                    table.ForeignKey(
                        name: "fk_bookings_bookings_replaces",
                        column: x => x.replaces,
                        principalTable: "bookings",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_bookings_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_bookings_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_bookings_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bookings_customer_id",
                table: "bookings",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_live_holds",
                table: "bookings",
                column: "hold_expires_at",
                filter: "status = 'Held'");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_professional_id",
                table: "bookings",
                column: "professional_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_replaces",
                table: "bookings",
                column: "replaces");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_service_id",
                table: "bookings",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_customers_business_id_email",
                table: "customers",
                columns: new[] { "business_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_professionals_business_id",
                table: "professionals",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "ix_services_business_id",
                table: "services",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "ix_time_off_professional_id",
                table: "time_off",
                column: "professional_id");

            migrationBuilder.CreateIndex(
                name: "ix_working_hours_professional_id",
                table: "working_hours",
                column: "professional_id");

            // No double booking, decided by Postgres: two bookings of one professional that take their slot (held or
            // confirmed) cannot overlap. tstzrange is half-open, so back-to-back appointments are fine. EF Core does
            // not model exclusion constraints, hence the SQL; btree_gist (created above) lets uuid equality join the
            // range overlap in one GiST index. The status literals are BookingStatus's names, as stored.
            migrationBuilder.Sql("""
                ALTER TABLE bookings ADD CONSTRAINT ex_bookings_no_overlap
                EXCLUDE USING gist (professional_id WITH =, period WITH &&)
                WHERE (status IN ('Held', 'Confirmed'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE bookings DROP CONSTRAINT IF EXISTS ex_bookings_no_overlap;");

            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "time_off");

            migrationBuilder.DropTable(
                name: "working_hours");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "services");

            migrationBuilder.DropTable(
                name: "professionals");

            migrationBuilder.DropTable(
                name: "businesses");
        }
    }
}
