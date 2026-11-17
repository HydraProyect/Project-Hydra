using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// <c>public.app_operacion_inbound_amparada(uuid)</c> (migración <c>AnadeAmparoDeOperacionInbound</c>): el amparo de una
/// Asignación de Operación Inbound externa (decisión de Chris del 2026-11-16, opción A de B-02b). Sin DelegacionTenant:
/// basta con que el Tenant propietario tenga la línea Inbound de su Producto Contratado vigente.
///
/// <para>
/// Va en PostgreSQL porque quien pregunta es una persona del Operador CAE externo, y la RLS de
/// <c>ProductosContratados</c> (<c>aislamiento_tenant</c>) no le deja leer la fila del Tenant TCE: ni en su Tenant de
/// origen, donde se elige el contexto, ni debe dejársela, porque la fila lleva el Pagador TALVEG y el tramo. La función
/// es SECURITY DEFINER, recibe solo el Id de la operación y responde solo un booleano, con el mismo criterio de
/// posición que <c>app_operacion_amparada_por_su_origen</c> (#2237): solo responde sobre una operación de la que el
/// Tenant activo o el de origen del contexto firmado es uno de los dos extremos.
/// </para>
///
/// <para>
/// No mira la vigencia ni el estado de la propia operación: los pone quien consulta
/// (<c>TenantsBeneficiariosAutorizados.OperacionesInboundQueAutorizan</c>), como hace la vía de Outbound con el amparo
/// de origen. La vigencia de la línea es la del gate DN-7 (<c>app_inbound_tenant_contratista_del_contrato</c>), con el
/// reloj del momento en que se evalúa.
/// </para>
/// </summary>
internal static class FuncionAmparoInbound
{
    internal const string Esquema = "public";
    internal const string Nombre = "app_operacion_inbound_amparada";

    /// <summary>Solo se traduce a SQL: llamarlo en memoria es un error.</summary>
    internal static bool Ampara(Guid asignacionOperacionId) =>
        throw new InvalidOperationException($"{Nombre} solo se evalúa en PostgreSQL, dentro de una consulta.");

    internal static void MapearAmparoInbound(this ModelBuilder builder) =>
        builder.HasDbFunction(() => Ampara(default))
            .HasSchema(Esquema)
            .HasName(Nombre);
}
