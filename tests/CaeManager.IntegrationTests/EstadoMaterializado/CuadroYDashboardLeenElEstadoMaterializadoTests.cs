using System.Text.Json;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
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
/// PR 10 del plan del estado materializado: el Cuadro de dirección (cumplimiento en contexto Empresa por Tenant beneficiario) y
/// el Dashboard ejecutivo (cumplimiento de los Centros) leen el cumplimiento de las tablas cuando el Tenant está completo y dan
/// lo mismo que el cálculo de siempre. Cada Tenant decide por separado, con su propio contexto, y nunca se mezclan datos entre
/// Tenants. Lo que el Operador CAE cobra por Tenant beneficiario no depende del estado documental: con las tablas envenenadas y
/// sin ellas, ningún importe cambia. Cada test toca las tablas de los Tenants del piloto y las deja como las encontró con el relleno.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class CuadroYDashboardLeenElEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private static readonly Regex Instantes = new("\"(MedidoEnUtc|CorteUtc)\":\"[^\"]*\"", RegexOptions.Compiled);

    // ------------------------------------------------------------------------------------------ Cuadro de dirección

    /// <summary>
    /// Lo leído coincide con el cálculo y sale de las tablas. Con las tablas limpias y la versión puesta, el Cuadro entero da lo
    /// mismo que sin la fila de versión (el cálculo de siempre). La prueba de que lo primero salió de las tablas: con la versión
    /// puesta y los pares envenenados, el cumplimiento de los Tenants pinta el veneno; con la versión borrada, no se mira.
    /// </summary>
    [Fact]
    public async Task El_Cuadro_lee_el_cumplimiento_de_las_tablas_y_da_lo_mismo_que_el_calculo()
    {
        try
        {
            var leido = await CuadroAsync();
            leido.Tenants.Should().NotBeEmpty("el Administrador del Operador CAE alcanza al menos un Tenant beneficiario");

            await ParaCadaTenantAsync(EnvenenarParesAsync);
            var envenenado = await CuadroAsync();

            await ParaCadaTenantAsync(db => db.Set<VersionEstadoTenant>().ExecuteDeleteAsync());
            var calculado = await CuadroAsync();

            Normalizar(calculado).Should().Be(Normalizar(leido), "sin fila de versión se calcula, y es lo mismo que sale de las tablas");

            var delVeneno = 0;
            foreach (var tenant in leido.Tenants)
            {
                var antes = tenant.Cifras.Cumplimiento.Exigido!;
                var despues = envenenado.Tenants.Single(t => t.TenantId == tenant.TenantId).Cifras.Cumplimiento.Exigido!;
                salida.WriteLine($"{tenant.TenantNombre}: leído {antes.AlDia}/{antes.Requeridos}, envenenado {despues.AlDia}/{despues.Requeridos}");
                despues.Requeridos.Should().Be(antes.Requeridos, "el veneno solo cambia el estado de los pares, no cuántos se exigen");
                if (antes.AlDia > 0)
                {
                    despues.AlDia.Should().Be(0, $"{tenant.TenantNombre}: con la versión puesta y todos los pares en Faltante, la pantalla pinta la tabla");
                    delVeneno++;
                }
            }

            delVeneno.Should().BePositive("algún Tenant tiene pares al día: si no, la coincidencia no prueba que se leyó de las tablas");
        }
        finally
        {
            await RestaurarTodosAsync();
        }
    }

    /// <summary>
    /// Varios Tenants con estados distintos en la misma petición: uno completo y otro sin fila de versión, con las tablas de los
    /// dos envenenadas. El completo pinta su tabla, el que no tiene versión calcula, el total es la suma de las filas, y ningún
    /// Tenant ve los datos del otro.
    /// </summary>
    [Fact]
    public async Task Un_Tenant_completo_y_otro_sin_version_en_el_mismo_Cuadro_se_deciden_por_separado()
    {
        try
        {
            var limpio = await CuadroAsync();
            var conPares = limpio.Tenants.Where(t => t.Cifras.Cumplimiento.Exigido is { AlDia: > 0 }).Take(2).ToList();
            conPares.Should().HaveCount(2, "el piloto tiene al menos dos Tenants beneficiarios con pares al día");
            var (completo, sinVersion) = (conPares[0].TenantId, conPares[1].TenantId);

            await ParaTenantsAsync([completo, sinVersion], EnvenenarParesAsync);
            await ParaTenantsAsync([sinVersion], db => db.Set<VersionEstadoTenant>().ExecuteDeleteAsync());

            var mezclado = await CuadroAsync();
            Exigido(mezclado, completo).AlDia.Should().Be(0, "el Tenant completo pinta su tabla envenenada");
            Exigido(mezclado, completo).Requeridos.Should().Be(Exigido(limpio, completo).Requeridos);
            Exigido(mezclado, sinVersion).Should().Be(Exigido(limpio, sinVersion), "el Tenant sin versión calcula y no mira su tabla envenenada");
            foreach (var otro in limpio.Tenants.Select(t => t.TenantId).Except(new[] { completo, sinVersion }))
                Exigido(mezclado, otro).Should().Be(Exigido(limpio, otro), "un tercer Tenant no se ve afectado por los otros dos");

            mezclado.Total.Cumplimiento.Exigido.Should().Be(
                FraccionCumplimiento.Sumar(mezclado.Tenants.Select(t => t.Cifras.Cumplimiento.Exigido!)),
                "el total del Cuadro es la suma de los Tenants, cada uno con lo suyo");
        }
        finally
        {
            await RestaurarTodosAsync();
        }
    }

    /// <summary>
    /// Cada vuelta al cálculo. Con el Tenant completo y los pares envenenados el Cuadro pinta el veneno (control); con cada causa
    /// puesta por separado vuelve a lo que calcula el cálculo de siempre.
    /// </summary>
    [Fact]
    public async Task Las_causas_de_vuelta_al_calculo_del_Cuadro()
    {
        try
        {
            var limpio = await CuadroAsync();
            var elegido = limpio.Tenants.First(t => t.Cifras.Cumplimiento.Exigido is { AlDia: > 0 });
            var tenant = elegido.TenantId;
            var esperado = Exigido(limpio, tenant);

            var causas = new (string Nombre, Func<CaeManagerDbContext, Task<int>> Pone)[]
            {
                ("una cola de recálculos con una entrada: lo guardado puede estar viejo", db => db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "RecalculosPendientes" ("Id", "Ambito", "EntidadId", "Motivo", "CreadoEnUtc", "IntentoEnUtc", "Intentos", "TenantId")
                    VALUES (gen_random_uuid(), {(short)AmbitoEstadoEntidad.Centro}, NULL, 'prueba del Cuadro', now(), NULL, 0, {tenant})
                    """)),
                ("un par cuya frontera ya llegó: el estado guardado es de un día anterior", db => db.Set<EstadoRequisito>()
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.ProximoCambioEl, (DateOnly?)DiaDeNegocio.Hoy().AddDays(-1)))),
                ("un par de otra versión de la regla", db => db.Set<EstadoRequisito>()
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.VersionCalculo, CalculoEstadoMaterializado.VersionDeLaRegla + 1))),
                ("los Centros sin su fila: un hueco del detector", db => db.Set<EstadoEntidad>()
                    .Where(e => e.Ambito == AmbitoEstadoEntidad.Centro).ExecuteDeleteAsync()),
                ("sin fila de versión: el Tenant no está completo", db => db.Set<VersionEstadoTenant>().ExecuteDeleteAsync()),
            };

            foreach (var (nombre, pone) in causas)
            {
                await RestaurarAsync(tenant);
                await ParaTenantsAsync([tenant], EnvenenarParesAsync);
                Exigido(await CuadroAsync(), tenant).AlDia.Should().Be(0, $"control ({nombre}): sin la causa se lee la tabla envenenada");

                await ParaTenantsAsync([tenant], pone);
                Exigido(await CuadroAsync(), tenant).Should().Be(esperado, $"con {nombre}: se calcula y no se mira la tabla");
            }
        }
        finally
        {
            await RestaurarTodosAsync();
        }
    }

    /// <summary>
    /// Alcance: quien no alcanza el Tenant entero no lee el agregado de las tablas, que es el de todos los Centros. Con la
    /// Gestora CAE en un Tenant que no alcanza entero el lector no decide (devuelve null); en el mismo Tenant, el Administrador
    /// del Operador CAE sí lee.
    /// </summary>
    [Fact]
    public async Task Quien_no_alcanza_el_Tenant_entero_no_lee_el_agregado_de_las_tablas()
    {
        var comprobados = 0;
        foreach (var tenantId in fixture.Tenants)
        {
            var nombre = await fixture.Arnes.ComoBootstrapAsync(b => b.Tenants.Where(t => t.Id == tenantId).Select(t => t.Nombre).SingleAsync());
            var centrosPorEmpresa = await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) => (await (
                from a in db.Asignaciones.Where(a => a.FechaBaja == null)
                join t in db.Trabajadores on a.TrabajadorId equals t.Id
                join e in db.Empresas.Where(e => e.EsPropia) on t.EmpresaId equals (Guid?)e.Id
                select new { EmpresaId = e.Id, a.CentroId }).Distinct().ToListAsync())
                .Select(x => (x.EmpresaId, x.CentroId)).ToHashSet());
            if (centrosPorEmpresa.Count == 0)
                continue;

            var gestora = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre, async sp =>
                (await sp.GetRequiredService<IAlcanceDatosService>().AlcanzaElTenantEnteroAsync(),
                 await sp.GetRequiredService<ILectorCumplimientoDeDireccion>().CumplimientoExigidoAsync(centrosPorEmpresa, CancellationToken.None)));
            if (gestora.Item1)
                continue;

            gestora.Item2.Should().BeNull($"{nombre}: la Gestora CAE no alcanza el Tenant entero y no puede leer su agregado");
            var administrador = await ComoAdministradorEnAsync(nombre, sp =>
                sp.GetRequiredService<ILectorCumplimientoDeDireccion>().CumplimientoExigidoAsync(centrosPorEmpresa, CancellationToken.None));
            // Control: el Administrador del Operador CAE sí lo lee del mismo Tenant (si tiene pares que leer).
            if (administrador is null)
                continue;
            comprobados++;
        }

        comprobados.Should().BePositive("hay Tenants del piloto fuera de la cartera de la Gestora CAE");
    }

    // ------------------------------------------------------------------------------------------ Dashboard ejecutivo

    /// <summary>
    /// El Dashboard ejecutivo lee el cumplimiento de los Centros de las tablas: con la versión puesta da lo mismo que sin ella
    /// (el cálculo de siempre), y con los Centros envenenados pinta el veneno. Lo único que cambia es el cumplimiento; la
    /// facturación estimada que lleva el mismo panel es la misma en los tres estados.
    /// </summary>
    [Fact]
    public async Task El_Dashboard_ejecutivo_lee_el_cumplimiento_de_los_Centros_de_las_tablas()
    {
        try
        {
            var leido = await DashboardAsync();
            leido.PorcentajeCumplimientoDocumental.Should().BeGreaterThan(0, "el piloto tiene Centros con pares al día");

            await ParaCadaTenantAsync(db => db.Set<EstadoEntidad>().Where(e => e.Ambito == AmbitoEstadoEntidad.Centro)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.AlDia, 0).SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));
            var envenenado = await DashboardAsync();

            await ParaCadaTenantAsync(db => db.Set<VersionEstadoTenant>().ExecuteDeleteAsync());
            var calculado = await DashboardAsync();

            Normalizar(calculado).Should().Be(Normalizar(leido), "sin fila de versión se calcula, y es lo mismo que sale de las tablas");
            envenenado.PorcentajeCumplimientoDocumental.Should().Be(0, "con la versión puesta, el porcentaje es el de las tablas envenenadas");
            envenenado.CentrosConMenorCumplimiento.Should().OnlyContain(c => c.Porcentaje == 0);
            envenenado.FacturacionEstimadaMesActual.Should().Be(leido.FacturacionEstimadaMesActual, "la facturación estimada no depende del estado");
        }
        finally
        {
            await RestaurarTodosAsync();
        }
    }

    // ------------------------------------------------------------------------------------------ Facturación del Operador CAE

    /// <summary>
    /// La facturación del Operador CAE se cobra por Tenant beneficiario (Trabajadores extranjeros con Visita, Centros y Obras
    /// nuevas) y no depende del estado documental: con las tablas limpias, con todas envenenadas y con el Tenant sin fila de
    /// versión, la pestaña «Facturación» da exactamente lo mismo, importe a importe.
    /// </summary>
    [Fact]
    public async Task Ningun_importe_de_la_facturacion_cambia_con_el_estado_materializado()
    {
        var (anyo, mes) = (DateTime.UtcNow.Year, DateTime.UtcNow.Month);
        Task<FacturacionOperadorCaeDto> FacturacionAsync() => ComoAdministradorOperadorAsync(sp =>
            sp.GetRequiredService<ISender>().Send(new ObtenerFacturacionOperadorCaeQuery(anyo, mes)));

        try
        {
            var limpia = await FacturacionAsync();
            limpia.Filas.Should().NotBeEmpty("el Operador CAE factura al menos una Empresa gestionada");
            var estimadaLimpia = (await DashboardAsync()).FacturacionEstimadaMesActual;
            salida.WriteLine($"Facturación: {limpia.Filas.Count} filas, total {limpia.Total.Cuota} {limpia.Total.Moneda}, estimada {estimadaLimpia}");

            await ParaCadaTenantAsync(EnvenenarTodoAsync);
            var envenenada = await FacturacionAsync();
            var estimadaEnvenenada = (await DashboardAsync()).FacturacionEstimadaMesActual;

            await ParaCadaTenantAsync(db => db.Set<VersionEstadoTenant>().ExecuteDeleteAsync());
            var sinVersion = await FacturacionAsync();
            var estimadaSinVersion = (await DashboardAsync()).FacturacionEstimadaMesActual;

            Normalizar(envenenada).Should().Be(Normalizar(limpia), "con todas las tablas de estado envenenadas, ningún importe ni unidad cambia");
            Normalizar(sinVersion).Should().Be(Normalizar(limpia), "sin fila de versión, ningún importe ni unidad cambia");
            estimadaEnvenenada.Should().Be(estimadaLimpia);
            estimadaSinVersion.Should().Be(estimadaLimpia);
        }
        finally
        {
            await RestaurarTodosAsync();
        }
    }

    // ------------------------------------------------------------------------------------------ Apoyos

    private static FraccionCumplimiento Exigido(CuadroDireccionDto cuadro, Guid tenantId) =>
        cuadro.Tenants.Single(t => t.TenantId == tenantId).Cifras.Cumplimiento.Exigido!;

    /// <summary>El valor sin los instantes de la medición, que cambian de una petición a otra, como texto comparable.</summary>
    private static string Normalizar(object valor) => Instantes.Replace(JsonSerializer.Serialize(valor), string.Empty);

    private Task<CuadroDireccionDto> CuadroAsync() => ComoAdministradorOperadorAsync(sp =>
        sp.GetRequiredService<ISender>().Send(new ObtenerCuadroDireccionQuery(DateTime.UtcNow.Year, DateTime.UtcNow.Month)));

    private Task<DashboardEjecutivoDto> DashboardAsync() => ComoAdministradorOperadorAsync(sp =>
        sp.GetRequiredService<ISender>().Send(new ObtenerDashboardEjecutivoQuery(PeriodoKpi.MesActual(DateTime.UtcNow))));

    private Task<T> ComoAdministradorOperadorAsync<T>(Func<IServiceProvider, Task<T>> lectura) =>
        fixture.Arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.AdministradorOperador, lectura);

    private async Task<T> ComoAdministradorEnAsync<T>(string nombreTenant, Func<IServiceProvider, Task<T>> lectura)
    {
        var tenantId = await fixture.Arnes.TenantIdAsync(nombreTenant);
        return await ComoAdministradorOperadorAsync(async sp =>
        {
            using (AmbitoTenantExplicito.Establecer(tenantId))
                return await lectura(sp);
        });
    }

    private Task ParaCadaTenantAsync(Func<CaeManagerDbContext, Task<int>> accion) => ParaTenantsAsync(fixture.Tenants, accion);

    private async Task ParaTenantsAsync(IEnumerable<Guid> tenants, Func<CaeManagerDbContext, Task<int>> accion)
    {
        foreach (var tenant in tenants)
            await fixture.Arnes.EnTenantAsync(tenant, (db, _) => accion(db));
    }

    /// <summary>Todos los pares de Trabajador en Faltante y sin frontera: el cumplimiento en contexto Empresa pasa a cero.</summary>
    private static Task<int> EnvenenarParesAsync(CaeManagerDbContext db) =>
        db.Set<EstadoRequisito>().Where(p => p.AmbitoTitular == AmbitoAplicacion.Trabajador)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.EstadoTalveg, EstadoTalvegRequisito.Faltante)
                .SetProperty(p => p.ProximoCambioEl, (DateOnly?)null));

    /// <summary>Pares, Centros, Empresas y Trabajadores con un estado que ningún documento explica.</summary>
    private static async Task<int> EnvenenarTodoAsync(CaeManagerDbContext db) =>
        await EnvenenarParesAsync(db)
        + await db.Set<EstadoEntidad>().ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Estado, (short)EstadoDocumento.Vencido)
            .SetProperty(e => e.Vencidos, 7).SetProperty(e => e.Faltan, 7)
            .SetProperty(e => e.AlDia, 0)
            .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null));

    /// <summary>El relleno deja las filas como el cálculo y crea la fila de versión si falta: los Tenants quedan completos y sin cola.</summary>
    private async Task RestaurarTodosAsync()
    {
        foreach (var tenant in fixture.Tenants)
            await RestaurarAsync(tenant);
    }

    private async Task RestaurarAsync(Guid tenant)
    {
        await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
        await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
            sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));
    }
}
