using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="ISesionPrivilegiadaActual" />
public class SesionPrivilegiadaActual(
    IPlataformaQueryContext contexto,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    ICurrentUserService currentUserService,
    TimeProvider? reloj = null)
    : ISesionPrivilegiadaActual
{
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;

    // Una sola referencia inmutable, no tres campos sueltos: en Blazor Server el
    // temporizador de RevalidacionCircuitoActivoHandler revalida desde otro hilo
    // mientras el renderizado lee, y una memo a medio escribir (sesión nueva con
    // la caducidad de la anterior) es justo la que no debe poder observarse.
    private Memo? _memo;

    public async Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        // Memoizado por ámbito de DI, como el alcance de datos: se consulta en
        // varios puntos del pipeline (revalidación, alcance, escritura) y
        // repetir el viaje a la base en cada uno multiplicaría el coste de todo
        // lo que haga una sesión de soporte.
        //
        // Ese ámbito es la petición en HTTP pero el circuito entero en Blazor
        // Server, así que la memo se descarta sin ir a la base en dos casos que
        // se deciden en memoria (Z2-1, revisión extensa 2026-10-23):
        //   - la ventana venció: la caducidad de la sesión (DEC-43, 4 h como
        //     máximo) y el fin de vigencia de la concesión se guardaron al
        //     resolver y se comparan con el reloj en cada lectura;
        //   - la selección ya no nombra la sesión memoizada: el circuito la
        //     invalidó (RevalidacionCircuitoActivoHandler) o cambió.
        //
        // Lo que la memo NO puede ver es lo que cambia en la base: una
        // concesión revocada o una sesión cerrada desde otra pestaña. Eso lo
        // detecta RevalidarAsync, que llaman el punto de mutación
        // (AutorizacionEscrituraBehavior) y la revalidación periódica del
        // circuito (RevalidacionClienteActivoMiddleware.SigueAutorizadoAsync,
        // cada Circuit:RevalidacionIntervaloSegundos, 60 s por defecto): la
        // lectura de un circuito ya vivo queda cortada en el siguiente tick.
        // No hay enforcement en la capa de datos que lo cierre antes: el
        // contexto RLS firmado no lleva la sesión ni su caducidad.
        if (_memo is { } memo && memo.SigueValidaPara(clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada, Ahora()))
            return memo.Sesion;

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
        var sesionIdSeleccionada = clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada;
        var memo = await ResolverAsync(sesionIdSeleccionada, cancellationToken);
        _memo = memo;
        return memo.Sesion;
    }

    private DateTime Ahora() => _reloj.GetUtcNow().UtcDateTime;

    private async Task<Memo> ResolverAsync(Guid? sesionIdSeleccionada, CancellationToken cancellationToken)
    {
        var sinSesion = new Memo(sesionIdSeleccionada, null, DateTime.MaxValue);

        if (sesionIdSeleccionada is not { } sesionId) return sinSesion;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return sinSesion;

        var ahora = Ahora();

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
                sesion.UsuarioSimuladoId,
                sesion.ExpiraEnUtc,
                concesion.VigenciaHasta
            }).FirstOrDefaultAsync(cancellationToken);

        if (candidata is null) return sinSesion;

        // Coherencia entre los dos campos del token: el tenant que el token
        // dice abrir tiene que ser el objetivo de la sesión que nombra. Sin
        // esto, un token con el tenant de aquí y una sesión de allá abriría un
        // contexto que nadie autorizó — el mismo chequeo que la vía de
        // operación hace en el middleware, aquí porque es el único punto por el
        // que pasan todos los consumidores de plano 3.
        if (clienteActivoSeleccionado.TenantIdSeleccionado != candidata.TenantObjetivoId) return sinSesion;

        // Y que la concesión SIGA cubriendo ese tenant. Se comprueba aparte
        // porque el alcance puede haberse recortado después de abrir la sesión:
        // un tenant retirado de la lista tiene que cortar el acceso en el acto,
        // no cuando venza la ventana.
        if (!candidata.EsAlcanceGlobal)
        {
            var sigueEnAlcance = await contexto.TenantsAlcanzadosPorConcesion
                .AnyAsync(t => t.ConcesionPrivilegioId == candidata.ConcesionId
                               && t.TenantId == candidata.TenantObjetivoId, cancellationToken);

            if (!sigueEnAlcance) return sinSesion;
        }

        // Lo primero que venza de las dos ventanas: la de la sesión o la de su
        // concesión. Pasado ese instante la memo ya no vale, aunque nadie haya
        // vuelto a preguntar a la base.
        var caducaEnUtc = candidata.VigenciaHasta is { } vigenciaHasta && vigenciaHasta < candidata.ExpiraEnUtc
            ? vigenciaHasta
            : candidata.ExpiraEnUtc;

        return new Memo(
            sesionIdSeleccionada,
            new SesionPrivilegiadaActiva(
                candidata.Id, candidata.ConcesionId, candidata.TenantObjetivoId,
                candidata.Capacidad, candidata.UsuarioSimuladoId),
            caducaEnUtc);
    }

    /// <param name="SesionIdSeleccionada">La sesión que nombraba la selección al resolver, nula incluida.</param>
    /// <param name="Sesion">Lo resuelto: nulo si no había sesión viva.</param>
    /// <param name="CaducaEnUtc">Primer vencimiento de la sesión o de su concesión; sin sesión, nunca.</param>
    private sealed record Memo(Guid? SesionIdSeleccionada, SesionPrivilegiadaActiva? Sesion, DateTime CaducaEnUtc)
    {
        public bool SigueValidaPara(Guid? sesionIdSeleccionada, DateTime ahora) =>
            sesionIdSeleccionada == SesionIdSeleccionada && ahora < CaducaEnUtc;
    }
}
