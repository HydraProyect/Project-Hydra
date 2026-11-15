using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// B-04a del plan backend Inbound: la vía de escritura de la línea Inbound del Producto Contratado
    /// (<c>ContratarLineaInboundCommand</c>, decisiones D-B y D-C del 2026-11-15).
    /// <para>
    /// <c>GRANT INSERT</c> sobre <c>ProductosContratados</c> solo a <c>cae_app_aprovisionamiento</c>, el rol que adopta el
    /// handler de un comando de aprovisionamiento dentro de una Sesión Privilegiada de Aprovisionamiento sobre el Tenant
    /// beneficiario (<c>ElevacionEscrituraAprovisionamientoBehavior</c>). Ni SELECT, ni UPDATE, ni DELETE: contratar es
    /// insertar, y la regla de un solo Producto Contratado de la línea Inbound vigente por Tenant la impone el índice, no
    /// una lectura. <c>cae_app_runtime</c> y <c>cae_app_soporte</c> siguen solo con SELECT: un INSERT del runtime bajo
    /// <c>aislamiento_tenant</c> dejaría a un Tenant concederse su propia línea Inbound, y con ella la lectura entre Tenants
    /// (gate DN-7). El INSERT pasa igualmente por el <c>WITH CHECK</c> de esa política, así que solo entra en el Tenant del
    /// contexto firmado, que es el objetivo de la Sesión Privilegiada.
    /// </para>
    /// <para>
    /// <c>UX_ProductosContratados_InboundVigentePorTenant</c>, índice único parcial (D-B): un Producto Contratado de la línea
    /// Inbound vigente y no eliminado por Tenant beneficiario. Una fila finalizada o eliminada no cuenta, como en el gate.
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo: solo añade un privilegio a un rol y un índice. La ranura anterior
    /// no tiene ningún código que escriba <c>ProductosContratados</c> ni que lea el índice, y su runtime conserva los mismos
    /// privilegios. Crear el índice no falla por datos: hasta esta versión ningún rol de la aplicación escribía la tabla y
    /// ninguna siembra crea filas de la línea Inbound, así que no puede haber dos vigentes del mismo Tenant.
    /// </para>
    /// <para>
    /// El Down retira el privilegio antes que el índice, para que nunca haya escritura sin la regla de unicidad. Las filas de
    /// la línea Inbound que se hubieran contratado se quedan; el Down de <c>AnadeLineaInboundProductoContratado</c> falla si
    /// las hay, y borrarlas es una decisión que no toma la migración.
    /// </para>
    /// </summary>
    public partial class ContrataLineaInboundPorAprovisionamiento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_ProductosContratados_InboundVigentePorTenant",
                table: "ProductosContratados",
                column: "TenantId",
                unique: true,
                filter: "\"Linea\" = 'Inbound' AND \"Estado\" = 'Vigente' AND NOT \"EstaEliminado\"");

            migrationBuilder.Sql(
                """
                GRANT INSERT ON TABLE public."ProductosContratados" TO cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE INSERT ON TABLE public."ProductosContratados" FROM cae_app_aprovisionamiento;
                """);

            migrationBuilder.DropIndex(
                name: "UX_ProductosContratados_InboundVigentePorTenant",
                table: "ProductosContratados");
        }
    }
}
