using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Opción B del 2026-11-15, contrato D2 (A) «sin hueco» (hallazgo 1 de Codex, rondas 3 y 4, en la #2256): el gate DN-7
    /// de <c>app_inbound_tenant_contratista_del_contrato</c> compara el inicio de la línea Inbound con
    /// <c>clock_timestamp()</c>, el reloj del momento en que se evalúa, no con el <c>now()</c> de la transacción lectora ni
    /// con el <c>statement_timestamp()</c> de su sentencia exterior.
    /// <para>
    /// Por qué: una corrección finaliza la línea sustituida y contrata la corregida con el inicio en el <c>now()</c> de la
    /// transacción escritora. Quien ve el COMMIT ve la sustituida ya Finalizada, así que solo le queda la corregida, y tiene
    /// que contarla aunque empezara antes que la escritora. Con <c>now()</c> fallaba una transacción lectora en READ
    /// COMMITTED iniciada antes de la corrección (cada sentencia toma su instantánea al empezar). Con
    /// <c>statement_timestamp()</c> fallaba una sola sentencia exterior: las funciones públicas de lectura
    /// (<c>app_inbound_documentos_autorizados</c> y demás) son PL/pgSQL VOLATILE, y cada consulta interna toma una
    /// instantánea nueva mientras <c>statement_timestamp()</c> sigue siendo el inicio de la sentencia que las llamó.
    /// </para>
    /// <para>
    /// Por qué es correcto <c>clock_timestamp()</c> dentro de una función STABLE: una función STABLE no toma instantánea
    /// propia, usa la de la consulta que la llama, y esa instantánea se toma antes de que la consulta evalúe la función.
    /// Así, para cualquier fila de Producto Contratado que la instantánea vea confirmada:
    /// <c>now()</c> de la escritora (su inicio de vigencia) &lt; COMMIT &lt; instantánea ≤ <c>clock_timestamp()</c>. El
    /// criterio temporal nunca deja fuera una fila que la instantánea ya ve confirmada, y no concede nada que no vea: una
    /// corrección aún no confirmada no existe para esa instantánea, que sigue viendo Vigente la sustituida; la razón es la
    /// visibilidad, no la hora. Una línea con el inicio en el futuro (el alta admite el reloj de la aplicación hasta 5
    /// minutos adelantado) sigue sin contar hasta que llega su hora. La función sigue siendo STABLE: dentro de una misma
    /// consulta el reloj solo avanza, y ninguna fila del resultado depende de que dos evaluaciones devuelvan el mismo valor.
    /// La cláusula SET impide que el planificador la incruste en la consulta llamadora, que no cambia ningún plan.
    /// </para>
    /// <para>
    /// Residual declarado (hallazgo de Codex, ronda 5, aceptado por Chris el 2026-11-15, «Opción 1»): el alta admite
    /// <c>VigenteDesdeUtc</c> hasta 5 minutos en el futuro (holgura del reloj de la aplicación en
    /// <c>aprovisionamiento_solo_contrata_inbound_vigente</c>). En torno a ese inicio, dos evaluaciones del gate dentro de
    /// una misma consulta pueden dar valores distintos, y PostgreSQL puede tratar como constante el de una de ellas: la
    /// promesa de STABLE no se cumple en ese instante. Es aceptable porque nunca concede antes del inicio real: el reloj de
    /// cada evaluación es posterior a la instantánea y solo avanza. El arreglo de raíz (que la vigencia nunca quede en el
    /// futuro al escribir, o un instante fijo pasado como parámetro por la cadena de funciones) es la issue #2259.
    /// </para>
    /// <para>
    /// Inventario: es la única comparación de vigencia del gate y de sus vecinas de lectura del Producto Contratado. El fin
    /// de vigencia (<c>VigenteHastaUtc</c>) no se compara con ningún reloj: la sustituida deja de contar por
    /// <c>"Estado" = 'Vigente'</c>, así que no queda otra cota con un reloj distinto. Fuera de este alcance, y sin cambios:
    /// el <c>p_hoy</c> con que las funciones públicas llaman al gate es el día de negocio de su transacción
    /// (<c>now()</c> en Europe/Madrid) y decide las fechas del Contrato de Acceso y de la autorización, no la vigencia del
    /// Producto Contratado; una transacción lectora que cruce la medianoche usa el día en que empezó, como cualquier otra
    /// lectura con día de negocio.
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
    public partial class GateInboundConRelojDeEvaluacion : Migration
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
                                    AND pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp());
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
