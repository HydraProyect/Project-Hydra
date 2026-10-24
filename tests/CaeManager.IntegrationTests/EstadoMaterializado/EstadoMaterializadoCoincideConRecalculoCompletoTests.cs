using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>El piloto Outbound sembrado una vez, con el relleno inicial del estado materializado en cada Tenant.</summary>
public sealed class EstadoMaterializadoPilotoFixture : IAsyncLifetime
{
    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal IReadOnlyList<Guid> Tenants { get; private set; } = [];
    internal Dictionary<Guid, ResultadoRecalculoEstado> Relleno { get; } = [];

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        await Arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()));
        await Arnes.BackfillAsync();
        Tenants = await Arnes.ComoBootstrapAsync(b => b.Tenants.OrderBy(t => t.Nombre).Select(t => t.Id).ToListAsync());
        foreach (var tenant in Tenants)
            Relleno[tenant] = await Arnes.EnTenantAsync(tenant, (_, sp) =>
                sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));
    }

    public async Task DisposeAsync() => await Arnes.DisposeAsync();
}

/// <summary>
/// Diseño del estado materializado, § 7.1 (primera parte): lo materializado coincide con el recálculo completo de hoy. El
/// estado de cada Centro, derivado de sus dos columnas, es el de <c>ICalculoEstadoCentroService</c>; el de cada Trabajador
/// y Empresa, el de su contexto (regla del 2026-10-23); y los pares, los pares exigidos de cada Centro. Un recálculo acotado
/// a unas raíces deja las tablas igual que el relleno completo, y <c>ProximoCambioEl</c> nunca llega tarde.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class EstadoMaterializadoCoincideConRecalculoCompletoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    [Fact]
    public async Task El_relleno_rellena_cada_Tenant_con_filas_y_su_version()
    {
        fixture.Relleno.Values.Sum(r => r.Centros).Should().BePositive("sin Centros la comparación no probaría nada");
        fixture.Relleno.Values.Sum(r => r.Pares).Should().BePositive();
        fixture.Relleno.Values.Sum(r => r.Trabajadores).Should().BePositive();
        fixture.Relleno.Values.Sum(r => r.Empresas).Should().BePositive();
        foreach (var (tenant, relleno) in fixture.Relleno)
            salida.WriteLine($"{tenant}: {relleno}");

        foreach (var tenant in fixture.Tenants)
        {
            var versiones = await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<VersionEstadoTenant>().CountAsync());
            versiones.Should().Be(1, $"el relleno crea la fila de versión del Tenant {tenant}");
        }
    }

    [Fact]
    public async Task El_estado_materializado_coincide_con_el_recalculo_completo()
    {
        var diferencias = new List<string>();
        foreach (var tenant in fixture.Tenants)
            diferencias.AddRange(await fixture.Arnes.EnTenantAsync(tenant, (db, sp) => CompararAsync(db, sp)));

        diferencias.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_recalculo_acotado_y_un_relleno_repetido_dejan_las_tablas_igual()
    {
        foreach (var tenant in fixture.Tenants)
        {
            await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
            {
                var recalculo = sp.GetRequiredService<IRecalculoEstadoMaterializado>();
                var antes = await FotoAsync(db);
                var version = await VersionAsync(db);

                var centro = await db.Centros.OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync();
                var trabajadores = await db.Trabajadores.OrderBy(t => t.Id).Select(t => t.Id).Take(3).ToArrayAsync();
                var empresas = await db.Empresas.OrderBy(e => e.Id).Select(e => e.Id).Take(3).ToArrayAsync();
                await recalculo.RecalcularAsync(RaicesDeRecalculo.DeCentros(centro), CancellationToken.None);
                await recalculo.RecalcularAsync(RaicesDeRecalculo.DeTrabajadores(trabajadores), CancellationToken.None);
                await recalculo.RecalcularAsync(RaicesDeRecalculo.DeEmpresas(empresas), CancellationToken.None);
                db.ChangeTracker.Clear();
                (await FotoAsync(db)).Should().Equal(antes, $"un recálculo acotado sin cambios no cambia nada en el Tenant {tenant}");
                (await VersionAsync(db)).Should().Be(version + 3, "cada recálculo sube la versión del Tenant");

                await recalculo.RellenarTenantAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                (await FotoAsync(db)).Should().Equal(antes, "el relleno es idempotente");
                return 0;
            });
        }
    }

    /// <summary>
    /// Día a día, durante seis semanas: mientras no llega su <c>ProximoCambioEl</c>, el estado calculado de cada entidad y de
    /// cada par es el mismo que hoy. Una frontera tardía dejaría un estado viejo, que es lo que la decisión del 2026-10-24
    /// prohíbe.
    /// </summary>
    [Fact]
    public async Task ProximoCambioEl_nunca_llega_tarde()
    {
        var hoy = DiaDeNegocio.Hoy();
        var tardias = new List<string>();
        foreach (var tenant in fixture.Tenants)
        {
            tardias.AddRange(await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
            {
                var recalculo = sp.GetRequiredService<IRecalculoEstadoMaterializado>();
                var raices = new RaicesDeRecalculo(
                    await db.Centros.Select(c => c.Id).ToListAsync(),
                    await db.Trabajadores.Select(t => t.Id).ToListAsync(),
                    await db.Empresas.Select(e => e.Id).ToListAsync());
                var deHoy = await recalculo.CalcularAsync(raices, CancellationToken.None);
                var encontradas = new List<string>();

                for (var dias = 1; dias <= 42; dias++)
                {
                    var dia = hoy.AddDays(dias);
                    CalculoEstadoMaterializado deEseDia;
                    using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(dia.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc))))
                        deEseDia = await recalculo.CalcularAsync(raices, CancellationToken.None);

                    var entidades = deEseDia.Entidades.ToDictionary(e => (e.Ambito, e.EntidadId));
                    foreach (var fila in deHoy.Entidades.Where(e => e.ProximoCambioEl is null || e.ProximoCambioEl > dia))
                    {
                        if (!entidades.TryGetValue((fila.Ambito, fila.EntidadId), out var otra)
                            || otra with { ProximoCambioEl = fila.ProximoCambioEl } != fila)
                            encontradas.Add($"{fila.Ambito} {fila.EntidadId}: frontera {fila.ProximoCambioEl}, cambia ya el {dia}");
                    }

                    var pares = deEseDia.Pares.ToDictionary(p => (p.CentroId, p.AmbitoTitular, p.TitularId, p.TipoDocumentoId));
                    foreach (var par in deHoy.Pares.Where(p => p.ProximoCambioEl is null || p.ProximoCambioEl > dia))
                    {
                        if (!pares.TryGetValue((par.CentroId, par.AmbitoTitular, par.TitularId, par.TipoDocumentoId), out var otro)
                            || otro.EstadoTalveg != par.EstadoTalveg)
                            encontradas.Add($"Par {par.CentroId}/{par.TitularId}/{par.TipoDocumentoId}: frontera {par.ProximoCambioEl}, cambia ya el {dia}");
                    }
                }

                return encontradas;
            }));
        }

        tardias.Should().BeEmpty();
    }

    private static async Task<List<string>> CompararAsync(CaeManagerDbContext db, IServiceProvider sp)
    {
        var ct = CancellationToken.None;
        var calculoCentro = sp.GetRequiredService<ICalculoEstadoCentroService>();
        var diferencias = new List<string>();
        var filas = await db.Set<EstadoEntidad>().AsNoTracking().ToListAsync(ct);
        var porEntidad = filas.ToDictionary(f => (f.Ambito, f.EntidadId));

        // ---- Centros ----
        var centroIds = await db.Centros.Select(c => c.Id).ToListAsync(ct);
        var estados = await calculoCentro.CalcularAsync(centroIds, ct);
        var cumplimiento = await calculoCentro.CalcularCumplimientoAsync(centroIds, ct);
        foreach (var centroId in centroIds)
        {
            if (!porEntidad.TryGetValue((AmbitoEstadoEntidad.Centro, centroId), out var fila))
            {
                diferencias.Add($"Centro {centroId}: sin fila");
                continue;
            }

            var derivado = EstadoCentroCombinado.Derivar((EstadoCentro)fila.Estado, fila.EstadoPlataforma);
            if (derivado != estados[centroId].Estado)
                diferencias.Add($"Centro {centroId}: materializado {derivado} ({(EstadoCentro)fila.Estado}, {fila.EstadoPlataforma}), hoy {estados[centroId].Estado}");
            var fraccion = cumplimiento.GetValueOrDefault(centroId, FraccionCumplimiento.SinRequisitos);
            if (fila.Requeridos != fraccion.Requeridos || fila.AlDia != fraccion.AlDia)
                diferencias.Add($"Centro {centroId}: cumplimiento {fila.AlDia}/{fila.Requeridos}, hoy {fraccion.AlDia}/{fraccion.Requeridos}");
        }

        // ---- Trabajadores y Empresas: su contexto, evaluado en todos los Centros del Tenant (donde no operan no les exige) ----
        var evaluacion = await sp.GetRequiredService<IEvaluacionDeAccesoDelTenant>().EvaluarCentrosAsync(centroIds, ct);
        var incumplidos = RequisitosObligatoriosIncumplidos.Desde(evaluacion.Requisitos, new Dictionary<Guid, string>());
        var documental = sp.GetRequiredService<ICalculoEstadoDocumentalService>();

        async Task CompararTitularesAsync(
            AmbitoEstadoEntidad ambito, AmbitoAplicacion aplicacion, List<Guid> ids,
            IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> porTitular)
        {
            var peores = await documental.CalcularPeorEstadoAsync(aplicacion, ids, ct);
            foreach (var id in ids)
            {
                var deEste = porTitular.GetValueOrDefault(id) ?? [];
                var esperado = SeveridadEstadoDocumento.ConRequisitosObligatorios(
                    peores.TryGetValue(id, out var peor) ? peor : null,
                    RequisitosObligatoriosIncumplidos.HayVencido(deEste),
                    RequisitosObligatoriosIncumplidos.HayAusente(deEste));
                var esperadoCorto = esperado is { } e ? (short)e : EstadoEntidad.SinEstado;
                if (!porEntidad.TryGetValue((ambito, id), out var fila))
                    diferencias.Add($"{ambito} {id}: sin fila");
                else if (fila.Estado != esperadoCorto)
                    diferencias.Add($"{ambito} {id}: materializado {fila.Estado}, hoy {esperadoCorto}");
            }
        }

        await CompararTitularesAsync(
            AmbitoEstadoEntidad.Trabajador, AmbitoAplicacion.Trabajador, await db.Trabajadores.Select(t => t.Id).ToListAsync(ct), incumplidos.PorTrabajador);
        await CompararTitularesAsync(
            AmbitoEstadoEntidad.Empresa, AmbitoAplicacion.Empresa, await db.Empresas.Select(e => e.Id).ToListAsync(ct), incumplidos.PorEmpresa);

        // ---- Pares exigidos ----
        var pares = await db.Set<EstadoRequisito>().AsNoTracking().ToListAsync(ct);
        var materializados = pares.ToDictionary(p => (p.CentroId, p.AmbitoTitular, p.TitularId, p.TipoDocumentoId), p => p.EstadoTalveg);
        var exigidos = (await calculoCentro.ObtenerParesExigidosAsync(centroIds, ct))
            .Select(p => ((p.CentroId, AmbitoAplicacion.Trabajador, p.TrabajadorId, p.TipoDocumentoId), p.Estado))
            .Concat((await calculoCentro.ObtenerParesDeEmpresaExigidosAsync(centroIds, ct))
                .Select(p => ((p.CentroId, AmbitoAplicacion.Empresa, p.EmpresaId, p.TipoDocumentoId), p.Estado)))
            .DistinctBy(p => p.Item1)
            .ToDictionary(p => p.Item1, p => p.Estado);
        foreach (var (clave, estado) in exigidos)
        {
            if (!materializados.TryGetValue(clave, out var materializado))
                diferencias.Add($"Par {clave}: sin fila");
            else if (materializado.ComoEstadoDocumento() != estado)
                diferencias.Add($"Par {clave}: materializado {materializado}, hoy {estado}");
        }

        foreach (var clave in materializados.Keys.Where(k => !exigidos.ContainsKey(k)))
            diferencias.Add($"Par {clave}: materializado y no exigido");

        return diferencias;
    }

    private static async Task<List<string>> FotoAsync(CaeManagerDbContext db)
    {
        var foto = new List<string>();
        foto.AddRange((await db.Set<EstadoEntidad>().AsNoTracking().ToListAsync())
            .Select(e => $"E {e.Ambito} {e.EntidadId} {e.Estado} {e.EstadoPlataforma} {e.Vencidos} {e.VencidosSinBloqueo} {e.Urgentes} " +
                         $"{e.Proximos} {e.Faltan} {e.SinConfirmar} {e.PendientesEnPlataforma} {e.Requeridos} {e.AlDia} {e.ProximoCambioEl} {e.DiaCalculo}"));
        foto.AddRange((await db.Set<EstadoRequisito>().AsNoTracking().ToListAsync())
            .Select(p => $"R {p.CentroId} {p.AmbitoTitular} {p.TitularId} {p.TipoDocumentoId} {p.DocumentoEfectivoId} {p.EstadoTalveg} " +
                         $"{p.BloqueaAcceso} {p.CuentaParaCumplimiento} {p.EstadoPlataforma} {p.VencimientoEfectivo} {p.ProximoCambioEl} {p.DiaCalculo}"));
        foto.AddRange((await db.Set<EstadoEnPlataforma>().AsNoTracking().ToListAsync())
            .Select(p => $"P {p.CentroId} {p.CanalGestionDocumentalId} {p.AmbitoTitular} {p.TitularId} {p.TipoDocumentoId} {p.Estado} " +
                         $"{p.EstadoVigencia} {p.FechaVencimientoEnPlataforma} {p.ProximoCambioEl} {p.DiaCalculo}"));
        foto.Sort(StringComparer.Ordinal);
        return foto;
    }

    private static Task<long> VersionAsync(CaeManagerDbContext db) =>
        db.Set<VersionEstadoTenant>().AsNoTracking().Select(v => v.Version).SingleAsync();

    private sealed class RelojEn(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
