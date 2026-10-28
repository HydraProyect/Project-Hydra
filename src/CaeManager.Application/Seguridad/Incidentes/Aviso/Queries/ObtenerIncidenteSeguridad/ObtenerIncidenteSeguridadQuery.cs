using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using MediatR;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso.Queries.ObtenerIncidenteSeguridad;

/// <summary>
/// Lo que la página del incidente muestra (enlace de la alerta, <c>/plataforma/incidentes/{id}</c>): la señal,
/// los Tenants afectados y a quién se avisaría en cada uno (marcando los que no tienen contacto de seguridad
/// validado), el borrador del aviso, los plazos desde la confirmación y el registro del incidente.
///
/// <para>
/// Lo lee un Actor de Plataforma TALVEG con concesión AdminPlataforma global, fuera de una Sesión Privilegiada;
/// sin ella, fallo y no un incidente vacío. Lo que acota las filas es la RLS (<c>actor_de_plataforma_lee</c>).
/// </para>
/// </summary>
public record ObtenerIncidenteSeguridadQuery(Guid IncidenteId) : IRequest<Result<VistaIncidenteSeguridad>>;

public record VistaIncidenteSeguridad(
    IncidenteSeguridadDto Incidente,
    string Referencia,
    string DescripcionSenal,
    IReadOnlyList<TenantAfectadoAviso> Tenants,
    ContenidoAvisoIncidente BorradorPrimerAviso,
    ContenidoAvisoIncidente BorradorNotificacionCompleta,
    DateTime? PrimerAvisoVenceEnUtc,
    DateTime? NotificacionCompletaVenceEnUtc,
    Guid ActorUsuarioId,
    Error? MotivoParaNoDecidir,
    bool SinCorreoContactoTalveg);

/// <summary>Un Tenant afectado y a quién se le avisa.</summary>
public record TenantAfectadoAviso(
    Guid TenantId, string Nombre, bool SinContactoValidado, IReadOnlyList<DestinatarioAvisoSeguridad> Destinatarios);

public class ObtenerIncidenteSeguridadQueryHandler(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IAutorizacionAdminPlataforma autorizacion,
    IIncidentesSeguridadPlataforma plataforma,
    IContactosSeguridadPlataforma contactos,
    IOptions<OpcionesIncidentesSeguridad> opciones)
    : IRequestHandler<ObtenerIncidenteSeguridadQuery, Result<VistaIncidenteSeguridad>>
{
    public async Task<Result<VistaIncidenteSeguridad>> Handle(ObtenerIncidenteSeguridadQuery request, CancellationToken cancellationToken)
    {
        if (await AutorizacionAvisoIncidenteSeguridad.ActorQueLeeAsync(
                currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken) is not { } actor)
            return Result.Fallo<VistaIncidenteSeguridad>(ErroresIncidenteSeguridad.SoloActorDePlataforma);

        var incidente = await plataforma.ObtenerAsync(request.IncidenteId, cancellationToken);
        if (incidente is null)
            return Result.Fallo<VistaIncidenteSeguridad>(ErroresIncidenteSeguridad.NoEncontrado);

        var nombres = await plataforma.NombresTenantsAsync(incidente.TenantsAfectados, cancellationToken);
        var destinatarios = await contactos.ResolverDestinatariosAsync(incidente.TenantsAfectados, cancellationToken);
        if (destinatarios.EsFallido)
            return Result.Fallo<VistaIncidenteSeguridad>(ErroresIncidenteSeguridad.SoloActorDePlataforma);

        var porTenant = destinatarios.Valor.ToDictionary(d => d.TenantId);
        IReadOnlyList<TenantAfectadoAviso> tenants = [.. incidente.TenantsAfectados.Select(id =>
            porTenant.TryGetValue(id, out var d)
                ? new TenantAfectadoAviso(id, nombres.GetValueOrDefault(id, id.ToString("D")), d.SinContactoValidado, d.Destinatarios)
                : new TenantAfectadoAviso(id, nombres.GetValueOrDefault(id, id.ToString("D")), true, []))];

        var correoTalveg = opciones.Value.CorreoContactoSeguridadTalveg;
        var motivo = await AutorizacionAvisoIncidenteSeguridad.MotivoParaNoDecidirAsync(
            currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken);

        return Result.Exito(new VistaIncidenteSeguridad(
            incidente,
            PlantillaAvisoIncidente.Referencia(incidente.Id, incidente.DetectadoEnUtc),
            PlantillaAvisoIncidente.DescripcionSenal(incidente.Senal, incidente.Evidencia),
            tenants,
            PlantillaAvisoIncidente.BorradorPrimerAviso(incidente.Senal, correoTalveg),
            PlantillaAvisoIncidente.BorradorNotificacionCompleta(incidente.Senal, correoTalveg),
            incidente.ConfirmadoEnUtc + PlazosAvisoIncidenteSeguridad.PrimerAviso,
            incidente.ConfirmadoEnUtc + PlazosAvisoIncidenteSeguridad.NotificacionCompleta,
            actor,
            motivo,
            string.IsNullOrWhiteSpace(correoTalveg)));
    }
}
