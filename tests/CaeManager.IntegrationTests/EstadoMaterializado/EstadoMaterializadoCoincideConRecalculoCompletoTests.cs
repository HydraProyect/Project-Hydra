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
                var antes = await FotoAsync(db, conIdentidad: true);
                var version = await VersionAsync(db);

                var centro = await db.Centros.OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync();
                var trabajadores = await db.Trabajadores.OrderBy(t => t.Id).Select(t => t.Id).Take(3).ToArrayAsync();
                var empresas = await db.Empresas.OrderBy(e => e.Id).Select(e => e.Id).Take(3).ToArrayAsync();
                var asignacion = await db.Asignaciones.Where(a => a.FechaBaja == null).OrderBy(a => a.Id)
                    .Select(a => new { a.CentroId, a.TrabajadorId }).FirstOrDefaultAsync();
                await recalculo.RecalcularAsync(RaicesDeRecalculo.DeCentros(centro), CancellationToken.None);
                await recalculo.RecalcularAsync(RaicesDeRecalculo.DeTrabajadores(trabajadores), CancellationToken.None);
                await recalculo.RecalcularAsync(RaicesDeRecalculo.DeEmpresas(empresas), CancellationToken.None);
                if (asignacion is not null)
                    await recalculo.RecalcularAsync(RaicesDeRecalculo.DeAsignacion(asignacion.CentroId, asignacion.TrabajadorId), CancellationToken.None);
                db.ChangeTracker.Clear();
                // Con el Id y la hora de cálculo: una fila sin cambios no se borra y se inserta ni se reescribe (diseño § 13.5).
                (await FotoAsync(db, conIdentidad: true)).Should().Equal(antes, $"un recálculo acotado sin cambios no reescribe ninguna fila en el Tenant {tenant}");
                (await VersionAsync(db)).Should().Be(version, "un recálculo que no cambia ninguna fila no sube la versión del Tenant");

                var repetido = await recalculo.RellenarTenantAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                (await FotoAsync(db, conIdentidad: true)).Should().Equal(antes, "el relleno es idempotente y tampoco reescribe filas iguales");
                repetido.FilasCambiadas.Should().Be(0);
                (await VersionAsync(db)).Should().Be(version, "un relleno sin cambios tampoco sube la versión");
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

    /// <summary>
    /// Sensible a la clausura: tras cada cambio de datos, recalcular solo las raíces que da la acción deja las tablas como el
    /// relleno completo. Una clausura que se quedara en las raíces dejaría sin rehacer los Centros de la Empresa cuyo
    /// documento vence, la Empresa del Trabajador dado de baja o los Trabajadores del Centro cuyo requisito cambia.
    /// </summary>
    [Fact]
    public async Task Tras_cada_cambio_el_recalculo_de_sus_raices_deja_lo_mismo_que_el_relleno()
    {
        var hoy = DiaDeNegocio.Hoy();
        var activasPorTenant = new Dictionary<Guid, int>();
        foreach (var t in fixture.Tenants)
            activasPorTenant[t] = await fixture.Arnes.EnTenantAsync(t, (db, _) => db.Asignaciones.CountAsync(a => a.FechaBaja == null));
        var tenant = activasPorTenant.MaxBy(x => x.Value).Key;

        var (cambios, fallos) = await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            var recalculo = sp.GetRequiredService<IRecalculoEstadoMaterializado>();
            var hechos = new List<string>();
            var distintos = new List<string>();
            db.ChangeTracker.Clear();
            var previa = await FotoAsync(db);

            async Task Comprobar(string cambio, RaicesDeRecalculo raices)
            {
                await recalculo.RecalcularAsync(raices, CancellationToken.None);
                db.ChangeTracker.Clear();
                var acotado = await FotoAsync(db);
                await recalculo.RellenarTenantAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                var completo = await FotoAsync(db);
                // Solo cuenta el cambio que movió el estado: uno que no cambia nada no prueba la clausura.
                if (!completo.SequenceEqual(previa))
                    hechos.Add(cambio);
                previa = completo;
                var faltan = completo.Except(acotado).ToList();
                var sobran = acotado.Except(completo).ToList();
                if (faltan.Count + sobran.Count > 0)
                    distintos.Add($"{cambio}: {faltan.Count} filas del relleno que el recálculo acotado no dejó, p. ej. «{faltan.FirstOrDefault()}»; " +
                                  $"{sobran.Count} que dejó de más, p. ej. «{sobran.FirstOrDefault()}»");
            }

            var centro = await db.Asignaciones.Where(a => a.FechaBaja == null)
                .GroupBy(a => a.CentroId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();

            // 1. Requisito del Centro: cambia «bloquea el acceso», que decide el rojo de sus Trabajadores y Empresas.
            var tipoDelCentro = await db.TiposDocumentoCentros.Where(r => r.CentroId == centro && r.Incluido)
                .OrderBy(r => r.TipoDocumentoId).Select(r => (Guid?)r.TipoDocumentoId).FirstOrDefaultAsync();
            if (tipoDelCentro is { } tipo)
            {
                await db.TiposDocumentoCentros.Where(r => r.CentroId == centro && r.TipoDocumentoId == tipo)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.BloqueaAcceso, r => !r.BloqueaAcceso));
                await Comprobar("requisito del Centro", RaicesDeRecalculo.DeCentros(centro));
            }

            // 2. Documento de la Empresa de un Centro: vence ayer; es causa de todos los Centros de esa Empresa.
            var empresaDelCentro = await db.Centros.Where(c => c.Id == centro).Select(c => c.EmpresaId).SingleAsync();
            var documentoDeEmpresa = await db.Documentos.Operativos()
                .Where(d => d.EmpresaId == empresaDelCentro && d.TrabajadorId == null
                            && d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha && d.FechaEmision < hoy.AddDays(-1))
                .OrderBy(d => d.Id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync();
            if (documentoDeEmpresa is { } idEmpresa)
            {
                await db.Documentos.Where(d => d.Id == idEmpresa)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(-1)));
                await Comprobar("documento de Empresa vencido", RaicesDeRecalculo.DeEmpresas(empresaDelCentro));
            }

            // 3. Documento de un Trabajador del Centro: pasa a urgente; cambia sus pares en todos sus Centros.
            var trabajadorDelCentro = await db.Asignaciones.Where(a => a.CentroId == centro && a.FechaBaja == null)
                .OrderBy(a => a.TrabajadorId).Select(a => a.TrabajadorId).FirstAsync();
            var documentoDeTrabajador = await db.Documentos.Operativos()
                .Where(d => d.TrabajadorId == trabajadorDelCentro && d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha
                            && d.FechaEmision < hoy)
                .OrderBy(d => d.Id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync();
            if (documentoDeTrabajador is { } idTrabajador)
            {
                await db.Documentos.Where(d => d.Id == idTrabajador)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(3)));
                await Comprobar("documento de Trabajador urgente", RaicesDeRecalculo.DeTrabajadores(trabajadorDelCentro));
            }

            // 4. Baja de Asignación de un Trabajador con Empresa: la acción da el Centro y el Trabajador; su Empresa deja de
            // operar allí y eso solo lo recoge la clausura.
            var baja = await (
                    from a in db.Asignaciones
                    where a.CentroId == centro && a.FechaBaja == null
                    join t in db.Trabajadores on a.TrabajadorId equals t.Id
                    where t.EmpresaId != null || t.SubcontrataId != null
                    orderby a.TrabajadorId
                    select new { a.CentroId, a.TrabajadorId })
                .FirstOrDefaultAsync();
            if (baja is not null)
            {
                await db.Asignaciones.Where(a => a.CentroId == baja.CentroId && a.TrabajadorId == baja.TrabajadorId && a.FechaBaja == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.FechaBaja, (DateOnly?)hoy));
                await Comprobar("baja de Asignación", new RaicesDeRecalculo([baja.CentroId], [baja.TrabajadorId], []));
            }

            // 5. Baja de Asignación dada solo con el Trabajador: el Centro ya no sale de las Asignaciones activas, y solo lo
            // recoge la clausura por las filas que el Trabajador ya tiene escritas allí (sus pares dejan de exigirse).
            var bajaSoloTrabajador = await db.Asignaciones.Where(a => a.CentroId == centro && a.FechaBaja == null)
                .OrderByDescending(a => a.TrabajadorId).Select(a => (Guid?)a.TrabajadorId).FirstOrDefaultAsync();
            if (bajaSoloTrabajador is { } trabajadorSinCentro)
            {
                await db.Asignaciones.Where(a => a.CentroId == centro && a.TrabajadorId == trabajadorSinCentro && a.FechaBaja == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.FechaBaja, (DateOnly?)hoy));
                await Comprobar("baja de Asignación con solo el Trabajador", RaicesDeRecalculo.DeTrabajadores(trabajadorSinCentro));
            }

            // 6. Documento de la Empresa con pares en más Centros: el recálculo por par rehace sus pares en todos esos Centros
            // (y la fila de cada Centro), sin rehacer los pares de los demás titulares. Se elige por lo materializado, un par
            // de Empresa cuyo documento efectivo vence lejos, para que el cambio mueva el estado seguro: el documento del paso 2
            // puede ser de la misma Empresa y ya estar vencido.
            var centrosPorEmpresa = (await db.Set<EstadoRequisito>().AsNoTracking()
                    .Where(p => p.AmbitoTitular == AmbitoAplicacion.Empresa)
                    .Select(p => new { p.TitularId, p.CentroId }).Distinct().ToListAsync())
                .GroupBy(p => p.TitularId).ToDictionary(g => g.Key, g => g.Count());
            var candidatosDeEmpresa = await (
                    from p in db.Set<EstadoRequisito>().AsNoTracking()
                    where p.AmbitoTitular == AmbitoAplicacion.Empresa && p.VencimientoEfectivo > hoy.AddDays(30)
                    join d in db.Documentos.Operativos() on p.DocumentoEfectivoId equals (Guid?)d.Id
                    where d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha && d.FechaEmision < hoy.AddDays(-1)
                    select new { p.TitularId, DocumentoId = d.Id })
                .Distinct().ToListAsync();
            var documentoDeEmpresaConCentros = candidatosDeEmpresa
                .OrderByDescending(p => centrosPorEmpresa.GetValueOrDefault(p.TitularId)).ThenBy(p => p.TitularId).ThenBy(p => p.DocumentoId)
                .FirstOrDefault();
            if (documentoDeEmpresaConCentros is not null)
            {
                await db.Documentos.Where(d => d.Id == documentoDeEmpresaConCentros.DocumentoId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(-1)));
                await Comprobar("documento de Empresa con muchos Centros", RaicesDeRecalculo.DeEmpresas(documentoDeEmpresaConCentros.TitularId));
            }

            // 7. Alta de Asignación por par: se reactiva la Asignación del paso 4 y se dan el Centro y el Trabajador como par.
            // El Centro no se rehace entero: solo su fila y los pares del Trabajador y de su Empresa.
            if (baja is not null)
            {
                await db.Asignaciones.Where(a => a.CentroId == baja.CentroId && a.TrabajadorId == baja.TrabajadorId && a.FechaBaja == hoy)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.FechaBaja, (DateOnly?)null));
                await Comprobar("alta de Asignación por par", RaicesDeRecalculo.DeAsignacion(baja.CentroId, baja.TrabajadorId));
            }

            // 8. Baja de Asignación por par, de otro Trabajador del Centro.
            var bajaPorPar = await db.Asignaciones.Where(a => a.CentroId == centro && a.FechaBaja == null)
                .OrderBy(a => a.TrabajadorId).Skip(1).Select(a => (Guid?)a.TrabajadorId).FirstOrDefaultAsync();
            if (bajaPorPar is { } trabajadorDeBajaPorPar)
            {
                await db.Asignaciones.Where(a => a.CentroId == centro && a.TrabajadorId == trabajadorDeBajaPorPar && a.FechaBaja == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.FechaBaja, (DateOnly?)hoy));
                await Comprobar("baja de Asignación por par", RaicesDeRecalculo.DeAsignacion(centro, trabajadorDeBajaPorPar));
            }

            return (hechos, distintos);
        });

        salida.WriteLine($"Tenant {tenant}: {string.Join(", ", cambios)}");
        cambios.Should().HaveCountGreaterThanOrEqualTo(3, "solo cuentan los cambios que movieron el estado; sin ellos el test no prueba la clausura");
        cambios.Should().Contain("baja de Asignación con solo el Trabajador", "es el caso que solo cubren las filas ya escritas");
        cambios.Should().Contain("documento de Empresa con muchos Centros", "el recálculo por par de una Empresa tiene que probarse con un cambio real");
        fallos.Should().BeEmpty();
    }

    /// <summary>
    /// El escritor solo toca el Tenant de la conexión: con el contexto de un Tenant, sustituir los Centros, Trabajadores y
    /// Empresas de otro no borra ninguna fila de ese otro (RLS forzada), y la clausura con raíces ajenas no encuentra nada.
    /// </summary>
    [Fact]
    public async Task El_escritor_y_la_clausura_no_tocan_otro_Tenant()
    {
        var conCentros = new List<Guid>();
        foreach (var t in fixture.Tenants)
            if (await fixture.Arnes.EnTenantAsync(t, (db, _) => db.Set<EstadoRequisito>().AnyAsync()))
                conCentros.Add(t);
        conCentros.Should().HaveCountGreaterThanOrEqualTo(2, "hacen falta dos Tenants con estado para probar el aislamiento");
        var (a, b) = (conCentros[0], conCentros[1]);

        var (centrosB, trabajadoresB, empresasB, antesB) = await fixture.Arnes.EnTenantAsync(b, async (db, _) => (
            await db.Centros.Select(c => c.Id).ToListAsync(),
            await db.Trabajadores.Select(t => t.Id).ToListAsync(),
            await db.Empresas.Select(e => e.Id).ToListAsync(),
            await FotoAsync(db)));

        var calculoDesdeA = await fixture.Arnes.EnTenantAsync(a, async (_, sp) =>
        {
            var raicesDeB = new RaicesDeRecalculo(centrosB, trabajadoresB, empresasB);
            var calculo = await sp.GetRequiredService<IRecalculoEstadoMaterializado>().CalcularAsync(raicesDeB, CancellationToken.None);
            await sp.GetRequiredService<IEscritorEstadoMaterializado>().EscribirAsync(
                new CalculoEstadoMaterializado(DiaDeNegocio.Hoy(), centrosB, trabajadoresB, empresasB, centrosB, [], [], []),
                crearVersionSiFalta: false, CancellationToken.None);
            return calculo;
        });

        calculoDesdeA.Pares.Should().BeEmpty("con el contexto de A, los Centros de B no existen");
        calculoDesdeA.Entidades.Should().BeEmpty();
        calculoDesdeA.Plataforma.Should().BeEmpty();
        (await fixture.Arnes.EnTenantAsync(b, (db, _) => FotoAsync(db)))
            .Should().Equal(antesB, "sustituir desde A las entidades de B no borra nada de B");
    }

    /// <summary>
    /// Alcance estrecho del recálculo por par: un cambio en un documento de un Trabajador rehace sus pares, y los pares de los
    /// demás titulares de sus Centros (Trabajadores y Empresas, TALVEG y plataforma) quedan intactos, con el mismo <c>Id</c> y
    /// el mismo <c>CalculadoEnUtc</c>. Un escritor que sustituyera por Centro con solo los pares del Trabajador los borraría.
    /// </summary>
    [Fact]
    public async Task El_recalculo_de_un_titular_no_toca_los_pares_de_los_demas_titulares()
    {
        var hoy = DiaDeNegocio.Hoy();
        string? probado = null;
        foreach (var tenant in fixture.Tenants)
        {
            probado = await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
            {
                // Un par de Trabajador, en un Centro con pares de otros titulares, cuyo documento efectivo vence lejos: hacer que
                // venza ayer cambia ese par seguro.
                var par = await (
                        from p in db.Set<EstadoRequisito>().AsNoTracking()
                        where p.AmbitoTitular == AmbitoAplicacion.Trabajador && p.VencimientoEfectivo > hoy.AddDays(30)
                              && db.Set<EstadoRequisito>().Any(o => o.CentroId == p.CentroId && o.TitularId != p.TitularId)
                        join d in db.Documentos.Operativos() on p.DocumentoEfectivoId equals (Guid?)d.Id
                        where d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha && d.FechaEmision < hoy.AddDays(-1)
                        orderby p.Id
                        select new { p.TitularId, DocumentoId = d.Id })
                    .FirstOrDefaultAsync();
                if (par is null)
                    return (string?)null;

                db.ChangeTracker.Clear();
                var antes = await ParesConIdentidadAsync(db);
                await db.Documentos.Where(d => d.Id == par.DocumentoId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(-1)));
                await sp.GetRequiredService<IRecalculoEstadoMaterializado>()
                    .RecalcularAsync(RaicesDeRecalculo.DeTrabajadores(par.TitularId), CancellationToken.None);
                db.ChangeTracker.Clear();
                var despues = await ParesConIdentidadAsync(db);

                bool EsDelTrabajador(ParConIdentidad f) => f.Ambito == AmbitoAplicacion.Trabajador && f.Titular == par.TitularId;
                List<string> Filas(IEnumerable<ParConIdentidad> filas, bool conIdentidad = true) =>
                    filas.Select(f => conIdentidad ? $"{f.Identidad} {f.Valores}" : f.Valores).Order(StringComparer.Ordinal).ToList();

                var centrosDelTrabajador = antes.Where(EsDelTrabajador).Select(f => f.CentroId).ToHashSet();
                antes.Any(f => !EsDelTrabajador(f) && centrosDelTrabajador.Contains(f.CentroId)).Should().BeTrue(
                    "sin pares de otros titulares en los Centros del Trabajador el test no probaría el alcance");
                var ajenosAntes = Filas(antes.Where(f => !EsDelTrabajador(f)));
                Filas(despues.Where(f => !EsDelTrabajador(f))).Should().Equal(ajenosAntes,
                    $"el recálculo del Trabajador {par.TitularId} no reescribe ni borra los pares de otros titulares en el Tenant {tenant}");
                Filas(despues.Where(EsDelTrabajador), conIdentidad: false).Should().NotEqual(Filas(antes.Where(EsDelTrabajador), conIdentidad: false),
                    "el documento vencido mueve los pares del Trabajador; si no, el test no probaría nada");
                return $"Tenant {tenant}, Trabajador {par.TitularId}";
            });
            if (probado is not null)
                break;
        }

        probado.Should().NotBeNull("hace falta un par de Trabajador con un documento efectivo que venza lejos");
        salida.WriteLine(probado);
    }

    /// <summary>
    /// Los métodos de pares por titular de <see cref="ICalculoEstadoCentroService"/> devuelven exactamente el subconjunto de
    /// los métodos completos: ni un par de más, ni uno de menos, ni un estado distinto. Con todos los Centros del Tenant y con
    /// la mitad, y con un titular que no existe en la lista.
    /// </summary>
    [Fact]
    public async Task Los_pares_por_titular_son_exactamente_el_subconjunto_de_los_completos()
    {
        var comparados = (Trabajador: 0, Empresa: 0);
        foreach (var tenant in fixture.Tenants)
        {
            var (deTrabajador, deEmpresa) = await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
            {
                var ct = CancellationToken.None;
                var calculo = sp.GetRequiredService<ICalculoEstadoCentroService>();
                var centros = await db.Centros.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
                var trabajadores = (await db.Trabajadores.OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct))
                    .Where((_, i) => i % 2 == 0).Append(Guid.NewGuid()).ToHashSet();
                var empresas = (await db.Empresas.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct))
                    .Where((_, i) => i % 2 == 0).Append(Guid.NewGuid()).ToHashSet();
                var (nTrabajador, nEmpresa) = (0, 0);

                foreach (var deCentros in new[] { centros, centros.Take((centros.Count + 1) / 2).ToList() })
                {
                    var esperados = Ordenar((await calculo.ObtenerParesExigidosAsync(deCentros, ct))
                        .Where(p => trabajadores.Contains(p.TrabajadorId)));
                    Ordenar(await calculo.ObtenerParesExigidosDeTrabajadoresAsync(deCentros, trabajadores, ct))
                        .Should().Equal(esperados, $"los pares por Trabajador son los completos filtrados ({deCentros.Count} Centros, Tenant {tenant})");

                    var esperadosEmpresa = Ordenar((await calculo.ObtenerParesDeEmpresaExigidosAsync(deCentros, ct))
                        .Where(p => empresas.Contains(p.EmpresaId)));
                    Ordenar(await calculo.ObtenerParesDeEmpresaExigidosDeEmpresasAsync(deCentros, empresas, ct))
                        .Should().Equal(esperadosEmpresa, $"los pares por Empresa son los completos filtrados ({deCentros.Count} Centros, Tenant {tenant})");

                    nTrabajador += esperados.Count;
                    nEmpresa += esperadosEmpresa.Count;
                }

                return (nTrabajador, nEmpresa);
            });
            comparados = (comparados.Trabajador + deTrabajador, comparados.Empresa + deEmpresa);
        }

        salida.WriteLine($"Pares comparados: {comparados.Trabajador} de Trabajador, {comparados.Empresa} de Empresa");
        comparados.Trabajador.Should().BePositive("sin pares de Trabajador el subconjunto no probaría nada");
        comparados.Empresa.Should().BePositive("sin pares de Empresa el subconjunto no probaría nada");
    }

    private static List<string> Ordenar<T>(IEnumerable<T> pares) =>
        pares.Select(p => p!.ToString()!).Order(StringComparer.Ordinal).ToList();

    /// <summary>Los pares TALVEG y de plataforma con su titular y su identidad (<c>Id</c> y <c>CalculadoEnUtc</c>).</summary>
    private static async Task<List<ParConIdentidad>> ParesConIdentidadAsync(CaeManagerDbContext db)
    {
        var filas = (await db.Set<EstadoRequisito>().AsNoTracking().ToListAsync())
            .Select(p => new ParConIdentidad(p.AmbitoTitular, p.TitularId, p.CentroId, $"R {p.Id} {p.CalculadoEnUtc:O}",
                $"R {p.CentroId} {p.TipoDocumentoId} {p.DocumentoEfectivoId} {p.EstadoTalveg} {p.BloqueaAcceso} " +
                $"{p.CuentaParaCumplimiento} {p.EstadoPlataforma} {p.VencimientoEfectivo} {p.ProximoCambioEl} {p.DiaCalculo}"))
            .ToList();
        filas.AddRange((await db.Set<EstadoEnPlataforma>().AsNoTracking().ToListAsync())
            .Select(p => new ParConIdentidad(p.AmbitoTitular, p.TitularId, p.CentroId, $"P {p.Id} {p.CalculadoEnUtc:O}",
                $"P {p.CentroId} {p.CanalGestionDocumentalId} {p.TipoDocumentoId} {p.Estado} " +
                $"{p.EstadoVigencia} {p.FechaVencimientoEnPlataforma} {p.ProximoCambioEl} {p.DiaCalculo}")));
        return filas;
    }

    private sealed record ParConIdentidad(AmbitoAplicacion Ambito, Guid Titular, Guid CentroId, string Identidad, string Valores);

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

    /// <param name="conIdentidad">
    /// Con el <c>Id</c> y <c>CalculadoEnUtc</c> de cada fila: así la foto distingue una fila intacta de una reescrita con los
    /// mismos valores.
    /// </param>
    /// <param name="conDiaCalculo">
    /// Sin el <c>DiaCalculo</c>: una fila igual calculada otro día cuenta como igual (el cambio de día no reescribe lo que
    /// no cambia).
    /// </param>
    internal static async Task<List<string>> FotoAsync(CaeManagerDbContext db, bool conIdentidad = false, bool conDiaCalculo = true)
    {
        var foto = new List<string>();
        string Identidad(Guid id, DateTime calculadoEnUtc) => conIdentidad ? $"{id} {calculadoEnUtc:O} " : "";
        string Dia(DateOnly dia) => conDiaCalculo ? dia.ToString("O") : "";
        foto.AddRange((await db.Set<EstadoEntidad>().AsNoTracking().ToListAsync())
            .Select(e => $"E {Identidad(e.Id, e.CalculadoEnUtc)}{e.Ambito} {e.EntidadId} {e.Estado} {e.EstadoPlataforma} {e.Vencidos} {e.VencidosSinBloqueo} {e.Urgentes} " +
                         $"{e.Proximos} {e.Faltan} {e.SinConfirmar} {e.PendientesEnPlataforma} {e.Requeridos} {e.AlDia} {e.ProximoCambioEl} {Dia(e.DiaCalculo)}"));
        foto.AddRange((await db.Set<EstadoRequisito>().AsNoTracking().ToListAsync())
            .Select(p => $"R {Identidad(p.Id, p.CalculadoEnUtc)}{p.CentroId} {p.AmbitoTitular} {p.TitularId} {p.TipoDocumentoId} {p.DocumentoEfectivoId} {p.EstadoTalveg} " +
                         $"{p.BloqueaAcceso} {p.CuentaParaCumplimiento} {p.EstadoPlataforma} {p.VencimientoEfectivo} {p.ProximoCambioEl} {Dia(p.DiaCalculo)}"));
        foto.AddRange((await db.Set<EstadoEnPlataforma>().AsNoTracking().ToListAsync())
            .Select(p => $"P {Identidad(p.Id, p.CalculadoEnUtc)}{p.CentroId} {p.CanalGestionDocumentalId} {p.AmbitoTitular} {p.TitularId} {p.TipoDocumentoId} {p.Estado} " +
                         $"{p.EstadoVigencia} {p.FechaVencimientoEnPlataforma} {p.ProximoCambioEl} {Dia(p.DiaCalculo)}"));
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
