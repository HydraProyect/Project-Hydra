using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Las acreditaciones que ya existen se quedan en EstadoVigencia = 0
    /// (SinConfirmar), incluidas las que están Aceptada. Es deliberado: nadie ha
    /// anotado nunca hasta cuándo valen en su plataforma, y darlas por "no
    /// caduca" sería inventarse el dato que esta migración existe para poder
    /// registrar. Aparecerán como pendientes de confirmar hasta que un Gestor
    /// CAE entre a la plataforma y lo diga.
    /// </summary>
    public partial class VigenciaDeAcreditacionPorPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EstadoVigencia",
                table: "AcreditacionesDocumentoPlataforma",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FechaVencimientoEnPlataforma",
                table: "AcreditacionesDocumentoPlataforma",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EstadoVigencia",
                table: "AcreditacionesDocumentoPlataforma");

            migrationBuilder.DropColumn(
                name: "FechaVencimientoEnPlataforma",
                table: "AcreditacionesDocumentoPlataforma");
        }
    }
}
