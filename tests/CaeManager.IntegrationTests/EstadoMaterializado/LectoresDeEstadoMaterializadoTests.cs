using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
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
/// PR 7 del plan del estado materializado: los listados de Empresas y de Trabajadores (y con ellos Empresa 360 y la cabecera de
/// Trabajador 360, que leen de esas dos consultas) leen el estado de <c>EstadosEntidad</c> cuando el Tenant está completo y el
/// usuario alcanza todos sus Centros, y dan lo mismo que el cálculo en lectura de siempre. Un Tenant sin su fila de versión (el
/// relleno inicial de la PR 4b aún no ha pasado por él) sigue calculando. Cada test toca las tablas de un Tenant del piloto y
/// lo deja como lo encontró con el relleno.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class LectoresDeEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private static IEnumerable<string> NombresDeTenants => CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre);

    /// <summary>
    /// Sensible al lector: con el Tenant completo, lo que leen las pantallas es lo que hay en las tablas; con la fila de versión
    /// borrada, las mismas tablas envenenadas no se miran y sale lo mismo que antes. Si el lector no leyera de las tablas, o
    /// las leyera sin fila de versión, uno de los dos extremos fallaría.
    /// </summary>
    [Fact]
    public async Task Un_Tenant_sin_fila_de_version_no_lee_de_las_tablas_y_calcula_lo_mismo()
    {
        var comparados = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, referencia) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            if (!disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
                    await EnvenenarAsync(db);
                    return 0;
                });

                var (disponibleSinVersion, calculado) = await ComoAdministradorAsync(nombre, async sp =>
                    (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));

                disponibleSinVersion.Should().BeFalse($"{nombre}: sin fila de versión el Tenant no está completo");
                calculado.Should().Equal(referencia, $"{nombre}: sin fila de versión se calcula, y no se mira ninguna fila (están envenenadas)");
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive("el Administrador del Operador CAE alcanza todos los Centros de al menos un Tenant del piloto");
    }

    /// <summary>
    /// Lo leído de las tablas coincide con el cálculo en lectura para el mismo Tenant ya rellenado: el listado de Empresas y el
    /// de Trabajadores, por el camino de página y por el del estado completo (franja, filtro por cada estado y orden por estado).
    /// La referencia es la misma lectura con la fila de versión borrada: el cálculo de siempre.
    /// </summary>
    [Fact]
    public async Task Lo_leido_de_las_tablas_coincide_con_el_calculo_en_lectura()
    {
        var comparados = 0;
        var estadosVistos = new HashSet<string>();
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, leido) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            if (!disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
                    return 0;
                });
                var calculado = await ComoAdministradorAsync(nombre, FotoDePantallasAsync);

                calculado.Should().Equal(leido, $"{nombre}: lo que sale de las tablas es lo que calcula la lectura");
                foreach (var linea in calculado.Where(l => l.StartsWith("E ") || l.StartsWith("T ")))
                    estadosVistos.Add(linea.Split(' ')[2]);
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive();
        salida.WriteLine($"Estados distintos comparados: {string.Join(", ", estadosVistos.Order())}");
        estadosVistos.Count.Should().BeGreaterThan(2, "una comparación en la que todos tienen el mismo estado no prueba la clave de estado");
    }

    /// <summary>La tabla manda: cambiar una fila cambia lo que pinta la pantalla, sin tocar ningún documento.</summary>
    [Fact]
    public async Task Con_el_Tenant_completo_la_pantalla_pinta_lo_que_dice_la_fila()
    {
        var nombre = NombresDeTenants.First();
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var (disponible, trabajadorId) = await ComoAdministradorAsync(nombre, async sp =>
        {
            var lector = sp.GetRequiredService<ILectorEstadoMaterializado>();
            var primero = (await sp.GetRequiredService<ISender>().Send(new ObtenerTrabajadoresQuery(null, TamanoPagina: 1))).Elementos.FirstOrDefault();
            return (await lector.DisponibleAsync(CancellationToken.None), primero?.Id ?? Guid.Empty);
        });
        disponible.Should().BeTrue($"{nombre}: el Administrador del Operador CAE alcanza todos sus Centros y el relleno lo completó");
        trabajadorId.Should().NotBeEmpty();

        Task<TrabajadorListaDto> LeerFilaAsync() => ComoAdministradorAsync(nombre, async sp => (await sp.GetRequiredService<ISender>().Send(
            new ObtenerTrabajadoresQuery(null, TrabajadorId: trabajadorId))).Elementos.Single());

        var real = await LeerFilaAsync();
        // Un estado que no es el de sus documentos: lo que se lea será lo escrito, no lo calculado.
        var escrito = real.EstadoDocumental == EstadoDocumento.Urgente ? EstadoDocumento.Proximo : EstadoDocumento.Urgente;

        try
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador && e.EntidadId == trabajadorId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Estado, (short)escrito)
                    .SetProperty(e => e.Vencidos, 0).SetProperty(e => e.VencidosSinBloqueo, 0).SetProperty(e => e.Faltan, 0)
                    .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));

            var fila = await LeerFilaAsync();
            fila.EstadoDocumental.Should().Be(escrito, "con el Tenant completo la pantalla pinta la fila");
            fila.VencidoSinBloqueo.Should().BeFalse();
            fila.DocumentosFaltantes.Should().BeEmpty();

            // Una fila cuya frontera ya llegó es de un día anterior: no se sirve, se calcula.
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador && e.EntidadId == trabajadorId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ProximoCambioEl, (DateOnly?)DiaDeNegocio.Hoy().AddDays(-1))));
            var calculada = await LeerFilaAsync();
            (calculada.EstadoDocumental, calculada.VencidoSinBloqueo, string.Join("|", calculada.DocumentosFaltantes)).Should().Be(
                (real.EstadoDocumental, real.VencidoSinBloqueo, string.Join("|", real.DocumentosFaltantes)),
                "con la frontera vencida el lector no sirve la fila y la pantalla calcula");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Alcance: quien no alcanza todos los Centros del Tenant no lee el estado guardado, que es el de todos ellos. Con las
    /// tablas envenenadas, sus pantallas dicen lo mismo que antes de envenenarlas.
    /// </summary>
    [Fact]
    public async Task Un_usuario_que_no_alcanza_todos_los_Centros_no_lee_de_las_tablas()
    {
        var sinLeerDeLasTablas = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, antes) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            salida.WriteLine($"{nombre}: la Gestora CAE {(disponible ? "alcanza" : "no alcanza")} todos los Centros");
            if (disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => EnvenenarAsync(db));
                var despues = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre, FotoDePantallasAsync);
                despues.Should().Equal(antes, $"{nombre}: con alcance parcial se calcula, y las tablas envenenadas no se miran");
                sinLeerDeLasTablas++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        sinLeerDeLasTablas.Should().BePositive("hay Tenants del piloto fuera de la cartera de la Gestora CAE");
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

    /// <summary>
    /// Lo que pintan los listados de Empresas y de Trabajadores, por los dos caminos de cada uno: página (sin pedir estado) y
    /// estado completo (franja, orden por estado y un filtro por cada estado que la franja cuenta).
    /// </summary>
    private static async Task<List<string>> FotoDePantallasAsync(IServiceProvider sp)
    {
        var sender = sp.GetRequiredService<ISender>();
        var foto = new List<string>();

        async Task<(List<T> Filas, string Recuentos)> TodasAsync<T>(Func<int, Task<ResultadoPaginado<T>>> pide, List<string>? estados = null)
        {
            var filas = new List<T>();
            var recuentos = string.Empty;
            for (var pagina = 1; ; pagina++)
            {
                var resultado = await pide(pagina);
                if (pagina == 1 && resultado.RecuentosPorEstado is { } franja)
                {
                    recuentos = string.Join(";", franja.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
                    estados?.AddRange(franja.Where(f => f.Value > 0).Select(f => f.Key).Order());
                }

                filas.AddRange(resultado.Elementos);
                if (resultado.Elementos.Count == 0 || filas.Count >= resultado.TotalElementos)
                    return (filas, recuentos);
            }
        }

        static string Empresa(EmpresaListaDto e) =>
            $"E {e.Id} {e.EstadoDocumental} {e.VencidoSinBloqueo} {e.CumplimientoPorcentaje} [{string.Join("|", e.DocumentosFaltantes)}]";
        static string Trabajador(TrabajadorListaDto t) =>
            $"T {t.Id} {t.EstadoDocumental} {t.VencidoSinBloqueo} {t.VoluntarioInactivo} [{string.Join("|", t.DocumentosFaltantes)}]";

        // Empresas: página.
        foto.AddRange((await TodasAsync(p => sender.Send(new ObtenerEmpresasQuery(null, Pagina: p, TamanoPagina: 100)))).Filas.Select(Empresa));
        // Empresas: estado completo (franja y orden), y un filtro por cada estado de la franja.
        var estadosEmpresa = new List<string>();
        var (empresas, franjaEmpresas) = await TodasAsync(
            p => sender.Send(new ObtenerEmpresasQuery(
                null, Pagina: p, TamanoPagina: 100, OrdenarPor: nameof(EmpresaListaDto.EstadoDocumental), ConRecuentosPorEstado: true)),
            estadosEmpresa);
        foto.Add($"Franja de Empresas {franjaEmpresas}");
        foto.AddRange(empresas.Select(Empresa));
        foreach (var estado in estadosEmpresa)
            foto.Add($"Empresas {estado}: {string.Join(",", (await TodasAsync(p => sender.Send(new ObtenerEmpresasQuery(
                null, Pagina: p, TamanoPagina: 100, EstadoDocumental: estado)))).Filas.Select(e => e.Id))}");

        // Trabajadores: lo mismo.
        foto.AddRange((await TodasAsync(p => sender.Send(new ObtenerTrabajadoresQuery(null, Pagina: p, TamanoPagina: 100)))).Filas.Select(Trabajador));
        var estadosTrabajador = new List<string>();
        var (trabajadores, franjaTrabajadores) = await TodasAsync(
            p => sender.Send(new ObtenerTrabajadoresQuery(
                null, Pagina: p, TamanoPagina: 100, OrdenarPor: nameof(TrabajadorListaDto.EstadoDocumental), ConRecuentosPorEstado: true)),
            estadosTrabajador);
        foto.Add($"Franja de Trabajadores {franjaTrabajadores}");
        foto.AddRange(trabajadores.Select(Trabajador));
        foreach (var estado in estadosTrabajador)
            foto.Add($"Trabajadores {estado}: {string.Join(",", (await TodasAsync(p => sender.Send(new ObtenerTrabajadoresQuery(
                null, Pagina: p, TamanoPagina: 100, EstadoDocumental: estado)))).Filas.Select(t => t.Id))}");

        return foto;
    }
}
