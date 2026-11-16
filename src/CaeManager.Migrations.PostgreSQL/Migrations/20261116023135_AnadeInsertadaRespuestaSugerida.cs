using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeInsertadaRespuestaSugerida : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Insertada",
                table: "RespuestasSugeridas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // La tabla concede UPDATE por columna a cae_app_runtime (AnadeRespuestaSugerida): Insertar() escribe esta
            // columna junto con Estado, así que necesita su propia concesión. Sin relleno de las filas anteriores: la tabla
            // fuerza RLS y una SinRevisar de antes del despliegue que luego se descarte sale como Persona (límite declarado).
            migrationBuilder.Sql("""
                GRANT UPDATE ("Insertada") ON TABLE public."RespuestasSugeridas" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE UPDATE ("Insertada") ON TABLE public."RespuestasSugeridas" FROM cae_app_runtime;
                """);

            migrationBuilder.DropColumn(
                name: "Insertada",
                table: "RespuestasSugeridas");
        }
    }
}
