using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Opción B del 2026-11-15, contrato D2 (A) «sin hueco» (hallazgo 1 de Codex, ronda 3, en la #2256): el gate DN-7 de
    /// <c>app_inbound_tenant_contratista_del_contrato</c> compara el inicio de la línea Inbound con el
    /// <c>statement_timestamp()</c> de la sentencia lectora, no con el <c>now()</c> de su transacción.
    /// <para>
    /// Por qué: en READ COMMITTED cada sentencia toma su instantánea al empezar. Una transacción lectora iniciada antes que
    /// una corrección y que consulta después de su COMMIT ve la sustituida ya Finalizada y la corregida Vigente, con el
    /// inicio en el <c>now()</c> de la transacción escritora; con el <c>now()</c> de la lectora, anterior, el gate no
    /// contaba ninguna de las dos. El instante de la sentencia es posterior al COMMIT que su instantánea ve, así que el
    /// criterio temporal coincide con lo que la instantánea ya ve confirmado y no concede nada más: una línea con el inicio
    /// en el futuro sigue sin contar.
    /// </para>
    /// <para>
    /// Inventario: es la única comparación de vigencia del gate. El fin de vigencia no se compara con ningún reloj: la
    /// sustituida deja de contar por <c>"Estado" = 'Vigente'</c>, así que no queda otra mitad con <c>now()</c>.
    /// </para>
    /// <para>
    /// Lo que no cambia: las políticas RESTRICTIVE de INSERT y UPDATE de <c>cae_app_aprovisionamiento</c>
    /// (<c>aprovisionamiento_solo_contrata_inbound_vigente</c> y <c>aprovisionamiento_solo_finaliza_inbound_vigente</c>)
    /// siguen con su margen de ±5 minutos sobre el <c>now()</c> de la transacción escritora, que es el instante que esa
    /// transacción escribe como inicio o fin. La función se redefine con el mismo texto que dejó
    /// <c>AnadeEstadoContratoAcceso</c> salvo ese reloj: misma firma, mismo propietario y privilegios (CREATE OR REPLACE los
    /// conserva), SECURITY INVOKER, STABLE y el mismo search_path. Compatible con la ranura anterior: no cambia ninguna
    /// tabla ni ninguna llamada. El Down restaura el texto previo.
    /// </para>
    /// </summary>
    public partial class GateInboundConInstanteDeSentencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantContratistaId"
                    FROM public."ContratosAcceso" c
                   WHERE c."TenantId" = p_lector
                     AND c."Id" = p_contrato
                     AND NOT c."EstaEliminado"
                     AND c."Estado" IN ('Activo', 'Finalizado')
                     AND c."FechaInicio" <= p_hoy
                     AND (c."FechaFin" IS NULL OR p_hoy <= c."FechaFin")
                     AND (c."FechaCierreReal" IS NULL OR p_hoy <= c."FechaCierreReal")
                     AND c."TenantContratistaId" IS NOT NULL
                     AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                  WHERE pc."TenantId" = p_lector AND pc."Linea" = 'Inbound'
                                    AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                    AND pc."VigenteDesdeUtc" <= pg_catalog.statement_timestamp());
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El texto exacto que dejó AnadeEstadoContratoAcceso.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_inbound_tenant_contratista_del_contrato(p_lector uuid, p_contrato uuid, p_hoy date) RETURNS uuid
                    LANGUAGE sql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantContratistaId"
                    FROM public."ContratosAcceso" c
                   WHERE c."TenantId" = p_lector
                     AND c."Id" = p_contrato
                     AND NOT c."EstaEliminado"
                     AND c."Estado" IN ('Activo', 'Finalizado')
                     AND c."FechaInicio" <= p_hoy
                     AND (c."FechaFin" IS NULL OR p_hoy <= c."FechaFin")
                     AND (c."FechaCierreReal" IS NULL OR p_hoy <= c."FechaCierreReal")
                     AND c."TenantContratistaId" IS NOT NULL
                     AND EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                                  WHERE pc."TenantId" = p_lector AND pc."Linea" = 'Inbound'
                                    AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                                    AND pc."VigenteDesdeUtc" <= pg_catalog.now());
                $$;
                """);
        }
    }
}
