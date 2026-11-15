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
    /// <c>aprovisionamiento_solo_contrata_inbound_vigente</c>, política RESTRICTIVE de INSERT solo para
    /// <c>cae_app_aprovisionamiento</c> (como <c>solo_nace_abierto_o_denegado</c> de <c>UsosConexionPlataformaCae</c>):
    /// el rol elevado lo adoptan todos los comandos de aprovisionamiento, así que el GRANT solo no basta para que lo único
    /// que inserte tenga la forma de lo que hace <c>ContratarLineaInboundCommand</c>: una fila de la línea Inbound,
    /// vigente, sin fin de vigencia, no eliminada, del Tenant del contexto firmado y con la vigencia empezando al contratar.
    /// Una fila de <c>OutboundGestionPropia</c>, una ya finalizada o una con la vigencia empezada más de 5 minutos antes
    /// o después del <c>now()</c> de la transacción se rechaza en la base aunque el código lo intentara (hallazgo 2 de Codex, ronda 1, y hallazgo 4, ronda 2). «Al contratar» es el
    /// <c>now()</c> de la transacción con 5 minutos de margen en los dos sentidos, la ventana de
    /// <c>sesion_privilegiada_apertura</c> para el reloj de la aplicación. La política no comprueba el resto de valores
    /// (Pagador, tramo, modalidad), que ya restringen las restricciones CHECK de la tabla. Repite la condición de Tenant de
    /// <c>aislamiento_tenant</c> para no depender de que esa política permisiva siga escrita igual.
    /// </para>
    /// <para>
    /// <c>UX_ProductosContratados_InboundVigentePorTenant</c>, índice único parcial (D-B): un Producto Contratado de la línea
    /// Inbound vigente y no eliminado por Tenant beneficiario. Una fila finalizada o eliminada no cuenta, como en el gate.
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo: solo añade un privilegio a un rol, una política que solo
    /// restringe a ese rol y un índice. La ranura anterior no tiene ningún código que escriba <c>ProductosContratados</c>
    /// ni que lea el índice, y su runtime conserva los mismos privilegios y políticas. Crear el índice no falla por datos: hasta esta versión ningún rol de la aplicación escribía la tabla y
    /// ninguna siembra crea filas de la línea Inbound, así que no puede haber dos vigentes del mismo Tenant.
    /// </para>
    /// <para>
    /// El Down retira el privilegio antes que la política y el índice, para que nunca haya escritura sin la regla de
    /// unicidad ni sin la restricción de línea. Las filas de
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

            // La política va antes que el privilegio: nunca hay un instante en que el rol elevado pueda insertar otra línea.
            migrationBuilder.Sql(
                """
                CREATE POLICY aprovisionamiento_solo_contrata_inbound_vigente ON public."ProductosContratados"
                    AS RESTRICTIVE FOR INSERT TO cae_app_aprovisionamiento
                    WITH CHECK ("Linea" = 'Inbound' AND "Estado" = 'Vigente' AND "VigenteHastaUtc" IS NULL
                                AND NOT "EstaEliminado"
                                AND "VigenteDesdeUtc" >= now() - interval '5 minutes'
                                AND "VigenteDesdeUtc" <= now() + interval '5 minutes'
                                AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                GRANT INSERT ON TABLE public."ProductosContratados" TO cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE INSERT ON TABLE public."ProductosContratados" FROM cae_app_aprovisionamiento;
                DROP POLICY IF EXISTS aprovisionamiento_solo_contrata_inbound_vigente ON public."ProductosContratados";
                """);

            migrationBuilder.DropIndex(
                name: "UX_ProductosContratados_InboundVigentePorTenant",
                table: "ProductosContratados");
        }
    }
}
