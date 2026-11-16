using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Issue #2259: la vigencia del Producto Contratado nunca queda en el futuro al escribirla, para que el gate DN-7 de
    /// <c>app_inbound_tenant_contratista_del_contrato</c> vuelva a cumplir de verdad la promesa de STABLE.
    /// <para>
    /// Causa: el gate compara el inicio de la línea Inbound con <c>clock_timestamp()</c> (<c>GateInboundConRelojDeEvaluacion</c>),
    /// y el alta admitía un <c>VigenteDesdeUtc</c> hasta 5 minutos posterior al <c>now()</c> de la transacción escritora
    /// (la holgura del reloj de la aplicación en <c>aprovisionamiento_solo_contrata_inbound_vigente</c>). Con un inicio
    /// futuro, dos evaluaciones del gate dentro de una misma consulta podían caer a ambos lados de ese instante y devolver
    /// valores distintos, y PostgreSQL puede tratar como constante el resultado de una función STABLE (residual de la ronda
    /// 5 de Codex en la #2256).
    /// </para>
    /// <para>
    /// Arreglo (opción 1 de la issue): las dos políticas RESTRICTIVE de <c>cae_app_aprovisionamiento</c>, el único rol de la
    /// aplicación que escribe <c>ProductosContratados</c>, rechazan un inicio (INSERT) o un fin (UPDATE) posterior al
    /// <c>now()</c> de la transacción. Conservan el margen de 5 minutos hacia atrás. El alta deja de tomar el instante del
    /// reloj de la aplicación y toma, como la baja y la corrección, el <c>now()</c> de su propia transacción
    /// (<c>IProductoContratadoRepository.InstanteDeLaTransaccionAsync</c>), así que un reloj de la aplicación adelantado no
    /// la rechaza.
    /// </para>
    /// <para>
    /// Por qué basta para que el gate sea STABLE: toda fila de Producto Contratado que una instantánea lectora ve confirmada
    /// cumple <c>VigenteDesdeUtc</c> ≤ <c>now()</c> de la escritora &lt; COMMIT &lt; instantánea ≤ <c>clock_timestamp()</c>
    /// de cualquier evaluación del gate. La comparación del gate es verdadera para todas ellas en cualquier momento de la
    /// consulta, así que su resultado depende solo de la instantánea. El gate no cambia: su comparación sigue como defensa
    /// para una fila que no pase por estas políticas.
    /// </para>
    /// <para>
    /// Residual declarado: el propietario de la base (migraciones y mantenimiento) no pasa por las políticas y podría escribir
    /// un inicio futuro; el gate sigue sin conceder antes de ese inicio. Una fila escrita antes de esta migración con el inicio
    /// en el futuro queda como mucho 5 minutos por delante de su <c>now()</c>, así que al desplegar ya está en el pasado o lo
    /// estará en esos minutos.
    /// </para>
    /// <para>
    /// Compatible con la ranura anterior durante el relevo: solo estrecha el WITH CHECK de dos políticas. La ranura anterior
    /// contrata con el reloj de la aplicación, que toma antes de abrir la transacción que inserta; con la aplicación y la base
    /// en la misma máquina ese instante es anterior al <c>now()</c> de la transacción y la política lo admite. Solo con el
    /// reloj de la aplicación adelantado respecto al de la base se rechazaría su alta, sin escribir nada. Su baja y su
    /// corrección ya usan el <c>now()</c> de la transacción. Sin cambios de modelo: el Designer repite el de la migración
    /// anterior. El Down restaura el margen de ± 5 minutos.
    /// </para>
    /// </summary>
    public partial class VigenciaProductoContratadoNuncaFutura : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ALTER POLICY cambia el WITH CHECK en el sitio: nunca hay un instante sin política.
            migrationBuilder.Sql(
                """
                ALTER POLICY aprovisionamiento_solo_contrata_inbound_vigente ON public."ProductosContratados"
                    WITH CHECK ("Linea" = 'Inbound' AND "Estado" = 'Vigente' AND "VigenteHastaUtc" IS NULL
                                AND NOT "EstaEliminado"
                                AND "VigenteDesdeUtc" >= now() - interval '5 minutes'
                                AND "VigenteDesdeUtc" <= now() + interval '5 minutes'
                                AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                ALTER POLICY aprovisionamiento_solo_finaliza_inbound_vigente ON public."ProductosContratados"
                    WITH CHECK ("Linea" = 'Inbound' AND "Estado" = 'Finalizado' AND NOT "EstaEliminado"
                                AND "VigenteHastaUtc" >= now() - interval '5 minutes'
                                AND "VigenteHastaUtc" <= now() + interval '5 minutes'
                                AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El texto exacto que dejaron ContrataLineaInboundPorAprovisionamiento y FinalizaLineaInboundPorAprovisionamiento.
            migrationBuilder.Sql(
                """
                ALTER POLICY aprovisionamiento_solo_contrata_inbound_vigente ON public."ProductosContratados"
                    WITH CHECK ("Linea" = 'Inbound' AND "Estado" = 'Vigente' AND "VigenteHastaUtc" IS NULL
                                AND NOT "EstaEliminado"
                                AND "VigenteDesdeUtc" >= now() - interval '5 minutes'
                                AND "VigenteDesdeUtc" <= now() + interval '5 minutes'
                                AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                ALTER POLICY aprovisionamiento_solo_finaliza_inbound_vigente ON public."ProductosContratados"
                    WITH CHECK ("Linea" = 'Inbound' AND "Estado" = 'Finalizado' AND NOT "EstaEliminado"
                                AND "VigenteHastaUtc" >= now() - interval '5 minutes'
                                AND "VigenteHastaUtc" <= now() + interval '5 minutes'
                                AND "TenantId" = (SELECT public.app_ctx_tenant_id()));
                """);
        }
    }
}
