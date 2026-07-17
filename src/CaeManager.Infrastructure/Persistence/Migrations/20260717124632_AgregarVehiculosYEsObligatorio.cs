using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarVehiculosYEsObligatorio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsObligatorio",
                table: "TiposDocumento",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "VehiculoId",
                table: "Documentos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Vehiculos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubcontrataId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Modelo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NumeroPlaca = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehiculos", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "EsObligatorio", "Nombre", "Notas", "Observaciones", "Orden", "SeSolicitaA", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), "Empresa", true, null, null, true, "Certificado de estar al corriente con la Seguridad Social", "Mensual.", null, 16, null, 1 },
                    { new Guid("20000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, true, "Certificado de estar al corriente con Hacienda", "Vigencia variable (1, 3, 6 o 12 meses según lo que exija el cliente) — la fecha de vencimiento se introduce a mano al subir el documento.", null, 17, null, null },
                    { new Guid("20000000-0000-0000-0000-000000000003"), "Empresa", true, null, null, true, "ITA", "Mensual.", null, 18, null, 1 },
                    { new Guid("20000000-0000-0000-0000-000000000004"), "Empresa", true, null, null, true, "RLC/TC1", "Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada.", null, 19, null, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000005"), "Empresa", true, null, null, true, "Recibo de pago RLC/TC1", "Mismo criterio de vigencia que el RLC/TC1.", null, 20, null, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000006"), "Empresa", true, null, null, true, "RLC/TC1 + Recibo de pago", "Variante combinada — mismo criterio de vigencia que el RLC/TC1.", null, 21, null, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000007"), "Empresa", true, null, null, true, "RNT/TC2", "Mismo criterio que el RLC/TC1.", null, 22, null, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000008"), "Empresa", false, null, null, true, "Mutua", "Vigencia sin especificar — fecha de vencimiento manual.", null, 23, null, null },
                    { new Guid("20000000-0000-0000-0000-000000000009"), "Empresa", false, null, null, true, "Seguro de Responsabilidad Civil + recibo de pago", "Vigencia sin especificar — fecha de vencimiento manual.", null, 24, null, null },
                    { new Guid("2000000a-0000-0000-0000-00000000000a"), "Empresa", false, null, null, true, "SPA (Servicio de Prevención Ajeno)", "Debe venir acompañado de un certificado de pago que indica la fecha fin de validez — se introduce esa fecha manualmente.", null, 25, null, null },
                    { new Guid("2000000b-0000-0000-0000-00000000000b"), "Empresa", false, null, null, true, "EVR (Evaluación de Riesgos Laborales)", "Vigencia sin especificar — fecha de vencimiento manual.", null, 26, null, null },
                    { new Guid("2000000c-0000-0000-0000-00000000000c"), "Empresa", false, null, null, true, "PAP (Planificación de la Actividad Preventiva)", "Vigencia sin especificar — fecha de vencimiento manual.", null, 27, null, null },
                    { new Guid("2000000d-0000-0000-0000-00000000000d"), "Empresa", false, null, null, false, "Tarjeta CIF", "Opcional — no obligatorio para todos los clientes.", null, 28, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000001"), "Vehiculo", false, null, null, true, "ITC", "Vigencia sin especificar — fecha de vencimiento manual.", null, 1, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "Vehiculo", false, null, null, true, "Ficha técnica", "No caduca por sí sola, pero se pide como documento adjunto del vehículo.", null, 2, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "Vehiculo", false, null, null, true, "Seguro", "Vigencia sin especificar — fecha de vencimiento manual.", null, 3, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "Vehiculo", false, null, null, true, "Autorización de circulación", "Vigencia sin especificar — fecha de vencimiento manual.", null, 4, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_VehiculoId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "VehiculoId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_EmpresaId",
                table: "Vehiculos",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_NumeroPlaca",
                table: "Vehiculos",
                column: "NumeroPlaca",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_SubcontrataId",
                table: "Vehiculos",
                column: "SubcontrataId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Vehiculos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_VehiculoId_TipoDocumentoId",
                table: "Documentos");

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"));

            migrationBuilder.DropColumn(
                name: "EsObligatorio",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "VehiculoId",
                table: "Documentos");
        }
    }
}
