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
/// Los catálogos globales que referencian este tenant o a sus usuarios sin FK
/// real (<c>DelegacionesTenant</c>, <c>AsignacionesOperadorDelegado</c>,
/// <c>AceptacionesTerminos</c>, <c>FiltrosGuardados</c>,
/// <c>PreferenciasDashboardUsuario</c>, <c>SesionesPrivilegiadas</c>,
/// <c>TenantsAlcanzadosPorConcesion</c> — ver los comentarios "Sin
/// HasQueryFilter" de sus configuraciones) no los alcanza el bucle de
/// <see cref="EntidadConTenant"/> porque no heredan de ella; se limpian aquí
/// explícitamente. Postgres no los habría bloqueado (no hay constraint física
/// hacia <c>Tenants</c> en ninguno, comprobado en las configuraciones), pero
/// dejarlos sería basura huérfana silenciosa.
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
    /// Lanza <see cref="InvalidOperationException"/> si el tenant no existe,
    /// es el de plataforma, o no está en <see cref="NombresTenantsDeDemo"/> —
    /// fallo cerrado: ante cualquier duda, no borra nada.
    /// </summary>
    public static async Task<ResultadoRetirada> RetirarAsync(
        CaeManagerDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Tenants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"No existe ningún tenant con Id {tenantId} — no se borra nada.");

        if (tenant.EsPlataforma)
            throw new InvalidOperationException(
                $"'{tenant.Nombre}' es el tenant de plataforma — la retirada lo rechaza siempre, sin excepción.");

        if (!NombresTenantsDeDemo.Contains(tenant.Nombre))
            throw new InvalidOperationException(
                $"'{tenant.Nombre}' no está en la lista de tenants de demo conocidos " +
                $"({string.Join(", ", NombresTenantsDeDemo)}) — la retirada se niega por diseño: solo borra " +
                "tenants cuyo nombre coincide EXACTAMENTE con uno de los que siembran DelegacionDemoSeeder o SegundoTenantSeeder.");

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
