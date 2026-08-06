using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RetirarRequisitoDocumental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Orden importa: las columnas nuevas de TiposDocumentoCentros tienen que
            // existir antes de la migración de datos de abajo, y esa migración de
            // datos tiene que correr antes del DropTable de RequisitosDocumentales
            // (PLAN-EJECUCION-UX.md § 0.4) — el scaffold por defecto ponía el
            // DropTable primero, lo que habría perdido cualquier fila real sin
            // trasladar.
            migrationBuilder.AddColumn<string>(
                name: "ArchivoUrl",
                table: "TiposDocumentoCentros",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BloqueaAcceso",
                table: "TiposDocumentoCentros",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // defaultValue: true — no defaultValue: false (lo que habría propuesto el
            // scaffold automático). Las filas YA existentes de TiposDocumentoCentros
            // (creadas desde el selector de Centros de /tipos-documento) significan
            // hoy "este Centro SÍ tiene este Tipo" bajo la semántica antigua de
            // allow-list — si el backfill las dejara en Incluido=false, la nueva
            // resolución (ResolucionTipoDocumentoCentro) las leería como exclusiones
            // explícitas y les invertiría el significado en silencio.
            migrationBuilder.AddColumn<bool>(
                name: "Incluido",
                table: "TiposDocumentoCentros",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "NombreArchivoOriginal",
                table: "TiposDocumentoCentros",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeriodicidadEspecialMeses",
                table: "TiposDocumentoCentros",
                type: "integer",
                nullable: true);

            // Migración de datos: cada RequisitoDocumental se convierte en un
            // TipoDocumento nuevo (Ámbito Trabajador — era el uso real de la
            // funcionalidad, ver PLAN-EJECUCION-UX.md § 0.4) más su fila
            // TipoDocumentoCentro (Incluido, BloqueaAcceso, adjunto de plantilla
            // trasladados tal cual). PeriodicidadEspecial NO se traslada a
            // PeriodicidadEspecialMeses: era texto libre ("cada 6 meses",
            // "trimestral"...), no un entero de meses — se pierde ese matiz
            // concreto, recuperable a mano por el gestor desde Requisitos del
            // Centro si hiciera falta. Cumplido/FechaCumplimiento tampoco se
            // trasladan: ese estado manual desaparece a propósito, sustituido por
            // la detección automática del Documento real (huecos por trabajador).
            // Un INSERT ... SELECT DISTINCT primero (no una fila por RequisitoDocumental):
            // "TiposDocumento" tiene un índice único (TenantId, Nombre) y dos
            // RequisitoDocumental del mismo tenant pueden compartir Descripcion —
            // en ese caso comparten también el TipoDocumento nuevo, resultado
            // razonable (mismo requisito, dos Centros).
            migrationBuilder.Sql("""
                INSERT INTO "TiposDocumento"
                    ("Id", "TenantId", "Nombre", "VigenciaMeses", "AplicaVencimientoAutomatico", "Orden",
                     "EsObligatorio", "AmbitoAplicacion", "LecturaIaActiva", "DeteccionTrabajadoresActiva",
                     "VerificacionIaActiva")
                SELECT
                    gen_random_uuid(), d."TenantId", d."Nombre", NULL, false, 999,
                    false, 'Trabajador', true, false, false
                FROM (SELECT DISTINCT r."TenantId", left(r."Descripcion", 150) AS "Nombre" FROM "RequisitosDocumentales" r) d;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "TiposDocumentoCentros"
                    ("Id", "TenantId", "TipoDocumentoId", "CentroId", "Incluido", "BloqueaAcceso",
                     "ArchivoUrl", "NombreArchivoOriginal", "PeriodicidadEspecialMeses")
                SELECT
                    gen_random_uuid(), r."TenantId", t."Id", r."CentroId", true, r."BloqueaAcceso",
                    r."ArchivoUrl", r."NombreArchivoOriginal", NULL
                FROM "RequisitosDocumentales" r
                JOIN "TiposDocumento" t
                    ON t."TenantId" = r."TenantId" AND t."Nombre" = left(r."Descripcion", 150) AND t."Orden" = 999;
                """);

            migrationBuilder.DropTable(
                name: "RequisitosDocumentales");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivoUrl",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropColumn(
                name: "BloqueaAcceso",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropColumn(
                name: "Incluido",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropColumn(
                name: "NombreArchivoOriginal",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropColumn(
                name: "PeriodicidadEspecialMeses",
                table: "TiposDocumentoCentros");

            migrationBuilder.CreateTable(
                name: "RequisitosDocumentales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BloqueaAcceso = table.Column<bool>(type: "boolean", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Cumplido = table.Column<bool>(type: "boolean", nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    FechaCumplimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    NombreArchivoOriginal = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PeriodicidadEspecial = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitosDocumentales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequisitosDocumentales_Centros_TenantId_CentroId",
                        columns: x => new { x.TenantId, x.CentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosDocumentales_CentroId",
                table: "RequisitosDocumentales",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosDocumentales_TenantId_CentroId",
                table: "RequisitosDocumentales",
                columns: new[] { "TenantId", "CentroId" });
        }
    }
}
