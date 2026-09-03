using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="ISesionPrivilegiadaActual" />
public class SesionPrivilegiadaActual(
    IPlataformaQueryContext contexto,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    ICurrentUserService currentUserService)
    : ISesionPrivilegiadaActual
{
    private SesionPrivilegiadaActiva? _resuelta;
    private bool _yaResuelto;

    public async Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        // Memoizado por ámbito de DI, como el alcance de datos: se consulta en
        // varios puntos del pipeline (revalidación, alcance, escritura) y
        // repetir el viaje a la base en cada uno multiplicaría el coste de todo
        // lo que haga una sesión de soporte.
        //
        // Limitación conocida y aceptada para LECTURA (REC-067): ese ámbito es
        // la petición en HTTP pero el circuito entero en Blazor Server, así
        // que revocar una concesión a mitad de circuito no vacía lo que ese
        // circuito ya está viendo — igual que hoy no se invalida el alcance
        // memoizado cuando cambia una cartera. Quien lo cierra es el
        // enforcement en la capa de datos, ADR-011 § 4bis.7.4. Para
        // ESCRITURA no vale: <see cref="RevalidarAsync"/> es el método que
        // tiene que consultar cualquier punto de mutación.
        if (_yaResuelto) return _resuelta;

        return await RevalidarAsync(cancellationToken);
    }

    public async Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default)
    {
        // Ignora la memo existente y vuelve a preguntar a la base — el punto
        // de mutación (AutorizacionEscrituraBehavior) no puede confiar en un
        // resultado que pudo resolverse antes de que la concesión se
        // revocara. El resultado fresco SÍ se deja como memo: una escritura
        // que revalida no puede dejar el ámbito de DI en un estado más viejo
        // que el que ella misma acaba de comprobar.
        _resuelta = await ResolverAsync(cancellationToken);
        _yaResuelto = true;
        return _resuelta;
    }

    private async Task<SesionPrivilegiadaActiva?> ResolverAsync(CancellationToken cancellationToken)
    {
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not { } sesionId) return null;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;

        var ahora = DateTime.UtcNow;

        // Se traen sesión y concesión juntas y se comprueban las tres
        // condiciones sobre el estado REAL de la base, no sobre lo que el token
        // afirmara: la ventana grabada en la sesión no sabe nada de una
        // concesión revocada después.
        var candidata = await (
            from sesion in contexto.SesionesPrivilegiadas
            join concesion in contexto.ConcesionesPrivilegio
                on sesion.ConcesionPrivilegioId equals concesion.Id
            where sesion.Id == sesionId
                  // La sesión, viva: abierta y dentro de su ventana.
                  && sesion.CerradaEnUtc == null
                  && sesion.InicioEnUtc <= ahora
                  && ahora < sesion.ExpiraEnUtc
                  // Ligada a quien la abrió: un identificador de sesión ajeno
                  // no puede servirle a otro usuario aunque lo consiga.
                  && concesion.UsuarioPlataformaId == usuarioId.Value
                  // La concesión, todavía válida por sí misma.
                  && concesion.Estado == EstadoConcesionPrivilegio.Vigente
                  && concesion.VigenciaDesde <= ahora
                  && (concesion.VigenciaHasta == null || ahora < concesion.VigenciaHasta)
            select new
            {
                sesion.Id,
                ConcesionId = concesion.Id,
                sesion.TenantObjetivoId,
                concesion.Capacidad,
                concesion.EsAlcanceGlobal,
                sesion.UsuarioSimuladoId
            }).FirstOrDefaultAsync(cancellationToken);

        if (candidata is null) return null;

        // Coherencia entre los dos campos del token: el tenant que el token
        // dice abrir tiene que ser el objetivo de la sesión que nombra. Sin
        // esto, un token con el tenant de aquí y una sesión de allá abriría un
        // contexto que nadie autorizó — el mismo chequeo que la vía de
        // operación hace en el middleware, aquí porque es el único punto por el
        // que pasan todos los consumidores de plano 3.
        if (clienteActivoSeleccionado.TenantIdSeleccionado != candidata.TenantObjetivoId) return null;

        // Y que la concesión SIGA cubriendo ese tenant. Se comprueba aparte
        // porque el alcance puede haberse recortado después de abrir la sesión:
        // un tenant retirado de la lista tiene que cortar el acceso en el acto,
        // no cuando venza la ventana.
        if (!candidata.EsAlcanceGlobal)
        {
            var sigueEnAlcance = await contexto.TenantsAlcanzadosPorConcesion
                .AnyAsync(t => t.ConcesionPrivilegioId == candidata.ConcesionId
                               && t.TenantId == candidata.TenantObjetivoId, cancellationToken);

            if (!sigueEnAlcance) return null;
        }

        return new SesionPrivilegiadaActiva(
            candidata.Id, candidata.ConcesionId, candidata.TenantObjetivoId,
            candidata.Capacidad, candidata.UsuarioSimuladoId);
    }
}
