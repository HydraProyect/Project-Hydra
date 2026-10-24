using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// PR 8 del plan del estado materializado (decisión D-1): el listado de Subcontratas pinta a cada Subcontrata con su fila de
/// ámbito Empresa en <c>EstadosEntidad</c> y cuenta aparte sus Trabajadores bloqueados con las filas de ellos, cuando el Tenant
/// está completo y el usuario alcanza todos sus Centros; si no, calcula como siempre. Lo leído tiene que dar lo mismo que el
/// cálculo. Cada test toca las tablas de un Tenant del piloto y lo deja como lo encontró con el relleno.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class SubcontratasLeenElEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private static IEnumerable<string> NombresDeTenants => CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre);

    /// <summary>
    /// Con el Tenant relleno, el listado (camino de página y camino del estado completo: franja y un filtro por cada estado que
    /// cuenta) da lo mismo leyendo de las tablas que calculando sin fila de versión. Y sirve de las tablas: con las filas
    /// envenenadas y la fila de versión puesta, el listado pinta el veneno; sin la fila de versión, las mismas filas
    /// envenenadas no se miran.
    /// </summary>
    [Fact]
    public async Task Lo_leido_de_las_tablas_coincide_con_el_calculo_y_sin_version_no_se_miran()
    {
        var comparados = 0;
        var sirvieronDeLasTablas = 0;
        var subcontratasVistas = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, leido) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoAsync(sp)));
            if (!disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => EnvenenarAsync(db));
                var envenenado = await ComoAdministradorAsync(nombre, FotoAsync);
                if (!envenenado.SequenceEqual(leido))
                    sirvieronDeLasTablas++;

                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
                    return 0;
                });
                var calculado = await ComoAdministradorAsync(nombre, FotoAsync);

                calculado.Should().Equal(leido, $"{nombre}: lo que sale de las tablas es lo que calcula la lectura, y sin versión las filas envenenadas no se miran");
                subcontratasVistas += calculado.Count(l => l.StartsWith("S ", StringComparison.Ordinal));
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        salida.WriteLine($"Tenants comparados: {comparados}; filas de Subcontrata comparadas: {subcontratasVistas}");
        comparados.Should().BePositive("el Administrador del Operador CAE alcanza todos los Centros de al menos un Tenant del piloto");
        subcontratasVistas.Should().BePositive("una comparación sin Subcontratas no prueba nada");
        sirvieronDeLasTablas.Should().BePositive(
            "con las filas envenenadas y la versión puesta el listado tiene que pintar el veneno: si no, no sirvió de las tablas");
    }

    /// <summary>
    /// La guarda de la frontera (a): una fila cuyo <c>ProximoCambioEl</c> ya llegó no se sirve. Con todas las filas envenenadas y
    /// vencidas en su frontera, el listado calcula y da lo mismo que antes de envenenarlas.
    /// </summary>
    [Fact]
    public async Task Las_filas_con_la_frontera_ya_llegada_no_se_sirven_y_se_calcula()
    {
        var comparados = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, referencia) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoAsync(sp)));
            if (!disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    await EnvenenarAsync(db);
                    return await db.Set<EstadoEntidad>()
                        .Where(e => e.Ambito == AmbitoEstadoEntidad.Empresa || e.Ambito == AmbitoEstadoEntidad.Trabajador)
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.ProximoCambioEl, (DateOnly?)DiaDeNegocio.Hoy().AddDays(-1)));
                });

                var conFronteraLlegada = await ComoAdministradorAsync(nombre, FotoAsync);
                conFronteraLlegada.Should().Equal(referencia, $"{nombre}: ninguna fila con la frontera llegada se sirve; se calcula");
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive();
    }

    /// <summary>
    /// El recuento de Trabajadores bloqueados sale de las filas de los Trabajadores cuando el lector las sirve: con solo las
    /// filas de Trabajador puestas en Faltante, cada Subcontrata cuenta como bloqueados a todos sus Trabajadores visibles que
    /// no son voluntarios inactivos, y su estado (fila de Empresa, sin tocar) no cambia.
    /// </summary>
    [Fact]
    public async Task Los_Trabajadores_bloqueados_se_cuentan_con_las_filas_de_los_Trabajadores_y_no_cambian_el_estado()
    {
        var comparados = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, deReferencia) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await PaginaAsync(sp)));
            if (!disponible || !deReferencia.Any(s => s.Trabajadores.Count > 0))
                continue;

            try
            {
                // Las filas de Empresa, en un estado que el lector sirve siempre (sin rojo ambiguo ni frontera): así la fila de la
                // Subcontrata sale de las tablas y el recuento de sus Trabajadores también se intenta leer de ellas.
                await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                    .Where(e => e.Ambito == AmbitoEstadoEntidad.Empresa)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Estado, (short)EstadoDocumento.Urgente)
                        .SetProperty(e => e.Vencidos, 0).SetProperty(e => e.VencidosSinBloqueo, 0).SetProperty(e => e.Faltan, 0)
                        .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));
                var antes = await ComoAdministradorAsync(nombre, PaginaAsync);
                antes.Should().OnlyContain(s => s.EstadoDocumental == EstadoDocumento.Urgente, $"{nombre}: el estado de la Subcontrata es su fila de Empresa");

                await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                    .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Estado, (short)EstadoDocumento.Faltante)
                        .SetProperty(e => e.Vencidos, 0).SetProperty(e => e.VencidosSinBloqueo, 0).SetProperty(e => e.Faltan, 1)
                        .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));

                var despues = (await ComoAdministradorAsync(nombre, PaginaAsync)).ToDictionary(s => s.Id);
                foreach (var subcontrata in antes)
                {
                    var leida = despues[subcontrata.Id];
                    leida.EstadoDocumental.Should().Be(subcontrata.EstadoDocumental,
                        $"{nombre}, {subcontrata.RazonSocial}: sus Trabajadores bloqueados no cambian el estado de la Subcontrata (D-1)");
                    leida.TrabajadoresBloqueados.Should().BeLessThanOrEqualTo(leida.Trabajadores.Count);
                }

                despues.Values.Sum(s => s.TrabajadoresBloqueados).Should().BeGreaterThan(antes.Sum(s => s.TrabajadoresBloqueados),
                    $"{nombre}: con las filas de Trabajador en Faltante, el recuento tiene que salir de ellas");
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive("algún Tenant del piloto completo tiene Subcontratas con Trabajadores");
    }

    private async Task<T> ComoAdministradorAsync<T>(string nombreTenant, Func<IServiceProvider, Task<T>> lectura)
    {
        var tenantId = await fixture.Arnes.TenantIdAsync(nombreTenant);
        return await fixture.Arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.AdministradorOperador, async sp =>
            {
                using (AmbitoTenantExplicito.Establecer(tenantId))
                    return await lectura(sp);
            });
    }

    /// <summary>Deja las filas de Empresa y Trabajador con un estado que ningún documento explica.</summary>
    private static async Task<int> EnvenenarAsync(CaeManager.Infrastructure.Persistence.CaeManagerDbContext db) =>
        await db.Set<EstadoEntidad>()
            .Where(e => e.Ambito == AmbitoEstadoEntidad.Empresa || e.Ambito == AmbitoEstadoEntidad.Trabajador)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Estado, (short)EstadoDocumento.Urgente)
                .SetProperty(e => e.Vencidos, 0).SetProperty(e => e.VencidosSinBloqueo, 0).SetProperty(e => e.Faltan, 0)
                .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null));

    /// <summary>El relleno deja las filas como el cálculo y crea la fila de versión si falta: el Tenant queda completo.</summary>
    private async Task RestaurarAsync(Guid tenantId) =>
        await fixture.Arnes.EnTenantAsync(tenantId, (_, sp) =>
            sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));

    private static async Task<List<SubcontrataListaDto>> PaginaAsync(IServiceProvider sp) =>
        await TodasAsync(sp.GetRequiredService<ISender>(), p => new ObtenerSubcontratasQuery(null, Pagina: p, TamanoPagina: 100));

    private static async Task<List<SubcontrataListaDto>> TodasAsync(ISender sender, Func<int, ObtenerSubcontratasQuery> consulta)
    {
        var filas = new List<SubcontrataListaDto>();
        for (var pagina = 1; ; pagina++)
        {
            var resultado = await sender.Send(consulta(pagina));
            filas.AddRange(resultado.Elementos);
            if (resultado.Elementos.Count == 0 || filas.Count >= resultado.TotalElementos)
                return filas;
        }
    }

    /// <summary>
    /// Lo que pinta el listado de Subcontratas por sus dos caminos: página (sin pedir estado) y estado completo (franja y un
    /// filtro por cada estado que la franja cuenta).
    /// </summary>
    private static async Task<List<string>> FotoAsync(IServiceProvider sp)
    {
        var sender = sp.GetRequiredService<ISender>();
        static string Fila(SubcontrataListaDto s) =>
            $"S {s.Id} {s.EstadoDocumental} {s.VencidoSinBloqueo} [{string.Join("|", s.DocumentosFaltantes)}] bloqueados={s.TrabajadoresBloqueados}";

        var foto = (await TodasAsync(sender, p => new ObtenerSubcontratasQuery(null, Pagina: p, TamanoPagina: 100))).Select(Fila).ToList();

        var primera = await sender.Send(new ObtenerSubcontratasQuery(null, TamanoPagina: 100, ConRecuentosPorEstado: true));
        var franja = primera.RecuentosPorEstado ?? new Dictionary<string, int>();
        foto.Add($"Franja {string.Join(";", franja.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"))}");
        foto.AddRange((await TodasAsync(sender, p => new ObtenerSubcontratasQuery(null, Pagina: p, TamanoPagina: 100, ConRecuentosPorEstado: true)))
            .Select(Fila));
        foreach (var estado in franja.Where(f => f.Value > 0).Select(f => f.Key).Order())
            foto.Add($"Subcontratas {estado}: {string.Join(",", (await TodasAsync(sender, p => new ObtenerSubcontratasQuery(
                null, Pagina: p, TamanoPagina: 100, EstadoDocumental: estado))).Select(s => s.Id))}");

        return foto;
    }
}
