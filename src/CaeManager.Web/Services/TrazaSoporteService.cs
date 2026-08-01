using CaeManager.Application.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Services;

/// <summary>
/// Registra lo que hace el equipo de Hydra mientras opera el tenant de un
/// cliente (decisión del propietario del producto, 2026-07-31: trazabilidad
/// completa, incluidas navegación e interacciones).
///
/// <b>Solo escribe si hay una sesión de soporte en curso.</b> Para el uso
/// normal de la aplicación no registra nada: el volumen depende de cuántas
/// incidencias se atienden, no de cuántos usuarios tiene el producto. Sin ese
/// filtro, registrar a este nivel de detalle sería inviable sobre SQLite.
///
/// La delegación en curso se resuelve una vez por circuito y se cachea: pasa a
/// ser un dato en memoria, así que registrar un clic no cuesta una consulta
/// extra. Que la ventana caduque a mitad de sesión no invalida lo registrado
/// —el acceso lo corta el middleware en la siguiente petición— y anotar de más
/// nunca es peor que anotar de menos en un registro de rendición de cuentas.
/// </summary>
public class TrazaSoporteService(
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    ICurrentUserService currentUserService,
    IApplicationDbContext dbContext,
    IRegistroActividadSoporteRepository repositorio,
    IUnitOfWork unitOfWork,
    IPuertaAccesoDatos puertaAccesoDatos)
{
    private bool _resuelto;
    private Guid? _delegacionSoporteId;
    private Guid? _tenantVisitadoId;
    private Guid? _usuarioId;

    public async Task RegistrarAsync(
        TipoActividadSoporte tipo, string? detalle, CancellationToken cancellationToken = default)
    {
        await ResolverSesionAsync(cancellationToken);

        if (_delegacionSoporteId is not { } delegacionId ||
            _tenantVisitadoId is not { } tenantId ||
            _usuarioId is not { } usuarioId)
        {
            return;
        }

        // El registro pertenece al tenant visitado, no al de Hydra: es el
        // cliente quien debe poder consultar qué se hizo en sus datos.
        // Por la puerta: un lote de interacciones puede llegar desde el
        // navegador mientras otro componente consulta (ver PuertaAccesoDatos).
        await puertaAccesoDatos.EjecutarAsync(async () =>
        {
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                repositorio.Agregar(new RegistroActividadSoporte(usuarioId, delegacionId, tipo, detalle));
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>Si esta sesión está operando un workspace de soporte — lo consulta la UI para avisarlo.</summary>
    public async Task<bool> EsSesionDeSoporteAsync(CancellationToken cancellationToken = default)
    {
        await ResolverSesionAsync(cancellationToken);
        return _delegacionSoporteId is not null;
    }

    private async Task ResolverSesionAsync(CancellationToken cancellationToken)
    {
        if (_resuelto) return;
        _resuelto = true;

        // Sin workspace ajeno seleccionado no hay sesión de soporte posible:
        // se sale sin tocar la base de datos, que es el caso de todo uso
        // normal de la aplicación.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado) return;

        // Por la puerta: la primera resolución dispara desde TrazaSoporte, que
        // se inicializa en paralelo con el resto del layout (ver PuertaAccesoDatos).
        var delegacionId = await puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null) return (Guid?)null;

            _usuarioId = usuarioId;

            return await (
                from asignacion in dbContext.AsignacionesOperadorDelegado
                join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
                where asignacion.UsuarioId == usuarioId.Value
                      && delegacion.TenantClienteId == tenantSeleccionado
                      && delegacion.Proposito == PropositoDelegacion.Soporte
                      && delegacion.Activa
                select (Guid?)delegacion.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }, cancellationToken);

        if (delegacionId is null) return;

        _delegacionSoporteId = delegacionId;
        _tenantVisitadoId = tenantSeleccionado;
    }
}
