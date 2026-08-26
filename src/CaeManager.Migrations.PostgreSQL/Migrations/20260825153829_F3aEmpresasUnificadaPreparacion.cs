using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class F3aEmpresasUnificadaPreparacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EjecutivoUsuarioId",
                table: "Empresas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsCritico",
                table: "Empresas",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsPropia",
                table: "Empresas",
                type: "boolean",
                nullable: false,
                // Las filas Empresa YA EXISTENTES son EsPropia=true, no el
                // default booleano de CLR (false) que EF habría usado sin
                // esta anotación explícita — habría marcado como
                // "contraparte" a las Empresas reales del propio operador.
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "NivelServicio",
                table: "Empresas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notas",
                table: "Empresas",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_EjecutivoUsuarioId",
                table: "Empresas",
                column: "EjecutivoUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_EsPropia",
                table: "Empresas",
                columns: new[] { "TenantId", "EsPropia" });

            // Backfill (único trabajo de datos de F3a, f3-diseno-fisico-
            // empresa-unificada-2026-08-25.md §4 pasos 3-4): copiar Clientes
            // y Subcontratas -> Empresas, MISMO Id, EsPropia=false. Cliente/
            // Subcontrata SIGUEN siendo la fuente de verdad activa — ningún
            // lector ni escritor se redirige en F3a (f3-comparativa-alcance-
            // abcd-2026-08-25.md, camino D). Incluye EstaEliminado/
            // EliminadoEnUtc/EliminadoPorUsuarioId: una fila soft-deleted en
            // origen debe llegar soft-deleted a la copia, o la comparación
            // de F3c encontraría una divergencia falsa.
            migrationBuilder.Sql(
                """
                INSERT INTO "Empresas"
                    ("Id", "TenantId", "RazonSocial", "Cif", "Cnae", "ConvenioAplicable", "EsActividadAnexoI",
                     "EsPropia", "EjecutivoUsuarioId", "EsCritico", "Notas", "NivelServicio",
                     "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version")
                SELECT
                    "Id", "TenantId", "RazonSocial", "Cif", NULL, NULL, false,
                    false, "EjecutivoUsuarioId", "EsCritico", "Notas", NULL,
                    "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version"
                FROM "Clientes";
                """);

            // NivelServicio se traduce del entero de NivelServicioSubcontrata
            // (0=Gestionada, 1=Supervisada, sin conversión explícita en
            // SubcontrataConfiguration — EF lo guarda como integer por
            // convención) al texto que la columna transitoria de Empresas
            // espera (ver Empresa.cs: texto y no el enum, a propósito, para
            // no acoplar Domain.Empresas a Domain.Subcontratas).
            migrationBuilder.Sql(
                """
                INSERT INTO "Empresas"
                    ("Id", "TenantId", "RazonSocial", "Cif", "Cnae", "ConvenioAplicable", "EsActividadAnexoI",
                     "EsPropia", "EjecutivoUsuarioId", "EsCritico", "Notas", "NivelServicio",
                     "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version")
                SELECT
                    "Id", "TenantId", "RazonSocial", "Cif", NULL, NULL, false,
                    false, NULL, NULL, NULL,
                    CASE "NivelServicio" WHEN 0 THEN 'Gestionada' WHEN 1 THEN 'Supervisada' END,
                    "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version"
                FROM "Subcontratas";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A propósito NO hay ningún DELETE aquí — mismo contrato de
            // rollback ya fijado para F3
            // (f3-analisis-pipeline-y-rollback-2026-08-25.md, decisión D):
            // Down() revierte esquema, nunca datos. Aunque en el momento
            // exacto de F3a ningún escritor está todavía redirigido (así
            // que un DELETE WHERE EsPropia=false sería técnicamente seguro
            // HOY), este Down() puede invocarse en cualquier momento futuro
            // — incluso después de que F3b redirija escritores y existan
            // filas EsPropia=false legítimas y nuevas. Un DELETE aquí no
            // podría distinguirlas. La regla se aplica de forma uniforme a
            // toda la familia de migraciones de F3, no solo a F3c.
            migrationBuilder.DropIndex(
                name: "IX_Empresas_EjecutivoUsuarioId",
                table: "Empresas");

            migrationBuilder.DropIndex(
                name: "IX_Empresas_TenantId_EsPropia",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "EjecutivoUsuarioId",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "EsCritico",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "EsPropia",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "NivelServicio",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "Notas",
                table: "Empresas");
        }
    }
}
