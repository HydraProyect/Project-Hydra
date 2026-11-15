using System.Reflection;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// El predicado de <see cref="AmparoDeOrigen"/> evaluado en PostgreSQL por
/// <c>public.app_operacion_amparada_por_su_origen</c> (migración <c>AnadeProcedenciaDeOperacionExterna</c>), para los
/// lectores que pueden correr bajo un rol sin <c>SELECT</c> sobre <c>DelegacionesTenant</c>: el comando que registra un
/// Encargo de Administración por Aprovisionamiento escribe como <c>cae_app_aprovisionamiento</c> (Codex r2 en la PR
/// #2237, media 8). La función es SECURITY DEFINER y solo responde un booleano; las columnas de la operación las pone
/// quien ya la leyó bajo su propia RLS.
///
/// <para>
/// Es el mismo predicado que <see cref="AmparoDeOrigen.AmparadasPorSuOrigen"/>, escrito en SQL:
/// <c>FuncionAmparoDeOrigenBajoRuntimeTests</c> compara las dos versiones sobre los mismos casos para que no se
/// separen. <c>EncargoAdministracionRepository</c> la usa en todas sus vías, también bajo
/// <c>cae_app_runtime</c> y <c>cae_app_soporte</c>, para que el Encargo de Administración no tenga dos definiciones.
/// </para>
/// </summary>
internal static class FuncionAmparoDeOrigen
{
    internal const string Esquema = "public";
    internal const string Nombre = "app_operacion_amparada_por_su_origen";

    /// <summary>Solo se traduce a SQL: llamarlo en memoria es un error.</summary>
    internal static bool Ampara(
        Guid propietarioTenantId, Guid operadorTenantId, Guid? origenId, int? revocacionesDelOrigen, DateTime ahora) =>
        throw new InvalidOperationException($"{Nombre} solo se evalúa en PostgreSQL, dentro de una consulta.");

    /// <summary>
    /// Las Asignaciones de Operación que su origen ampara en <paramref name="ahora"/>: la raíz y las internas siempre,
    /// y una externa si la función lo confirma.
    /// </summary>
    internal static IQueryable<AsignacionOperacion> AmparadasPorSuOrigenEnBase(
        this IQueryable<AsignacionOperacion> operaciones, DateTime ahora) =>
        operaciones.Where(o =>
            o.PropietarioTenantId == o.OperadorTenantId
            || Ampara(o.PropietarioTenantId, o.OperadorTenantId, o.OrigenId, o.RevocacionesDelOrigen, ahora));

    internal static void MapearAmparoDeOrigen(this ModelBuilder builder) =>
        builder.HasDbFunction(typeof(FuncionAmparoDeOrigen).GetMethod(
                nameof(Ampara), BindingFlags.Static | BindingFlags.NonPublic)!)
            .HasSchema(Esquema)
            .HasName(Nombre);
}
