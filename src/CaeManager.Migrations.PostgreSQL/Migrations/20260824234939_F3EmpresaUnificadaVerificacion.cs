using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class F3EmpresaUnificadaVerificacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Centros_Clientes_TenantId_ClienteId",
                table: "Centros");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Clientes_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Subcontratas_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Clientes_TenantId_ClienteId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_EmpresasClientes_Clientes_TenantId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Clientes_TenantId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasEmpresas_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas");

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
                // Paso 2 del diseño (f3-diseno-fisico-empresa-unificada…): las
                // filas Empresa YA EXISTENTES se marcan EsPropia=true, no el
                // default booleano de CLR (false) que EF habría usado sin
                // esta anotación explícita — habría marcado como "contraparte"
                // a las Empresas reales del propio operador.
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

            migrationBuilder.AddCheckConstraint(
                name: "CK_SubcontratasEmpresas_NoAutorreferencia",
                table: "SubcontratasEmpresas",
                sql: "\"SubcontrataId\" <> \"EmpresaId\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SubcontratasClientes_NoAutorreferencia",
                table: "SubcontratasClientes",
                sql: "\"SubcontrataId\" <> \"ClienteId\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmpresasClientes_NoAutorreferencia",
                table: "EmpresasClientes",
                sql: "\"EmpresaId\" <> \"ClienteId\"");

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_EjecutivoUsuarioId",
                table: "Empresas",
                column: "EjecutivoUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_EsPropia",
                table: "Empresas",
                columns: new[] { "TenantId", "EsPropia" });

            // Paso 3 del diseño: copiar Clientes -> Empresas, MISMO Id,
            // EsPropia=false. Debe ejecutarse ANTES de repuntear las FKs de
            // abajo — si no, "AddForeignKey" fallaría al validar filas de
            // Centro/Documento/etc. cuyo ClienteId todavía no existiría en
            // Empresas.
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

            // Paso 4 del diseño: copiar Subcontratas -> Empresas, MISMO Id,
            // EsPropia=false. NivelServicio se traduce del entero de
            // NivelServicioSubcontrata (0=Gestionada, 1=Supervisada, sin
            // conversión explícita en SubcontrataConfiguration — EF lo
            // guarda como integer por convención) al texto que la columna
            // transitoria de Empresas espera (ver Empresa.cs, comentario de
            // NivelServicio: texto y no el enum, a propósito).
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

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_Empresas_TenantId_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CredencialesAccesoEmpresa_Empresas_TenantId_EmpresaId",
                table: "CredencialesAccesoEmpresa",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CredencialesAccesoSubcontrata_Empresas_TenantId_Subcontrata~",
                table: "CredencialesAccesoSubcontrata",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Empresas_TenantId_ClienteId",
                table: "Documentos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmpresasClientes_Empresas_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasEmpresas_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Centros_Empresas_TenantId_ClienteId",
                table: "Centros");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_CredencialesAccesoEmpresa_Empresas_TenantId_EmpresaId",
                table: "CredencialesAccesoEmpresa");

            migrationBuilder.DropForeignKey(
                name: "FK_CredencialesAccesoSubcontrata_Empresas_TenantId_Subcontrata~",
                table: "CredencialesAccesoSubcontrata");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Empresas_TenantId_ClienteId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_EmpresasClientes_Empresas_TenantId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasEmpresas_Empresas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas");

            // A propósito NO hay ningún DELETE aquí. Contrato de F3
            // (f3-analisis-pipeline-y-rollback-2026-08-25.md, decisión D):
            // el rollback soportado de F3 es únicamente transaccional —
            // si Up() falla, PostgreSQL revierte TODO el Up() de una vez
            // (columnas, copia de datos, constraints) antes de que la app
            // llegue a arrancar. Una vez Up() termina con éxito, la
            // unificación se considera aplicada y no hay Down() capaz de
            // reconstruir el estado anterior sin riesgo de borrar datos
            // reales: un DELETE FROM "Empresas" WHERE "EsPropia" = false
            // no puede distinguir una fila copiada por la migración de una
            // fila creada legítimamente después del corte (misma condición,
            // sin marcador de origen) — probado como un riesgo real, no
            // teórico, antes de escribir este Down(). Este método revierte
            // el ESQUEMA (columnas, constraints, FKs) y deja los DATOS tal
            // como estén en el momento de ejecutarlo. Una separación
            // posterior de Empresas es su propio incremento de ingeniería,
            // con su propio diseño de datos — no algo que este Down() deba
            // resolver.
            migrationBuilder.DropCheckConstraint(
                name: "CK_SubcontratasEmpresas_NoAutorreferencia",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SubcontratasClientes_NoAutorreferencia",
                table: "SubcontratasClientes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmpresasClientes_NoAutorreferencia",
                table: "EmpresasClientes");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_Clientes_TenantId_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Clientes_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Subcontratas_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId",
                principalTable: "Subcontratas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Clientes_TenantId_ClienteId",
                table: "Documentos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmpresasClientes_Clientes_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Clientes_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasEmpresas_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
