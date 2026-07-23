using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SembrarTenantPorDefecto : Migration
    {
        /// <summary>
        /// Las 25 tablas de PLAN-MIGRACION-MULTITENANT.md § 0.1 (24 de Domain
        /// + AspNetUsers) — mismo listado exacto que AgregarTenantNullable.
        /// </summary>
        private static readonly string[] TablasConTenantId =
        [
            "VisitasTrabajadores", "Visitas", "Vehiculos", "Trabajadores",
            "TiposDocumentoCentros", "TiposDocumento", "SubcontratasEmpresas",
            "SubcontratasClientes", "Subcontratas", "RequisitosDocumentales",
            "RegistrosAuditoria", "PlataformasAcceso", "ParametrosSistema",
            "NotificacionesUsuario", "EmpresasClientes", "Empresas", "Documentos",
            "DeteccionesTrabajador", "CredencialesAccesoSubcontrata",
            "CredencialesAccesoEmpresa", "ConfiguracionesIaDocumentoCliente",
            "Clientes", "Centros", "AspNetUsers", "Asignaciones", "Alertas",
        ];

        private const string IdTenantPorDefecto = "00000000-0000-0000-0000-000000000001";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "CreadoEnUtc", "Estado", "Nombre" },
                values: new object[] { new Guid(IdTenantPorDefecto), new DateTime(2026, 7, 23, 0, 0, 0, 0, DateTimeKind.Utc), "Activo", "Organización principal" });

            // Backfill (Etapa 2 de PLAN-MIGRACION-MULTITENANT.md § 3): toda
            // fila preexistente con TenantId nulo (sembrada en la Etapa 1)
            // pasa a pertenecer al tenant #1 — incluye tanto los datos reales
            // ya existentes como las filas de catálogo (TipoDocumento,
            // ParametroSistema) que la migración anterior dejó en null.
            foreach (var tabla in TablasConTenantId)
            {
                migrationBuilder.Sql(
                    $"UPDATE \"{tabla}\" SET \"TenantId\" = '{IdTenantPorDefecto}' WHERE \"TenantId\" IS NULL;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var tabla in TablasConTenantId)
            {
                migrationBuilder.Sql(
                    $"UPDATE \"{tabla}\" SET \"TenantId\" = NULL WHERE \"TenantId\" = '{IdTenantPorDefecto}';");
            }

            migrationBuilder.DeleteData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid(IdTenantPorDefecto));
        }
    }
}
