using CaeManager.Application.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Traslada el reparto de responsabilidad operativa que hoy vive disperso en
/// <c>DelegacionTenant</c> y <c>Cliente.EjecutivoUsuarioId</c> a las tablas de
/// asignación (F1 del plan de migración).
///
/// <b>Idempotente y reconciliador</b>, no solo "insertar si falta". Durante F1
/// los mecanismos antiguos siguen siendo los autoritativos y se escriben en
/// paralelo, pero entre el backfill y la activación de la doble escritura hay
/// una ventana real —despliegue rolling, varias instancias— en la que un
/// ejecutivo puede cambiar. Un seeder que solo insertara dejaría esa diferencia
/// congelada para siempre; este cierra lo que ya no corresponde y abre lo que
/// falta, en cada arranque, hasta que la doble escritura quede establecida.
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
    private static readonly string[] RolesDeAlcanceTotal =
        [Roles.Administrador, Roles.DireccionCae, Roles.Consulta];

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

        // --- 1. La operación raíz de cada tenant ---
        //
        // Para TODOS los tenants, no solo los activos: la raíz es el ancla de
        // las carteras internas, y un tenant suspendido con clientes que tienen
        // ejecutivo asignado necesita tenerla o el paso 4 no encontraría dónde
        // colgarlas. Crearla no concede nada por sí sola — sin cartera no hay
        // acceso.
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
            .Where(d => d.Proposito == PropositoDelegacion.Comercial)
            .OrderBy(d => d.CreadoEnUtc)
            .ToListAsync(cancellationToken);

        foreach (var delegacion in delegacionesComerciales)
        {
            var operacionDeEsteOperador = operaciones.FirstOrDefault(o =>
                !o.EsRaiz
                && o.PropietarioTenantId == delegacion.TenantClienteId
                && o.OperadorTenantId == delegacion.TenantConsultoraId
                && o.Servicio == ServicioCae.Outbound
                && o.Ambito.EsUniversal
                && o.Estado != EstadoAsignacion.Cerrada);

            if (!delegacion.Activa)
            {
                if (operacionDeEsteOperador is not null)
                {
                    // Reconciliación: la delegación se desactivó después de que
                    // el backfill creara la operación. El modelo antiguo no
                    // guardó cuándo, así que se marca como tal en vez de
                    // inventar una fecha — la etapa quedó con vigencia real
                    // desconocida.
                    operacionDeEsteOperador.Cerrar(MotivoCierreAsignacion.MigradaSinFecha, ahora);
                    cerradas++;
                }

                continue;
            }

            if (operacionDeEsteOperador is not null) continue; // ya migrada para este operador

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
                && o.PropietarioTenantId == delegacion.TenantClienteId
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

        // --- 3. Los operadores delegados, como carteras externas ---
        var idsDelegacionesComerciales = delegacionesComerciales.Select(d => d.Id).ToHashSet();
        var operadoresDelegados = await dbContext.AsignacionesOperadorDelegado
            .Where(a => idsDelegacionesComerciales.Contains(a.DelegacionTenantId))
            .ToListAsync(cancellationToken);

        var tenantPorUsuario = await dbContext.Users
            .Select(u => new { u.Id, u.TenantId })
            .ToDictionaryAsync(u => u.Id, u => u.TenantId, cancellationToken);

        foreach (var operador in operadoresDelegados)
        {
            var delegacion = delegacionesComerciales.First(d => d.Id == operador.DelegacionTenantId);

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
            // de cartera (GestorCae, CoordinadorCae) ve hoy exactamente los
            // clientes de los que es ejecutivo; darle una universal le
            // entregaría de golpe todos los clientes del tenant delegado — un
            // ensanchamiento de alcance que F1 no debe introducir. Sus carteras
            // salen del paso 4, cliente a cliente.
            if (!RolesDeAlcanceTotal.Contains(operador.Rol)) continue;

            var yaTiene = carteras.Any(c =>
                c.AsignacionOperacionId == operacion.Id
                && c.UsuarioId == operador.UsuarioId
                && c.Ambito.EsUniversal
                && c.Estado == EstadoAsignacion.Vigente);

            if (yaTiene) continue;

            var nueva = AsignacionCartera.Externa(
                operacion, operador.UsuarioId, operador.Rol, AmbitoAsignacion.Universal,
                operador.CreadoEnUtc, vigenciaHasta: null, ahora);

            dbContext.AsignacionesCartera.Add(nueva);
            carteras.Add(nueva);
            creadas++;
        }

        // --- 4. Los ejecutivos de cliente, como carteras por relación ---
        //
        // IgnoreQueryFilters porque el backfill recorre todos los tenants a la
        // vez y arranca sin sesión: es la única lectura del sistema que
        // legítimamente cruza la frontera, y solo para escribir el reparto que
        // ya existe. Se excluyen los clientes eliminados: una cartera vigente
        // sobre algo invisible ocuparía su índice único para siempre.
        //
        // F3b — lee Empresas (EsCritico != null identifica a un ex-Cliente),
        // no la tabla legacy Clientes: desde la congelación, un Cliente nuevo
        // solo existe en Empresas, y F3a ya copió ahí el EjecutivoUsuarioId de
        // todo Cliente preexistente — Empresas es la fuente completa para las
        // dos generaciones, la legacy dejaría invisibles a los posteriores al
        // corte.
        var clientes = await dbContext.Empresas
            .IgnoreQueryFilters()
            .Where(c => c.EsCritico != null && !c.EstaEliminado)
            .Select(c => new { c.Id, c.TenantId, c.EjecutivoUsuarioId })
            .ToListAsync(cancellationToken);

        foreach (var cliente in clientes)
        {
            var vigentes = carteras
                .Where(c => c.AmbitoRelacionClienteId == cliente.Id
                            && c.PropietarioTenantId == cliente.TenantId
                            && c.Estado == EstadoAsignacion.Vigente)
                .ToList();

            // Sin ejecutivo: cerrar lo que hubiera. Es la mitad reconciliadora
            // — un cliente al que le quitaron el gestor no puede quedarse con
            // la cartera abierta.
            if (cliente.EjecutivoUsuarioId is not { } ejecutivoId)
            {
                foreach (var sobrante in vigentes)
                {
                    sobrante.Cerrar(MotivoCierreAsignacion.Reorganizada, ahora);
                    cerradas++;
                }
                continue;
            }

            if (vigentes.Any(c => c.UsuarioId == ejecutivoId))
            {
                // Ya está, pero puede haber otras de un ejecutivo anterior si la
                // reasignación ocurrió fuera de la doble escritura.
                foreach (var sobrante in vigentes.Where(c => c.UsuarioId != ejecutivoId))
                {
                    sobrante.Cerrar(MotivoCierreAsignacion.Reorganizada, ahora);
                    cerradas++;
                }
                continue;
            }

            if (!tenantPorUsuario.TryGetValue(ejecutivoId, out var tenantDelEjecutivo))
            {
                incidencias.Add(
                    $"Cliente {cliente.Id}: su ejecutivo {ejecutivoId} no existe como usuario. Cartera no migrada.");
                continue;
            }

            AsignacionCartera? nueva;

            if (tenantDelEjecutivo == cliente.TenantId)
            {
                var raiz = operaciones.FirstOrDefault(o =>
                    o.EsRaiz && o.PropietarioTenantId == cliente.TenantId
                    && o.Servicio == ServicioCae.Outbound && o.Estado == EstadoAsignacion.Vigente);

                if (raiz is null)
                {
                    incidencias.Add($"Cliente {cliente.Id}: no hay operación raíz vigente en su tenant. Cartera no migrada.");
                    continue;
                }

                nueva = AsignacionCartera.Interna(
                    raiz, ejecutivoId, AmbitoAsignacion.DeRelacionCliente(cliente.Id),
                    raiz.VigenciaDesde, vigenciaHasta: null, ahora);
            }
            else
            {
                // El ejecutivo pertenece a otro tenant: es un operador delegado.
                // Su cartera cuelga de la operación EXTERNA de su tenant, no de
                // la raíz — colgarla de la raíz rompería la cadena "el usuario
                // pertenece al tenant operador", y dejarla sin rol le devolvería
                // el rol que tiene en SU tenant, que es justo el fallo que el
                // rol efectivo por delegación corrigió en su día.
                var externa = operaciones.FirstOrDefault(o =>
                    !o.EsRaiz
                    && o.PropietarioTenantId == cliente.TenantId
                    && o.OperadorTenantId == tenantDelEjecutivo
                    && o.Servicio == ServicioCae.Outbound
                    && o.Estado == EstadoAsignacion.Vigente);

                if (externa is null)
                {
                    incidencias.Add(
                        $"Cliente {cliente.Id}: su ejecutivo {ejecutivoId} pertenece al tenant {tenantDelEjecutivo}, " +
                        "que no tiene delegación comercial activa sobre este tenant. Cartera no migrada.");
                    continue;
                }

                var rol = operadoresDelegados
                    .FirstOrDefault(a => a.UsuarioId == ejecutivoId
                                         && delegacionesComerciales.Any(d => d.Id == a.DelegacionTenantId
                                                                             && d.TenantClienteId == cliente.TenantId
                                                                             && d.TenantConsultoraId == tenantDelEjecutivo))
                    ?.Rol ?? Roles.GestorCae;

                nueva = AsignacionCartera.Externa(
                    externa, ejecutivoId, rol, AmbitoAsignacion.DeRelacionCliente(cliente.Id),
                    externa.VigenciaDesde, vigenciaHasta: null, ahora);
            }

            foreach (var sobrante in vigentes)
            {
                sobrante.Cerrar(MotivoCierreAsignacion.Reorganizada, ahora);
                cerradas++;
            }

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
