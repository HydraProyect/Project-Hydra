using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Exportacion;
using MediatR;

namespace CaeManager.Application.Exportacion;

/// <summary>
/// El Administrador del Tenant propietario pide la exportación completa de su Tenant en autoservicio, sin Actor de
/// Plataforma TALVEG (#2008, D-8). Devuelve la contraseña del zip, y es la única vez que sale: la fila la guarda
/// protegida hasta que el servicio de fondo cifra el fichero, y entonces la borra. El fichero lo genera
/// <c>GeneradorExportacionCompletaHostedService</c> bajo la RLS del propio Tenant.
/// <para>Como mucho una solicitud cada 24 horas por Tenant, contando todas las registradas, también las fallidas
/// (OWASP API4:2023, ASVS 4.0.3 V11.1.4): un fallo repetible no permite volver a pedirla sin límite. Serializada con
/// un candado por Tenant (y un índice único parcial que impide dos solicitudes Pendiente o EnCurso del mismo
/// Tenant).</para>
/// </summary>
[AreaDelEncargo("Exportacion")]
public sealed record SolicitarExportacionCompletaCommand : ICommand<SolicitudExportacionCompletaRegistrada>, IComandoDePortabilidadDelTenantPropietario;

public static class ErroresSolicitudExportacionCompleta
{
    public static readonly Error UnaCadaVeinticuatroHoras = Error.Crear(
        "ExportacionCompleta.UnaCadaVeinticuatroHoras",
        "Tu organización ya pidió una exportación completa en las últimas 24 horas, aunque no llegara a generarse. Podrás pedir otra cuando pase ese plazo.");
}

public sealed class SolicitarExportacionCompletaCommandHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IActorAuditoria actorAuditoria,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ITenantsQueryContext tenants,
    ICandadoExportacionCompleta candado,
    ISolicitudExportacionCompletaRepository repositorio,
    IProtectorContrasenaExportacion protector,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<SolicitarExportacionCompletaCommand, Result<SolicitudExportacionCompletaRegistrada>>
{
    public async Task<Result<SolicitudExportacionCompletaRegistrada>> Handle(
        SolicitarExportacionCompletaCommand request, CancellationToken cancellationToken)
    {
        var (error, usuarioId, _) = await AutoridadExportacionCompleta.ComprobarAsync(
            usuarioActual, tenantActual, actorAuditoria, sesionPrivilegiada, administradorDelTenant, tenants, cancellationToken);
        if (error is not null)
            return Result.Fallo<SolicitudExportacionCompletaRegistrada>(error);

        var actor = await actorAuditoria.ObtenerAsync();
        SolicitudExportacionCompletaRegistrada? registrada = null;

        var resultado = await candado.EjecutarAsync(async ct =>
        {
            var ahora = reloj.GetUtcNow().UtcDateTime;
            if (await repositorio.ExisteRegistradaDesdeAsync(ahora - SolicitudExportacionCompleta.IntervaloMinimo, ct))
                return Result.Fallo(ErroresSolicitudExportacionCompleta.UnaCadaVeinticuatroHoras);

            var contrasena = ArchivoExportacion.GenerarContrasena();
            var solicitud = new SolicitudExportacionCompleta(usuarioId, protector.Proteger(contrasena), ahora);
            repositorio.Agregar(solicitud);
            repositorio.AgregarAuditoria(AuditoriaExportacionCompleta.DelActor(
                actor, solicitud.Id, AuditoriaExportacionCompleta.FaseSolicitada));
            await unitOfWork.SaveChangesAsync(ct);

            registrada = new SolicitudExportacionCompletaRegistrada(solicitud.Id, contrasena, ahora);
            return Result.Exito();
        }, cancellationToken);

        return resultado.EsExitoso && registrada is not null
            ? Result.Exito(registrada)
            : Result.Fallo<SolicitudExportacionCompletaRegistrada>(resultado.Error);
    }
}
