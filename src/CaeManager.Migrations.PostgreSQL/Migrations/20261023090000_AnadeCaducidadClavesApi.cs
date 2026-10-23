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
            // dependa de la visibilidad del rol migrador. Un DEFAULT se conserva al exigir después NOT NULL:
            // en un relevo azul/verde la ranura anterior no conoce la columna e inserta claves sin
            // ella; sin DEFAULT esas inserciones fallarían. La aplicación nueva siempre la envía.
            //
            // El DEFAULT del ADD COLUMN es volátil (clock_timestamp) a propósito: con uno estable
            // como now(), PostgreSQL no reescribe la tabla y guarda el instante de la migración como
            // valor ausente del catálogo (pg_attribute.attmissingval). Ese valor sería distinto en
            // cada base migrada —la plantilla de los tests y una base migrada después no
            // coincidirían— y quedaría en el catálogo para siempre. Con uno volátil la tabla se
            // reescribe y cada fila existente recibe su valor en ella, sin UPDATE y sin RLS de por
            // medio. Después se deja el DEFAULT estable que declara el modelo.
            migrationBuilder.AddColumn<DateTime>(
                name: "CaducaEnUtc",
                table: "ClavesApi",
                type: "timestamp with time zone",
                nullable: true,
                defaultValueSql: "clock_timestamp() + interval '30 days'");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CaducaEnUtc",
                table: "ClavesApi",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() + interval '30 days'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true,
                oldDefaultValueSql: "clock_timestamp() + interval '30 days'");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CaducaEnUtc", table: "ClavesApi");
        }
    }
}
