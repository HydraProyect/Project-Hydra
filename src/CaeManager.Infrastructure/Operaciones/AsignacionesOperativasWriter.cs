using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Operaciones;

/// <inheritdoc cref="IAsignacionesOperativasWriter" />
public class AsignacionesOperativasWriter(
    CaeManagerDbContext dbContext,
    ITenantActual tenantActual,
    ICurrentUserService currentUserService)
    : IAsignacionesOperativasWriter
{
    /// <summary>
    /// Roles que ven todo el workspace por su rol, sin depender del ámbito de
    /// su cartera. Solo a estos se les emite una cartera universal: para un rol
    /// de cartera sería un ensanchamiento silencioso del alcance.
    /// </summary>
    private static readonly string[] RolesDeAlcanceTotal =
        [Roles.Administrador, Roles.DireccionCae, Roles.Consulta];

    public async Task ReasignarCarteraClienteAsync(
        Guid clienteId, Guid? nuevoEjecutivoUsuarioId, CancellationToken cancellationToken = default)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId)
            throw new InvalidOperationException(
                $"No se puede repartir la cartera del cliente {clienteId} sin un tenant resuelto (ver ITenantActual).");

        var ahora = DateTime.UtcNow;
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        // Se cierran TODAS las vigentes sobre este cliente, no solo las de la
        // operación esperada: si el ejecutivo pasa de interno a delegado (o al
        // revés) la anterior cuelga de otra operación, y el índice único, que
        // es por operación, no la habría detectado.
        var vigentes = await dbContext.AsignacionesCartera
            .Where(c => c.PropietarioTenantId == propietarioTenantId
                        && c.AmbitoRelacionClienteId == clienteId
                        && c.Estado == EstadoAsignacion.Vigente)
            .ToListAsync(cancellationToken);

        // Se decide antes de cerrar nada, y se cierran todas las que no sean
        // suyas: con el "ya es suya" dentro del bucle, el resultado dependía
        // del orden en que la base de datos devolviera las filas — si la
        // coincidente salía primero, las demás quedaban vigentes y su usuario
        // conservaba el acceso al cliente.
        var yaEsSuya = nuevoEjecutivoUsuarioId is not null
                       && vigentes.Any(v => v.UsuarioId == nuevoEjecutivoUsuarioId);

        foreach (var vigente in vigentes.Where(v => v.UsuarioId != nuevoEjecutivoUsuarioId))
            vigente.Cerrar(MotivoCierreAsignacion.Reorganizada, ahora);

        if (yaEsSuya || nuevoEjecutivoUsuarioId is not { } ejecutivoId) return;

        var tenantDelEjecutivo = await dbContext.Users
            .Where(u => u.Id == ejecutivoId)
            .Select(u => (Guid?)u.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantDelEjecutivo is null)
            throw new InvalidOperationException(
                $"No se puede asignar el cliente {clienteId} al usuario {ejecutivoId}: ese usuario no existe.");

        var ambito = AmbitoAsignacion.DeRelacionCliente(clienteId);

        if (tenantDelEjecutivo == propietarioTenantId)
        {
            var raiz = await ObtenerRaizVigenteAsync(propietarioTenantId, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"El tenant {propietarioTenantId} no tiene operación raíz vigente: no hay dónde colgar la cartera del cliente {clienteId}.");

            dbContext.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raiz, ejecutivoId, ambito, ahora, vigenciaHasta: null, ahora, actorId));
            return;
        }

        // El ejecutivo es de otro tenant: su cartera cuelga de la operación
        // externa de ese tenant. Colgarla de la raíz rompería la cadena "el
        // usuario pertenece al tenant operador".
        var externa = await ObtenerOperacionExternaVigenteAsync(
                          propietarioTenantId, tenantDelEjecutivo.Value, cancellationToken)
                      ?? throw new InvalidOperationException(
                          $"El usuario {ejecutivoId} pertenece al tenant {tenantDelEjecutivo}, que no tiene operación " +
                          $"externa vigente sobre {propietarioTenantId}: no puede ser ejecutivo del cliente {clienteId}.");

        var rol = await ObtenerRolDelegadoAsync(ejecutivoId, propietarioTenantId, tenantDelEjecutivo.Value, cancellationToken);

        dbContext.AsignacionesCartera.Add(AsignacionCartera.Externa(
            externa, ejecutivoId, rol, ambito, ahora, vigenciaHasta: null, ahora, actorId));
    }

    public async Task AsegurarOperacionRaizAsync(
        Guid propietarioTenantId, DateTime vigenciaDesde, CancellationToken cancellationToken = default)
    {
        var existente = await dbContext.AsignacionesOperacion
            .AnyAsync(o => o.EsRaiz
                           && o.PropietarioTenantId == propietarioTenantId
                           && o.Servicio == ServicioCae.Outbound
                           && o.Estado != EstadoAsignacion.Cerrada, cancellationToken);

        if (existente) return;

        dbContext.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(
            propietarioTenantId, ServicioCae.Outbound, vigenciaDesde, DateTime.UtcNow));
    }

    public async Task<AsignacionOperacion> AbrirOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, DateTime vigenciaDesde, DateTime? vigenciaHasta,
        CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        var existente = await ObtenerOperacionExternaVigenteAsync(propietarioTenantId, operadorTenantId, cancellationToken);
        if (existente is not null) return existente;

        var nueva = AsignacionOperacion.Externa(
            propietarioTenantId, operadorTenantId, ServicioCae.Outbound,
            AmbitoAsignacion.Universal, vigenciaDesde, vigenciaHasta, ahora, actorId);

        dbContext.AsignacionesOperacion.Add(nueva);
        return nueva;
    }

    public async Task CerrarOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;

        var operacion = await ObtenerOperacionExternaVigenteAsync(propietarioTenantId, operadorTenantId, cancellationToken);
        if (operacion is null) return;

        // Las carteras se cierran en cascada: una cartera vigente bajo una
        // operación cerrada concedería acceso sin nada que lo ampare, y su
        // ámbito efectivo (intersección con el de la operación) ya no
        // significaría nada.
        var carteras = await dbContext.AsignacionesCartera
            .Where(c => c.AsignacionOperacionId == operacion.Id && c.Estado == EstadoAsignacion.Vigente)
            .ToListAsync(cancellationToken);

        foreach (var cartera in carteras)
            cartera.Cerrar(motivo, ahora);

        operacion.Cerrar(motivo, ahora);
    }

    public async Task AbrirCarteraOperadorAsync(
        AsignacionOperacion operacion, Guid usuarioId, string rol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operacion);

        // Un rol de cartera no recibe cartera universal: sus carteras nacen
        // cliente a cliente al asignárselos. Emitirle una universal aquí le
        // daría de golpe todos los clientes del tenant delegado, que es más de
        // lo que tiene hoy.
        if (!RolesDeAlcanceTotal.Contains(rol)) return;

        var ahora = DateTime.UtcNow;
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        // Se busca también entre las entidades ya añadidas al contexto: la
        // operación puede ser de este mismo comando y todavía sin guardar, y
        // entonces una consulta LINQ no vería ninguna de sus carteras.
        var yaTiene = dbContext.ChangeTracker.Entries<AsignacionCartera>()
            .Any(e => e.Entity.AsignacionOperacionId == operacion.Id
                      && e.Entity.UsuarioId == usuarioId
                      && e.Entity.Ambito.EsUniversal
                      && e.Entity.Estado == EstadoAsignacion.Vigente);

        if (!yaTiene && dbContext.Entry(operacion).State != EntityState.Added)
            yaTiene = await dbContext.AsignacionesCartera
                .AnyAsync(c => c.AsignacionOperacionId == operacion.Id
                               && c.UsuarioId == usuarioId
                               && c.AmbitoRelacionClienteId == null
                               && c.AmbitoCentroId == null
                               && c.AmbitoTrabajadorId == null
                               && c.AmbitoProyectoId == null
                               && c.Estado == EstadoAsignacion.Vigente, cancellationToken);

        if (yaTiene) return;

        dbContext.AsignacionesCartera.Add(AsignacionCartera.Externa(
            operacion, usuarioId, rol, AmbitoAsignacion.Universal, ahora, vigenciaHasta: null, ahora, actorId));
    }

    public async Task AbrirCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, string rol,
        CancellationToken cancellationToken = default)
    {
        var operacion = await ObtenerOperacionExternaVigenteAsync(propietarioTenantId, operadorTenantId, cancellationToken)
                        ?? throw new InvalidOperationException(
                            $"No hay operación externa vigente de {operadorTenantId} sobre {propietarioTenantId}: " +
                            $"no se puede autorizar al usuario {usuarioId}.");

        await AbrirCarteraOperadorAsync(operacion, usuarioId, rol, cancellationToken);
    }

    public async Task ReabrirCarterasDeOperadoresAsync(
        AsignacionOperacion operacion, Guid delegacionTenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operacion);

        var operadores = await dbContext.AsignacionesOperadorDelegado
            .Where(a => a.DelegacionTenantId == delegacionTenantId)
            .Select(a => new { a.UsuarioId, a.Rol })
            .ToListAsync(cancellationToken);

        foreach (var operador in operadores)
            await AbrirCarteraOperadorAsync(operacion, operador.UsuarioId, operador.Rol, cancellationToken);

        // Los roles de cartera recuperan sus clientes: sus carteras se
        // cerraron en cascada al desactivar y hay que reconstruirlas desde la
        // proyección, que es la que conserva el reparto durante F1.
        var idsOperadores = operadores.Select(o => o.UsuarioId).ToList();
        if (idsOperadores.Count == 0) return;

        var clientes = await dbContext.Clientes
            .Where(c => c.EjecutivoUsuarioId != null && idsOperadores.Contains(c.EjecutivoUsuarioId!.Value))
            .Select(c => new { c.Id, EjecutivoId = c.EjecutivoUsuarioId!.Value })
            .ToListAsync(cancellationToken);

        var ahora = DateTime.UtcNow;
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        foreach (var cliente in clientes)
        {
            var rol = operadores.First(o => o.UsuarioId == cliente.EjecutivoId).Rol;

            dbContext.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, cliente.EjecutivoId, rol, AmbitoAsignacion.DeRelacionCliente(cliente.Id),
                ahora, vigenciaHasta: null, ahora, actorId));
        }
    }

    public async Task CerrarCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;

        var operacion = await ObtenerOperacionExternaVigenteAsync(propietarioTenantId, operadorTenantId, cancellationToken);
        if (operacion is null) return;

        var carteras = await dbContext.AsignacionesCartera
            .Where(c => c.AsignacionOperacionId == operacion.Id
                        && c.UsuarioId == usuarioId
                        && c.Estado == EstadoAsignacion.Vigente)
            .ToListAsync(cancellationToken);

        foreach (var cartera in carteras)
            cartera.Cerrar(motivo, ahora);
    }

    private async Task<string> ObtenerRolDelegadoAsync(
        Guid usuarioId, Guid propietarioTenantId, Guid operadorTenantId, CancellationToken cancellationToken)
    {
        var rol = await dbContext.AsignacionesOperadorDelegado
            .Where(a => a.UsuarioId == usuarioId)
            .Join(dbContext.DelegacionesTenant,
                a => a.DelegacionTenantId, d => d.Id, (a, d) => new { a.Rol, d.TenantClienteId, d.TenantConsultoraId })
            .Where(x => x.TenantClienteId == propietarioTenantId && x.TenantConsultoraId == operadorTenantId)
            .Select(x => x.Rol)
            .FirstOrDefaultAsync(cancellationToken);

        return rol ?? Roles.GestorCae;
    }

    private Task<AsignacionOperacion?> ObtenerRaizVigenteAsync(Guid propietarioTenantId, CancellationToken cancellationToken) =>
        dbContext.AsignacionesOperacion
            .FirstOrDefaultAsync(o => o.EsRaiz
                                      && o.PropietarioTenantId == propietarioTenantId
                                      && o.Servicio == ServicioCae.Outbound
                                      && o.Estado == EstadoAsignacion.Vigente, cancellationToken);

    /// <summary>
    /// Busca primero entre las entidades ya añadidas al contexto y solo
    /// después en la base de datos. El orden importa: una operación creada en
    /// este mismo comando está únicamente en el <c>ChangeTracker</c>, y una
    /// consulta LINQ —que se traduce a SQL— no la encontraría. Ese era el
    /// motivo por el que el alta de un Cliente Delegante creaba la operación y
    /// se quedaba sin cartera.
    /// </summary>
    private async Task<AsignacionOperacion?> ObtenerOperacionExternaVigenteAsync(
        Guid propietarioTenantId, Guid operadorTenantId, CancellationToken cancellationToken)
    {
        var enElContexto = dbContext.ChangeTracker.Entries<AsignacionOperacion>()
            .Select(e => e.Entity)
            .FirstOrDefault(o => !o.EsRaiz
                                 && o.PropietarioTenantId == propietarioTenantId
                                 && o.OperadorTenantId == operadorTenantId
                                 && o.Servicio == ServicioCae.Outbound
                                 && o.Ambito.EsUniversal
                                 && o.Estado == EstadoAsignacion.Vigente);

        if (enElContexto is not null) return enElContexto;

        return await dbContext.AsignacionesOperacion
            .FirstOrDefaultAsync(o => !o.EsRaiz
                                      && o.PropietarioTenantId == propietarioTenantId
                                      && o.OperadorTenantId == operadorTenantId
                                      && o.Servicio == ServicioCae.Outbound
                                      && o.AmbitoRelacionClienteId == null
                                      && o.AmbitoCentroId == null
                                      && o.AmbitoTrabajadorId == null
                                      && o.AmbitoProyectoId == null
                                      && o.Estado == EstadoAsignacion.Vigente, cancellationToken);
    }
}
