using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.MultiTenancy;

/// <summary>
/// Retira POR COMPLETO un tenant de demo — tenant, usuarios, y toda fila
/// tenant-scoped — de forma explícita, auditable y fuera del arranque. Nunca
/// corre sola: la invoca un operador con el <c>TenantId</c> exacto (ver el
/// modo <c>--retirar-tenant-demo</c> de <c>CaeManager.Web/Program.cs</c>).
///
/// <b>Este servicio corre con identidad de propietario de base de datos y
/// bypassa RLS por diseño</b> (<c>FabricaContextoDeBootstrap</c>, no el
/// contexto inyectado) — más poder de lectura/escritura que cualquier tráfico
/// normal de la aplicación tiene jamás. Igual que
/// <c>AsignacionesOperativasBackfillSeeder</c>: la política RLS de
/// <c>AsignacionesCartera</c>/<c>AsignacionesOperacion</c>
/// (<c>posicion_en_la_asignacion</c>) solo deja ver el lado Operador bajo
/// <c>app.tenant_origen_id</c> == el tenant del usuario autenticado, una
/// coordenada que no existe fuera de una sesión HTTP real. Con el contexto
/// inyectado (rol restringido), retirar un tenant que opera la cartera de
/// otro (una Consultora) dejaría esas filas huérfanas sin ningún error — RLS
/// no falla, solo no muestra.
///
/// <b>Precisamente porque eleva privilegios, la validación va ANTES de
/// elevar, nunca después — y eso está en la firma de los métodos, no en el
/// orden de las líneas.</b> <see cref="ValidarTenantRetirableAsync"/> corre
/// con la identidad NO privilegiada (el contexto inyectado — <c>Tenants</c>
/// no lleva RLS, así que esta lectura no la necesita) y es la ÚNICA forma de
/// obtener el <see cref="Tenant"/> que <see cref="RetirarAsync"/> exige como
/// parámetro: no hay ningún camino de compilación que llegue a
/// <see cref="RetirarAsync"/> con un <c>Guid</c> suelto sin haber pasado antes
/// por la validación. <see cref="RetirarAsync"/> repite igualmente el
/// rechazo con el tenant ya validado, en profundidad, por si algún día algo
/// construye un <see cref="Tenant"/> por otra vía — pero la validación
/// determinante, la que decide si vale la pena elevar el proceso a todo, es
/// la de antes.
///
/// <b>La propiedad que importa de verdad, la única que este servicio existe
/// para garantizar</b>: no puede alcanzar un tenant que no sea de demo. Se
/// demuestra, no se promete — ver
/// <c>RetiradaTenantDemoServiceTests.La_retirada_se_niega_...</c> (falsado por
/// mutación: invertir el <c>Contains</c> de abajo debe volver rojo ese test).
/// La comprobación es una allowlist explícita de los nombres EXACTOS que
/// siembran <see cref="DelegacionDemoSeeder"/> y <see cref="SegundoTenantSeeder"/>
/// — nunca una heurística sobre el nombre (ni "contiene 'demo'", ni un
/// prefijo), porque una heurística puede coincidir con un tenant real que un
/// cliente haya bautizado igual por casualidad. El tenant de plataforma
/// (<see cref="Tenant.EsPlataforma"/>) se rechaza primero y por separado, ni
/// siquiera llega a mirar la allowlist.
///
/// <para>
/// <b>Cómo borra, y por qué así</b>: recorre por reflexión el mismo universo
/// de tipos que <see cref="CaeManagerDbContext"/> usa para aplicar el filtro
/// de tenant (<see cref="EntidadConTenant"/>) — la misma garantía estructural
/// que hace imposible olvidar una entidad nueva se aplica aquí. Cada fila se
/// CARGA (no <c>ExecuteDelete</c>) y se marca con <c>Remove</c>: un único
/// <c>SaveChangesAsync</c> al final deja que EF Core resuelva el orden de
/// borrado por las FK compuestas <c>(TenantId, XxxId)</c> que este modelo usa
/// entre agregados — reproducir ese orden a mano, tabla a tabla, sería la
/// misma clase de trampa que ya forzó a la reflexión en el filtro de lectura.
/// </para>
///
/// <para>
/// Los catálogos globales que referencian este tenant o a sus usuarios no los
/// alcanza el bucle de <see cref="EntidadConTenant"/> porque no heredan de
/// ella; se limpian aquí explícitamente, y no todos por el mismo motivo.
/// <c>DelegacionesTenant</c>, <c>AsignacionesOperadorDelegado</c>,
/// <c>AceptacionesTerminos</c>, <c>FiltrosGuardados</c>,
/// <c>PreferenciasDashboardUsuario</c>, <c>SesionesPrivilegiadas</c> y
/// <c>TenantsAlcanzadosPorConcesion</c> no tienen FK física hacia
/// <c>Tenants</c> (ver los comentarios "Sin HasQueryFilter" de sus
/// configuraciones) — dejarlos sería basura huérfana silenciosa, pero
/// Postgres no los habría bloqueado. <c>AsignacionesCartera</c> y
/// <c>AsignacionesOperacion</c> son distintas: SÍ llevan FK compuesta
/// <c>Restrict</c> hacia Empresa/Centro/Trabajador/Proyecto por
/// <c>PropietarioTenantId</c> (ADR-011, plano 2) — sin limpiarlas primero,
/// Postgres rechaza con un 23503 en cuanto el tenant retirado es propietario
/// de alguna asignación. Esto no lo demostró el test contra el arnés (que
/// nunca corre <c>AsignacionesOperativasBackfillSeeder</c>): lo destapó la
/// primera ejecución real del binario contra una base sembrada de verdad.
/// </para>
/// </summary>
public static class RetiradaTenantDemoService
{
    /// <summary>
    /// Allowlist exacta — cualquier tenant cuyo <see cref="Tenant.Nombre"/> no
    /// esté aquí, letra por letra, se rechaza. Deliberadamente enumerada a
    /// mano (no derivada de un prefijo ni de una constante compartida): que
    /// añadir un tenant de demo nuevo obligue a tocar esta lista es el freno,
    /// no un descuido a corregir.
    /// </summary>
    public static readonly IReadOnlyList<string> NombresTenantsDeDemo =
    [
        DelegacionDemoSeeder.NombreTenantConsultora,
        DelegacionDemoSeeder.NombreTenantRefrielectric,
        DelegacionDemoSeeder.NombreTenantClienteDemo,
        DelegacionDemoSeeder.NombreTenantClienteDemo2,
        DelegacionDemoSeeder.NombreTenantClienteDemo3,
        SegundoTenantSeeder.NombreSegundoTenant,
    ];

    private static readonly MethodInfo MetodoCargarFilasDeTenant =
        typeof(RetiradaTenantDemoService).GetMethod(nameof(CargarFilasDeTenantAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public sealed record ResultadoRetirada(string NombreTenant, Guid TenantId, int FilasBorradas, int UsuariosBorrados);

    /// <summary>
    /// Paso 1, con identidad NO privilegiada — <paramref name="dbContext"/> es
    /// el contexto inyectado normal (rol restringido), nunca el de bootstrap.
    /// <c>Tenants</c> no lleva RLS (es el catálogo raíz, se lee antes de que
    /// exista ningún contexto de tenant — ver <c>TenantConfiguration</c>), así
    /// que esta lectura no necesita ningún privilegio elevado.
    ///
    /// Lanza <see cref="InvalidOperationException"/> si el tenant no existe,
    /// es el de plataforma, o no está en <see cref="NombresTenantsDeDemo"/> —
    /// fallo cerrado: ante cualquier duda, no se llega a elevar nada.
    /// </summary>
    public static async Task<Tenant> ValidarTenantRetirableAsync(
        CaeManagerDbContext dbContext, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Tenants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"No existe ningún tenant con Id {tenantId} — no se borra nada.");

        RechazarSiNoEsRetirable(tenant);

        return tenant;
    }

    private static void RechazarSiNoEsRetirable(Tenant tenant)
    {
        if (tenant.EsPlataforma)
            throw new InvalidOperationException(
                $"'{tenant.Nombre}' es el tenant de plataforma — la retirada lo rechaza siempre, sin excepción.");

        if (!NombresTenantsDeDemo.Contains(tenant.Nombre))
            throw new InvalidOperationException(
                $"'{tenant.Nombre}' no está en la lista de tenants de demo conocidos " +
                $"({string.Join(", ", NombresTenantsDeDemo)}) — la retirada se niega por diseño: solo borra " +
                "tenants cuyo nombre coincide EXACTAMENTE con uno de los que siembran DelegacionDemoSeeder o SegundoTenantSeeder.");
    }

    /// <summary>
    /// Paso 2, con identidad de BOOTSTRAP — <paramref name="dbContext"/> tiene
    /// que venir de <c>FabricaContextoDeBootstrap</c>. Exige un
    /// <see cref="Tenant"/> ya validado por <see cref="ValidarTenantRetirableAsync"/>,
    /// no un <c>Guid</c> suelto: la firma hace estructuralmente imposible
    /// llegar aquí sin haber pasado antes por el paso 1. Repite el rechazo de
    /// todas formas (en profundidad, no por desconfianza del llamante) antes
    /// de tocar una sola fila.
    /// </summary>
    public static async Task<ResultadoRetirada> RetirarAsync(
        CaeManagerDbContext dbContext, Tenant tenant, ILogger logger, CancellationToken cancellationToken = default)
    {
        RechazarSiNoEsRetirable(tenant);

        var tenantId = tenant.Id;
        var filasBorradas = 0;
        var usuariosBorrados = 0;

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            foreach (var tipoEntidad in dbContext.Model.GetEntityTypes()
                         .Select(t => t.ClrType)
                         .Where(t => typeof(EntidadConTenant).IsAssignableFrom(t))
                         .Distinct())
            {
                var tarea = (Task)MetodoCargarFilasDeTenant.MakeGenericMethod(tipoEntidad)
                    .Invoke(null, [dbContext, tenantId, cancellationToken])!;
                await tarea.ConfigureAwait(false);
                var filas = (System.Collections.IEnumerable)tarea.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(tarea)!;
                foreach (var fila in filas)
                {
                    dbContext.Remove(fila);
                    filasBorradas++;
                }
            }

            // AsignacionesCartera/AsignacionesOperacion (plano 2, ADR-011): NO
            // heredan de EntidadConTenant (cruzan tenants por naturaleza, ver
            // AsignacionOperacionConfiguration), así que el bucle de arriba no
            // las toca. Pero SÍ llevan FK compuesta Restrict hacia
            // Empresa/Centro/Trabajador/Proyecto por PropietarioTenantId — sin
            // esto, Postgres rechaza el borrado de la cartera con un 23503 en
            // cuanto el tenant retirado es Propietario de alguna asignación
            // (comprobado contra el proceso real, no solo en test: la primera
            // vez que corrí --retirar-tenant-demo de verdad falló exactamente
            // aquí). OperadorTenantId se limpia igual, por higiene — no tiene
            // FK física, pero una asignación cuyo operador ya no existe es
            // basura huérfana. Cartera antes que Operación en el código para
            // que se lea en el orden de dependencia, aunque el único
            // SaveChangesAsync de abajo resuelve el orden real por sí solo.
            dbContext.RemoveRange(await dbContext.AsignacionesCartera
                .Where(a => a.PropietarioTenantId == tenantId || a.OperadorTenantId == tenantId)
                .ToListAsync(cancellationToken));
            dbContext.RemoveRange(await dbContext.AsignacionesOperacion
                .Where(a => a.PropietarioTenantId == tenantId || a.OperadorTenantId == tenantId)
                .ToListAsync(cancellationToken));

            var usuarios = await dbContext.Users.Where(u => u.TenantId == tenantId).ToListAsync(cancellationToken);
            var idsUsuarios = usuarios.Select(u => u.Id).ToHashSet();

            if (idsUsuarios.Count > 0)
            {
                dbContext.RemoveRange(await dbContext.AceptacionesTerminos.Where(a => idsUsuarios.Contains(a.UsuarioId)).ToListAsync(cancellationToken));
                dbContext.RemoveRange(await dbContext.FiltrosGuardados.Where(f => idsUsuarios.Contains(f.UsuarioId)).ToListAsync(cancellationToken));
                dbContext.RemoveRange(await dbContext.PreferenciasDashboardUsuario.Where(p => idsUsuarios.Contains(p.UsuarioId)).ToListAsync(cancellationToken));
                dbContext.RemoveRange(await dbContext.AsignacionesOperadorDelegado.Where(a => idsUsuarios.Contains(a.UsuarioId)).ToListAsync(cancellationToken));
            }

            var delegaciones = await dbContext.DelegacionesTenant
                .Where(d => d.TenantConsultoraId == tenantId || d.TenantClienteId == tenantId)
                .ToListAsync(cancellationToken);
            if (delegaciones.Count > 0)
            {
                var idsDelegaciones = delegaciones.Select(d => d.Id).ToHashSet();
                dbContext.RemoveRange(await dbContext.AsignacionesOperadorDelegado
                    .Where(a => idsDelegaciones.Contains(a.DelegacionTenantId)).ToListAsync(cancellationToken));
                dbContext.RemoveRange(delegaciones);
            }

            dbContext.RemoveRange(await dbContext.SesionesPrivilegiadas
                .Where(s => s.TenantObjetivoId == tenantId).ToListAsync(cancellationToken));
            dbContext.RemoveRange(await dbContext.TenantsAlcanzadosPorConcesion
                .Where(t => t.TenantId == tenantId).ToListAsync(cancellationToken));

            usuariosBorrados = usuarios.Count;
            dbContext.RemoveRange(usuarios);
            dbContext.Remove(tenant);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning(
            "RETIRADA: tenant de demo '{Nombre}' ({TenantId}) borrado por completo — {FilasBorradas} filas tenant-scoped, {UsuariosBorrados} usuarios.",
            tenant.Nombre, tenantId, filasBorradas, usuariosBorrados);

        return new ResultadoRetirada(tenant.Nombre, tenantId, filasBorradas, usuariosBorrados);
    }

    private static async Task<List<TEntidad>> CargarFilasDeTenantAsync<TEntidad>(
        CaeManagerDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
        where TEntidad : EntidadConTenant =>
        await dbContext.Set<TEntidad>().IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ToListAsync(cancellationToken);
}
