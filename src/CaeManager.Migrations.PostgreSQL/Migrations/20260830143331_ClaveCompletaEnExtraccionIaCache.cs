using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ClaveCompletaEnExtraccionIaCache : Migration
    {
        /// <summary>
        /// Las filas que ya existan quedan con TipoEsperado y VersionPipeline
        /// vacios, y eso las deja fuera de toda busqueda futura — que es
        /// justamente lo que se quiere. Se escribieron bajo la clave anterior
        /// (solo hash), asi que no consta con que tipo esperado se pidio cada
        /// una: rellenarlas con un valor inventado seria afirmar algo que no
        /// sabemos, y servirlas despues como si fuera cierto. Se quedan como
        /// filas muertas hasta que exista una politica de retencion que las
        /// borre; el coste es volver a extraer esos documentos una vez.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExtraccionesIaCache_TenantId_HashSha256",
                table: "ExtraccionesIaCache");

            migrationBuilder.AddColumn<string>(
                name: "TipoEsperado",
                table: "ExtraccionesIaCache",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VersionPipeline",
                table: "ExtraccionesIaCache",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCache_TenantId_HashSha256_TipoEsperado_Versio~",
                table: "ExtraccionesIaCache",
                columns: new[] { "TenantId", "HashSha256", "TipoEsperado", "VersionPipeline" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExtraccionesIaCache_TenantId_HashSha256_TipoEsperado_Versio~",
                table: "ExtraccionesIaCache");

            migrationBuilder.DropColumn(
                name: "TipoEsperado",
                table: "ExtraccionesIaCache");

            migrationBuilder.DropColumn(
                name: "VersionPipeline",
                table: "ExtraccionesIaCache");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCache_TenantId_HashSha256",
                table: "ExtraccionesIaCache",
                columns: new[] { "TenantId", "HashSha256" },
                unique: true);
        }
    }
}
