using CaeManager.Application.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Traslada el reparto de responsabilidad operativa que vive en
/// <c>DelegacionTenant</c> a las tablas de asignación (F1 del plan de migración).
/// Ya no deriva carteras de <c>Empresa.EjecutivoUsuarioId</c>: el reparto por Cliente
/// empresarial está retirado (D-7, 2026-10-02) y esa columna es una referencia.
///
/// <b>Idempotente y reconciliador</b>, no solo "insertar si falta". Durante F1
/// los mecanismos antiguos siguen siendo los autoritativos y se escriben en
/// paralelo, pero entre el backfill y la activación de la doble escritura hay
/// una ventana real —despliegue rolling, varias instancias— en la que un
/// operador delegado puede cambiar. Un seeder que solo insertara dejaría esa diferencia
/// congelada para siempre; este cierra lo que ya no corresponde y abre lo que
/// falta, en cada arranque, hasta que la doble escritura quede establecida.
///
/// <para>
/// <b>Nunca reabre ni recrea una Asignación de Operación externa</b> (decisión de
/// Chris del 2026-11-14): solo crea la de una delegación que no tuvo ninguna. Si el
/// par ya tiene alguna, aunque esté cerrada o expirada, lo que falte lo abre un acto
/// explícito (reactivar la delegación), nunca el arranque.
/// </para>
///
/// <para>
/// <b>Solo concede desde una delegación activa y vigente</b> (ronda 1 de Codex en la PR #2223):
/// una delegación inactiva, o marcada activa pero ya caducada, ni mantiene abierto lo del par ni
/// da cartera a sus personas; y con una activa y vigente se cierra la cartera Outbound, de
/// cualquier ámbito, de quien ya no tenga fila heredada en ella. Inbound no se toca.
/// </para>
///
/// Lo que <b>no</b> migra:
/// <list type="bullet">
/// <item>Las delegaciones de propósito <c>Soporte</c>. El soporte de TALVEG no
/// es un operador CAE: es plano 3 (ADR-011 § 8) y conserva su mecánica actual
/// hasta su propia fase. Migrarlas aquí sería justo la falsa delegación que ese
/// plano prohíbe.</item>
/// <item>Las filas cuyo usuario no pertenece al tenant que operaría. Existen:
/// el comando de alta actual admite usuarios del tenant propietario, no solo de
/// la consultora. Se reportan para decisión humana en vez de migrarse a ciegas
/// — su acceso sigue gobernado por su cartera interna, que sí se migra.</item>
/// </list>
/// </summary>
public static class AsignacionesOperativasBackfillSeeder
{
    /// <summary>
    /// Roles que ven todo el workspace por su rol, sin depender del ámbito de
    /// su cartera — los únicos a los que se les emite una cartera universal.
    /// </summary>
    private static readonly string[] RolesDeAlcanceTotal = [Roles.Consulta];

    /// <summary>
    /// Lo único que una cartera externa (Operación, no Propiedad) puede
    /// conceder en el Tenant propietario — decisión del propietario,
    /// 2026-09-23, la misma lista que <c>AsignacionesOperativasWriter</c>.
    /// Administrador y Dirección CAE son autoridad de Propiedad: el backfill
    /// ni los emite ni deja vivos los que ya hubiera.
    /// </summary>
    private static readonly string[] RolesDelegables =
        [Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;

        var tenants = await dbContext.Tenants
            .Select(t => new { t.Id, t.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        if (tenants.Count == 0) return;

        var operaciones = await dbContext.AsignacionesOperacion.ToListAsync(cancellationToken);
        var carteras = await dbContext.AsignacionesCartera.ToListAsync(cancellationToken);

        var incidencias = new List<string>();
        var creadas = 0;
        var cerradas = 0;

        // --- 0. Reconciliación: carteras externas con un rol de Propiedad ---
        //
        // Antes de 2026-09-23 el writer y este mismo backfill admitían
        // Administrador y Dirección CAE en una cartera externa, y el rol
        // efectivo del Context Workspace delegado sale de ella. Cerrarlas es la
        // decisión del propietario aplicada a los datos que ya existen, no solo
        // a los futuros; las internas (Tenant propio) no se tocan. Solo Outbound: Inbound tiene su
        // propio ciclo y este backfill no lo toca (ronda 1 de Codex en la PR #2223).
        var operacionesExternas = operaciones
            .Where(o => !o.EsOperacionInterna && o.Servicio == ServicioCae.Outbound)
            .Select(o => o.Id)
            .ToHashSet();
        // No solo las vigentes: una Programada la activaría el job al llegar su fecha, y una
        // Suspendida no tiene por qué quedar viva con un rol que no se delega.
        foreach (var prohibida in carteras.Where(c =>
                     c.Estado != EstadoAsignacion.Cerrada
                     && operacionesExternas.Contains(c.AsignacionOperacionId)
                     && c.Rol is not null
                     && !RolesDelegables.Contains(c.Rol)))
        {
            prohibida.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
            cerradas++;
            incidencias.Add(
                $"Cartera externa {prohibida.Id} del usuario {prohibida.UsuarioId} con rol {prohibida.Rol}: " +
                "Administrador y Dirección CAE no se delegan. Cerrada.");
        }

        // --- 1. La operación raíz de cada tenant ---
        //
        // Para TODOS los tenants, no solo los activos: la raíz es el ancla de
        // las carteras internas, y un tenant suspendido necesita tenerla para
        // que, al reactivarse, haya dónde colgar una cartera interna. Crearla no
        // concede nada por sí sola — sin cartera no hay acceso.
        foreach (var tenant in tenants)
        {
            var raiz = operaciones.FirstOrDefault(o =>
                o.EsRaiz
                && o.PropietarioTenantId == tenant.Id
                && o.Servicio == ServicioCae.Outbound
                && o.Estado != EstadoAsignacion.Cerrada);

            if (raiz is not null) continue;

            var nueva = AsignacionOperacion.Raiz(tenant.Id, ServicioCae.Outbound, tenant.CreadoEnUtc, ahora);
            dbContext.AsignacionesOperacion.Add(nueva);
            operaciones.Add(nueva);
            creadas++;
        }

        // --- 2. Las delegaciones comerciales, como operaciones externas ---
        //
        // Orden determinista por antigüedad: si dos delegaciones activas del
        // MISMO cliente compiten (ver más abajo), la más antigua es la que se
        // migra y la más nueva queda como incidencia — nunca al revés, o el
        // resultado del backfill dependería del orden en que Postgres
        // devuelva las filas.
        var delegacionesComerciales = await dbContext.DelegacionesTenant
            .Where(d => d.Proposito == PropositoDelegacion.OperadorExterno)
            .OrderBy(d => d.CreadoEnUtc)
            .ToListAsync(cancellationToken);

        // Ids de las delegaciones activas y vigentes por par (Tenant propietario, Operador CAE
        // externo), para la reconciliación del paso 2b.
        var idsVigentesPorPar = new Dictionary<(Guid Propietario, Guid Operador), HashSet<Guid>>();

        foreach (var delegacion in delegacionesComerciales)
        {
            // Todas las Asignaciones de Operación Outbound externas de este par, de cualquier ámbito
            // y estado: lo que el backfill puede cerrar y lo que le dice si el par ya se migró.
            var propietarioId = delegacion.TenantClienteId;
            if (delegacion.EstaVigente(ahora))
            {
                var clave = (propietarioId, delegacion.TenantConsultoraId);
                if (!idsVigentesPorPar.TryGetValue(clave, out var ids))
                    idsVigentesPorPar[clave] = ids = [];
                ids.Add(delegacion.Id);
            }
            var operacionesDelPar = operaciones.Where(o =>
                    !o.EsRaiz
                    && o.PropietarioTenantId == propietarioId
                    && o.OperadorTenantId == delegacion.TenantConsultoraId
                    && o.Servicio == ServicioCae.Outbound)
                .ToList();

            // Una delegación inactiva, o activa pero ya caducada: ninguna de las dos autoriza nada. Antes
            // solo se cerraba lo del par con la inactiva, y la caducada dejaba abiertas sus operaciones
            // acotadas, sin fecha de fin y con su cartera: el job de expiración solo cierra la del Tenant
            // entero, que lleva la caducidad de la delegación (ronda 2 de Codex en la PR #2223).
            if (!delegacion.EstaVigente(ahora))
            {
                // Las operaciones del par no dicen de qué delegación nacieron. Si el mismo Operador
                // CAE externo tiene hoy otra delegación activa sobre este Tenant propietario (se
                // revocó una y se autorizó otra), lo abierto es de la activa: cerrarlo aquí la dejaría
                // sin autorización y, como el backfill ya no recrea, sin vía de vuelta. La revocación
                // de esta ya cerró lo suyo en su momento. Solo cuenta una activa y vigente: una que
                // sigue marcada activa pero ya caducó no autoriza nada, y dejar abierto lo del par
                // por ella mantendría viva una operación sin delegación que la ampare (ronda 1 de
                // Codex en la PR #2223).
                if (delegacionesComerciales.Any(d =>
                        d.EstaVigente(ahora) && d.Id != delegacion.Id
                        && d.TenantClienteId == propietarioId
                        && d.TenantConsultoraId == delegacion.TenantConsultoraId))
                    continue;

                // Reconciliación: la delegación se desactivó, o caducó, después de que el backfill
                // creara la operación. De una desactivación el modelo antiguo no guardó cuándo, así que
                // se marca como tal en vez de inventar una fecha — la etapa quedó con vigencia real
                // desconocida; de una caducidad sí se sabe, y se cierra como Expirada en su fecha. Se
                // cierran todas las no cerradas del par, del Tenant entero y acotadas, y sus carteras
                // por la cascada, que recuerda su estado efectivo y no deja nada restaurable: una
                // delegación que no autoriza no deja ninguna autorización Outbound derivada (decisión de
                // Chris del 2026-11-14).
                var (motivo, cierre) = delegacion is { Activa: true, ExpiraEnUtc: { } caducidad }
                    ? (MotivoCierreAsignacion.Expirada, caducidad)
                    : (MotivoCierreAsignacion.MigradaSinFecha, ahora);
                foreach (var abierta in operacionesDelPar.Where(o => o.Estado != EstadoAsignacion.Cerrada))
                {
                    foreach (var cartera in carteras.Where(c =>
                                 c.AsignacionOperacionId == abierta.Id && c.Estado != EstadoAsignacion.Cerrada))
                    {
                        cartera.CerrarPorCascadaDeLaOperacion(abierta, motivo, cierre);
                        cerradas++;
                    }

                    abierta.Cerrar(motivo, cierre);
                    cerradas++;
                }

                continue;
            }

            // Ya migrada para este operador: tiene su operación del Tenant entero sin cerrar.
            if (operacionesDelPar.Any(o => o.Ambito.EsUniversal && o.Estado != EstadoAsignacion.Cerrada)) continue;

            // El backfill solo traslada al modelo nuevo una delegación que NUNCA tuvo Asignación de
            // Operación. Si el par ya tiene alguna —cerrada, expirada o acotada—, la delegación ya
            // vive en el modelo nuevo y lo que falte lo abre un acto explícito (reactivar la
            // delegación), no el arranque. Antes se ignoraba la cerrada y se creaba otra: una
            // operación expirada antes que su delegación volvía a autorizar en el arranque
            // siguiente, también a los perfiles que autorizan sin cartera (hallazgo de Codex en la
            // revisión de la PR #2215; decisión de Chris del 2026-11-14).
            if (operacionesDelPar.Count > 0)
            {
                incidencias.Add(
                    $"Delegación {delegacion.Id}: activa, pero sin Asignación de Operación Outbound del Tenant " +
                    "entero abierta y con otras ya cerradas, expiradas o acotadas. El backfill no reabre ni recrea " +
                    "operaciones: no migrada, requiere un acto explícito (reactivar la delegación).");
                continue;
            }

            // El invariante real es POR CLIENTE, no por (cliente, operador):
            // IX_AsignacionesOperacion_DelegacionTotalVigente exige como mucho
            // una delegación total vigente por cliente, sea quien sea el
            // operador — comparar también por operador aquí (como hacía antes
            // este chequeo) dejaba pasar dos delegaciones activas simultáneas
            // del mismo cliente hacia operadores distintos, un estado que el
            // modelo antiguo nunca impidió y que el índice único rechaza con
            // 23505. Encontrado en producción el 2026-08-24 (ver
            // Project-Hydra-Negocio/tecnico/d8-vps-evidence.md).
            var operacionDeOtroOperador = operaciones.FirstOrDefault(o =>
                !o.EsRaiz
                && o.PropietarioTenantId == propietarioId
                && o.OperadorTenantId != delegacion.TenantConsultoraId
                && o.Servicio == ServicioCae.Outbound
                && o.Ambito.EsUniversal
                && o.Estado == EstadoAsignacion.Vigente);

            if (operacionDeOtroOperador is not null)
            {
                incidencias.Add(
                    $"Delegación {delegacion.Id} (cliente {delegacion.TenantClienteId} → consultora " +
                    $"{delegacion.TenantConsultoraId}): el cliente ya tiene una delegación total vigente hacia " +
                    $"otro operador ({operacionDeOtroOperador.OperadorTenantId}). El modelo nuevo exige una sola " +
                    "delegación total vigente por cliente — no adivinar cuál es la vigente. No migrada, requiere " +
                    "decisión humana.");
                continue;
            }

            var nueva = AsignacionOperacion.Externa(
                delegacion.TenantClienteId, delegacion.TenantConsultoraId, ServicioCae.Outbound,
                AmbitoAsignacion.Universal,
                delegacion.ActivadaEnUtc ?? delegacion.CreadoEnUtc,
                delegacion.ExpiraEnUtc, ahora);

            dbContext.AsignacionesOperacion.Add(nueva);
            operaciones.Add(nueva);
            creadas++;
        }

        var idsComerciales = delegacionesComerciales.Select(d => d.Id).ToHashSet();
        var operadoresDelegados = await dbContext.AsignacionesOperadorDelegado
            .Where(a => idsComerciales.Contains(a.DelegacionTenantId))
            .ToListAsync(cancellationToken);

        // --- 2b. Reconciliación: carteras Outbound de personas retiradas ---
        //
        // Antes de la PR #2223, retirar a una persona del equipo del Operador CAE externo
        // (CerrarCarteraOperadorAsync) solo cerraba su cartera bajo la operación vigente del Tenant
        // entero, y la fila heredada se borraba: su cartera bajo una operación acotada siguió
        // abierta, y con la delegación activa nada la reconciliaba (ronda 1 de Codex en la PR
        // #2223). Con al menos una delegación activa y vigente del par, se cierra toda cartera no
        // cerrada bajo cualquier Asignación de Operación Outbound externa del par —del Tenant
        // entero o acotada, en cualquier estado— cuya persona no tenga una fila heredada no
        // revocada, con un rol delegable, en una delegación activa y vigente de ese mismo par. Es
        // la misma condición que exige el writer para conceder (ObtenerRolDelegadoAsync), y toda
        // vía que emite una cartera externa escribe también su fila heredada
        // (CatalogoIncorporacionCartera). Sin delegación activa y vigente, la rama de la inactiva
        // o caducada ya lo cerró todo. Inbound no se toca.
        foreach (var par in idsVigentesPorPar)
        {
            var delegacionesVigentesDelPar = par.Value;
            var personasConFila = operadoresDelegados
                .Where(a => delegacionesVigentesDelPar.Contains(a.DelegacionTenantId) && RolesDelegables.Contains(a.Rol))
                .Select(a => a.UsuarioId)
                .ToHashSet();

            var operacionesDelPar = operaciones
                .Where(o => !o.EsRaiz
                            && o.PropietarioTenantId == par.Key.Propietario
                            && o.OperadorTenantId == par.Key.Operador
                            && o.Servicio == ServicioCae.Outbound)
                .Select(o => o.Id)
                .ToHashSet();

            foreach (var huerfana in carteras.Where(c =>
                         operacionesDelPar.Contains(c.AsignacionOperacionId)
                         && c.Estado != EstadoAsignacion.Cerrada
                         && !personasConFila.Contains(c.UsuarioId)))
            {
                huerfana.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
                cerradas++;
                incidencias.Add(
                    $"Cartera externa {huerfana.Id} del usuario {huerfana.UsuarioId}: sin asignación de operador " +
                    $"delegado vigente en una delegación activa de {par.Key.Operador} sobre " +
                    $"{par.Key.Propietario}. Cerrada.");
            }
        }

        // --- 3. Los operadores delegados, como carteras externas ---
        var tenantPorUsuario = await dbContext.Users
            .Select(u => new { u.Id, u.TenantId })
            .ToDictionaryAsync(u => u.Id, u => u.TenantId, cancellationToken);

        foreach (var operador in operadoresDelegados)
        {
            var delegacion = delegacionesComerciales.First(d => d.Id == operador.DelegacionTenantId);

            // Solo las personas de una delegación activa y vigente: con una inactiva o caducada y otra
            // activa del mismo par, la asignación histórica de la primera encontraría la operación
            // vigente de la segunda y recibiría una cartera universal que nadie le concedió (ronda 1
            // de Codex en la PR #2223). No es una incidencia: esa delegación ya no autoriza.
            if (!delegacion.EstaVigente(ahora)) continue;

            if (!tenantPorUsuario.TryGetValue(operador.UsuarioId, out var tenantDelUsuario))
            {
                incidencias.Add(
                    $"Operador delegado {operador.UsuarioId} sobre la delegación {delegacion.Id}: el usuario no existe. No migrado.");
                continue;
            }

            // El invariante de la cadena de autorización: el usuario de una
            // cartera pertenece al tenant que opera. Se comprueba en vez de
            // asumirse porque el comando de alta actual no lo exige y pueden
            // existir filas que ya lo violan.
            if (tenantDelUsuario != delegacion.TenantConsultoraId)
            {
                incidencias.Add(
                    $"Operador delegado {operador.UsuarioId} sobre la delegación {delegacion.Id}: pertenece al tenant " +
                    $"{tenantDelUsuario}, no a la consultora {delegacion.TenantConsultoraId}. No migrado como cartera externa " +
                    "— su acceso queda gobernado por su cartera interna. Requiere decisión humana.");
                continue;
            }

            var operacion = operaciones.FirstOrDefault(o =>
                !o.EsRaiz
                && o.PropietarioTenantId == delegacion.TenantClienteId
                && o.OperadorTenantId == delegacion.TenantConsultoraId
                && o.Servicio == ServicioCae.Outbound
                && o.Ambito.EsUniversal
                && o.Estado == EstadoAsignacion.Vigente);

            if (operacion is null)
            {
                // Delegación inactiva: su operación está cerrada y no procede
                // cartera. No es una incidencia.
                continue;
            }

            // Solo los roles de alcance total reciben cartera universal. Un rol
            // de cartera (GestorCae, CoordinadorCae) no la recibe de este
            // reconciliador: darle una universal le entregaría de golpe el tenant
            // delegado entero —todas sus ramas operativas, ver AlcanceDatosService—
            // sin que nadie lo decidiera. Su cartera (siempre el Tenant entero, D-7)
            // nace de un acto explícito: ver CatalogoIncorporacionCartera y
            // IAsignacionesOperativasWriter.AsegurarCarteraTenantEnteroAsync. El
            // reparto por Cliente empresarial a partir de Empresa.EjecutivoUsuarioId
            // se retiró: la referencia no concede alcance.
            if (!RolesDelegables.Contains(operador.Rol))
            {
                incidencias.Add(
                    $"Operador delegado {operador.UsuarioId} sobre la delegación {delegacion.Id}: rol {operador.Rol} " +
                    "no delegable (solo Coordinador CAE, Gestor CAE o Consulta). No migrado.");
                continue;
            }

            if (!RolesDeAlcanceTotal.Contains(operador.Rol)) continue;

            var yaTiene = carteras.Any(c =>
                c.AsignacionOperacionId == operacion.Id
                && c.UsuarioId == operador.UsuarioId
                && c.Ambito.EsUniversal
                && c.Estado == EstadoAsignacion.Vigente);

            if (yaTiene) continue;

            // Lo suspendido nunca vuelve como vigente, la misma guarda que el writer
            // (AsignacionCartera.AlgunaQuedoSuspendida): con una cartera Suspendida viva o la última
            // del par suspendida, el arranque no emite otra Vigente. Antes la emitía en cuanto no
            // encontraba una Vigente (ronda 2 de Codex en la PR #2223).
            var operacionesDelParPorId = operaciones
                .Where(o => !o.EsRaiz
                            && o.PropietarioTenantId == operacion.PropietarioTenantId
                            && o.OperadorTenantId == operacion.OperadorTenantId
                            && o.Servicio == ServicioCae.Outbound)
                .ToDictionary(o => o.Id);
            var deLaPersonaEnElPar = carteras
                .Where(c => c.UsuarioId == operador.UsuarioId && operacionesDelParPorId.ContainsKey(c.AsignacionOperacionId))
                .Select(c => (Cartera: c, Operacion: operacionesDelParPorId[c.AsignacionOperacionId]));
            if (AsignacionCartera.AlgunaQuedoSuspendida(deLaPersonaEnElPar))
            {
                incidencias.Add(
                    $"Operador delegado {operador.UsuarioId} sobre la delegación {delegacion.Id}: su cartera del par " +
                    "está o quedó suspendida. No se emite otra vigente: reanudarla requiere un acto explícito.");
                continue;
            }

            var nueva = AsignacionCartera.Externa(
                operacion, operador.UsuarioId, operador.Rol, AmbitoAsignacion.Universal,
                operador.CreadoEnUtc, vigenciaHasta: null, ahora);

            dbContext.AsignacionesCartera.Add(nueva);
            carteras.Add(nueva);
            creadas++;
        }

        if (creadas == 0 && cerradas == 0)
        {
            RegistrarIncidencias(logger, incidencias);
            return;
        }

        // Las dos tablas son catálogo global (no llevan TenantId y el
        // interceptor de sellado no las toca), pero el ámbito explícito
        // mantiene el patrón del resto de seeders y deja resuelto el tenant por
        // si el guardado arrastrara alguna entidad con tenant.
        using (AmbitoTenantExplicito.Establecer(tenants[0].Id))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Backfill de asignaciones operativas: {Creadas} creadas, {Cerradas} cerradas.", creadas, cerradas);

        RegistrarIncidencias(logger, incidencias);
    }

    private static void RegistrarIncidencias(ILogger logger, List<string> incidencias)
    {
        if (incidencias.Count == 0) return;

        // Warning y no Error: el sistema arranca y funciona: son filas que
        // requieren una decisión humana, no un fallo. Enumeradas una a una a
        // propósito — un contador no permitiría actuar sobre ellas.
        logger.LogWarning(
            "Backfill de asignaciones operativas: {Cantidad} filas no migradas, requieren revisión.", incidencias.Count);

        foreach (var incidencia in incidencias)
            logger.LogWarning("Backfill de asignaciones operativas — {Incidencia}", incidencia);
    }
}
