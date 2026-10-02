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
    /// su cartera. Solo a estos se les emite aquí una cartera universal: para
    /// un rol de cartera sería un ensanchamiento silencioso del alcance. Un rol
    /// de cartera solo la recibe por una decisión explícita —aceptar una
    /// solicitud de incorporación a cartera, o elegirla en el alta del Gestor
    /// CAE—, las dos por CatalogoIncorporacionCartera, no por este writer. Entre los roles que una cartera externa puede conceder
    /// (<see cref="RolesDelegadosPermitidos"/>), solo Consulta lo es:
    /// Administrador y Dirección CAE ya no llegan hasta aquí.
    /// </summary>
    private static readonly string[] RolesDeAlcanceTotal = [Roles.Consulta];

    public async Task AsegurarCarteraTenantEnteroAsync(
        Guid propietarioTenantId, Guid gestorUsuarioId, CancellationToken cancellationToken = default)
    {
        // Como toda escritura de cartera, ocurre dentro del workspace del propietario: quien llama
        // no puede conceder el Tenant entero de un Tenant que no es el del contexto.
        if (tenantActual.TenantId != propietarioTenantId)
            throw new InvalidOperationException(
                $"No se puede dar la cartera del Tenant {propietarioTenantId} desde el contexto del Tenant {tenantActual.TenantId}.");

        var tenantDelGestor = await dbContext.Users
            .Where(u => u.Id == gestorUsuarioId)
            .Select(u => (Guid?)u.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantDelGestor is null)
            throw new InvalidOperationException(
                $"No se puede dar la cartera del Tenant {propietarioTenantId} al usuario {gestorUsuarioId}: ese usuario no existe.");

        var ahora = DateTime.UtcNow;
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        if (tenantDelGestor == propietarioTenantId)
        {
            // Un Gestor CAE del propio Tenant opera con el rol de Identity: la cartera interna no lleva rol.
            var roles = await (
                from ur in dbContext.UserRoles
                join r in dbContext.Roles on ur.RoleId equals r.Id
                where ur.UserId == gestorUsuarioId
                select r.Name).ToListAsync(cancellationToken);

            if (roles.Count != 1 || roles[0] != Roles.GestorCae)
                throw new UnauthorizedAccessException(
                    $"El usuario {gestorUsuarioId} no es un {Roles.GestorCae}: la cartera del Tenant entero solo la lleva un Gestor CAE.");

            var raiz = await ObtenerRaizVigenteAsync(propietarioTenantId, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"El Tenant {propietarioTenantId} no tiene operación raíz vigente: no hay dónde colgar la cartera del usuario {gestorUsuarioId}.");

            if (await TieneUniversalVigenteAsync(raiz, gestorUsuarioId, cancellationToken)) return;

            dbContext.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raiz, gestorUsuarioId, AmbitoAsignacion.Universal, ahora, vigenciaHasta: null, ahora, actorId));
            return;
        }

        // El Gestor CAE es de otro Tenant: su cartera cuelga de la operación externa de ese Tenant.
        // Colgarla de la raíz rompería la cadena "el usuario pertenece al Tenant operador".
        var externa = await ObtenerOperacionExternaVigenteAsync(propietarioTenantId, tenantDelGestor.Value, cancellationToken)
                      ?? throw new InvalidOperationException(
                          $"El usuario {gestorUsuarioId} pertenece al Tenant {tenantDelGestor}, que no tiene operación " +
                          $"externa vigente sobre {propietarioTenantId}: no puede llevar su cartera.");

        var rol = await ObtenerRolDelegadoAsync(gestorUsuarioId, propietarioTenantId, tenantDelGestor.Value, cancellationToken);

        // La cartera del Tenant entero solo la lleva un Gestor CAE, también por delegación (revisión
        // Codex de la PR #931). Un Coordinador CAE delegado no deriva su alcance de sus propias
        // carteras sino de las de sus Gestores CAE, y Consulta ya lo ve todo por su rol.
        if (rol != Roles.GestorCae)
            throw new UnauthorizedAccessException(
                $"El usuario {gestorUsuarioId} opera este tenant como {rol} por delegación: la cartera del Tenant " +
                $"{propietarioTenantId} solo puede ir a un {Roles.GestorCae}.");

        if (await TieneUniversalVigenteAsync(externa, gestorUsuarioId, cancellationToken)) return;

        dbContext.AsignacionesCartera.Add(AsignacionCartera.Externa(
            externa, gestorUsuarioId, rol, AmbitoAsignacion.Universal, ahora, vigenciaHasta: null, ahora, actorId));
    }

    /// <summary>
    /// Si el usuario ya tiene una cartera universal vigente bajo la operación, mirando también las
    /// ya añadidas al contexto y aún sin guardar (mismo motivo que <see cref="ObtenerOperacionExternaVigenteAsync"/>).
    /// </summary>
    private async Task<bool> TieneUniversalVigenteAsync(
        AsignacionOperacion operacion, Guid usuarioId, CancellationToken cancellationToken)
    {
        var enElContexto = dbContext.ChangeTracker.Entries<AsignacionCartera>()
            .Any(e => e.Entity.AsignacionOperacionId == operacion.Id
                      && e.Entity.UsuarioId == usuarioId
                      && e.Entity.Ambito.EsUniversal
                      && e.Entity.Estado == EstadoAsignacion.Vigente);
        if (enElContexto) return true;
        if (dbContext.Entry(operacion).State == EntityState.Added) return false;

        return await dbContext.AsignacionesCartera
            .AnyAsync(c => c.AsignacionOperacionId == operacion.Id
                           && c.UsuarioId == usuarioId
                           && c.AmbitoRelacionClienteId == null
                           && c.AmbitoCentroId == null
                           && c.AmbitoTrabajadorId == null
                           && c.AmbitoProyectoId == null
                           && c.Estado == EstadoAsignacion.Vigente, cancellationToken);
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

        // Falla cerrado antes de decidir el ámbito: un rol fuera de la lista
        // blanca (Administrador, Dirección CAE o un valor corrupto) no recibe
        // ninguna cartera, ni universal ni de cliente. Antes, un Administrador
        // o una Dirección CAE delegados recibían aquí una cartera universal.
        if (!RolesDelegadosPermitidos.Contains(rol))
            throw new UnauthorizedAccessException(
                $"El rol {rol} no se concede por Asignación de Cartera ni por delegación: " +
                $"solo {string.Join(", ", RolesDelegadosPermitidos)}.");

        // Un rol de cartera no recibe aquí cartera universal: su cartera (siempre
        // el Tenant entero, D-7) nace solo cuando un Coordinador CAE acepta su
        // solicitud de incorporación o quien tiene la autoridad se la asigna o
        // elige en su alta (CatalogoIncorporacionCartera). Emitirle una
        // universal aquí le daría de golpe el tenant delegado entero —todas sus
        // ramas operativas, ver AlcanceDatosService— sin que nadie lo decidiera.
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

        // Una fila heredada con Administrador o Dirección CAE (anterior a la
        // decisión del 2026-09-23) no se reabre, pero tampoco impide reactivar
        // la delegación para los operadores válidos: se omite, sin lanzar.
        var operadores = await dbContext.AsignacionesOperadorDelegado
            .Where(a => a.DelegacionTenantId == delegacionTenantId && RolesDelegadosPermitidos.Contains(a.Rol))
            .Select(a => new { a.UsuarioId, a.Rol })
            .ToListAsync(cancellationToken);

        foreach (var operador in operadores)
            await AbrirCarteraOperadorAsync(operacion, operador.UsuarioId, operador.Rol, cancellationToken);

        // Un Gestor CAE recupera la cartera del Tenant entero solo si la tenía vigente en la
        // operación que esta desactivación cerró: una concesión explícita anterior, no una
        // referencia (Empresa.EjecutivoUsuarioId) ni la mera fila de operador delegado.
        var gestores = operadores.Where(o => o.Rol == Roles.GestorCae).Select(o => o.UsuarioId).ToList();
        if (gestores.Count == 0) return;

        // La última operación externa cerrada del mismo par, sea cual sea su motivo: si la última etapa
        // terminó por caducidad (Expirada) o por traspaso, no se repone nada desde una anterior.
        var anterior = await dbContext.AsignacionesOperacion
            .Where(o => o.Id != operacion.Id
                        && !o.EsRaiz
                        && o.PropietarioTenantId == operacion.PropietarioTenantId
                        && o.OperadorTenantId == operacion.OperadorTenantId
                        && o.Servicio == operacion.Servicio
                        && o.Estado == EstadoAsignacion.Cerrada)
            .OrderByDescending(o => o.VigenciaHasta)
            .Select(o => new { o.Id, o.MotivoCierre, o.VigenciaHasta })
            .FirstOrDefaultAsync(cancellationToken);
        if (anterior is null || anterior.MotivoCierre != MotivoCierreAsignacion.Revocada) return;

        // Solo las carteras que cerró la cascada de esa misma desactivación: CerrarOperacionDelegadaAsync
        // cierra operación y carteras con el mismo instante, así que comparten VigenciaHasta. Una cartera
        // revocada a un operador concreto con la operación aún vigente (CerrarCarteraOperadorAsync) se
        // cerró antes y no se repone: nadie decidió devolvérsela.
        var conCarteraCerrada = await dbContext.AsignacionesCartera
            .Where(c => c.AsignacionOperacionId == anterior.Id
                        && c.Estado == EstadoAsignacion.Cerrada
                        && c.MotivoCierre == MotivoCierreAsignacion.Revocada
                        && c.VigenciaHasta == anterior.VigenciaHasta
                        && gestores.Contains(c.UsuarioId))
            .Select(c => c.UsuarioId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var ahora = DateTime.UtcNow;
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        foreach (var gestorId in conCarteraCerrada)
        {
            if (await TieneUniversalVigenteAsync(operacion, gestorId, cancellationToken)) continue;

            dbContext.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, gestorId, Roles.GestorCae, AmbitoAsignacion.Universal,
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

    /// <summary>
    /// Roles que una Asignación de Cartera externa o una delegación pueden
    /// conceder. El código de rol persistido en
    /// <see cref="AsignacionOperadorDelegado.Rol"/> es texto plano (Domain no
    /// referencia <c>Roles</c> — ver su doc-comment); esta lista blanca es lo
    /// único que impide que un valor corrupto, inesperado o heredado se cuele
    /// como rol efectivo de la cartera nueva.
    ///
    /// <para>
    /// Administrador y Dirección CAE quedan fuera (decisión del propietario,
    /// 2026-09-23): son autoridad de Propiedad del Tenant propietario, no de
    /// Operación, y una cartera o delegación solo concede Operación (ADR-011
    /// § 1). Es la misma lista que ya exigía
    /// <c>CrearAsignacionOperadorDelegadoCommandValidator</c> al crear la
    /// delegación; aquí se exige también al leerla, para que una fila anterior
    /// a esa validación no la esquive.
    /// </para>
    /// </summary>
    private static readonly string[] RolesDelegadosPermitidos =
        [Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    /// <summary>
    /// Falla cerrado: sin una <see cref="AsignacionOperadorDelegado"/> única y
    /// vigente para exactamente este usuario, propietario y operador, no hay
    /// autoridad que conceder. Antes, la ausencia de fila degradaba a
    /// <see cref="Roles.GestorCae"/> — cualquier usuario del tenant operador
    /// con una operación externa vigente entraba al tenant propietario sin
    /// estar en su lista de operadores delegados.
    /// </summary>
    private async Task<string> ObtenerRolDelegadoAsync(
        Guid usuarioId, Guid propietarioTenantId, Guid operadorTenantId, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;

        var roles = await dbContext.AsignacionesOperadorDelegado
            .Where(a => a.UsuarioId == usuarioId)
            .Join(
                dbContext.DelegacionesTenant,
                a => a.DelegacionTenantId,
                d => d.Id,
                (a, d) => new { a.Rol, d.TenantClienteId, d.TenantConsultoraId, d.Activa, d.ExpiraEnUtc })
            .Where(x =>
                x.TenantClienteId == propietarioTenantId &&
                x.TenantConsultoraId == operadorTenantId &&
                x.Activa &&
                (x.ExpiraEnUtc == null || ahora < x.ExpiraEnUtc.Value))
            .Select(x => x.Rol)
            .Distinct()
            .Take(2)
            .ToListAsync(cancellationToken);

        if (roles.Count != 1 || !RolesDelegadosPermitidos.Contains(roles[0]))
            throw new UnauthorizedAccessException(
                $"El usuario {usuarioId} no tiene una asignación delegada única y vigente sobre el tenant " +
                $"{propietarioTenantId} desde el operador {operadorTenantId}.");

        return roles[0];
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
