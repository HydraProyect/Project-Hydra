using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarConfiguracionOperativaAParametroSistema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los defaultValue son los mismos que los inicializadores de campo de
            // ParametroSistema, no los ceros que genera EF por tipo: las filas de los
            // tenants ya aprovisionados (que no son la semilla del tenant #1) se
            // rellenan con este valor, y una jornada 00:00-00:00 con 0 horas al mes
            // dejaría toda entrada clasificada como Exprés y el KPI de ocupación
            // dividiendo por cero.
            migrationBuilder.AddColumn<bool>(
                name: "ExcluirFueraDeJornadaEnMetricas",
                table: "ParametrosSistema",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoraFinJornada",
                table: "ParametrosSistema",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(18, 0, 0));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoraInicioJornada",
                table: "ParametrosSistema",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(8, 0, 0));

            migrationBuilder.AddColumn<int>(
                name: "HorasJornadaMensualGestor",
                table: "ParametrosSistema",
                type: "integer",
                nullable: false,
                defaultValue: 160);

            // Apagada de fábrica: encenderla exige información previa a la plantilla
            // (art. 87 LOPDGDD). Ninguna migración la activa por nadie.
            migrationBuilder.AddColumn<bool>(
                name: "MedicionTiempoActiva",
                table: "ParametrosSistema",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SegundosInactividadPausa",
                table: "ParametrosSistema",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.UpdateData(
                table: "ParametrosSistema",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "ExcluirFueraDeJornadaEnMetricas", "HoraFinJornada", "HoraInicioJornada", "HorasJornadaMensualGestor", "MedicionTiempoActiva", "SegundosInactividadPausa" },
                values: new object[] { true, new TimeOnly(18, 0, 0), new TimeOnly(8, 0, 0), 160, false, 120 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcluirFueraDeJornadaEnMetricas",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "HoraFinJornada",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "HoraInicioJornada",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "HorasJornadaMensualGestor",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "MedicionTiempoActiva",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "SegundosInactividadPausa",
                table: "ParametrosSistema");
        }
    }
}
