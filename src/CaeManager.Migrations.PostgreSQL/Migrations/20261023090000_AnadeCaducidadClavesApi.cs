using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCaducidadClavesApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El DEFAULT rellena también las filas existentes bajo FORCE RLS, sin un UPDATE que
            // dependa de la visibilidad del rol migrador. Se conserva al exigir después NOT NULL:
            // en un relevo azul/verde la ranura anterior no conoce la columna e inserta claves sin
            // ella; sin DEFAULT esas inserciones fallarían. La aplicación nueva siempre la envía.
            migrationBuilder.AddColumn<DateTime>(
                name: "CaducaEnUtc",
                table: "ClavesApi",
                type: "timestamp with time zone",
                nullable: true,
                defaultValueSql: "now() + interval '30 days'");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CaducaEnUtc",
                table: "ClavesApi",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() + interval '30 days'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true,
                oldDefaultValueSql: "now() + interval '30 days'");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CaducaEnUtc", table: "ClavesApi");
        }
    }
}
