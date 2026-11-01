using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Seguridad.Incidentes;

/// <summary>
/// <see cref="IRegistroIncidentesSeguridad"/> sobre PostgreSQL.
///
/// <para>
/// <b>Por la función, no por EF.</b> Ningún rol de la aplicación tiene INSERT ni UPDATE
/// sobre <c>IncidentesSeguridadSospechados</c>, y su única política RLS es de lectura
/// para el Actor de Plataforma TALVEG: quien dispara la señal (cualquier usuario de
/// cualquier Tenant) no puede leer los incidentes, así que tampoco podría deduplicar
/// consultándolos. La función <c>app_registrar_incidente_seguridad</c> (SECURITY DEFINER)
/// deduplica bajo un cerrojo transaccional por señal y sujeto, crea el incidente con su
/// primera transición y devuelve solo su identificador.
/// </para>
///
/// <para>
/// <b>Ámbito propio.</b> Cada registro abre su ámbito de servicios y, con él, su propia
/// conexión: si el DbContext de la petición estuviera dentro de una transacción, un
/// error aquí la abortaría, y detectar no puede romper la operación que disparó la señal.
/// En una petición HTTP el contexto RLS (Tenant y usuario) fluye por el ámbito. La
/// función no autoriza con él, pero sí lo exige y lo contrasta: sin contexto firmado
/// rechaza; con usuario lo guarda en <c>ContextoUsuarioId</c>, columna que solo escribe la
/// función, y solo agrupa con ese mismo usuario, y si es distinto del sujeto lo marca
/// además; y sin usuario (el enlace «No he sido yo» llega sin sesión, y este ámbito nuevo
/// no hereda la identidad de un circuito de Blazor) lo marca también, sin inventar un
/// Actor, y la detección nunca agrupa ni es agrupada.
/// </para>
/// </summary>
public sealed class RegistroIncidentesSeguridadEnBase(IServiceScopeFactory ambitos) : IRegistroIncidentesSeguridad
{
    public async Task<IncidenteRegistrado> RegistrarAsync(
        SenalIncidenteSeguridad senal, string sujetoClave, IReadOnlyCollection<Guid> tenantsAfectados,
        EvidenciaIncidenteSeguridad evidencia, TimeSpan ventanaDeduplicacion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sujetoClave) || sujetoClave.Length > IncidenteSeguridadSospechado.LongitudMaximaSujeto)
            throw new ArgumentException("El sujeto del incidente es obligatorio y corto.", nameof(sujetoClave));

        var incidenteId = IdentificadorEntidad.Nuevo();
        var transicionId = IdentificadorEntidad.Nuevo();
        var tenants = tenantsAfectados.Distinct().ToArray();
        var json = evidencia.ComoJson();
        var segundos = (int)Math.Clamp(ventanaDeduplicacion.TotalSeconds, 60, 86_400);
        var codigoSenal = senal.ToString();

        using var ambito = ambitos.CreateScope();
        var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        // Los siete valores van parametrizados por EF.
        var fila = await db.Database
            .SqlQuery<FilaIncidenteRegistrado>(
                $"SELECT incidente_id AS \"IncidenteId\", es_nuevo AS \"EsNuevo\" FROM app_registrar_incidente_seguridad({incidenteId}, {transicionId}, {codigoSenal}, {sujetoClave}, {tenants}, {json}::jsonb, {segundos})")
            .ToListAsync(ct);

        var resultado = fila.Single();
        return new IncidenteRegistrado(resultado.IncidenteId, resultado.EsNuevo);
    }

    private sealed record FilaIncidenteRegistrado(Guid IncidenteId, bool EsNuevo);
}
