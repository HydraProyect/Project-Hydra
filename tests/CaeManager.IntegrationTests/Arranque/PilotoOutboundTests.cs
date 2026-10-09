using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using Xunit;
using Xunit.Abstractions;
using Informe = CaeManager.Infrastructure.Persistence.Seed.PilotoOutboundAutoverificacion.Informe;
using MedicionCentro = CaeManager.Infrastructure.Persistence.Seed.PilotoOutboundAutoverificacion.MedicionCentro;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Las clases del piloto corren una detrás de otra: cada una crea y migra su base
/// y genera un centenar de PDF, uno de ellos pesado, y así cargan menos el clúster
/// compartido. En la primera ejecución, con las cinco en paralelo, el
/// <c>DROP DATABASE</c> del desmontaje de una agotó su tiempo de espera (causa no aislada).
/// </summary>
[CollectionDefinition(Nombre)]
public sealed class ColeccionPilotoOutbound
{
    public const string Nombre = "Piloto Outbound";
}

/// <summary>Una siembra del piloto, una vez, con el correo de contactos configurado.</summary>
public sealed class PilotoOutboundFixture : IAsyncLifetime
{
    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;
    internal PilotoOutboundSeeder.Resultado Resultado { get; private set; } = null!;
    internal Informe Informe { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        Configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(),
            extra: (OpcionesPilotoOutbound.ClaveCorreoContactos, ArnesPilotoOutbound.CorreoDePrueba));

        Resultado = (await Arnes.SembrarAsync(Configuracion))!;
        await Arnes.BackfillAsync();
        Informe = await Arnes.MedirAsync(Configuracion);
    }

    public async Task DisposeAsync() => await Arnes.DisposeAsync();
}

/// <summary>
/// La siembra del piloto del Servicio TALVEG Outbound, medida con las consultas
/// de las pantallas y la identidad de las cuentas sembradas, bajo el rol
/// restringido y con el almacén real. Los valores esperados están escritos a
/// mano aquí —no salen del catálogo ni del sembrador—: son los de la matriz.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundTests(PilotoOutboundFixture fixture, ITestOutputHelper salida) : IClassFixture<PilotoOutboundFixture>
{
    private static readonly TenantPilotoOutbound T2 = CatalogoPilotoOutbound.T2;
    private static readonly TenantPilotoOutbound T3 = CatalogoPilotoOutbound.T3;
    private static readonly TenantPilotoOutbound T4 = CatalogoPilotoOutbound.T4;
    private static readonly TenantPilotoOutbound T5 = CatalogoPilotoOutbound.T5;
    private static readonly TenantPilotoOutbound T6 = CatalogoPilotoOutbound.T6;

    [Fact]
    public void La_siembra_escribio_y_la_autoverificacion_pasa()
    {
        fixture.Resultado.Escribio.Should().BeTrue();
        fixture.Resultado.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.EnOrdenDeSiembra.Select(t => t.Nombre), "MEDIDO: los pequeños primero y T1 el último");
        fixture.Resultado.Pdf.Should().Be(fixture.Resultado.Documentos, "MEDIDO: un PDF por documento");

        foreach (var m in fixture.Informe.Tenants)
            salida.WriteLine("MEDIDO " + PilotoOutboundTextos.Linea(m));

        PilotoOutboundAutoverificacion.Discrepancias(fixture.Informe).Should().BeEmpty();
        fixture.Informe.Tenants.Should().HaveCount(6);
    }

    [Fact]
    public void T2_esta_todo_al_dia_en_las_seis_pantallas()
    {
        var m = fixture.Informe.De(T2);

        m.MiTrabajoPresente.Should().BeTrue();
        m.MiTrabajoAlcanceCero.Should().BeFalse("MEDIDO: cero filas con alcance, no por falta de cartera");
        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be((0, 0, 0, 0));
        m.InicioCumplimiento.Should().Be(100);
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((0, 0, 0, 0));
        (m.InicioCentrosBloqueados, m.InicioTrabajadoresBloqueados, m.InicioVisitasUrgentes).Should().Be((0, 0, 0));
        m.VisionCarteraCumplimiento.Should().Be(100);
        m.EmpresaCumplimiento.Should().Be(100);
        m.Centros.Should().HaveCount(5).And.OnlyContain(c => c.Estado == EstadoCentro.Vigente && c.Cumplimiento == 100);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((65, 0), "MEDIDO: trece Asignaciones activas × cinco tipos");
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto, m.ClientesEmpresarialesConAlertas).Should().Be((3, 0, 0));
        (m.Documentos, m.DocumentosSinPdf).Should().Be((77, 0), "MEDIDO: 12 Trabajadores × 5, 13 de la Empresa y 4 del Vehículo");
    }

    [Fact]
    public void T3_da_la_misma_mitad_en_Centros_Empresa_Inicio_y_Vision_de_cartera()
    {
        var m = fixture.Informe.De(T3);

        m.Centros.Should().HaveCount(4).And.OnlyContain(c => c.Cumplimiento == 50);
        m.EmpresaCumplimiento.Should().Be(50);
        m.InicioCumplimiento.Should().Be(50);
        m.VisionCarteraCumplimiento.Should().Be(50);
        (m.VisionCarteraPresente, m.VisionCarteraSinCartera, m.VisionCarteraSinDatos).Should().Be(
            (true, false, false), "MEDIDO: la fila pinta el porcentaje, no «Sin cartera» ni «Sin datos»");

        m.Centros.Select(c => c.Estado).Should().BeEquivalentTo(
            [EstadoCentro.Vencido, EstadoCentro.Faltante, EstadoCentro.Vencido, EstadoCentro.Faltante]);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((40, 10));
        (m.Documentos, m.InicioVencidos).Should().Be((20, 10));
        m.MiTrabajoFilas.Should().Be(20, "MEDIDO: por Trabajador, un documento vencido y uno que falta");
        m.MiTrabajoAlcanceCero.Should().BeFalse();
    }

    [Fact]
    public void T4_sin_documentos_da_cero_en_Centros_y_Empresa_y_declara_la_divergencia_de_Inicio_y_Vision_de_cartera()
    {
        var m = fixture.Informe.De(T4);

        m.Centros.Should().HaveCount(3).And.OnlyContain(c => c.Estado == EstadoCentro.Faltante && c.Cumplimiento == 0);
        m.EmpresaCumplimiento.Should().Be(0);
        (m.ParesExigidos, m.ParesFaltantes, m.Documentos).Should().Be((19, 19, 0));
        m.MiTrabajoFilas.Should().Be(19);

        // La divergencia declarada: sin documentos, estas dos pantallas no dicen 0 %. El número vive en UN sitio.
        CatalogoPilotoOutbound.CumplimientoDeInicioYVisionDeCarteraEnT4.Should().Be(100, "es lo que main pinta hoy; si cambia, se cambia la constante");
        m.InicioCumplimiento.Should().Be(100);
        m.VisionCarteraCumplimiento.Should().Be(100);

        var advertencias = PilotoOutboundAutoverificacion.Advertencias(fixture.Informe);
        advertencias.Should().HaveCount(3, "MEDIDO: T4, T5 y T6 declaran que Inicio y Visión de cartera no dan la cifra de Empresas");
        advertencias.Should().ContainSingle(a => a.StartsWith($"T4 «{CatalogoPilotoOutbound.NombreTenantT4}»"))
            .Which.Should().Contain("Inicio 100 %").And.Contain("Visión de cartera 100 %").And.Contain("Empresas 0 %");
    }

    [Fact]
    public void La_autoverificacion_exige_en_T4_la_constante_de_Inicio_y_Vision_de_cartera_y_avisa_solo_mientras_diverge()
    {
        var prefijo = $"T4 «{CatalogoPilotoOutbound.NombreTenantT4}» · ";
        Informe ConT4(Func<PilotoOutboundAutoverificacion.MedicionTenant, PilotoOutboundAutoverificacion.MedicionTenant> cambio) =>
            new([.. fixture.Informe.Tenants.Select(t => t.Clave == T4.Clave ? cambio(t) : t)]);

        // El día que Inicio pase a contar pares exigidos medirá 0: la autoverificación lo dice, con el contador.
        PilotoOutboundAutoverificacion.Discrepancias(ConT4(t => t with { InicioCumplimiento = 0 }))
            .Should().Equal(prefijo + "Inicio · % de cumplimiento: medido 0, esperado 100.");
        PilotoOutboundAutoverificacion.Discrepancias(ConT4(t => t with { VisionCarteraCumplimiento = 0 }))
            .Should().Equal(prefijo + "Visión de cartera · % de cumplimiento: medido 0, esperado 100.");

        // La advertencia sale de comparar los esperados del catálogo, no de un texto fijo.
        var esperadoHoy = CatalogoPilotoOutbound.Esperado(T4)!;
        esperadoHoy.InicioOVisionDeCarteraDivergenDeEmpresa.Should().BeTrue();
        (esperadoHoy with { CumplimientoInicio = 0, CumplimientoVisionCartera = 0 }).InicioOVisionDeCarteraDivergenDeEmpresa
            .Should().BeFalse("con la constante a 0, igual que Empresas, deja de haber divergencia que declarar");
        CatalogoPilotoOutbound.Esperado(T2)!.InicioOVisionDeCarteraDivergenDeEmpresa.Should().BeFalse("control: donde las tres cifras coinciden no se avisa");
    }

    [Fact]
    public void T5_cuatro_Trabajadores_en_catorce_Centros_dan_los_contadores_de_la_matriz()
    {
        var m = fixture.Informe.De(T5);

        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be(
            (3, 1, 0, 0), "MEDIDO: dos Trabajadores bloqueados en el Centro de la periodicidad especial, un vencido y un urgente");
        m.MiTrabajoAlcanceCero.Should().BeFalse();
        (m.InicioCumplimiento, m.VisionCarteraCumplimiento).Should().Be((95, 95), "MEDIDO: 19 de 20 documentos al día");
        m.EmpresaCumplimiento.Should().Be(96, "MEDIDO: 212 de 220 pares; el vencido cuenta en los ocho Centros de su Trabajador");
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((1, 1, 0, 0));
        m.InicioTrabajadoresBloqueados.Should().Be(2);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((220, 0), "MEDIDO: 14 + 12 + 10 + 8 Asignaciones activas × cinco tipos");
        (m.Documentos, m.DocumentosSinPdf).Should().Be((20, 0), "MEDIDO: cuatro Trabajadores × cinco tipos");
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto).Should().Be((6, 0));

        m.Centros.Should().HaveCount(14);
        m.Centros.Count(c => c is { Estado: EstadoCentro.Vencido, Cumplimiento: 95 }).Should().Be(8, "MEDIDO: los ocho Centros del Trabajador con el vencido");
        m.Centros.Count(c => c is { Estado: EstadoCentro.Urgente, Cumplimiento: 100 }).Should().Be(2);
        m.Centros.Count(c => c is { Estado: EstadoCentro.Vigente, Cumplimiento: 100 }).Should().Be(4);
    }

    [Fact]
    public async Task T5_cada_Trabajador_esta_en_entre_ocho_y_catorce_Centros()
    {
        var porTrabajador = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(T5.Nombre), async (db, _) =>
        {
            (await db.Trabajadores.CountAsync()).Should().Be(4);
            return await db.Asignaciones.Where(a => a.FechaBaja == null)
                .GroupBy(a => a.TrabajadorId).Select(g => g.Select(a => a.CentroId).Distinct().Count()).ToListAsync();
        });

        porTrabajador.Should().BeEquivalentTo([14, 12, 10, 8]);
        porTrabajador.Should().OnlyContain(n => n >= 8 && n <= 14);
    }

    /// <summary>
    /// El mismo documento —el certificado de aptitud médica del Trabajador que está
    /// en los catorce Centros— evaluado en tres Centros que lo exigen para acceder:
    /// vale sin más en uno, venció por la periodicidad especial en otro, y en el
    /// tercero venció igual pero la tolerancia del Centro todavía lo admite.
    /// </summary>
    [Fact]
    public async Task T5_el_mismo_documento_vale_vence_o_esta_en_tolerancia_segun_el_Centro()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();

        var (resultados, documentos, pintado) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T5.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var trabajadorId = await db.Asignaciones.Where(a => a.FechaBaja == null)
                .GroupBy(a => a.TrabajadorId).Where(g => g.Count() == 14).Select(g => g.Key).SingleAsync();
            var tipoId = await db.TiposDocumento.Where(t => t.Nombre == CatalogoPilotoOutbound.AptitudMedica).Select(t => t.Id).SingleAsync();
            var centroPorNombre = await db.Centros.ToDictionaryAsync(c => c.Nombre, c => c.Id);

            var evaluacion = await sp.GetRequiredService<CaeManager.Application.Centros.IEvaluacionDeAccesoPorCentroService>()
                .EvaluarAsync(null, CancellationToken.None);
            var delDocumento = evaluacion.Requisitos
                .Where(r => r.TrabajadorId == trabajadorId && r.TipoDocumentoId == tipoId)
                .ToDictionary(r => centroPorNombre.Single(c => c.Value == r.CentroId).Key, r => r.Resultado);

            var documentosDelTipo = await db.Documentos.Where(x => x.TrabajadorId == trabajadorId && x.TipoDocumentoId == tipoId)
                .Select(x => new { x.Id, x.FechaEmision, x.FechaVencimiento }).ToListAsync();

            var porCentro = await sp.GetRequiredService<ISender>().Send(
                new CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador.ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId));
            var comoSePinta = porCentro.ToDictionary(
                c => c.CentroNombre, c => c.Documentos.Single(x => x.TipoDocumentoId == tipoId).Estado);

            return (delDocumento, documentosDelTipo, comoSePinta);
        });

        // Es UN documento, Vigente por su propia fecha.
        var documento = documentos.Should().ContainSingle().Subject;
        documento.FechaVencimiento.Should().BeAfter(d.AddDays(60), "MEDIDO: por su fecha, el documento no vence hasta dentro de meses");

        var sinCondiciones = T5.Centros[DisenoT5PilotoOutbound.CentroSinCondicionesPropias].Nombre;
        var conPeriodicidad = T5.Centros[DisenoT5PilotoOutbound.CentroConPeriodicidadEspecial].Nombre;
        var conPeriodicidadYTolerancia = T5.Centros[DisenoT5PilotoOutbound.CentroConPeriodicidadEspecialYTolerancia].Nombre;

        resultados.Keys.Should().BeEquivalentTo([sinCondiciones, conPeriodicidad, conPeriodicidadYTolerancia], "MEDIDO: solo esos tres Centros lo exigen para acceder");
        foreach (var (centro, r) in resultados)
            salida.WriteLine($"MEDIDO aptitud en «{centro}»: {r.Situacion}, vencimiento efectivo {r.VencimientoEfectivo}, en tolerancia hasta {r.EnToleranciaHasta}");

        // 1. Sin condiciones propias: vale, y vence cuando dice el documento.
        resultados[sinCondiciones].Should().Be(new ResultadoDeRequisito(SituacionDeRequisitoBloqueante.Cumplido, documento.FechaVencimiento, null));

        // 2. Renovación cada seis meses: venció hace unos veinte días (antes de D) y el Trabajador no puede acceder.
        resultados[conPeriodicidad].Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        resultados[conPeriodicidad].VencimientoEfectivo.Should().Be(documento.FechaEmision.AddMonths(6))
            .And.BeOnOrAfter(d.AddDays(-23)).And.BeOnOrBefore(d.AddDays(-20));
        resultados[conPeriodicidad].EnToleranciaHasta.Should().BeNull();

        // 3. La misma renovación con 45 días de tolerancia: venció igual, pero todavía vale.
        resultados[conPeriodicidadYTolerancia].Should().Be(new ResultadoDeRequisito(
            SituacionDeRequisitoBloqueante.Cumplido, documento.FechaEmision.AddMonths(6), documento.FechaEmision.AddMonths(6).AddDays(45)));
        resultados[conPeriodicidadYTolerancia].EnToleranciaHasta.Should().BeAfter(d, "MEDIDO: la tolerancia cubre el día de la demostración");

        // Lo que se pinta en la ficha del Trabajador, Centro a Centro: el rótulo sale de la fecha del documento,
        // así que dice «Vigente» en los catorce, también donde la regla de acceso lo da por vencido.
        pintado.Should().HaveCount(14).And.OnlyContain(p => p.Value == EstadoDocumento.Vigente);
    }

    /// <summary>
    /// Lo que sí cambia de rótulo según el Centro: un documento vencido por su
    /// propia fecha es «Vencido» en todos los Centros de su Trabajador menos en el
    /// que le concede tolerancia, donde es «En tolerancia» con su fecha límite.
    /// </summary>
    [Fact]
    public async Task T5_un_documento_vencido_se_pinta_en_tolerancia_solo_en_el_Centro_que_la_concede()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();

        var pintado = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T5.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var trabajadorId = await db.Asignaciones.Where(a => a.FechaBaja == null)
                .GroupBy(a => a.TrabajadorId).Where(g => g.Count() == 8).Select(g => g.Key).SingleAsync();
            var tipoId = await db.TiposDocumento.Where(t => t.Nombre == CatalogoPilotoOutbound.FormacionArt19).Select(t => t.Id).SingleAsync();

            var porCentro = await sp.GetRequiredService<ISender>().Send(
                new CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador.ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId));
            return porCentro.ToDictionary(c => c.CentroNombre, c => c.Documentos.Single(x => x.TipoDocumentoId == tipoId));
        });

        var conTolerancia = T5.Centros[DisenoT5PilotoOutbound.CentroConToleranciaParaElVencido].Nombre;
        pintado.Should().HaveCount(8);
        pintado.Values.Select(p => p.DocumentoId).Distinct().Should().ContainSingle("MEDIDO: es el mismo documento en los ocho Centros");
        pintado.Values.Should().OnlyContain(p => p.FechaVencimiento == d.AddDays(-12));

        pintado[conTolerancia].Estado.Should().Be(EstadoDocumento.EnTolerancia);
        pintado[conTolerancia].EnToleranciaHasta.Should().Be(d.AddDays(18), "MEDIDO: venció en D−12 y el Centro concede 30 días");
        pintado.Where(p => p.Key != conTolerancia).Should().OnlyContain(
            p => p.Value.Estado == EstadoDocumento.Vencido && p.Value.EnToleranciaHasta == null);
    }

    [Fact]
    public void T6_da_los_contadores_de_la_matriz_y_contiene_los_cinco_estados()
    {
        var m = fixture.Informe.De(T6);

        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be(
            (12, 4, 2, 0), "MEDIDO: 5 vencidos + 6 «Falta» + 1 requisito bloqueante pendiente; 4 urgentes; 2 próximos");
        m.MiTrabajoAlcanceCero.Should().BeFalse();
        (m.InicioCumplimiento, m.VisionCarteraCumplimiento).Should().Be((96, 96), "MEDIDO: 110 de 115 documentos de Trabajador al día");
        m.EmpresaCumplimiento.Should().Be(92, "MEDIDO: 138 de 150 pares");
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((5, 4, 2, 0), "MEDIDO: hay Vencido, Urgente y Próximo");
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((150, 6), "MEDIDO: treinta Asignaciones activas × cinco tipos; seis pares sin documento");
        m.InicioTrabajadoresBloqueados.Should().Be(1, "MEDIDO: uno de los Faltantes es de un requisito que bloquea el acceso; los otros cinco pares, no");
        (m.Documentos, m.DocumentosSinPdf).Should().Be((121, 0), "MEDIDO: 115 de Trabajador y tres por cada una de las dos subcontratas");
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto).Should().Be((9, 0), "MEDIDO: las dos subcontratas no cuentan como Clientes empresariales");
    }

    [Fact]
    public void T6_el_trabajo_pendiente_es_desigual_entre_las_tres_zonas()
    {
        var m = fixture.Informe.De(T6);
        List<(EstadoCentro, int?)> De(ZonaPilotoOutbound zona) =>
            [.. T6.CentrosDe(zona).Select(c => m.Centros.Single(x => x.Nombre == c.Nombre)).Select(c => (c.Estado, c.Cumplimiento))];

        // Barcelona, la peor: ningún Centro al 100 %.
        De(DisenoT6PilotoOutbound.Barcelona).Should().Equal(
            (EstadoCentro.Vencido, 80), (EstadoCentro.Vencido, 80), (EstadoCentro.Faltante, 80), (EstadoCentro.Faltante, 87));
        // Madrid, intermedia: dos Centros con pares incumplidos y dos solo con avisos.
        De(DisenoT6PilotoOutbound.Madrid).Should().Equal(
            (EstadoCentro.Vencido, 90), (EstadoCentro.Urgente, 100), (EstadoCentro.Urgente, 100), (EstadoCentro.Faltante, 87));
        // Santander, casi limpia: los cuatro al 100 %, y solo dos avisos.
        De(DisenoT6PilotoOutbound.Santander).Should().Equal(
            (EstadoCentro.Vigente, 100), (EstadoCentro.Proximo, 100), (EstadoCentro.Urgente, 100), (EstadoCentro.Vigente, 100));
    }

    [Fact]
    public async Task T6_lleva_la_zona_en_el_nombre_y_en_el_codigo_de_cada_Centro_y_tres_contactos_internos_en_la_Empresa_propia()
    {
        var (centros, cargosDeLaEmpresaPropia, contactosDeCentro) = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(T6.Nombre), async (db, _) =>
        {
            var propiaId = await db.Empresas.Where(e => e.RazonSocial == CatalogoPilotoOutbound.NombreTenantT6).Select(e => e.Id).SingleAsync();
            return (
                await db.Centros.Select(c => new { c.Nombre, c.CodigoCentro }).ToListAsync(),
                await db.ContactosAgenda.Where(c => c.EmpresaId == propiaId).Select(c => c.Cargo).ToListAsync(),
                await db.ContactosAgenda.Where(c => c.CentroId != null).Select(c => c.Cargo).ToListAsync());
        });

        centros.Should().HaveCount(12);
        foreach (var (zona, codigo) in new[] { ("Barcelona", "BCN"), ("Madrid", "MAD"), ("Santander", "SDR") })
        {
            var deLaZona = centros.Where(c => c.Nombre.StartsWith(zona + " · ", StringComparison.Ordinal)).ToList();
            deLaZona.Select(c => c.CodigoCentro).Should().BeEquivalentTo(
                [$"T6-{codigo}-01", $"T6-{codigo}-02", $"T6-{codigo}-03", $"T6-{codigo}-04"], $"MEDIDO: los cuatro Centros de {zona} llevan su zona en el código");
        }

        cargosDeLaEmpresaPropia.Where(c => c!.StartsWith("Coordinación documental", StringComparison.Ordinal)).Should().BeEquivalentTo(
            ["Coordinación documental — Barcelona", "Coordinación documental — Madrid", "Coordinación documental — Santander"]);
        cargosDeLaEmpresaPropia.Should().HaveCount(5, "MEDIDO: los dos contactos de toda Empresa propia y los tres de zona");
        contactosDeCentro.Should().HaveCount(12).And.NotContain(
            c => c!.StartsWith("Coordinación documental", StringComparison.Ordinal), "MEDIDO: el contacto interno no se duplica en la agenda de cada Centro");
    }

    [Fact]
    public async Task T6_un_Trabajador_de_Madrid_tiene_una_Asignacion_temporal_en_un_Centro_de_Barcelona_sin_duplicarse()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();

        var (trabajadores, asignaciones) = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(T6.Nombre), async (db, _) => (
            await db.Trabajadores.CountAsync(),
            await (from a in db.Asignaciones
                   join c in db.Centros on a.CentroId equals c.Id
                   select new { a.TrabajadorId, Centro = c.Nombre, a.FechaAlta, a.FechaBaja }).ToListAsync()));

        trabajadores.Should().Be(24, "MEDIDO: ocho por zona; el desplazado no es un Trabajador más");
        asignaciones.Select(a => a.TrabajadorId).Distinct().Should().HaveCount(24, "MEDIDO: todos tienen Asignación, y ninguna es de alguien de fuera de esos 24");
        asignaciones.Count(a => a.FechaBaja == null).Should().Be(30);

        var temporal = asignaciones.Should().ContainSingle(a => a.FechaBaja != null).Subject;
        temporal.Centro.Should().StartWith("Barcelona · ");
        (temporal.FechaAlta, temporal.FechaBaja).Should().Be((d.AddDays(-10), d.AddDays(20)), "MEDIDO: tiene fecha de alta y fecha de baja");

        asignaciones.Where(a => a.TrabajadorId == temporal.TrabajadorId && a.FechaBaja == null).Select(a => a.Centro)
            .Should().NotBeEmpty().And.OnlyContain(c => c.StartsWith("Madrid · "), "MEDIDO: sus Asignaciones activas siguen en su zona");
    }

    [Fact]
    public async Task T6_tiene_dos_subcontratas_con_su_contacto_y_su_documentacion_propia()
    {
        var subcontratas = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(T6.Nombre), async (db, _) =>
            await (from e in db.Empresas
                   where !e.EsPropia && e.NivelServicio != null
                   select new
                   {
                       e.RazonSocial,
                       Documentos = db.Documentos.Count(x => x.EmpresaId == e.Id),
                       Contactos = db.ContactosAgenda.Count(c => c.SubcontrataId == e.Id),
                       Trabajadores = db.Trabajadores.Count(t => t.SubcontrataId == e.Id)
                   }).ToListAsync());

        subcontratas.Select(s => s.RazonSocial).Should().BeEquivalentTo(T6.Subcontratas);
        subcontratas.Should().HaveCount(2).And.OnlyContain(s => s.Documentos == 3 && s.Contactos == 1 && s.Trabajadores == 0);
    }

    [Fact]
    public void Mi_trabajo_suma_entre_T2_y_T6_un_volumen_de_demostracion()
    {
        var filas = new[] { T2, T3, T4, T5, T6 }.ToDictionary(t => t.Clave, t => fixture.Informe.De(t).MiTrabajoFilas);
        salida.WriteLine($"MEDIDO filas de Mi trabajo: {string.Join(", ", filas.Select(p => $"{p.Key} {p.Value}"))}; suma {filas.Values.Sum()}");

        filas.Should().Equal(new Dictionary<string, int> { ["T2"] = 0, ["T3"] = 20, ["T4"] = 19, ["T5"] = 4, ["T6"] = 18 });
        filas.Values.Sum().Should().BeInRange(40, 80, "MEDIDO: ni una cola vacía ni una que no se pueda recorrer en la demostración");
    }

    [Fact]
    public async Task La_siembra_no_envia_ni_deja_encolado_ningun_correo()
    {
        fixture.Arnes.Correo.Intentos.Should().BeEmpty("MEDIDO: ni la siembra ni la medición llaman a IEmailService");

        using (var ambito = fixture.Arnes.Servicios.CreateScope())
            ambito.ServiceProvider.GetRequiredService<IEmailService>().Should().BeSameAs(
                fixture.Arnes.Correo, "control positivo: el servicio de correo que resuelve la aplicación es el espía");

        // Lo que deja en la base cualquier envío o aviso de la aplicación (no hay cola de salida de correo).
        var rastros = await fixture.Arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            return new Dictionary<string, int>
            {
                ["Documentos (control positivo)"] = await b.Documentos.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Reclamaciones documentales"] = await b.ReclamacionesDocumentales.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Conversaciones"] = await b.Conversaciones.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Mensajes"] = await b.Mensajes.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Eventos de conversación"] = await b.EventosConversacion.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Notificaciones a usuarios"] = await b.NotificacionesUsuario.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
            };
        });

        salida.WriteLine("MEDIDO rastros de envío: " + string.Join(", ", rastros.Select(p => $"{p.Key} {p.Value}")));
        rastros["Documentos (control positivo)"].Should().Be(238, "control positivo: el recuento ve las filas de los Tenants del piloto");
        rastros.Where(p => !p.Key.StartsWith("Documentos", StringComparison.Ordinal)).Should().OnlyContain(p => p.Value == 0);
    }

    [Fact]
    public async Task Un_re_arranque_con_la_fecha_de_demostracion_ya_pasada_avisa_y_no_escribe_porque_no_queda_nada_por_sembrar()
    {
        var antes = await fixture.Arnes.RecuentoAsync();
        var avisos = new ArnesPilotoOutbound.RegistroDeAvisos();
        var ayer = ArnesPilotoOutbound.Configurar(DiaDeNegocio.Hoy().AddDays(-1));

        var resultado = await fixture.Arnes.SembrarAsync(ayer, logger: avisos);

        resultado!.Escribio.Should().BeFalse("MEDIDO: no lanza; el arranque sigue");
        resultado.TenantsConDatosNuevos.Should().BeEmpty();
        avisos.Avisos.Should().ContainSingle().Which.Should().Contain("es anterior a hoy").And.Contain("no se escribe nada");
        (await fixture.Arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: ni una fila, ni una cuenta, ni un fichero más");
    }

    [Fact]
    public async Task Los_resultados_no_cambian_en_ninguno_de_los_dias_en_que_se_puede_sembrar_para_esa_demostracion()
    {
        var fecha = ArnesPilotoOutbound.FechaDemostracion();
        DiaDeNegocio.Hoy().Should().Be(fecha.AddDays(-5), "control: sin fijar el reloj, hoy es D−5");

        // Los dos extremos: el primer día en que la fecha vale (D−9) y el propio día de la demostración.
        foreach (var dia in new[] { fecha.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias), fecha })
        {
            Informe eseDia;
            using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(dia.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc))))
            {
                DiaDeNegocio.Hoy().Should().Be(dia, "control: el reloj fijado mueve el día de negocio");
                eseDia = await fixture.Arnes.MedirAsync(fixture.Configuracion);
            }

            PilotoOutboundAutoverificacion.Discrepancias(eseDia).Should().BeEmpty($"MEDIDO el día {dia:yyyy-MM-dd}");
            eseDia.Tenants.Should().BeEquivalentTo(fixture.Informe.Tenants, $"MEDIDO: lo que se ensaya el día D−5 es lo que se ve el día {dia:yyyy-MM-dd}");
        }
    }

    [Fact]
    public void Solo_T1_sigue_en_esqueleto_existe_tiene_agenda_y_esta_en_la_cartera_de_la_Gestora_CAE()
    {
        CatalogoPilotoOutbound.Tenants.Where(t => t.Escenario == EscenarioPilotoOutbound.Esqueleto).Should().Equal(CatalogoPilotoOutbound.T1);

        var m = fixture.Informe.De(CatalogoPilotoOutbound.T1);
        m.MiTrabajoPresente.Should().BeTrue("MEDIDO: T1 está en la cartera de la Gestora CAE primera");
        m.ContactosDeAgenda.Should().BeGreaterThan(0);
        (m.Documentos, m.Centros.Count, m.ClientesEmpresariales).Should().Be((0, 0, 0), "MEDIDO: sus Clientes empresariales están declarados, no sembrados");
    }

    [Fact]
    public async Task Cada_documento_se_abre_como_PDF_y_exactamente_uno_pesa_entre_5_MB_y_10_MiB()
    {
        var tamanos = new List<(string Tenant, long Bytes)>();

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(tenant.Nombre);
            await fixture.Arnes.EnTenantAsync(tenantId, async (db, sp) =>
            {
                var almacen = sp.GetRequiredService<IFileStorageService>();
                foreach (var clave in await db.Documentos.Select(d => d.ArchivoUrl).ToListAsync())
                {
                    clave.Should().NotBeNull("MEDIDO: ningún documento del piloto nace sin fichero");

                    await using var flujo = await almacen.AbrirAsync(clave!);
                    using var memoria = new MemoryStream();
                    await flujo.CopyToAsync(memoria);
                    memoria.Position = 0;

                    using var pdf = PdfReader.Open(memoria, PdfDocumentOpenMode.Import);
                    pdf.PageCount.Should().BeGreaterThan(0);
                    tamanos.Add((tenant.Clave, memoria.Length));
                }

                return 0;
            });
        }

        tamanos.Should().HaveCount(238, "MEDIDO: 77 de T2, 20 de T3, ninguno de T4, 20 de T5 y 121 de T6");
        var pesados = tamanos.Where(t => t.Bytes >= 5_000_000).ToList();
        salida.WriteLine($"MEDIDO PDF pesado: {string.Join(", ", pesados.Select(p => $"{p.Tenant} {p.Bytes} bytes"))}; el mayor de los demás: {tamanos.Where(t => t.Bytes < 5_000_000).Max(t => t.Bytes)} bytes");
        pesados.Should().ContainSingle().Which.Should().Match<(string Tenant, long Bytes)>(p => p.Tenant == "T2" && p.Bytes <= 10 * 1024 * 1024);
    }

    [Fact]
    public async Task Todos_los_contactos_de_agenda_llevan_una_variante_con_etiqueta_del_correo_configurado()
    {
        var correos = new List<string>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(tenant.Nombre);
            var delTenant = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.ContactosAgenda.Select(c => c.Email).ToListAsync());
            delTenant.Should().NotBeEmpty($"MEDIDO: {tenant.Clave} tiene a quién escribir");
            correos.AddRange(delTenant);
        }

        correos.Should().OnlyContain(c => c.StartsWith("ensayo+") && c.EndsWith("@destino.example"));
        correos.Should().OnlyHaveUniqueItems("MEDIDO: la etiqueta dice a qué contacto se escribió");
        salida.WriteLine($"MEDIDO contactos de agenda: {correos.Count}; ejemplo: {correos[0]}");
    }

    [Fact]
    public async Task El_Gestor_CAE_segundo_ve_en_Mi_trabajo_solo_T2_T3_y_T6_y_la_Gestora_CAE_primera_los_seis()
    {
        var idPorClave = new Dictionary<string, Guid>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
            idPorClave[tenant.Clave] = await fixture.Arnes.TenantIdAsync(tenant.Nombre);

        async Task<List<string>> ClavesVistasPorAsync(string email)
        {
            var miTrabajo = await fixture.Arnes.ComoCuentaAsync(
                CatalogoPilotoOutbound.NombreTenantOperador, email,
                sp => sp.GetRequiredService<ISender>().Send(new ObtenerMiTrabajoAgregadoQuery()));
            var vistos = miTrabajo.Tenants.Select(t => t.TenantId).Concat(miTrabajo.NoConsultados.Select(t => t.TenantId)).ToHashSet();
            miTrabajo.NoConsultados.Should().BeEmpty();
            return [.. idPorClave.Where(p => vistos.Contains(p.Value)).Select(p => p.Key).Order()];
        }

        (await ClavesVistasPorAsync(CuentasPilotoOutbound.Locales.GestorSegundo)).Should().Equal("T2", "T3", "T6");
        (await ClavesVistasPorAsync(CuentasPilotoOutbound.Locales.GestoraPrimera)).Should().Equal("T1", "T2", "T3", "T4", "T5", "T6");
    }

    [Theory]
    [InlineData("administrador-del-operador", "*no tiene exactamente el rol GestorCae*")]
    [InlineData("administrador-de-T1", "*no es de ese Tenant*")]
    [InlineData("coordinadora-como-gestora", "*no tiene exactamente el rol GestorCae*")]
    [InlineData("gestora-como-coordinadora", "*no tiene exactamente el rol CoordinadorCae*")]
    [InlineData("cuenta-que-no-existe", "*no es de ese Tenant*")]
    public async Task La_medicion_no_construye_identidad_para_una_cuenta_que_no_es_la_de_su_papel(string caso, string mensaje)
    {
        var locales = CuentasPilotoOutbound.Locales;
        var cuentas = caso switch
        {
            "administrador-del-operador" => locales with { GestoraPrimera = locales.AdministradorOperador },
            "administrador-de-T1" => locales with { GestoraPrimera = locales.AdministradorT1 },
            "coordinadora-como-gestora" => locales with { GestoraPrimera = locales.Coordinadora },
            "gestora-como-coordinadora" => locales with { Coordinadora = locales.GestoraPrimera },
            _ => locales with { GestoraPrimera = "nadie@destino.example" },
        };

        var medicion = () => PilotoOutboundAutoverificacion.MedirAsync(
            fixture.Arnes.FabricaDeAmbitos, cuentas, ContactosPilotoOutbound.NoEntregables, CancellationToken.None);

        await medicion.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        fixture.Arnes.Servicios.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull();
    }

    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await fixture.Arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(7, "control: el recuento ve los seis Tenants propietarios y el del Operador CAE externo");
        antes["Cuentas del piloto"].Should().Be(5);
        antes["Documentos"].Should().Be(238);
        antes["Ficheros en el almacén"].Should().Be(238);

        var segunda = await fixture.Arnes.SembrarAsync(fixture.Configuracion);

        segunda!.Escribio.Should().BeFalse();
        segunda.TenantsConDatosNuevos.Should().BeEmpty();
        (await fixture.Arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: ni una fila, ni una cuenta, ni un fichero más");
        salida.WriteLine("MEDIDO recuento: " + string.Join(", ", antes.Select(p => $"{p.Key} {p.Value}")));
    }

    private sealed class RelojEn(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}

internal static class PilotoOutboundTextos
{
    public static string Linea(PilotoOutboundAutoverificacion.MedicionTenant m) =>
        $"{m.Clave}: Mi trabajo presente={m.MiTrabajoPresente} alcanceCero={m.MiTrabajoAlcanceCero} noConsultado={m.MiTrabajoNoConsultado} " +
        $"filas={m.MiTrabajoFilas} (bloqueos {m.MiTrabajoBloqueos}, actuaciones {m.MiTrabajoActuaciones}, próximos {m.MiTrabajoProximos}, " +
        $"seguimiento {m.MiTrabajoSeguimiento}) | Inicio {m.InicioCumplimiento}% vencidos={m.InicioVencidos} urgentes={m.InicioUrgentes} " +
        $"próximos={m.InicioProximos} sinConfirmar={m.InicioSinConfirmar} centrosBloqueados={m.InicioCentrosBloqueados} " +
        $"trabajadoresBloqueados={m.InicioTrabajadoresBloqueados} visitasUrgentes={m.InicioVisitasUrgentes} | Visión de cartera " +
        $"presente={m.VisionCarteraPresente} sinCartera={m.VisionCarteraSinCartera} sinDatos={m.VisionCarteraSinDatos} {m.VisionCarteraCumplimiento}% | Empresa {m.EmpresaCumplimiento}% | Centros " +
        $"[{string.Join("; ", m.Centros.Select(c => $"{c.Estado} {c.Cumplimiento}%"))}] | pares {m.ParesExigidos} (faltantes {m.ParesFaltantes}) | " +
        $"Clientes empresariales {m.ClientesEmpresariales} (sin contacto {m.ClientesEmpresarialesSinContacto}, con alertas " +
        $"{m.ClientesEmpresarialesConAlertas}) | documentos {m.Documentos} (sin PDF {m.DocumentosSinPdf}) | agenda {m.ContactosDeAgenda} " +
        $"(fuera de regla {m.ContactosFueraDeLaReglaDeCorreo})";
}

/// <summary>
/// Sensibilidad de la autoverificación sobre datos que dejan de ser los de la
/// matriz, y que medir no escribe: su propia base, porque la estropea.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundSensibilidadTests(ITestOutputHelper salida)
{
    [Fact]
    public async Task Medir_no_escribe_y_Exigir_nombra_el_Tenant_y_el_contador_cuando_los_datos_se_apartan_de_la_matriz()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        // Sin correo de contactos y con un dominio propio: la agenda no es entregable (la otra rama de la regla de correo).
        var configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(), extra: (OpcionesPilotoOutbound.ClaveDominioContactos, "contactos.example"));
        await arnes.SembrarAsync(configuracion);
        await arnes.BackfillAsync();

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var correos = await arnes.EnTenantAsync(
                await arnes.TenantIdAsync(tenant.Nombre), (db, _) => db.ContactosAgenda.Select(c => c.Email).ToListAsync());
            correos.Should().NotBeEmpty().And.OnlyContain(c => c.EndsWith("@contactos.example") && !c.Contains('+'));
        }

        // 1. Medir no escribe: ni filas del piloto, ni auditoría, ni cuentas tocadas.
        var filasAntes = await arnes.RecuentoAsync();
        var huellaAntes = await HuellaDeEscriturasAsync(arnes);

        var informe = await arnes.MedirAsync(configuracion);

        PilotoOutboundAutoverificacion.Exigir(informe);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(filasAntes);
        (await HuellaDeEscriturasAsync(arnes)).Should().Be(huellaAntes, "MEDIDO: la medición no deja auditoría ni toca las cuentas con las que mide");
        arnes.Servicios.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull("MEDIDO: la identidad se retira al terminar");

        // 2. Un documento de Trabajador de T2 pasa a estar vencido.
        var t2 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT2);
        await arnes.EnTenantAsync(t2, async (db, _) =>
        {
            var documento = await db.Documentos.Where(d => d.TrabajadorId != null && d.FechaVencimiento != null).OrderBy(d => d.Id).FirstAsync();
            var vence = DiaDeNegocio.Hoy().AddDays(-20);
            documento.CorregirVigencia(vence.AddYears(-1), VigenciaDocumento.VenceEl(vence));
            return await db.SaveChangesAsync();
        });

        (await HuellaDeEscriturasAsync(arnes)).Should().NotBe(huellaAntes, "control positivo: la huella sí ve una escritura de verdad");

        var conVencido = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in conVencido) salida.WriteLine("MEDIDO " + linea);

        var prefijoT2 = $"T2 «{CatalogoPilotoOutbound.NombreTenantT2}» · ";
        conVencido.Should().NotBeEmpty().And.OnlyContain(l => l.StartsWith(prefijoT2), "MEDIDO: solo se estropeó T2");
        conVencido.Should().Contain(prefijoT2 + "Inicio · documentos vencidos: medido 1, esperado 0.");
        conVencido.Should().Contain(l => l.StartsWith(prefijoT2 + "Mi trabajo · filas: medido "));
        conVencido.Should().Contain(l => l.StartsWith(prefijoT2 + "Empresas · % de cumplimiento de la Empresa propia: medido "));

        // 3. Un documento vigente de T3 desaparece.
        var t3 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT3);
        await arnes.EnTenantAsync(t3, async (db, _) =>
        {
            var hoy = DiaDeNegocio.Hoy();
            db.Documentos.Remove(await db.Documentos.Where(d => d.FechaVencimiento > hoy).OrderBy(d => d.Id).FirstAsync());
            return await db.SaveChangesAsync();
        });

        var exigir = async () => PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var mensaje = (await exigir.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        salida.WriteLine("MEDIDO " + mensaje);

        var prefijoT3 = $"T3 «{CatalogoPilotoOutbound.NombreTenantT3}» · ";
        mensaje.Should().Contain(prefijoT3 + "Documentos · total: medido 19, esperado 20.");
        mensaje.Should().Contain(prefijoT3 + "Inicio · % de cumplimiento: medido ");
        mensaje.Should().Contain(prefijoT3 + "Visión de cartera · % de cumplimiento: medido ");
        mensaje.Should().NotContain($"T4 «{CatalogoPilotoOutbound.NombreTenantT4}»");
        mensaje.Should().NotContain($"T5 «{CatalogoPilotoOutbound.NombreTenantT5}»").And.NotContain($"T6 «{CatalogoPilotoOutbound.NombreTenantT6}»");

        // 4. En T5, el Centro que concedía 45 días de tolerancia deja de concederla: los dos Trabajadores con el
        //    certificado antiguo pasan a estar bloqueados también allí.
        var t5 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT5);
        var centroConTolerancia = CatalogoPilotoOutbound.T5.Centros[DisenoT5PilotoOutbound.CentroConPeriodicidadEspecialYTolerancia].Nombre;
        (await arnes.EnTenantAsync(t5, async (db, _) =>
        {
            var fila = await (from f in db.TiposDocumentoCentros
                              join c in db.Centros on f.CentroId equals c.Id
                              where c.Nombre == centroConTolerancia && f.ToleranciaDias == DisenoT5PilotoOutbound.DiasDeTolerancia
                              select f).SingleAsync();
            fila.Actualizar(fila.Incluido, fila.PeriodicidadEspecialMeses, fila.BloqueaAcceso, fila.ArchivoUrl, fila.NombreArchivoOriginal, toleranciaDias: 0);
            return await db.SaveChangesAsync();
        })).Should().Be(1, "control: la alteración de T5 se escribió");

        // 5. En T6, el Centro que exigía el certificado para acceder deja de exigirlo así: nadie queda bloqueado.
        var t6 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT6);
        (await arnes.EnTenantAsync(t6, async (db, _) =>
        {
            var fila = await db.TiposDocumentoCentros.Where(f => f.BloqueaAcceso).SingleAsync();
            fila.Actualizar(fila.Incluido, fila.PeriodicidadEspecialMeses, bloqueaAcceso: false, fila.ArchivoUrl, fila.NombreArchivoOriginal, fila.ToleranciaDias);
            return await db.SaveChangesAsync();
        })).Should().Be(1, "control: la alteración de T6 se escribió");

        var conT5yT6 = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        var prefijoT5 = $"T5 «{CatalogoPilotoOutbound.NombreTenantT5}» · ";
        var prefijoT6 = $"T6 «{CatalogoPilotoOutbound.NombreTenantT6}» · ";
        foreach (var linea in conT5yT6.Where(l => l.StartsWith(prefijoT5) || l.StartsWith(prefijoT6))) salida.WriteLine("MEDIDO " + linea);

        conT5yT6.Where(l => l.StartsWith(prefijoT5)).Should().Equal(
            [prefijoT5 + "Mi trabajo · filas: medido 6, esperado 4."], "MEDIDO: dos requisitos bloqueantes pendientes más, y nada más cambia en T5");
        conT5yT6.Where(l => l.StartsWith(prefijoT6)).Should().Equal(
            [prefijoT6 + "Mi trabajo · filas: medido 17, esperado 18.", prefijoT6 + "Inicio · Trabajadores bloqueados: medido 0, esperado 1."],
            "MEDIDO: desaparece el requisito bloqueante pendiente y el Trabajador deja de estar bloqueado; el «Falta» sigue");
    }

    /// <summary>Filas de auditoría de toda la base y sellos de concurrencia de las cuentas: cambia con cualquier escritura por la aplicación.</summary>
    private static Task<string> HuellaDeEscriturasAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var auditoria = await b.RegistrosAuditoria.IgnoreQueryFilters().CountAsync();
            var sellos = await b.Users.IgnoreQueryFilters().OrderBy(u => u.Id).Select(u => u.ConcurrencyStamp + "/" + u.SecurityStamp).ToListAsync();
            return $"{auditoria}|{string.Join(",", sellos)}";
        });
}

/// <summary>Lo que la siembra se niega a hacer, sobre una base en la que nunca llega a escribir.</summary>
public sealed class PilotoOutboundSinSiembraFixture : IAsyncLifetime
{
    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;

    public async Task InitializeAsync() => Arnes = await ArnesPilotoOutbound.CrearAsync();

    public async Task DisposeAsync() => await Arnes.DisposeAsync();
}

[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundNoEscribeTests(PilotoOutboundSinSiembraFixture fixture) : IClassFixture<PilotoOutboundSinSiembraFixture>
{
    private async Task NoHaNacidoNadaAsync()
    {
        var recuento = await fixture.Arnes.RecuentoAsync();
        recuento["Tenants del piloto"].Should().Be(0, "MEDIDO: el rechazo es previo a cualquier escritura");
        recuento["Cuentas del piloto"].Should().Be(0);
        recuento["Ficheros en el almacén"].Should().Be(0);
        recuento["Tenants en total"].Should().BeGreaterThan(0, "control positivo: el recuento ve los Tenants de la base");
    }

    [Fact]
    public async Task Sin_su_clave_no_siembra_nada_aunque_DatosPrueba_este_activo()
    {
        var resultado = await fixture.Arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion(), activo: false));

        resultado.Should().BeNull("MEDIDO: inerte por defecto");
        await NoHaNacidoNadaAsync();
    }

    [Fact]
    public async Task En_Produccion_lanza_con_su_propio_mensaje_y_no_siembra_nada()
    {
        var siembra = () => fixture.Arnes.SembrarAsync(
            ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()), new EntornoDePrueba("Production"));

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(
            "*PilotoOutbound:Activo no puede activarse en Producción*",
            "MEDIDO: la guarda es la de ESTA siembra; CredencialesDemo lanza otra que también dice «Producción»");
        await NoHaNacidoNadaAsync();
    }

    [Theory]
    [InlineData("Production", true, true, true)]
    [InlineData("Production", true, false, false)]
    [InlineData("Production", false, true, false)]
    [InlineData("Development", true, true, false)]
    [InlineData("Staging", true, true, false)]
    public void La_guarda_de_Produccion_rechaza_solo_con_las_dos_claves_activas_en_Produccion(
        string entorno, bool datosPrueba, bool piloto, bool debeLanzar)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [OpcionesPilotoOutbound.ClaveActivo] = piloto.ToString(),
        }).Build();

        var llamada = () => PilotoOutboundSeeder.RechazarEnProduccion(configuracion, new EntornoDePrueba(entorno));

        if (debeLanzar)
            llamada.Should().Throw<InvalidOperationException>().WithMessage("*PilotoOutbound:Activo no puede activarse en Producción*");
        else
            llamada.Should().NotThrow();
    }

    [Fact]
    public void El_arranque_rechaza_la_clave_en_Produccion_antes_de_cualquier_otra_siembra()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull("el instrumento tiene que localizar la raíz del repositorio para leer Program.cs");

        var programa = File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "Program.cs"));
        var rechazo = programa.IndexOf("PilotoOutboundSeeder.RechazarEnProduccion(", StringComparison.Ordinal);
        var primeraSiembra = programa.IndexOf("IdentitySeeder.SeedAsync(", StringComparison.Ordinal);

        rechazo.Should().BeGreaterThan(-1, "el arranque tiene que invocar la guarda");
        primeraSiembra.Should().BeGreaterThan(-1, "control positivo: el instrumento ve la primera siembra del arranque");
        rechazo.Should().BeLessThan(primeraSiembra, "con la clave activa en Producción, las siembras anteriores ya habrían escrito");
    }

    [Theory]
    [InlineData(null, "*Falta DatosPrueba:PilotoOutbound:FechaDemostracion*")]
    [InlineData("14/10/2026", "*no es una fecha con formato yyyy-MM-dd*")]
    [InlineData("-1", "*es anterior a hoy*")]
    [InlineData("+10", "*queda a más de 9 días de hoy*")]
    public async Task Con_la_fecha_de_demostracion_ausente_ilegible_pasada_o_lejana_se_niega_antes_de_escribir(string? fecha, string mensaje)
    {
        var valor = fecha is not null && (fecha[0] is '-' or '+')
            ? DiaDeNegocio.Hoy().AddDays(int.Parse(fecha)).ToString("yyyy-MM-dd")
            : fecha;
        var configuracion = ArnesPilotoOutbound.Configurar(null, extra: (OpcionesPilotoOutbound.ClaveFechaDemostracion, valor));

        var siembra = () => fixture.Arnes.SembrarAsync(configuracion);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        await NoHaNacidoNadaAsync();
    }

    [Theory]
    [InlineData("CorreoContactos", "ensayo+yo@destino.example", "*CorreoContactos no es una dirección utilizable*")]
    [InlineData("CorreoContactos", "sin-arroba", "*CorreoContactos no es una dirección utilizable*")]
    public async Task Con_un_correo_de_contactos_que_no_vale_se_niega_antes_de_escribir(string clave, string valor, string mensaje)
    {
        var configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(), extra: ($"{OpcionesPilotoOutbound.Seccion}:{clave}", valor));

        var siembra = () => fixture.Arnes.SembrarAsync(configuracion);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        await NoHaNacidoNadaAsync();
    }

    [Fact]
    public void En_el_limite_de_nueve_dias_y_en_el_propio_dia_la_fecha_vale()
    {
        var hoy = new DateOnly(2026, 10, 9);

        var enElLimite = () => OpcionesPilotoOutbound.ValidarFecha(hoy.AddDays(9), hoy);
        var hoyMismo = () => OpcionesPilotoOutbound.ValidarFecha(hoy, hoy);

        enElLimite.Should().NotThrow();
        hoyMismo.Should().NotThrow();
        OpcionesPilotoOutbound.MotivoFechaNoUtilizable(hoy.AddDays(9), hoy).Should().BeNull();
        OpcionesPilotoOutbound.MotivoFechaNoUtilizable(hoy.AddDays(-1), hoy).Should().Contain("es anterior a hoy");
        OpcionesPilotoOutbound.MotivoFechaNoUtilizable(hoy.AddDays(10), hoy).Should().Contain("queda a más de 9 días de hoy");
    }

    [Fact]
    public void La_variante_de_T4_ya_no_existe_ni_como_clave_ni_como_tipo()
    {
        typeof(OpcionesPilotoOutbound).GetFields().Select(f => f.Name).Should().Contain(nameof(OpcionesPilotoOutbound.ClaveActivo), "control positivo: el instrumento ve las claves")
            .And.NotContain(n => n.Contains("Variante", StringComparison.Ordinal));
        typeof(OpcionesPilotoOutbound).Assembly.GetTypes().Should().NotContain(t => t.Name.Contains("VarianteT4", StringComparison.Ordinal));

        // Una clave «VarianteT4» que alguien deje en la configuración se ignora: T4 no tiene documentos, se diga lo que se diga.
        var conLaClaveVieja = ArnesPilotoOutbound.Configurar(
            new DateOnly(2026, 10, 14), extra: ($"{OpcionesPilotoOutbound.Seccion}:VarianteT4", "DocumentosVencidos"));
        OpcionesPilotoOutbound.Leer(conLaClaveVieja).Should().Be(
            new OpcionesPilotoOutbound(new DateOnly(2026, 10, 14), ContactosPilotoOutbound.NoEntregables));
        CatalogoPilotoOutbound.Esperado(CatalogoPilotoOutbound.T4)!.Documentos.Should().Be(0);
    }

    [Fact]
    public void Las_fechas_de_la_siembra_respetan_los_margenes_de_la_matriz_para_cualquier_indice()
    {
        var d = new DateOnly(2026, 10, 14);
        var fechas = new FechasPilotoOutbound(d);

        foreach (var i in Enumerable.Range(0, 2_000))
        {
            fechas.Vigente(i).Should().BeAfter(d.AddDays(60));
            fechas.Proximo(i).Should().BeOnOrAfter(d.AddDays(20)).And.BeOnOrBefore(d.AddDays(25));
            fechas.Urgente(i).Should().BeOnOrAfter(d.AddDays(5)).And.BeOnOrBefore(d.AddDays(10));
            fechas.Vencido(i).Should().BeOnOrAfter(d.AddDays(-90)).And.BeOnOrBefore(d.AddDays(-10));
            fechas.Emision(i).Should().BeBefore(d.AddDays(-30));
        }
    }

    /// <summary>
    /// El estado se calcula contra hoy, y hoy puede ser cualquier día entre D−9 y D. Con los umbrales
    /// por defecto (ámbar 30, rojo 15), cada fecha tiene que dar el mismo estado los diez días.
    /// </summary>
    [Fact]
    public void Las_fechas_de_la_siembra_dan_el_mismo_estado_cualquier_dia_en_que_se_pueda_sembrar()
    {
        var d = new DateOnly(2026, 10, 14);
        var fechas = new FechasPilotoOutbound(d);
        EstadoDocumento Estado(DateOnly vence, DateOnly hoy) => CalculadoraEstadoDocumento.Calcular(
            VigenciaDocumento.VenceEl(vence), hoy, umbralAmbarDias: 30, umbralRojoDias: 15);

        Estado(d.AddDays(22), d.AddDays(-9)).Should().Be(EstadoDocumento.Vigente, "control positivo: una fecha del margen ancho de «Próximo» cambia de estado dentro de la ventana");
        Estado(d.AddDays(22), d).Should().Be(EstadoDocumento.Proximo);

        foreach (var hoy in Enumerable.Range(0, OpcionesPilotoOutbound.MargenMaximoDias + 1).Select(n => d.AddDays(-n)))
        {
            foreach (var i in Enumerable.Range(0, 300))
            {
                Estado(fechas.Vigente(i), hoy).Should().Be(EstadoDocumento.Vigente);
                Estado(fechas.Proximo(i), hoy).Should().Be(EstadoDocumento.Proximo);
                Estado(fechas.Urgente(i), hoy).Should().Be(EstadoDocumento.Urgente);
                Estado(fechas.Vencido(i), hoy).Should().Be(EstadoDocumento.Vencido);
            }
        }
    }

    [Fact]
    public void La_regla_de_correo_de_los_contactos_distingue_el_buzon_configurado_del_dominio_no_entregable()
    {
        var conCorreo = ContactosPilotoOutbound.Crear(ArnesPilotoOutbound.CorreoDePrueba, null);
        conCorreo.DireccionDe("t2-centro1").Should().Be("ensayo+t2-centro1@destino.example");
        conCorreo.Cumple("ensayo+t2-centro1@destino.example").Should().BeTrue();
        conCorreo.Cumple("ensayo@destino.example").Should().BeFalse("sin etiqueta no se sabe a qué contacto se escribió");
        conCorreo.Cumple("otra+t2-centro1@destino.example").Should().BeFalse();
        conCorreo.Cumple("t2-centro1@caemanager.local").Should().BeFalse();

        var sinCorreo = ContactosPilotoOutbound.Crear(" ", null);
        sinCorreo.Should().Be(ContactosPilotoOutbound.NoEntregables);
        sinCorreo.DireccionDe("t2-centro1").Should().Be("t2-centro1@caemanager.local");
        sinCorreo.Cumple("ensayo+t2-centro1@destino.example").Should().BeFalse();
    }
}

/// <summary>
/// Una siembra cortada a mitad: lo que deja, que se puede reanudar sin retirar,
/// que la retirada lo limpia todo y que una siembra nueva se completa como una
/// primera. Y el nombre del piloto ocupado por un Tenant que no es de demo.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundInterrupcionTests(ITestOutputHelper salida)
{
    // T4 no escribe ningún PDF y T3 escribe 20: el n.º 30 es el décimo de T2.
    private const int PdfEnElQueFalla = 30;
    private const int PdfDeT3 = 20;

    [Fact]
    public async Task Una_siembra_cortada_limpia_los_PDF_de_su_Tenant_se_reanuda_se_retira_entera_y_se_vuelve_a_sembrar()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
        var sinPiloto = await arnes.RecuentoAsync();

        // 1. El almacén falla en mitad de T2.
        var conFallo = () => arnes.SembrarAsync(configuracion, envolverAlmacen: real => new AlmacenQueSeCorta(real, PdfEnElQueFalla));
        await conFallo.Should().ThrowAsync<IOException>().WithMessage("*disco lleno de mentira*");

        var trasElFallo = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO tras el fallo del almacén: " + Texto(trasElFallo));
        trasElFallo["Ficheros en el almacén"].Should().Be(PdfDeT3, "MEDIDO: los nueve PDF que T2 ya había escrito se eliminan; quedan los de T3, que sí se guardó");
        trasElFallo["Documentos"].Should().Be(PdfDeT3);
        (await TenantsSinMarcadorAsync(arnes)).Should().BeEmpty("MEDIDO: todo Tenant del piloto lleva el marcador desde que nace");

        // 1 bis. Con la fecha de la demostración ya pasada y T2 a medias, la siembra se niega: queda algo por escribir.
        var conLaFechaPasada = () => arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(DiaDeNegocio.Hoy().AddDays(-1)));
        await conLaFechaPasada.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*es anterior a hoy*Queda por sembrar «{CatalogoPilotoOutbound.NombreTenantT2}»*");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(trasElFallo, "MEDIDO: el rechazo no escribe");

        // 2. Reanudar sin retirar: se completa lo que faltaba y la matriz sale.
        var reanudada = await arnes.SembrarAsync(configuracion);
        reanudada!.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.NombreTenantT2, CatalogoPilotoOutbound.NombreTenantT5,
            CatalogoPilotoOutbound.NombreTenantT6, CatalogoPilotoOutbound.NombreTenantT1);
        await arnes.BackfillAsync();
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var completa = await arnes.RecuentoAsync();
        completa["Ficheros en el almacén"].Should().Be(238);

        // 3. La retirada no deja nada del piloto y no toca lo demás.
        (await arnes.RetirarAsync()).Should().HaveCount(7);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto, "MEDIDO: ni Tenants, ni cuentas, ni filas, ni ficheros del piloto; lo demás, igual que antes");
        sinPiloto["Tenants en total"].Should().BeGreaterThan(0, "control positivo: había otros Tenants que no debían tocarse");

        // 4. Cancelación en mitad de T2.
        using var cancelacion = new CancellationTokenSource();
        var cancelada = () => arnes.SembrarAsync(
            configuracion, envolverAlmacen: real => new AlmacenQueSeCorta(real, PdfEnElQueFalla, cancelacion.Cancel), cancellationToken: cancelacion.Token);
        await cancelada.Should().ThrowAsync<OperationCanceledException>();

        var trasCancelar = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO tras la cancelación: " + Texto(trasCancelar));
        trasCancelar["Ficheros en el almacén"].Should().Be(PdfDeT3, "MEDIDO: la limpieza corre aunque la causa sea la cancelación");

        // 5. Retirar lo que dejó la siembra cortada, y sembrar de nuevo como la primera vez.
        await arnes.RetirarAsync();
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto);

        var nueva = await arnes.SembrarAsync(configuracion);
        nueva!.TenantsConDatosNuevos.Should().Equal(CatalogoPilotoOutbound.EnOrdenDeSiembra.Select(t => t.Nombre));
        await arnes.BackfillAsync();
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: igual que la siembra completa anterior");
    }

    [Fact]
    public async Task Con_un_nombre_del_piloto_ocupado_por_un_Tenant_sin_marcador_de_demo_se_niega_y_lo_nombra()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();

        using (var ambito = arnes.Servicios.CreateScope())
            await DelegacionDemoSeeder.AprovisionarTenantAsync(
                ambito.ServiceProvider.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>(),
                CatalogoPilotoOutbound.NombreTenantT3, PerfilVocabularioTenant.ClienteDirecto, NullLogger.Instance,
                CancellationToken.None, esOperadorCaeExterno: false);

        var antes = await arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(1, "control: el Tenant que ocupa el nombre existe");

        var siembra = () => arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()));

        await siembra.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*«{CatalogoPilotoOutbound.NombreTenantT3}»*NO lleva el marcador de datos de demo*");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: no escribe ni en ese Tenant ni a su lado");

        var retirada = () => arnes.RetirarAsync();
        await retirada.Should().ThrowAsync<InvalidOperationException>("la retirada tampoco toca un Tenant sin marcador");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes);
    }

    private static string Texto(Dictionary<string, int> recuento) => string.Join(", ", recuento.Select(p => $"{p.Key} {p.Value}"));

    private static Task<List<string>> TenantsSinMarcadorAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            return b.Tenants.Where(t => nombres.Contains(t.Nombre) && t.DatosDemoCompletadosEnUtc == null).Select(t => t.Nombre).ToListAsync();
        });

    /// <summary>El almacén real, que al llegar al guardado n.º <paramref name="falloEn"/> cancela (si se le da con qué) o falla.</summary>
    private sealed class AlmacenQueSeCorta(IFileStorageService real, int falloEn, Action? cancelar = null) : IFileStorageService
    {
        private int _guardados;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            if (++_guardados == falloEn)
            {
                if (cancelar is null)
                    throw new IOException("disco lleno de mentira");

                cancelar();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return real.GuardarAsync(contenido, nombreArchivoOriginal, cancellationToken);
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            real.AbrirAsync(identificador, cancellationToken);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            real.EliminarAsync(identificador, cancellationToken);
    }
}
