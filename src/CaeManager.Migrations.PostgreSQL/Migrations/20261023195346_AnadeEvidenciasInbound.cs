using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEvidenciasInbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SoloConclusionAptitud",
                table: "TiposDocumento",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ContratosAcceso_TenantId_Id",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "Id" });

            // La AK de arriba ya es única sobre (TenantId, Id): el índice único de AnadeContratosAcceso la repetía.
            migrationBuilder.DropIndex(
                name: "IX_ContratosAcceso_TenantId_Id",
                table: "ContratosAcceso");

            migrationBuilder.CreateTable(
                name: "EvidenciasInbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContratoAccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantOrigenId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoOrigenId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutorizacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorOrigenId = table.Column<Guid>(type: "uuid", nullable: true),
                    SujetoNombre = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SujetoDocumentoIdentidad = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FechaEmision = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaVencimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    ArchivoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HuellaSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    SoloConclusionAptitud = table.Column<bool>(type: "boolean", nullable: false),
                    ConclusionAptitud = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ValidadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValidadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CausaRechazo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    MotivoRechazo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RechazadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RechazadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenciasInbound", x => x.Id);
                    table.CheckConstraint("CK_EvidenciasInbound_ArchivoSegunConclusion", "(\"SoloConclusionAptitud\" AND \"ArchivoUrl\" IS NULL AND \"HuellaSha256\" IS NULL AND \"ConclusionAptitud\" IS NOT NULL) OR (NOT \"SoloConclusionAptitud\" AND \"ArchivoUrl\" IS NOT NULL AND \"HuellaSha256\" IS NOT NULL AND \"ConclusionAptitud\" IS NULL)");
                    table.CheckConstraint("CK_EvidenciasInbound_OrigenEnOtroTenant", "\"TenantOrigenId\" <> \"TenantId\"");
                    table.CheckConstraint("CK_EvidenciasInbound_SujetoYConclusionDeLaMedica", "NOT \"SoloConclusionAptitud\" OR (\"TrabajadorOrigenId\" IS NOT NULL AND \"SujetoDocumentoIdentidad\" IS NOT NULL AND \"ConclusionAptitud\" IN ('Apto', 'AptoConRestricciones', 'NoApto'))");
                    table.CheckConstraint("CK_EvidenciasInbound_VencimientoNoAnteriorAEmision", "\"FechaVencimiento\" IS NULL OR \"FechaVencimiento\" >= \"FechaEmision\"");
                    table.ForeignKey(
                        name: "FK_EvidenciasInbound_ContratosAcceso_TenantId_ContratoAccesoId",
                        columns: x => new { x.TenantId, x.ContratoAccesoId },
                        principalTable: "ContratosAcceso",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenciasInbound_TiposDocumento_TenantId_TipoDocumentoId",
                        columns: x => new { x.TenantId, x.TipoDocumentoId },
                        principalTable: "TiposDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000006"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000001"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000002"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000003"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000004"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000005"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("80000000-0000-0000-0000-000000000006"),
                column: "SoloConclusionAptitud",
                value: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TiposDocumento_SoloConclusionAptitudEsDeSalud",
                table: "TiposDocumento",
                sql: "NOT \"SoloConclusionAptitud\" OR \"Sensibilidad\" = 'CategoriaEspecialSalud'");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenciasInbound_TenantId_Contrato_DocumentoOrigen",
                table: "EvidenciasInbound",
                columns: new[] { "TenantId", "ContratoAccesoId", "DocumentoOrigenId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenciasInbound_TenantId_DocumentoOrigenId",
                table: "EvidenciasInbound",
                columns: new[] { "TenantId", "DocumentoOrigenId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenciasInbound_TenantId_TipoDocumentoId",
                table: "EvidenciasInbound",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            // Inbound M1, PR 4. "EvidenciasInbound" vive en el Tenant del Cliente empresarial (su Tenant propietario) con la
            // RLS estándar, como "ContratosAcceso". El runtime inserta y solo actualiza las columnas de estado (rechazar y
            // volver a validar): la procedencia, el sujeto, el archivo, la huella y la conclusión no cambian después de
            // registrar. Sin DELETE para nadie de la aplicación: la retención de la copia (DI-11) no está decidida y revocar
            // la autorización no la toca. Soporte TALVEG solo lee; aprovisionamiento, nada.
            //
            // TR_EvidenciasInbound_TipoSoloConclusion: una evidencia de un tipo del Tenant marcado «solo conclusión de
            // aptitud» es de solo conclusión (CK_EvidenciasInbound_ArchivoSegunConclusion hace el resto). El trigger corre
            // como quien escribe y lee el tipo con su RLS, que es la del mismo Tenant de la FK compuesta.
            //
            // Lectura entre Tenants (DN-5 (b) del 2026-10-23): app_inbound_documentos_del_alcance deja pasar un tipo
            // CategoriaEspecialSalud del Tenant de la Empresa contratista solo si ese tipo lleva "SoloConclusionAptitud"; el
            // resto de tipos de salud, el informe médico incluido, sigue fuera. CREATE OR REPLACE con la misma firma: conserva
            // propietario, privilegios y todos los demás filtros, que son el texto de AnadeLecturaInboundEntreTenants sin
            // tocar. app_inbound_documentos_autorizados añade la columna "SoloConclusionAptitud" para que el Tenant del
            // Cliente empresarial sepa que la evidencia de ese original no lleva archivo; cambiar las columnas de salida
            // obliga a DROP y CREATE, con el cuerpo idéntico al anterior salvo esa columna y los mismos REVOKE y GRANT.
            // app_inbound_archivo_autorizado, igual: DROP y CREATE para devolver también "TenantPropietarioId" (el
            // v_aut.tenant_contratista con el que la aplicación abre el binario, sin componerlo ella) y el
            // "SoloConclusionAptitud" del tipo del original, leído en la misma sentencia que da la apertura; el registro de
            // evidencia no copia el binario de uno marcado. El Down restaura las dos funciones tal como eran.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."EvidenciasInbound" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."EvidenciasInbound" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."EvidenciasInbound"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."EvidenciasInbound" FROM cae_app_runtime;
                GRANT SELECT, INSERT ON TABLE public."EvidenciasInbound" TO cae_app_runtime;
                GRANT UPDATE ("Estado", "CausaRechazo", "MotivoRechazo", "RechazadaPorUsuarioId", "RechazadaEnUtc",
                              "ValidadaPorUsuarioId", "ValidadaEnUtc", "Version")
                    ON TABLE public."EvidenciasInbound" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."EvidenciasInbound" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."EvidenciasInbound" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."EvidenciasInbound" FROM cae_app_aprovisionamiento;

                CREATE FUNCTION public.app_evidencias_inbound_tipo_solo_conclusion() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT NEW."SoloConclusionAptitud" AND EXISTS (
                        SELECT 1 FROM public."TiposDocumento" t
                         WHERE t."TenantId" = NEW."TenantId" AND t."Id" = NEW."TipoDocumentoId" AND t."SoloConclusionAptitud") THEN
                        RAISE EXCEPTION 'Una evidencia de un tipo de solo conclusión de aptitud no lleva archivo: solo la conclusión.'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $$;
                REVOKE ALL ON FUNCTION public.app_evidencias_inbound_tipo_solo_conclusion() FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                CREATE TRIGGER "TR_EvidenciasInbound_TipoSoloConclusion"
                    BEFORE INSERT OR UPDATE OF "TipoDocumentoId", "SoloConclusionAptitud" ON public."EvidenciasInbound"
                    FOR EACH ROW EXECUTE FUNCTION public.app_evidencias_inbound_tipo_solo_conclusion();

                DROP FUNCTION public.app_inbound_documentos_autorizados(uuid);
                CREATE FUNCTION public.app_inbound_documentos_autorizados(p_contrato uuid)
                    RETURNS TABLE("DocumentoId" uuid, "TrabajadorId" uuid, "TrabajadorNombre" text, "TrabajadorApellidos" text,
                                  "TrabajadorDni" text, "TipoDocumentoNombre" text, "FechaEmision" date, "FechaVencimiento" date,
                                  "EstadoVigencia" integer, "SoloConclusionAptitud" boolean)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_usuario uuid;
                    v_aut record;
                BEGIN
                    IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                        RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                    END IF;
                    v_lector := public.app_ctx_tenant_id();
                    v_usuario := public.app_ctx_usuario_id();
                    SELECT * INTO v_aut FROM public.app_inbound_autorizacion_aplicable(
                        v_lector, p_contrato, (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date);
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    INSERT INTO public."AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                                                                "TenantOrigenLectorId", "UsuarioId", "DocumentoId", "Tipo", "MomentoUtc")
                        VALUES (public.app_inbound_nuevo_id(), v_aut.tenant_contratista, v_aut.autorizacion_id, p_contrato, v_lector,
                                NULLIF(public.app_ctx_tenant_origen_id(), v_lector), v_usuario, NULL, 'Listado', pg_catalog.now());
                    RETURN QUERY
                        SELECT d."Id", d."TrabajadorId", t."Nombre"::text, t."Apellidos"::text, t."Dni"::text, td."Nombre"::text,
                               d."FechaEmision", d."FechaVencimiento", d."EstadoVigencia", td."SoloConclusionAptitud"
                          FROM public."Documentos" d
                          JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                          LEFT JOIN public."Trabajadores" t ON t."TenantId" = d."TenantId" AND t."Id" = d."TrabajadorId"
                         WHERE d."TenantId" = v_aut.tenant_contratista
                           AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                              v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa))
                           AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaDocumental" a
                                        WHERE a."TenantId" = v_aut.tenant_contratista AND a."Id" = v_aut.autorizacion_id
                                          AND a."RevocadaEnUtc" IS NULL)
                         ORDER BY d."TrabajadorId" NULLS FIRST, td."Nombre", d."FechaEmision", d."Id";
                END;
                $$;
                CREATE OR REPLACE FUNCTION public.app_inbound_documentos_del_alcance(p_tenant uuid, p_autorizacion uuid, p_incluye_empresa boolean)
                    RETURNS SETOF uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT d."Id"
                    FROM public."Documentos" d
                    JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                   WHERE d."TenantId" = p_tenant
                     AND NOT d."EstaEliminado"
                     AND d."AnonimizadoEnUtc" IS NULL
                     AND d."SustituidoEnUtc" IS NULL
                     AND (td."Sensibilidad" <> 'CategoriaEspecialSalud' OR td."SoloConclusionAptitud")
                     AND ((p_incluye_empresa
                           AND d."EmpresaId" IS NOT NULL
                           AND EXISTS (SELECT 1 FROM public."Empresas" e
                                        WHERE e."TenantId" = p_tenant AND e."Id" = d."EmpresaId"
                                          AND e."EsPropia" AND NOT e."EstaEliminado"))
                          OR (d."TrabajadorId" IS NOT NULL
                              AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaTrabajadores" h
                                            JOIN public."Trabajadores" t ON t."TenantId" = h."TenantId" AND t."Id" = h."TrabajadorId"
                                           WHERE h."TenantId" = p_tenant AND h."AutorizacionId" = p_autorizacion
                                             AND h."TrabajadorId" = d."TrabajadorId"
                                             AND NOT t."EstaEliminado" AND t."AnonimizadoEnUtc" IS NULL)));
                $$;
                REVOKE ALL ON FUNCTION public.app_inbound_documentos_autorizados(uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_inbound_documentos_autorizados(uuid) TO cae_app_runtime;

                DROP FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid);
                CREATE FUNCTION public.app_inbound_archivo_autorizado(p_contrato uuid, p_documento uuid)
                    RETURNS TABLE("DocumentoId" uuid, "TenantPropietarioId" uuid, "ArchivoUrl" text, "SoloConclusionAptitud" boolean)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_usuario uuid;
                    v_aut record;
                    v_archivo text;
                    v_solo_conclusion boolean;
                BEGIN
                    IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                        RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                    END IF;
                    v_lector := public.app_ctx_tenant_id();
                    v_usuario := public.app_ctx_usuario_id();
                    SELECT * INTO v_aut FROM public.app_inbound_autorizacion_aplicable(
                        v_lector, p_contrato, (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date);
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    SELECT d."ArchivoUrl", td."SoloConclusionAptitud" INTO v_archivo, v_solo_conclusion
                      FROM public."Documentos" d
                      JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                     WHERE d."TenantId" = v_aut.tenant_contratista
                       AND d."Id" = p_documento
                       AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                          v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa))
                       AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaDocumental" a
                                    WHERE a."TenantId" = v_aut.tenant_contratista AND a."Id" = v_aut.autorizacion_id
                                      AND a."RevocadaEnUtc" IS NULL);
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    INSERT INTO public."AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                                                                "TenantOrigenLectorId", "UsuarioId", "DocumentoId", "Tipo", "MomentoUtc")
                        VALUES (public.app_inbound_nuevo_id(), v_aut.tenant_contratista, v_aut.autorizacion_id, p_contrato, v_lector,
                                NULLIF(public.app_ctx_tenant_origen_id(), v_lector), v_usuario, p_documento, 'Apertura', pg_catalog.now());
                    RETURN QUERY SELECT p_documento, v_aut.tenant_contratista, v_archivo, v_solo_conclusion;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER "TR_EvidenciasInbound_TipoSoloConclusion" ON public."EvidenciasInbound";
                DROP FUNCTION public.app_evidencias_inbound_tipo_solo_conclusion();

                DROP FUNCTION public.app_inbound_documentos_autorizados(uuid);
                CREATE FUNCTION public.app_inbound_documentos_autorizados(p_contrato uuid)
                    RETURNS TABLE("DocumentoId" uuid, "TrabajadorId" uuid, "TrabajadorNombre" text, "TrabajadorApellidos" text,
                                  "TrabajadorDni" text, "TipoDocumentoNombre" text, "FechaEmision" date, "FechaVencimiento" date,
                                  "EstadoVigencia" integer)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_usuario uuid;
                    v_aut record;
                BEGIN
                    IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                        RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                    END IF;
                    v_lector := public.app_ctx_tenant_id();
                    v_usuario := public.app_ctx_usuario_id();
                    SELECT * INTO v_aut FROM public.app_inbound_autorizacion_aplicable(
                        v_lector, p_contrato, (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date);
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    INSERT INTO public."AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                                                                "TenantOrigenLectorId", "UsuarioId", "DocumentoId", "Tipo", "MomentoUtc")
                        VALUES (public.app_inbound_nuevo_id(), v_aut.tenant_contratista, v_aut.autorizacion_id, p_contrato, v_lector,
                                NULLIF(public.app_ctx_tenant_origen_id(), v_lector), v_usuario, NULL, 'Listado', pg_catalog.now());
                    RETURN QUERY
                        SELECT d."Id", d."TrabajadorId", t."Nombre"::text, t."Apellidos"::text, t."Dni"::text, td."Nombre"::text,
                               d."FechaEmision", d."FechaVencimiento", d."EstadoVigencia"
                          FROM public."Documentos" d
                          JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                          LEFT JOIN public."Trabajadores" t ON t."TenantId" = d."TenantId" AND t."Id" = d."TrabajadorId"
                         WHERE d."TenantId" = v_aut.tenant_contratista
                           AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                              v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa))
                           AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaDocumental" a
                                        WHERE a."TenantId" = v_aut.tenant_contratista AND a."Id" = v_aut.autorizacion_id
                                          AND a."RevocadaEnUtc" IS NULL)
                         ORDER BY d."TrabajadorId" NULLS FIRST, td."Nombre", d."FechaEmision", d."Id";
                END;
                $$;
                CREATE OR REPLACE FUNCTION public.app_inbound_documentos_del_alcance(p_tenant uuid, p_autorizacion uuid, p_incluye_empresa boolean)
                    RETURNS SETOF uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT d."Id"
                    FROM public."Documentos" d
                    JOIN public."TiposDocumento" td ON td."TenantId" = d."TenantId" AND td."Id" = d."TipoDocumentoId"
                   WHERE d."TenantId" = p_tenant
                     AND NOT d."EstaEliminado"
                     AND d."AnonimizadoEnUtc" IS NULL
                     AND d."SustituidoEnUtc" IS NULL
                     AND td."Sensibilidad" <> 'CategoriaEspecialSalud'
                     AND ((p_incluye_empresa
                           AND d."EmpresaId" IS NOT NULL
                           AND EXISTS (SELECT 1 FROM public."Empresas" e
                                        WHERE e."TenantId" = p_tenant AND e."Id" = d."EmpresaId"
                                          AND e."EsPropia" AND NOT e."EstaEliminado"))
                          OR (d."TrabajadorId" IS NOT NULL
                              AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaTrabajadores" h
                                            JOIN public."Trabajadores" t ON t."TenantId" = h."TenantId" AND t."Id" = h."TrabajadorId"
                                           WHERE h."TenantId" = p_tenant AND h."AutorizacionId" = p_autorizacion
                                             AND h."TrabajadorId" = d."TrabajadorId"
                                             AND NOT t."EstaEliminado" AND t."AnonimizadoEnUtc" IS NULL)));
                $$;
                REVOKE ALL ON FUNCTION public.app_inbound_documentos_autorizados(uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_inbound_documentos_autorizados(uuid) TO cae_app_runtime;

                DROP FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid);
                CREATE FUNCTION public.app_inbound_archivo_autorizado(p_contrato uuid, p_documento uuid)
                    RETURNS TABLE("DocumentoId" uuid, "ArchivoUrl" text)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                DECLARE
                    v_lector uuid;
                    v_usuario uuid;
                    v_aut record;
                    v_archivo text;
                BEGIN
                    IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN
                        RAISE EXCEPTION 'Sin contexto firmado con Tenant y usuario: no se lee nada de otro Tenant.' USING ERRCODE = '42501';
                    END IF;
                    v_lector := public.app_ctx_tenant_id();
                    v_usuario := public.app_ctx_usuario_id();
                    SELECT * INTO v_aut FROM public.app_inbound_autorizacion_aplicable(
                        v_lector, p_contrato, (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date);
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    SELECT d."ArchivoUrl" INTO v_archivo
                      FROM public."Documentos" d
                     WHERE d."TenantId" = v_aut.tenant_contratista
                       AND d."Id" = p_documento
                       AND d."Id" IN (SELECT public.app_inbound_documentos_del_alcance(
                                          v_aut.tenant_contratista, v_aut.autorizacion_id, v_aut.incluye_empresa))
                       AND EXISTS (SELECT 1 FROM public."AutorizacionesLecturaDocumental" a
                                    WHERE a."TenantId" = v_aut.tenant_contratista AND a."Id" = v_aut.autorizacion_id
                                      AND a."RevocadaEnUtc" IS NULL);
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    INSERT INTO public."AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                                                                "TenantOrigenLectorId", "UsuarioId", "DocumentoId", "Tipo", "MomentoUtc")
                        VALUES (public.app_inbound_nuevo_id(), v_aut.tenant_contratista, v_aut.autorizacion_id, p_contrato, v_lector,
                                NULLIF(public.app_ctx_tenant_origen_id(), v_lector), v_usuario, p_documento, 'Apertura', pg_catalog.now());
                    RETURN QUERY SELECT p_documento, v_archivo;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_inbound_archivo_autorizado(uuid, uuid) TO cae_app_runtime;
                """);
            migrationBuilder.DropTable(
                name: "EvidenciasInbound");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosAcceso_TenantId_Id",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.DropCheckConstraint(
                name: "CK_TiposDocumento_SoloConclusionAptitudEsDeSalud",
                table: "TiposDocumento");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ContratosAcceso_TenantId_Id",
                table: "ContratosAcceso");

            migrationBuilder.DropColumn(
                name: "SoloConclusionAptitud",
                table: "TiposDocumento");
        }
    }
}
