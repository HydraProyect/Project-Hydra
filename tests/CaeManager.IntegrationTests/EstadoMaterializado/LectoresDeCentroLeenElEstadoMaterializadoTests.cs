using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// PR 6 del estado materializado: el listado de Centros, sus recuentos por estado, el resumen por grupo y la exportación leen
/// el estado y el cumplimiento de <c>EstadosEntidad</c>, y solo en un Tenant completo (con su fila en
/// <c>VersionesEstadoTenant</c> y sin recálculos pendientes). Todo lo demás se calcula como antes.
///
/// <para>
/// Cada prueba usa el piloto Outbound sembrado y relleno (el mismo que comprueba la coincidencia de las tablas con el
/// recálculo completo) y compara dos manos del mismo handler: la real, que lee las tablas, y una con un lector que declara
/// el Tenant sin completar, que es el cálculo en cada lectura de siempre. Las pruebas que alteran las tablas con SQL lo
/// restauran al final con el relleno, que es idempotente.
/// </para>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class LectoresDeCentroLeenElEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private const short EstadoDeCentro = (short)AmbitoEstadoEntidad.Centro;

    [Fact]
    public async Task El_listado_leido_de_las_tablas_da_lo_mismo_que_el_calculo_de_cada_lectura()
    {
        var diferencias = new List<string>();
        var estadosVistos = new HashSet<EstadoCentro>();
        var centrosComparados = 0;

        foreach (var tenant in fixture.Tenants)
        {
            diferencias.AddRange(await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
            {
                var lector = sp.GetRequiredService<ILectorEstadoCentros>();
                (await lector.TenantCompletoAsync(CancellationToken.None)).Should().BeTrue($"el relleno completó el Tenant {tenant}");
                var ids = await db.Centros.Select(c => c.Id).ToListAsync();
                // Sin esto la comparación sería trivial: si el lector no devolviera filas, las dos manos calcularían.
                (await lector.LeerAsync(ids, CancellationToken.None)).Should().HaveCount(ids.Count,
                    "cada Centro del Tenant relleno tiene una fila fiable en EstadosEntidad");
                centrosComparados += ids.Count;

                var encontradas = new List<string>();
                var completo = new ObtenerCentrosQuery(null, null, TamanoPagina: 1000, ConRecuentosPorEstado: true, ConResumenPorGrupo: true);
                var listado = await Handler(db, sp, desdeTablas: true).Handle(completo, CancellationToken.None);
                foreach (var centro in listado.Elementos)
                    estadosVistos.Add(centro.Estado);

                var consultas = new List<(string Nombre, ObtenerCentrosQuery Consulta, bool ConRecuentos)>
                {
                    ("listado con franja y grupos", completo, true),
                    ("camino normal, página 2", new ObtenerCentrosQuery(null, null, Pagina: 2, TamanoPagina: 7), true),
                    ("orden por estado descendente", new ObtenerCentrosQuery(null, null, OrdenarPor: nameof(CentroListaDto.Estado), Descendente: true, TamanoPagina: 1000), true),
                    ("orden por cumplimiento", new ObtenerCentrosQuery(null, null, OrdenarPor: nameof(CentroListaDto.CumplimientoPorcentaje), TamanoPagina: 1000), true),
                    ("exportación (sin incidencias)", new ObtenerCentrosQuery(null, null, TamanoPagina: 1000, ConIncidencias: false), false),
                };
                consultas.AddRange(listado.Elementos.Select(c => c.Estado).Distinct().Select(estado =>
                    ($"filtrado por {estado}", new ObtenerCentrosQuery(null, null, Estados: [estado], TamanoPagina: 1000, ConRecuentosPorEstado: true, ConResumenPorGrupo: true), true)));

                foreach (var (nombre, consulta, conRecuentos) in consultas)
                {
                    var deTablas = Firma(await Handler(db, sp, desdeTablas: true).Handle(consulta, CancellationToken.None), conRecuentos);
                    var deCalculo = Firma(await Handler(db, sp, desdeTablas: false).Handle(consulta, CancellationToken.None), conRecuentos);
                    if (deTablas != deCalculo)
                        encontradas.Add($"Tenant {tenant}, {nombre}:\n  tablas:  {deTablas.Replace("\n", " ; ")}\n  cálculo: {deCalculo.Replace("\n", " ; ")}");
                }

                return encontradas;
            }));
        }

        centrosComparados.Should().BePositive("sin Centros la comparación no mediría nada");
        estadosVistos.Should().HaveCountGreaterThan(1, "con un solo estado la comparación no distingue casi nada");
        diferencias.Should().BeEmpty();
    }

    /// <summary>
    /// Condición crítica del PR: en producción aún no hay filas de versión, y un Tenant sin ella no está completo. Para
    /// distinguir el camino que se toma de un resultado que coincide por casualidad, se altera el estado guardado: con la fila
    /// de versión el listado devuelve lo guardado; sin ella, lo calculado, aunque las filas de <c>EstadosEntidad</c> sigan ahí.
    /// </summary>
    [Fact]
    public async Task Un_Tenant_sin_fila_de_version_se_calcula_como_siempre_aunque_sus_tablas_tengan_filas()
    {
        var tenant = await TenantConMasCentrosAsync();
        await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            var ids = await db.Centros.Select(c => c.Id).ToListAsync();
            var consulta = new ObtenerCentrosQuery(null, null, TamanoPagina: 1000, ConRecuentosPorEstado: true, ConResumenPorGrupo: true);
            var calculado = await EstadosAsync(db, sp, desdeTablas: false, consulta);
            var guardado = await FilasDeCentroAsync(db);
            var envenenado = guardado.ToDictionary(f => f.Key, f => Envenenar(f.Value));
            try
            {
                await EnvenenarTodasAsync(db);

                var conVersion = await EstadosAsync(db, sp, desdeTablas: true, consulta);
                conVersion.Should().BeEquivalentTo(
                    ids.ToDictionary(id => id, id => guardado[id] == (short)EstadoCentro.SinGestionCae ? EstadoCentro.SinGestionCae : envenenado[id]),
                    "con la fila de versión, el listado devuelve lo que dicen las tablas (aquí, un estado inventado)");
                conVersion.Should().NotBeEquivalentTo(calculado, "el estado inventado tiene que distinguirse del calculado");

                await db.Database.ExecuteSqlAsync($"""DELETE FROM "VersionesEstadoTenant" """);
                (await sp.GetRequiredService<ILectorEstadoCentros>().TenantCompletoAsync(CancellationToken.None)).Should().BeFalse();

                (await EstadosAsync(db, sp, desdeTablas: true, consulta)).Should().BeEquivalentTo(calculado,
                    "sin su fila de versión el Tenant no está completo: el lector no se usa y se calcula, tenga o no filas guardadas");
                Firma(await Handler(db, sp, desdeTablas: true).Handle(consulta, CancellationToken.None), true)
                    .Should().Be(Firma(await Handler(db, sp, desdeTablas: false).Handle(consulta, CancellationToken.None), true),
                        "ni el recuento por estado, ni el resumen por grupo, ni los recuentos leen las tablas");
            }
            finally
            {
                await sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None);
            }

            (await sp.GetRequiredService<ILectorEstadoCentros>().TenantCompletoAsync(CancellationToken.None)).Should().BeTrue("el relleno restaura la fila de versión");
            (await EstadosAsync(db, sp, desdeTablas: true, consulta)).Should().BeEquivalentTo(calculado, "restaurado, las tablas vuelven a coincidir con el cálculo");
            return 0;
        });
    }

    /// <summary>
    /// Un Tenant completo no es un Tenant sin huecos: una fila ausente, de una regla anterior o con su día de cambio ya pasado
    /// se calcula en línea, y solo ella; y mientras haya un recálculo diferido sin consumir, todo el Tenant.
    /// </summary>
    [Fact]
    public async Task Una_fila_no_fiable_se_calcula_en_linea_y_solo_ella_y_una_cola_pendiente_calcula_todo()
    {
        var tenant = await TenantConMasCentrosAsync();
        await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            var consulta = new ObtenerCentrosQuery(null, null, TamanoPagina: 1000);
            var calculado = await EstadosAsync(db, sp, desdeTablas: false, consulta);
            var guardado = await FilasDeCentroAsync(db);
            var elegibles = guardado.Where(f => f.Value != (short)EstadoCentro.SinGestionCae).Select(f => f.Key).OrderBy(id => id).ToList();
            elegibles.Should().HaveCountGreaterThanOrEqualTo(4, "tres filas no fiables y al menos una fiable de control");
            var (sinFila, diaPasado, reglaAnterior) = (elegibles[0], elegibles[1], elegibles[2]);
            var hoy = DiaDeNegocio.Hoy();
            var lector = sp.GetRequiredService<ILectorEstadoCentros>();
            try
            {
                await EnvenenarTodasAsync(db);
                await db.Database.ExecuteSqlAsync($"""DELETE FROM "EstadosEntidad" WHERE "Ambito" = {EstadoDeCentro} AND "EntidadId" = {sinFila}""");
                await db.Database.ExecuteSqlAsync($"""UPDATE "EstadosEntidad" SET "ProximoCambioEl" = {hoy.AddDays(-1)} WHERE "Ambito" = {EstadoDeCentro} AND "EntidadId" = {diaPasado}""");
                await db.Database.ExecuteSqlAsync($"""UPDATE "EstadosEntidad" SET "VersionCalculo" = 0 WHERE "Ambito" = {EstadoDeCentro} AND "EntidadId" = {reglaAnterior}""");

                var leidos = await lector.LeerAsync(guardado.Keys.ToList(), CancellationToken.None);
                leidos.Keys.Should().NotContain(new[] { sinFila, diaPasado, reglaAnterior },"ninguna de las tres es fiable");
                leidos.Should().HaveCount(guardado.Count - 3);

                var esperado = guardado.ToDictionary(
                    f => f.Key,
                    f => f.Key == sinFila || f.Key == diaPasado || f.Key == reglaAnterior || f.Value == (short)EstadoCentro.SinGestionCae
                        ? calculado[f.Key]
                        : Envenenar(f.Value));
                (await EstadosAsync(db, sp, desdeTablas: true, consulta)).Should().BeEquivalentTo(esperado,
                    "las tres filas no fiables se calculan; las demás, con la fila fiable, salen de las tablas");

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "RecalculosPendientes" ("Id", "Ambito", "EntidadId", "Motivo", "CreadoEnUtc", "IntentoEnUtc", "Intentos", "TenantId")
                    SELECT gen_random_uuid(), {EstadoDeCentro}, NULL, {"Prueba del lector"}, now(), NULL, 0, {tenant}
                    """);
                (await lector.TenantCompletoAsync(CancellationToken.None)).Should().BeFalse("hay un recálculo diferido sin consumir");
                (await EstadosAsync(db, sp, desdeTablas: true, consulta)).Should().BeEquivalentTo(calculado,
                    "con la cola sin vaciar, lo guardado puede estar viejo: se calcula todo");
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"""DELETE FROM "RecalculosPendientes" """);
                await sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None);
            }

            (await EstadosAsync(db, sp, desdeTablas: true, consulta)).Should().BeEquivalentTo(calculado, "restaurado, las tablas vuelven a coincidir con el cálculo");
            return 0;
        });
    }

    /// <summary>
    /// La exportación no necesita el detalle de las causas, que no se guarda: en un Tenant completo lee estado y cumplimiento
    /// de las tablas sin calcular nada, mientras que el cálculo de siempre emite muchas más sentencias.
    /// </summary>
    [Fact]
    public async Task La_exportacion_de_un_Tenant_completo_no_calcula_el_estado_de_los_Centros()
    {
        var tenant = await TenantConMasCentrosAsync();
        await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            var exportacion = new ObtenerCentrosQuery(null, null, TamanoPagina: 1000, ConIncidencias: false);
            var (deTablas, sentenciasDeTablas) = await ArnesPilotoOutbound.Contador.ContarAsync(
                () => Handler(db, sp, desdeTablas: true).Handle(exportacion, CancellationToken.None));
            var (deCalculo, sentenciasDeCalculo) = await ArnesPilotoOutbound.Contador.ContarAsync(
                () => Handler(db, sp, desdeTablas: false).Handle(exportacion, CancellationToken.None));

            Firma(deTablas, conRecuentos: false).Should().Be(Firma(deCalculo, conRecuentos: false));
            deTablas.Elementos.Should().NotBeEmpty();
            deTablas.Elementos.Should().OnlyContain(c => c.Recuentos == RecuentosCentroDto.Vacio,
                "sin incidencias pedidas, el detalle de las causas no se calcula");
            sentenciasDeTablas.Should().BeLessThanOrEqualTo(10, "tenant completo, los Centros y las filas de EstadosEntidad");
            sentenciasDeTablas.Should().BeLessThan(sentenciasDeCalculo, "el cálculo de cada Centro emite muchas más sentencias");
            return 0;
        });
    }

    private static ObtenerCentrosQueryHandler Handler(CaeManagerDbContext db, IServiceProvider sp, bool desdeTablas) =>
        new(db, db, new AlcanceDatosServiceFalso(), sp.GetRequiredService<ICalculoEstadoCentroService>(),
            desdeTablas ? sp.GetRequiredService<ILectorEstadoCentros>() : new LectorDeTenantSinCompletar());

    private static async Task<Dictionary<Guid, EstadoCentro>> EstadosAsync(
        CaeManagerDbContext db, IServiceProvider sp, bool desdeTablas, ObtenerCentrosQuery consulta) =>
        (await Handler(db, sp, desdeTablas).Handle(consulta, CancellationToken.None)).Elementos.ToDictionary(c => c.Id, c => c.Estado);

    private static async Task<Dictionary<Guid, short>> FilasDeCentroAsync(CaeManagerDbContext db) =>
        await db.EstadosEntidad.AsNoTracking()
            .Where(e => e.Ambito == AmbitoEstadoEntidad.Centro)
            .ToDictionaryAsync(e => e.EntidadId, e => e.Estado);

    /// <summary>Un estado de TALVEG que nunca coincide con el guardado ni con el rótulo combinado que saldría de él.</summary>
    private static EstadoCentro Envenenar(short estadoGuardado) =>
        estadoGuardado == (short)EstadoCentro.Vencido ? EstadoCentro.Urgente : EstadoCentro.Vencido;

    private static Task<int> EnvenenarTodasAsync(CaeManagerDbContext db) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE "EstadosEntidad"
            SET "Estado" = CASE WHEN "Estado" = {(short)EstadoCentro.Vencido} THEN {(short)EstadoCentro.Urgente} ELSE {(short)EstadoCentro.Vencido} END,
                "EstadoPlataforma" = NULL
            WHERE "Ambito" = {EstadoDeCentro} AND "Estado" <> {(short)EstadoCentro.SinGestionCae}
            """);

    private async Task<Guid> TenantConMasCentrosAsync()
    {
        var cuentas = new List<(Guid Tenant, int Centros)>();
        foreach (var tenant in fixture.Tenants)
            cuentas.Add((tenant, await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Centros.CountAsync())));
        return cuentas.OrderByDescending(c => c.Centros).First().Tenant;
    }

    /// <summary>Todo lo que el listado devuelve de una consulta, en texto, para compararlo entre las dos manos.</summary>
    private static string Firma(ResultadoPaginado<CentroListaDto> resultado, bool conRecuentos)
    {
        static string Diccionario(IReadOnlyDictionary<string, int>? d) =>
            d is null ? "-" : string.Join(",", d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

        var filas = resultado.Elementos.Select(c =>
        {
            var incidencias = conRecuentos
                ? $"{c.Recuentos.TotalVencidas}/{c.Recuentos.TotalProximas}/{c.Recuentos.TotalPendientesEnPlataforma}:" + string.Join(
                    ",", c.Recuentos.Vencidas.Concat(c.Recuentos.Proximas).Concat(c.Recuentos.PendientesEnPlataforma)
                        .Select(i => i.Descripcion).OrderBy(d => d, StringComparer.Ordinal))
                : "";
            return $"{c.Id}|{c.Estado}|{c.CumplimientoPorcentaje?.ToString() ?? "-"}|{incidencias}";
        });
        var resumen = resultado.ResumenPorGrupo is null
            ? "-"
            : string.Join(";", resultado.ResumenPorGrupo.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}:{p.Value.Total}:{Diccionario(p.Value.PorEstado)}"));
        return $"total={resultado.TotalElementos}\nfranja={Diccionario(resultado.RecuentosPorEstado)}\ngrupos={resumen}\n"
            + string.Join("\n", filas);
    }

    /// <summary>El lector de un Tenant al que le falta su fila de versión: nada se lee de las tablas, todo se calcula.</summary>
    private sealed class LectorDeTenantSinCompletar : ILectorEstadoCentros
    {
        public Task<bool> TenantCompletoAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyDictionary<Guid, EstadoCentroLeido>> LeerAsync(
            IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, EstadoCentroLeido>>(new Dictionary<Guid, EstadoCentroLeido>());
    }
}
