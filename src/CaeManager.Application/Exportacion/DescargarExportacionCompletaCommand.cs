using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Exportacion;
using MediatR;

namespace CaeManager.Application.Exportacion;

/// <summary>
/// Descarga del zip de una exportación completa ya generada (#2008, D-8): la misma regla que la solicitud
/// (<see cref="AutoridadExportacionCompleta"/>, con TOTP reciente) y solo antes de que caduque. Es un Command y no
/// una Query porque cada descarga queda en la auditoría, en el mismo guardado que la autoriza: si la fila no se
/// guarda, no se entrega el fichero.
/// <para>Hueco declarado: el punto de entrada HTTP que sirve el flujo llega con la pantalla, en una PR posterior.</para>
/// </summary>
[AreaDelEncargo("Exportacion")]
public sealed record DescargarExportacionCompletaCommand(Guid SolicitudId) : ICommand<ExportacionCompletaDescargable>, IComandoDePortabilidadDelTenantPropietario;

public static class ErroresDescargaExportacionCompleta
{
    public static readonly Error NoDisponible = Error.Crear(
        "ExportacionCompleta.NoDisponible",
        "Esta exportación no está disponible para descargar: todavía se está generando, falló o ya caducó.");
}

public sealed class DescargarExportacionCompletaCommandHandler(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IActorAuditoria actorAuditoria,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ITenantsQueryContext tenants,
    ISolicitudExportacionCompletaRepository repositorio,
    IAlmacenExportacionesCompletas almacen,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<DescargarExportacionCompletaCommand, Result<ExportacionCompletaDescargable>>
{
    public async Task<Result<ExportacionCompletaDescargable>> Handle(
        DescargarExportacionCompletaCommand request, CancellationToken cancellationToken)
    {
        var (error, _, tenantId) = await AutoridadExportacionCompleta.ComprobarAsync(
            usuarioActual, tenantActual, actorAuditoria, sesionPrivilegiada, administradorDelTenant, tenants, cancellationToken);
        if (error is not null)
            return Result.Fallo<ExportacionCompletaDescargable>(error);

        // RLS y el filtro de Tenant acotan la lectura al Tenant en curso: la de otro Tenant no existe aquí.
        var solicitud = await repositorio.ObtenerPorIdAsync(request.SolicitudId, cancellationToken);
        if (solicitud is null || solicitud.TenantId != tenantId
            || !solicitud.EstaDisponibleParaDescarga(reloj.GetUtcNow().UtcDateTime))
            return Result.Fallo<ExportacionCompletaDescargable>(ErroresDescargaExportacionCompleta.NoDisponible);

        Stream contenido;
        try
        {
            contenido = await almacen.AbrirAsync(tenantId, solicitud.UbicacionFichero!, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Result.Fallo<ExportacionCompletaDescargable>(ErroresDescargaExportacionCompleta.NoDisponible);
        }

        try
        {
            var actor = await actorAuditoria.ObtenerAsync();
            repositorio.AgregarAuditoria(AuditoriaExportacionCompleta.DelActor(
                actor, solicitud.Id, AuditoriaExportacionCompleta.FaseDescargada, new { solicitud.TamanoBytes }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await contenido.DisposeAsync();
            throw;
        }

        return Result.Exito(new ExportacionCompletaDescargable(
            contenido, $"exportacion-{solicitud.SolicitadaEnUtc:yyyyMMdd-HHmm}.zip", solicitud.TamanoBytes ?? 0));
    }
}
