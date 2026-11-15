using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// El predicado de <see cref="AmparoDeOrigen"/> evaluado en PostgreSQL por
/// <c>public.app_operacion_amparada_por_su_origen(uuid)</c> (migración <c>AnadeProcedenciaDeOperacionExterna</c>), para
/// los lectores que pueden correr bajo un rol sin <c>SELECT</c> sobre <c>DelegacionesTenant</c>: el comando que registra
/// un Encargo de Administración por Aprovisionamiento escribe como <c>cae_app_aprovisionamiento</c> (Codex r2 en la PR
/// #2237, media 8). La función es SECURITY DEFINER, recibe solo el Id de la operación y responde solo un booleano: lee
/// ella misma el par, el origen y el contador sellado, y compara la caducidad con la hora de la base, para no servir de
/// oráculo de <c>Revocaciones</c> ni de <c>ExpiraEnUtc</c> (ronda 3 de Codex, media 4).
///
/// <para>
/// Es el mismo predicado que <see cref="AmparoDeOrigen.AmparadasPorSuOrigen"/>, escrito en SQL:
/// <c>FuncionAmparoDeOrigenBajoRuntimeTests</c> compara las dos versiones sobre los mismos casos para que no se
/// separen. Con una diferencia deliberada: la función solo lee Asignaciones de Operación Outbound (trinquete de
/// acceso crudo sin Servicio, ADR-013 § 4.6), porque solo las operaciones Outbound nacen de una delegación de
/// Operador CAE externo (el único escritor de operaciones externas es <c>AsignacionesOperativasWriter</c>, con
/// <c>ServicioCae.Outbound</c>). Para una operación de otro servicio responde <c>false</c>, también si es interna;
/// por eso <see cref="AmparadasPorSuOrigenEnBase"/> deja pasar las internas antes de llamarla. <c>EncargoAdministracionRepository</c> la usa en todas sus vías, también bajo
/// <c>cae_app_runtime</c> y <c>cae_app_soporte</c>, para que el Encargo de Administración no tenga dos definiciones; y
/// el trigger <c>TR_EncargosAdministracion_ExigeAmparoDeOrigen</c> la vuelve a evaluar al insertar el encargo, con la
/// DelegacionTenant de origen bloqueada.
/// </para>
/// </summary>
internal static class FuncionAmparoDeOrigen
{
    internal const string Esquema = "public";
    internal const string Nombre = "app_operacion_amparada_por_su_origen";

    /// <summary>Solo se traduce a SQL: llamarlo en memoria es un error.</summary>
    internal static bool Ampara(Guid asignacionOperacionId) =>
        throw new InvalidOperationException($"{Nombre} solo se evalúa en PostgreSQL, dentro de una consulta.");

    /// <summary>
    /// Las Asignaciones de Operación que su origen ampara ahora, según la hora de la base: la raíz y las internas
    /// siempre, y una externa si la función lo confirma.
    /// </summary>
    internal static IQueryable<AsignacionOperacion> AmparadasPorSuOrigenEnBase(this IQueryable<AsignacionOperacion> operaciones) =>
        operaciones.Where(o => o.PropietarioTenantId == o.OperadorTenantId || Ampara(o.Id));

    internal static void MapearAmparoDeOrigen(this ModelBuilder builder) =>
        builder.HasDbFunction(() => Ampara(default))
            .HasSchema(Esquema)
            .HasName(Nombre);
}
