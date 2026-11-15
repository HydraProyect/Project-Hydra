using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Opción B del 2026-11-15: la baja y la corrección de la línea Inbound del Producto Contratado
    /// (<c>DarDeBajaLineaInboundCommand</c> y <c>CorregirLineaInboundCommand</c>), bajo Sesión Privilegiada de
    /// Aprovisionamiento, con un UPDATE acotado para <c>cae_app_aprovisionamiento</c>. Las dos solo finalizan la fila
    /// vigente; la corrección contrata después otra por el INSERT de <c>ContrataLineaInboundPorAprovisionamiento</c>, así
    /// que lo contratado nunca se sobrescribe (DECISION_LOG 2026-11-05 (a)).
    /// <para>
    /// Privilegios por columna, el mínimo de la baja: <c>UPDATE ("Estado", "VigenteHastaUtc")</c>, lo único que cambia una
    /// finalización, y <c>SELECT ("Id", "TenantId", "Linea", "Estado", "EstaEliminado")</c>, lo que lee su WHERE (el Id de
    /// la fila, la línea y el estado de partida, más el Tenant y la papelera del filtro global de la entidad). El rol
    /// elevado sigue sin leer el Pagador TALVEG, el tramo, la modalidad ni las fechas, y sin poder cambiar ninguna otra
    /// columna. <c>cae_app_runtime</c> y <c>cae_app_soporte</c> no ganan nada: un UPDATE del runtime bajo
    /// <c>aislamiento_tenant</c> dejaría a un Tenant reabrirse o cerrarse su propia línea Inbound (gate DN-7).
    /// </para>
    /// <para>
    /// <c>aprovisionamiento_solo_finaliza_inbound_vigente</c>, RESTRICTIVE de UPDATE solo para el rol elevado, que adoptan
    /// todos los comandos de aprovisionamiento: la única transición es de una fila de la línea Inbound vigente, no eliminada
    /// y del Tenant del contexto firmado (USING) a la misma fila Finalizado, con el fin de vigencia en el <c>now()</c> de la
    /// transacción ± 5 minutos (WITH CHECK), el margen del INSERT para el reloj de la aplicación. La baja se registra cuando
    /// ocurre: el cierre del periodo es manual hasta que se modele la Subscription (condiciones de facturación, baja y salida
    /// § 2.4, 4.2 y 4.4). Que el fin no quede antes del inicio lo impone ya <c>CK_ProductosContratados_EstadoYVigencia</c>.
    /// </para>
    /// <para>
    /// <c>aprovisionamiento_solo_lee_inbound</c>, RESTRICTIVE de SELECT para el rol elevado: de las columnas que puede leer,
    /// solo las filas de la línea Inbound del Tenant del contexto firmado. Las dos restrictivas repiten la condición de Tenant
    /// de <c>aislamiento_tenant</c> para no depender de que esa permisiva siga escrita igual.
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo: solo añade privilegios de columna a un rol y dos políticas que
    /// solo lo restringen a él. La ranura anterior no lee ni actualiza la tabla con ese rol, y su INSERT no lleva RETURNING,
    /// así que la política de SELECT no le afecta. Sin cambios de modelo: el Designer repite el de la migración anterior.
    /// </para>
    /// <para>
    /// El Down retira los privilegios antes que las políticas, para que nunca haya UPDATE sin la regla de transición. Las
    /// filas finalizadas se quedan: son histórico.
    /// </para>
    /// </summary>
    public partial class FinalizaLineaInboundPorAprovisionamiento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Las políticas van antes que los privilegios: nunca hay un instante en que el rol elevado pueda actualizar o
            // leer sin ellas.
            migrationBuilder.Sql(
                """
                CREATE POLICY aprovisionamiento_solo_lee_inbound ON public."ProductosContratados"
                    AS RESTRICTIVE FOR SELECT TO cae_app_aprovisionamiento
                    USING ("Linea" = 'Inbound' AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                CREATE POLICY aprovisionamiento_solo_finaliza_inbound_vigente ON public."ProductosContratados"
                    AS RESTRICTIVE FOR UPDATE TO cae_app_aprovisionamiento
                    USING ("Linea" = 'Inbound' AND "Estado" = 'Vigente' AND NOT "EstaEliminado"
                           AND "TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("Linea" = 'Inbound' AND "Estado" = 'Finalizado' AND NOT "EstaEliminado"
                                AND "VigenteHastaUtc" >= now() - interval '5 minutes'
                                AND "VigenteHastaUtc" <= now() + interval '5 minutes'
                                AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                GRANT SELECT ("Id", "TenantId", "Linea", "Estado", "EstaEliminado")
                    ON TABLE public."ProductosContratados" TO cae_app_aprovisionamiento;
                GRANT UPDATE ("Estado", "VigenteHastaUtc")
                    ON TABLE public."ProductosContratados" TO cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE UPDATE ("Estado", "VigenteHastaUtc")
                    ON TABLE public."ProductosContratados" FROM cae_app_aprovisionamiento;
                REVOKE SELECT ("Id", "TenantId", "Linea", "Estado", "EstaEliminado")
                    ON TABLE public."ProductosContratados" FROM cae_app_aprovisionamiento;
                DROP POLICY IF EXISTS aprovisionamiento_solo_finaliza_inbound_vigente ON public."ProductosContratados";
                DROP POLICY IF EXISTS aprovisionamiento_solo_lee_inbound ON public."ProductosContratados";
                """);
        }
    }
}
