using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class VencimientoAnualDocumentosVehiculo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { true, "Vigencia anual.", 12 });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { true, "Vigencia anual.", 12 });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { true, "Vigencia anual.", 12 });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { true, "Vigencia anual.", 12 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { false, "Vigencia sin especificar — fecha de vencimiento manual.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { false, "No caduca por sí sola, pero se pide como documento adjunto del vehículo.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { false, "Vigencia sin especificar — fecha de vencimiento manual.", null });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                columns: new[] { "AplicaVencimientoAutomatico", "Notas", "VigenciaMeses" },
                values: new object[] { false, "Vigencia sin especificar — fecha de vencimiento manual.", null });
        }
    }
}
