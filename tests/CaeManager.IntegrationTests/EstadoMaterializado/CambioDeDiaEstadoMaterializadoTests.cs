using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// Diseño del estado materializado, § 2.2, § 7.2 y § 13.12 (PR 5): la pasada de medianoche, la reposición al arrancar y el
/// cotejo nocturno, bajo <c>cae_app_runtime</c> y Tenant por Tenant. La pasada solo toca lo que cambia hoy y deja las
/// tablas como el relleno completo; repetirla el mismo día no hace nada; tras varios días parado, un ciclo lo repone; el
/// cotejo encuentra lo que el detector no vio, lo corrige y avisa; con la cola pendiente se aplaza; y el cambio de día de
/// un Tenant no toca las filas ni la marca de otro.
///
/// <para>
/// Fixture propia (otra base sembrada): estos tests mueven el reloj y estropean filas a propósito. Cada test parte de un
/// relleno con el reloj real, la marca a NULL y la cola vacía.
/// </para>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class CambioDeDiaEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    [Fact]
    public async Task La_pasada_del_dia_siguiente_deja_las_tablas_como_el_relleno_y_repetirla_no_hace_nada()
    {
        var tenant = await TenantConMasCambiosProgramadosAsync();
        await ReiniciarAsync(tenant);
        var dia = await ProximoDiaDeCambioAsync(tenant);
        salida.WriteLine($"Tenant {tenant}: próximo cambio el {dia:O}");

        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(DiaDeNegocio.InicioEnUtc(dia).AddMinutes(1)));
        var alerta = new AlertaFalsa();
        var resultado = await CambioDeDiaAsync(tenant, dia, alerta);

        salida.WriteLine($"Pasada: {resultado.RaicesDeLaPasada} raíces (-1 = Tenant entero); cotejo: {resultado.Cotejo}");
        resultado.Estado.Should().Be(EstadoCambioDeDia.Hecho);
        resultado.RaicesDeLaPasada.Should().NotBe(0, "había filas cuyo estado cambia ese día");
        // La pasada sola deja el día al día: si el cotejo encontrara algo, la pasada se habría dejado filas (o el cotejo
        // daría falsos positivos con un reloj que no es el del relleno) y avisaría cada noche.
        resultado.Cotejo!.Total.Should().Be(0, "la pasada de las filas con ProximoCambioEl <= ese día basta");
        alerta.Mensajes.Should().BeEmpty();

        var (vencidas, marca, cola, version) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) => (
            await FilasConCambioHastaAsync(db, dia), await MarcaAsync(db), await db.Set<RecalculoPendiente>().CountAsync(), await VersionAsync(db)));
        vencidas.Should().Be(0, "la pasada rehace toda fila cuyo ProximoCambioEl es ese día o anterior");
        marca.Should().Be(dia);
        cola.Should().Be(0, "la pasada drena la cola que ella misma llena");

        // Idempotente por día: un segundo ciclo el mismo día no encola ni escribe.
        var otraVez = await CambioDeDiaAsync(tenant, dia, alerta);
        otraVez.Estado.Should().Be(EstadoCambioDeDia.YaProcesado);
        (await fixture.Arnes.EnTenantAsync(tenant, (db, _) => VersionAsync(db))).Should().Be(version, "repetir el cambio de día no cambia ninguna fila");
        (await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<RecalculoPendiente>().CountAsync())).Should().Be(0);

        // Lo que queda es lo que daría el relleno completo de ese día (sin DiaCalculo: lo que no cambió no se reescribe).
        var (trasCambio, relleno) = await FotoYRellenoAsync(tenant);
        relleno.Except(trasCambio).Should().BeEmpty("tras la pasada y el cotejo no falta nada del relleno");
        trasCambio.Except(relleno).Should().BeEmpty("tras la pasada y el cotejo no sobra nada");
    }

    [Fact]
    public async Task Tras_varios_dias_parado_el_primer_ciclo_repone_cada_Tenant_y_el_siguiente_no_hace_nada()
    {
        foreach (var t in fixture.Tenants)
            await ReiniciarAsync(t);
        var tenant = await TenantConMasCambiosProgramadosAsync();
        // Tres días después del próximo cambio: el servidor estuvo parado esas medianoches.
        var dia = (await ProximoDiaDeCambioAsync(tenant)).AddDays(3);

        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(DiaDeNegocio.InicioEnUtc(dia).AddHours(9)));
        var alerta = new AlertaFalsa();
        var resultados = await CicloAsync(alerta);

        resultados.Keys.Should().BeEquivalentTo(fixture.Tenants, "el ciclo recorre todos los Tenants y ninguno falla");
        resultados.Values.Should().OnlyContain(r => r.Estado == EstadoCambioDeDia.Hecho);
        resultados.Values.Should().OnlyContain(r => r.Cotejo!.Total == 0, "la pasada sola repone los días que el servidor estuvo parado");
        alerta.Mensajes.Should().BeEmpty();
        foreach (var t in fixture.Tenants)
        {
            var (vencidas, marca) = await fixture.Arnes.EnTenantAsync(t, async (db, _) => (await FilasConCambioHastaAsync(db, dia), await MarcaAsync(db)));
            vencidas.Should().Be(0, $"el Tenant {t} queda al día");
            marca.Should().Be(dia, $"la marca del Tenant {t} queda en hoy");
        }

        var segundo = await CicloAsync(alerta);
        segundo.Values.Should().OnlyContain(r => r.Estado == EstadoCambioDeDia.YaProcesado, "con la marca al día el ciclo no hace nada");
    }

    [Fact]
    public async Task El_cotejo_encuentra_lo_que_el_detector_no_vio_lo_corrige_y_avisa_sin_datos_personales()
    {
        var tenant = await TenantConMasCambiosProgramadosAsync();
        await ReiniciarAsync(tenant);
        var hoy = DiaDeNegocio.Hoy();
        var (trabajador, documento) = await DocumentoDeTrabajadorAsignadoAsync(tenant);

        var huerfano = Guid.NewGuid();
        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            // Un cambio que el detector no ve (como uno guardado con el contexto de otro Tenant o tras una caída).
            await db.Documentos.Where(d => d.Id == documento)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(-1)));
            // Una fila estropeada, otra borrada y una de una entidad que no existe.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE "EstadosEntidad" SET "Vencidos" = "Vencidos" + 7
                WHERE "Id" = (SELECT "Id" FROM "EstadosEntidad" WHERE "TenantId" = {tenant} AND "Ambito" = {(short)AmbitoEstadoEntidad.Centro} ORDER BY "Id" LIMIT 1)
                """);
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "EstadosRequisito"
                WHERE "Id" = (SELECT "Id" FROM "EstadosRequisito" WHERE "TenantId" = {tenant} AND "TitularId" <> {trabajador} ORDER BY "Id" LIMIT 1)
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "EstadosEntidad" ("Id", "Ambito", "EntidadId", "Estado", "EstadoPlataforma", "Vencidos",
                    "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                    "Requeridos", "AlDia", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), "Ambito", {huerfano}, "Estado", "EstadoPlataforma", "Vencidos",
                    "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                    "Requeridos", "AlDia", NULL, "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId"
                FROM "EstadosEntidad" WHERE "TenantId" = {tenant} AND "Ambito" = {(short)AmbitoEstadoEntidad.Trabajador} ORDER BY "Id" LIMIT 1
                """);
            return 0;
        });

        var alerta = new AlertaFalsa();
        var resultado = await CambioDeDiaAsync(tenant, hoy, alerta);

        salida.WriteLine($"Cotejo: {resultado.Cotejo}");
        resultado.Estado.Should().Be(EstadoCambioDeDia.Hecho);
        resultado.Cotejo!.Entidades.Distintas.Should().BeGreaterThanOrEqualTo(1, "la fila estropeada");
        resultado.Cotejo.Pares.Faltan.Should().BeGreaterThanOrEqualTo(1, "la fila borrada");
        resultado.Cotejo.Huerfanas.Should().Be(1, "la fila de una entidad que no existe");

        alerta.Mensajes.Should().ContainSingle().Which.Nivel.Should().Be(NivelAlertaOperativa.Aviso);
        var mensaje = alerta.Mensajes.Single().Mensaje;
        mensaje.Should().Contain(tenant.ToString()).And.Contain("Tenant entero");
        mensaje.Should().NotContain(trabajador.ToString(), "la alerta lleva recuentos, no identificadores de personas");
        mensaje.Should().NotContain(huerfano.ToString());

        var (trasCambio, relleno) = await FotoYRellenoAsync(tenant);
        relleno.Except(trasCambio).Should().BeEmpty("el cotejo corrigió lo que faltaba o estaba distinto");
        trasCambio.Except(relleno).Should().BeEmpty("el cotejo corrigió lo que sobraba");

        // Un segundo cotejo (otro día de la marca) ya no encuentra nada.
        await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.ExecuteSqlAsync(
            $"""UPDATE "VersionesEstadoTenant" SET "UltimoDiaProcesado" = NULL WHERE "TenantId" = {tenant}"""));
        var segundo = await CambioDeDiaAsync(tenant, hoy, alerta);
        segundo.Cotejo!.Total.Should().Be(0);
        alerta.Mensajes.Should().HaveCount(1, "sin diferencias no hay aviso");
    }

    [Fact]
    public async Task Un_documento_cambiado_sin_pasar_por_el_detector_lo_corrige_el_cotejo_por_sus_raices()
    {
        var tenant = await TenantConMasCambiosProgramadosAsync();
        await ReiniciarAsync(tenant);
        var hoy = DiaDeNegocio.Hoy();

        // El documento efectivo de un par de Trabajador, que vence más adelante: pasa a vencido sin que el detector lo vea
        // (como un cambio guardado con el contexto de otro Tenant, con uno creado a mano o tras una caída).
        var (trabajador, documento) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var par = await (from r in db.Set<EstadoRequisito>()
                             join d in db.Documentos.Operativos() on r.DocumentoEfectivoId equals d.Id
                             where r.AmbitoTitular == AmbitoAplicacion.Trabajador && d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha
                                   && d.FechaVencimiento > hoy
                             orderby d.Id
                             select new { r.TitularId, d.Id }).FirstAsync();
            await db.Documentos.Where(d => d.Id == par.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(-1)));
            return (par.TitularId, par.Id);
        });
        salida.WriteLine($"Documento {documento} del Trabajador {trabajador}");

        var alerta = new AlertaFalsa();
        var resultado = await CambioDeDiaAsync(tenant, hoy, alerta);

        salida.WriteLine($"Cotejo: {resultado.Cotejo}");
        resultado.Estado.Should().Be(EstadoCambioDeDia.Hecho);
        resultado.Cotejo!.Huerfanas.Should().Be(0);
        resultado.Cotejo.Pares.Distintas.Should().BeGreaterThanOrEqualTo(1, "el par cuyo documento efectivo venció");
        resultado.Cotejo.Raices.TrabajadorIds.Should().Contain(trabajador);
        alerta.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Contain("raíces").And.NotContain("Tenant entero",
            "sin huérfanas y con pocas raíces se corrige por raíces, no rellenando el Tenant entero");

        var (trasCambio, relleno) = await FotoYRellenoAsync(tenant);
        relleno.Except(trasCambio).Should().BeEmpty("la corrección por raíces dejó el par y sus entidades como el relleno");
        trasCambio.Except(relleno).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_la_cola_pendiente_el_cambio_de_dia_se_aplaza_sin_marca_y_el_siguiente_ciclo_lo_completa()
    {
        var tenant = await TenantConMasCambiosProgramadosAsync();
        await ReiniciarAsync(tenant);
        var hoy = DiaDeNegocio.Hoy();
        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            // Una entrada que falló hace un momento: espera su reintento y la cola no la reclama todavía.
            await ColaEstadoMaterializado.EncolarTenantEnteroAsync(db, tenant, "prueba del aplazamiento");
            await db.Database.ExecuteSqlAsync($"""UPDATE "RecalculosPendientes" SET "IntentoEnUtc" = now(), "Intentos" = 1""");
            return 0;
        });

        var alerta = new AlertaFalsa();
        var aplazado = await CambioDeDiaAsync(tenant, hoy, alerta);
        aplazado.Estado.Should().Be(EstadoCambioDeDia.Aplazado);
        aplazado.Cotejo.Should().BeNull("con cola pendiente lo guardado no está al día y el cotejo daría diferencias falsas");
        (await fixture.Arnes.EnTenantAsync(tenant, (db, _) => MarcaAsync(db))).Should().BeNull();

        await VaciarColaAsync(tenant);
        var completado = await CambioDeDiaAsync(tenant, hoy, alerta);
        completado.Estado.Should().Be(EstadoCambioDeDia.Hecho);
        (await fixture.Arnes.EnTenantAsync(tenant, (db, _) => MarcaAsync(db))).Should().Be(hoy);
        alerta.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task El_cambio_de_dia_de_un_Tenant_no_toca_las_filas_ni_la_marca_de_otro()
    {
        fixture.Tenants.Count.Should().BeGreaterThanOrEqualTo(2, "el aislamiento necesita dos Tenants");
        var tenantA = await TenantConMasCambiosProgramadosAsync();
        var tenantB = fixture.Tenants.First(t => t != tenantA);
        await ReiniciarAsync(tenantA);
        await ReiniciarAsync(tenantB);

        // Una fila estropeada en B: el cotejo de A no la ve ni la corrige.
        await fixture.Arnes.EnTenantAsync(tenantB, (db, _) => db.Database.ExecuteSqlAsync($"""
            UPDATE "EstadosEntidad" SET "Vencidos" = "Vencidos" + 7
            WHERE "Id" = (SELECT "Id" FROM "EstadosEntidad" WHERE "TenantId" = {tenantB} ORDER BY "Id" LIMIT 1)
            """));
        var antesB = await fixture.Arnes.EnTenantAsync(tenantB, async (db, _) =>
            (await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db, conIdentidad: true), await VersionAsync(db)));

        var dia = (await ProximoDiaDeCambioAsync(tenantA)).AddDays(1);
        using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(DiaDeNegocio.InicioEnUtc(dia).AddMinutes(1))))
            (await CambioDeDiaAsync(tenantA, dia, new AlertaFalsa())).Estado.Should().Be(EstadoCambioDeDia.Hecho);

        var despuesB = await fixture.Arnes.EnTenantAsync(tenantB, async (db, _) =>
            (await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db, conIdentidad: true), await VersionAsync(db),
             await MarcaAsync(db), await db.Set<RecalculoPendiente>().CountAsync()));
        despuesB.Item1.Should().Equal(antesB.Item1, "ninguna fila de B se reescribió");
        despuesB.Item2.Should().Be(antesB.Item2);
        despuesB.Item3.Should().BeNull("la marca de B no se movió");
        despuesB.Item4.Should().Be(0, "nada se encoló en B");

        await ReiniciarAsync(tenantB);
    }

    // ---- Ayudas ----

    private Task<ResultadoCambioDeDia> CambioDeDiaAsync(Guid tenant, DateOnly hoy, IAlertaOperativa alerta) =>
        fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
            ActivatorUtilities.CreateInstance<CambioDeDiaEstadoMaterializado>(sp, alerta).ProcesarTenantAsync(hoy, CancellationToken.None));

    /// <summary>Un ciclo del servicio de fondo sobre todos los Tenants, sin la elección de líder ni la espera.</summary>
    private Task<IReadOnlyDictionary<Guid, ResultadoCambioDeDia>> CicloAsync(IAlertaOperativa alerta)
    {
        var servicios = fixture.Arnes.Servicios;
        var servicio = new CambioDeDiaEstadoMaterializadoHostedService(
            servicios.GetRequiredService<IServiceScopeFactory>(), servicios.GetRequiredService<IEleccionLiderService>(),
            TimeProvider.System, alerta, servicios.GetRequiredService<ILogger<CambioDeDiaEstadoMaterializadoHostedService>>());
        return servicio.ProcesarAsync(CancellationToken.None);
    }

    /// <summary>Relleno con el reloj real, marca a NULL y cola vacía.</summary>
    private Task ReiniciarAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """);
            await sp.GetRequiredService<CaeManager.Application.EstadoMaterializado.IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None);
            await db.Database.ExecuteSqlAsync($"""UPDATE "VersionesEstadoTenant" SET "UltimoDiaProcesado" = NULL WHERE "TenantId" = {tenant}""");
            return 0;
        });

    private Task VaciarColaAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));

    /// <summary>La foto sin DiaCalculo tras el cambio de día, y la del relleno completo con el mismo reloj.</summary>
    private Task<(List<string> TrasCambio, List<string> Relleno)> FotoYRellenoAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            var tras = await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db, conDiaCalculo: false);
            await sp.GetRequiredService<CaeManager.Application.EstadoMaterializado.IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return (tras, await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db, conDiaCalculo: false));
        });

    private async Task<Guid> TenantConMasCambiosProgramadosAsync()
    {
        var programados = new Dictionary<Guid, int>();
        foreach (var t in fixture.Tenants)
            programados[t] = await fixture.Arnes.EnTenantAsync(t, (db, _) => db.Set<EstadoRequisito>().CountAsync(r => r.ProximoCambioEl != null));
        return programados.MaxBy(x => x.Value).Key;
    }

    /// <summary>El primer día, después de hoy, en que cambia alguna fila del Tenant.</summary>
    private Task<DateOnly> ProximoDiaDeCambioAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var hoy = DiaDeNegocio.Hoy();
            var candidatos = new List<DateOnly?>
            {
                await db.Set<EstadoEntidad>().Where(e => e.ProximoCambioEl > hoy).MinAsync(e => e.ProximoCambioEl),
                await db.Set<EstadoRequisito>().Where(r => r.ProximoCambioEl > hoy).MinAsync(r => r.ProximoCambioEl),
                await db.Set<EstadoEnPlataforma>().Where(p => p.ProximoCambioEl > hoy).MinAsync(p => p.ProximoCambioEl),
            };
            var dia = candidatos.Where(d => d is not null).Min();
            dia.Should().NotBeNull("el piloto tiene documentos que cambian de estado después de hoy");
            return dia!.Value;
        });

    private Task<(Guid Trabajador, Guid Documento)> DocumentoDeTrabajadorAsignadoAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var hoy = DiaDeNegocio.Hoy();
            var d = await (from a in db.Asignaciones
                           join doc in db.Documentos.Operativos() on a.TrabajadorId equals doc.TrabajadorId
                           where a.FechaBaja == null && doc.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha
                                 && doc.FechaVencimiento > hoy
                           orderby doc.Id
                           select new { a.TrabajadorId, doc.Id }).FirstAsync();
            return (d.TrabajadorId, d.Id);
        });

    private static async Task<int> FilasConCambioHastaAsync(CaeManagerDbContext db, DateOnly dia) =>
        await db.Set<EstadoEntidad>().CountAsync(e => e.ProximoCambioEl <= dia)
        + await db.Set<EstadoRequisito>().CountAsync(r => r.ProximoCambioEl <= dia)
        + await db.Set<EstadoEnPlataforma>().CountAsync(p => p.ProximoCambioEl <= dia);

    private static Task<DateOnly?> MarcaAsync(CaeManagerDbContext db) =>
        db.Set<VersionEstadoTenant>().AsNoTracking().Select(v => v.UltimoDiaProcesado).SingleAsync();

    private static Task<long> VersionAsync(CaeManagerDbContext db) =>
        db.Set<VersionEstadoTenant>().AsNoTracking().Select(v => v.Version).SingleAsync();

    private sealed class RelojEn(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class AlertaFalsa : IAlertaOperativa
    {
        public List<(string Mensaje, NivelAlertaOperativa Nivel)> Mensajes { get; } = [];

        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Mensajes.Add((mensaje, nivel));

        public void CapturarExcepcion(Exception excepcion) => throw excepcion;

        public void DejarMigaDePan(string mensaje)
        {
        }

        public IDisposable IniciarAmbitoDeCaptura() => new Nada();

        private sealed class Nada : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
