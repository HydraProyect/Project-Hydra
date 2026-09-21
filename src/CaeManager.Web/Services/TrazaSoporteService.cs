using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
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
    ITenantsQueryContext dbContext,
    IRegistroActividadSoporteRepository repositorio,
    IUnitOfWork unitOfWork,
    PuertaAccesoDatos puertaAccesoDatos) : IVentanaDeSoporteActual
{
    private Task? _resolucion;
    private Guid? _delegacionSoporteId;
    private Guid? _tenantVisitadoId;
    private Guid? _usuarioId;
    private DateTime? _expiraEnUtc;
    private bool _soloSoporte;

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
                repositorio.Agregar(RegistroActividadSoporte.PorViaHeredada(usuarioId, delegacionId, tipo, detalle));
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>Si esta sesión está operando un workspace de soporte — lo consulta la UI para avisarlo.</summary>
    /// <inheritdoc />
    public async Task<DateTime?> ObtenerExpiracionAsync(CancellationToken cancellationToken = default)
    {
        await ResolverSesionAsync(cancellationToken);
        return _delegacionSoporteId is null || !_soloSoporte ? null : _expiraEnUtc;
    }

    public async Task<bool> EsSesionDeSoporteAsync(CancellationToken cancellationToken = default)
    {
        await ResolverSesionAsync(cancellationToken);
        return _delegacionSoporteId is not null;
    }

    /// <summary>
    /// Todos los que preguntan esperan a la <b>misma</b> resolución. Antes un
    /// booleano se marcaba al empezar: el segundo componente del layout que
    /// preguntaba mientras la primera consulta seguía en vuelo veía «resuelto»
    /// y leía campos todavía vacíos, es decir, «no es sesión de soporte». La
    /// resolución no lleva el token de quien la lanza: es compartida, y que uno
    /// de ellos cancele no puede dejar a los demás sin respuesta.
    /// </summary>
    private Task ResolverSesionAsync(CancellationToken cancellationToken) =>
        (_resolucion ??= ResolverAsync()).WaitAsync(cancellationToken);

    private async Task ResolverAsync()
    {
        var cancellationToken = CancellationToken.None;

        // Sin workspace ajeno seleccionado no hay sesión de soporte posible:
        // se sale sin tocar la base de datos, que es el caso de todo uso
        // normal de la aplicación.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado) return;

        // Por la puerta: la primera resolución dispara desde TrazaSoporte, que
        // se inicializa en paralelo con el resto del layout (ver PuertaAccesoDatos).
        var delegaciones = await puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null) return null;

            _usuarioId = usuarioId;

            return await (
                from asignacion in dbContext.AsignacionesOperadorDelegado
                join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
                where asignacion.UsuarioId == usuarioId.Value
                      && delegacion.TenantClienteId == tenantSeleccionado
                select new { delegacion.Id, delegacion.ExpiraEnUtc, delegacion.Proposito, delegacion.Activa })
                .ToListAsync(cancellationToken);
        }, cancellationToken);

        var soporte = PropositoDelegacion.Soporte;
        var delegacion = delegaciones?.FirstOrDefault(d => d.Proposito == soporte && d.Activa);

        if (delegacion is null) return;

        _delegacionSoporteId = delegacion.Id;
        _expiraEnUtc = delegacion.ExpiraEnUtc;
        // Con otra delegaciÃ³n del usuario hacia ese tenant, la caducidad de la
        // ventana no dice cuÃ¡ndo se acaba su acceso: la selecciÃ³n puede seguir
        // viva por la otra vÃ­a, y avisar Â«terminÃ³Â» serÃ­a falso.
        _soloSoporte = delegaciones!.All(d => d.Proposito == soporte);
        _tenantVisitadoId = tenantSeleccionado;
    }
}
