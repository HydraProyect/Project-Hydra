using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// D-7, incremento 3 (contracción de expand/contract). Tras
    /// <c>ConvierteCarterasPorClienteATenantEntero</c> (que cerró con motivo Reorganizada todas las
    /// Asignaciones de Cartera por Cliente empresarial no cerradas) y su despliegue, la base de datos deja de
    /// admitir el modo retirado:
    /// <list type="number">
    /// <item>añade <c>CK_AsignacionesCartera_TenantEnteroSalvoCerrada</c>: una cartera con
    /// <c>AmbitoRelacionClienteId</c> solo puede estar <c>Cerrada</c> (histórico). El
    /// <c>ADD CONSTRAINT</c> valida las filas existentes: <b>falla si queda alguna cartera por Cliente
    /// empresarial no cerrada</b>, que es la verificación de seguridad (la consulta READ ONLY previa al
    /// despliegue debe dar 0 filas);</item>
    /// <item>borra el índice único transitorio <c>IX_AsignacionesCartera_ResponsableRelacionVigente</c>
    /// (un responsable vigente por Cliente empresarial), que solo existía mientras el reparto era posible.
    /// EF Core crea en su lugar el índice no único por defecto de la FK compuesta
    /// (<c>PropietarioTenantId, AmbitoRelacionClienteId</c>).</item>
    /// </list>
    /// No toca RLS, políticas, roles ni datos. No renombra columnas ni tipos. <c>Down</c> deshace las dos
    /// cosas (con el CHECK vigente no puede haber filas que violen el índice recreado).
    /// </summary>
    public partial class RetiraRepartoPorClienteDeLasCarteras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoRelacionClien~",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AsignacionesCartera_TenantEnteroSalvoCerrada",
                table: "AsignacionesCartera",
                sql: "\"AmbitoRelacionClienteId\" IS NULL OR \"Estado\" = 'Cerrada'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoRelacionClien~",
                table: "AsignacionesCartera");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AsignacionesCartera_TenantEnteroSalvoCerrada",
                table: "AsignacionesCartera");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                unique: true,
                filter: "\"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NOT NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");
        }
    }
}
