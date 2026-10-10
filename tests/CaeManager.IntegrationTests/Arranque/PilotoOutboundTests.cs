using System.IO.Compression;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
    internal ArnesPilotoOutbound.RegistroDeAvisos Registro { get; } = new();

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        Configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(),
            extra: (OpcionesPilotoOutbound.ClaveCorreoContactos, ArnesPilotoOutbound.CorreoDePrueba));

        Resultado = (await Arnes.SembrarAsync(Configuracion, logger: Registro))!;
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
    private static readonly TenantPilotoOutbound T1 = CatalogoPilotoOutbound.T1;
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
        fixture.Resultado.Documentos.Should().Be(1164, "MEDIDO: 885 de T1, 77 de T2, 33 de T3, ninguno de T4, 33 de T5 y 136 de T6");

        // La duración del lote y la memoria pico las dice la propia siembra, en su línea de registro.
        var lineaDelLote = fixture.Registro.Informativas.Should()
            .ContainSingle(l => l.StartsWith("Siembra del piloto Outbound:", StringComparison.Ordinal)).Subject;
        salida.WriteLine($"MEDIDO {lineaDelLote}");
        lineaDelLote.Should().Contain("memoria pico del proceso");
        salida.WriteLine($"MEDIDO duración del lote según su resultado: {fixture.Resultado.Duracion.TotalSeconds:F1} s; avisos de la siembra: {fixture.Registro.Avisos.Count}");

        foreach (var m in fixture.Informe.Tenants)
            salida.WriteLine("MEDIDO " + PilotoOutboundTextos.Linea(m));

        // Lo que se lee de un vistazo antes de la demostración: los Centros de cada Tenant por estado, y si queda alguno en verde.
        foreach (var m in fixture.Informe.Tenants)
            salida.WriteLine($"MEDIDO {m.Clave}: {PilotoOutboundTextos.CentrosPorEstado(m)}");
        salida.WriteLine(
            "MEDIDO Centros «Vigente» en los seis Tenants: " +
            fixture.Informe.Tenants.Sum(m => m.Centros.Count(c => c.Estado == EstadoCentro.Vigente)));

        PilotoOutboundAutoverificacion.Discrepancias(fixture.Informe).Should().BeEmpty();
        fixture.Informe.Tenants.Should().HaveCount(6);
    }

    /// <summary>
    /// Lo que el Asistente IA comprueba antes de enviar nada al proveedor, con la identidad de las dos cuentas que
    /// lo usan en la demostración: el Tenant en pantalla y toda la cartera tienen instrucción de tratamiento de IA
    /// vigente. Se afirma por nombre contra los siete del catálogo: una cartera vacía también «pasaría».
    /// </summary>
    [Fact]
    public async Task El_asistente_IA_no_falla_cerrado_para_la_Gestora_CAE_ni_para_la_Coordinadora_CAE()
    {
        var asistenteIa = fixture.Informe.AsistenteIa;
        asistenteIa.Should().NotBeNull("control: la autoverificación midió la comprobación de cartera");
        CatalogoPilotoOutbound.NombresTenants.Should().HaveCount(7, "control: el Operador CAE externo y los seis Tenants propietarios");

        foreach (var (cuenta, cartera) in new[] { ("Gestora CAE", asistenteIa!.Gestora), ("Coordinadora CAE", asistenteIa.Coordinadora) })
        {
            salida.WriteLine($"MEDIDO cartera de la {cuenta}: con instrucción {cartera.ConInstruccion.Count}, sin instrucción {cartera.SinInstruccion.Count}");
            cartera.SinInstruccion.Should().BeEmpty($"MEDIDO: ningún Tenant de la cartera de la {cuenta} está sin instrucción");
            cartera.ConInstruccion.Should().BeEquivalentTo(
                CatalogoPilotoOutbound.NombresTenants, $"MEDIDO: la cartera de la {cuenta} son los siete Tenants del piloto, todos con instrucción");
        }

        // Las dos condiciones que el modo Preguntar exige antes de llamar al proveedor, con la Gestora CAE dentro de T1.
        var (enPantalla, errorDeCartera) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T1.Nombre, async sp =>
        {
            var tenantId = sp.GetRequiredService<ITenantActual>().TenantId;
            tenantId.Should().NotBeNull("control: dentro de T1 hay Tenant en pantalla");
            return (
                await sp.GetRequiredService<IInstruccionTratamientoIaService>().EstaHabilitadaAsync(tenantId!.Value),
                (await sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery())).ErrorSiFalta());
        });
        enPantalla.Should().BeTrue("MEDIDO: T1, el Tenant en pantalla, tiene instrucción vigente");
        errorDeCartera.Should().BeNull("MEDIDO: la comprobación de cartera no devuelve el error que impide enviar el mensaje");
    }

    [Fact]
    public void T2_esta_todo_al_dia_en_las_seis_pantallas()
    {
        var m = fixture.Informe.De(T2);
        salida.WriteLine(
            $"MEDIDO T2: Mi trabajo (bloqueos {m.MiTrabajoBloqueos}, actuaciones {m.MiTrabajoActuaciones}, próximos {m.MiTrabajoProximos}, " +
            $"seguimiento {m.MiTrabajoSeguimiento}); Inicio (vencidos {m.InicioVencidos}, urgentes {m.InicioUrgentes}, próximos {m.InicioProximos}, " +
            $"sin confirmar {m.InicioSinConfirmar}); {PilotoOutboundTextos.CentrosPorEstado(m)}");

        m.MiTrabajoPresente.Should().BeTrue();
        m.MiTrabajoAlcanceCero.Should().BeFalse("MEDIDO: cero filas con alcance, no por falta de cartera");
        // Los dos certificados mensuales de la Empresa propia están «Próximo», y aun así Mi trabajo e Inicio no traen
        // nada: sus alertas y sus recuentos solo leen documentos de Trabajador.
        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be((0, 0, 0, 0));
        m.InicioCumplimiento.Should().Be(100);
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((0, 0, 0, 0));
        (m.InicioCentrosBloqueados, m.InicioTrabajadoresBloqueados, m.InicioVisitasUrgentes).Should().Be((0, 0, 0));
        m.VisionCarteraCumplimiento.Should().Be(100);
        m.EmpresaCumplimiento.Should().Be(100);
        // Donde sí se ven es en los Centros: un documento de la Empresa propia que no está Vigente tiñe todos los suyos.
        m.Centros.Should().HaveCount(5).And.OnlyContain(c => c.Estado == EstadoCentro.Proximo && c.Cumplimiento == 100);
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

        m.Centros.Select(c => c.Estado).Should().Equal(
            EstadoCentro.Vencido, EstadoCentro.Vencido, EstadoCentro.Vencido, EstadoCentro.Vencido);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((20, 0), "MEDIDO: diez Trabajadores, un Centro cada uno, dos tipos exigidos; no falta ninguno");
        (m.Documentos, m.InicioVencidos).Should().Be((33, 10), "MEDIDO: 20 de Trabajador y los 13 de la Empresa propia, que Inicio no cuenta");
        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be(
            (10, 0, 0, 0), "MEDIDO: una fila por documento vencido; la Visita, a quince días, no añade ninguna");
        m.InicioVisitasUrgentes.Should().Be(0);
        m.MiTrabajoAlcanceCero.Should().BeFalse();
    }

    /// <summary>
    /// La pregunta que la construcción de T3 responde: lo que cada Centro enseña en el
    /// listado de Centros, y lo que la comprobación previa de una Visita enseña de
    /// quienes acuden, es solo lo que ESE Centro exige a SUS Trabajadores.
    /// </summary>
    [Fact]
    public async Task T3_ningun_Centro_ensena_un_documento_que_no_exige_ni_de_un_Trabajador_que_no_es_suyo()
    {
        var (centros, asignaciones, filas, tipos, previa, centroDeLaVisita, mensuales) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T3.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var sender = sp.GetRequiredService<ISender>();
            var visita = await db.Visitas.Select(v => new { v.Id, v.CentroId }).SingleAsync();

            return (
                (await sender.Send(new CaeManager.Application.Centros.Queries.ObtenerCentros.ObtenerCentrosQuery(null, null, TamanoPagina: 1000))).Elementos,
                await db.Asignaciones.Where(a => a.FechaBaja == null).Select(a => new { a.CentroId, a.TrabajadorId }).ToListAsync(),
                await db.TiposDocumentoCentros.Select(f => new { f.CentroId, f.TipoDocumentoId, f.Incluido }).ToListAsync(),
                await db.TiposDocumento.Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
                    .Select(t => new { t.Id, t.Nombre, PorDefecto = t.Requerido == RequisitoDocumental.Si }).ToListAsync(),
                await sender.Send(new CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita.ObtenerDocumentacionVisitaQuery(visita.Id)),
                visita.CentroId,
                await db.TiposDocumento.Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa && CertificadosMensuales.Contains(t.Nombre))
                    .Select(t => new { t.Id, t.Nombre }).ToListAsync());
        });

        mensuales.Select(t => t.Nombre).Should().BeEquivalentTo(CertificadosMensuales, "control: el instrumento sabe cuáles son los dos Tipos mensuales de Empresa");

        string NombreDelTipo(Guid? id) => tipos.SingleOrDefault(t => t.Id == id)?.Nombre ?? "(tipo de otro ámbito)";
        string[] loQueExigeCadaCentro = [CatalogoPilotoOutbound.AptitudMedica, CatalogoPilotoOutbound.FormacionArt19];

        asignaciones.Select(a => a.TrabajadorId).Should().HaveCount(10).And.OnlyHaveUniqueItems("MEDIDO: un Centro por Trabajador");
        centros.Should().HaveCount(4);

        var trabajadoresPorCentro = new List<int>();
        foreach (var centro in centros.OrderBy(c => c.CodigoCentro, StringComparer.Ordinal))
        {
            var suyos = asignaciones.Where(a => a.CentroId == centro.Id).Select(a => a.TrabajadorId).ToHashSet();
            var excluidos = filas.Where(f => f.CentroId == centro.Id && !f.Incluido).Select(f => f.TipoDocumentoId).ToHashSet();
            var exigidos = tipos.Where(t => t.PorDefecto && !excluidos.Contains(t.Id)).Select(t => t.Id)
                .Concat(filas.Where(f => f.CentroId == centro.Id && f.Incluido).Select(f => f.TipoDocumentoId)).ToHashSet();
            var incidencias = centro.Recuentos.Vencidas.Concat(centro.Recuentos.Proximas).ToList();

            salida.WriteLine(
                $"MEDIDO {centro.CodigoCentro} «{centro.Nombre}»: {centro.CumplimientoPorcentaje} % {centro.Estado}; Trabajadores {suyos.Count}; " +
                $"TotalVencidas {centro.Recuentos.TotalVencidas}; TotalProximas {centro.Recuentos.TotalProximas}; incidencias " +
                $"[{string.Join("; ", incidencias.Select(i => $"{i.Estado} · {NombreDelTipo(i.TipoDocumentoId)} · {i.Ambito} · suyo={i.TrabajadorId is { } t && suyos.Contains(t)}"))}]");

            exigidos.Select(id => NombreDelTipo(id)).Should().BeEquivalentTo(loQueExigeCadaCentro, "control: el instrumento sabe qué exige el Centro");
            centro.CumplimientoPorcentaje.Should().Be(50);
            centro.Estado.Should().Be(EstadoCentro.Vencido);
            centro.Recuentos.TotalVencidas.Should().Be(suyos.Count, "MEDIDO: un vencido por Trabajador del Centro, y ninguno más");
            // Lo único que un Centro enseña sin ser de sus Trabajadores es de su Empresa: los dos certificados mensuales,
            // «Próximo», que llegan a todos los Centros de la Empresa propia.
            var deLaEmpresa = incidencias.Where(i => i.Ambito == CaeManager.Application.Centros.AmbitoCausa.Empresa).ToList();
            deLaEmpresa.Should().HaveCount(2).And.OnlyContain(i => i.Estado == EstadoDocumento.Proximo && i.TrabajadorId == null);
            deLaEmpresa.Select(i => i.TipoDocumentoId).Should().BeEquivalentTo(
                mensuales.Select(t => (Guid?)t.Id), "MEDIDO: las dos incidencias de Empresa son las de los dos certificados mensuales, una de cada Tipo");
            centro.Recuentos.Proximas.Should().BeEquivalentTo(deLaEmpresa);
            incidencias.Except(deLaEmpresa).Should().OnlyContain(
                i => i.TipoDocumentoId != null && exigidos.Contains(i.TipoDocumentoId.Value) && i.TrabajadorId != null && suyos.Contains(i.TrabajadorId.Value),
                "MEDIDO: toda incidencia de Trabajador es de un tipo que ESE Centro exige y de un Trabajador asignado a ESE Centro");
            trabajadoresPorCentro.Add(suyos.Count);
        }

        trabajadoresPorCentro.Should().Equal(3, 3, 2, 2);

        // La comprobación previa de la Visita (lo que la pantalla de Visitas consulta de quienes acuden).
        previa.Should().NotBeNull();
        var delCentroDeLaVisita = asignaciones.Where(a => a.CentroId == centroDeLaVisita).Select(a => a.TrabajadorId).ToList();
        previa!.Trabajadores.Select(t => t.TrabajadorId).Should().BeEquivalentTo(delCentroDeLaVisita).And.HaveCount(3);
        foreach (var trabajador in previa.Trabajadores)
        {
            var documentos = trabajador.Documentacion.Documentos;
            salida.WriteLine(
                $"MEDIDO comprobación previa · {trabajador.NombreCompleto}: peor estado {trabajador.Documentacion.PeorEstado}; " +
                $"[{string.Join("; ", documentos.Select(x => $"{x.Estado} · {x.TipoDocumentoNombre}"))}]");

            documentos.Should().OnlyContain(x => loQueExigeCadaCentro.Contains(x.TipoDocumentoNombre) && x.TrabajadorId == trabajador.TrabajadorId);
            documentos.Select(x => $"{x.Estado} · {x.TipoDocumentoNombre}").Should().BeEquivalentTo(
                [$"Vencido · {CatalogoPilotoOutbound.FormacionArt19}", $"Vigente · {CatalogoPilotoOutbound.AptitudMedica}"]);
            trabajador.Documentacion.PeorEstado.Should().Be(EstadoDocumento.Vencido);
        }

        salida.WriteLine(
            $"MEDIDO comprobación previa · Empresa propia: peor estado {previa.Empresa.PeorEstado}; {previa.Empresa.Documentos.Count} filas " +
            $"[{string.Join("; ", previa.Empresa.Documentos.GroupBy(x => x.Estado).Select(g => $"{g.Key} {g.Count()}"))}]");
    }

    [Fact]
    public void T4_sin_documentos_da_cero_en_Centros_Empresa_Inicio_y_Vision_de_cartera()
    {
        var m = fixture.Informe.De(T4);

        m.Centros.Should().HaveCount(3).And.OnlyContain(c => c.Estado == EstadoCentro.Faltante && c.Cumplimiento == 0);
        m.EmpresaCumplimiento.Should().Be(0);
        (m.ParesExigidos, m.ParesFaltantes, m.Documentos).Should().Be((19, 19, 0));
        m.MiTrabajoFilas.Should().Be(19);

        // Sin documentos, Inicio y Visión de cartera cuentan los pares exigidos: 0 de 19, como Centros y Empresas. El
        // número vive en UN sitio.
        CatalogoPilotoOutbound.CumplimientoDeInicioYVisionDeCarteraEnT4.Should().Be(0, "decisión del 2026-10-09: sin documentos y con pares exigidos, Inicio no da 100 %");
        m.InicioCumplimiento.Should().Be(0);
        m.VisionCarteraCumplimiento.Should().Be(0);
        (m.VisionCarteraPresente, m.VisionCarteraSinCartera, m.VisionCarteraSinDatos).Should().Be(
            (true, false, false), "la fila pinta 0 %, no «Sin cartera» ni «Sin datos»: hay diecinueve pares exigidos");

        var advertencias = PilotoOutboundAutoverificacion.Advertencias(fixture.Informe);
        advertencias.Should().HaveCount(2, "T5 y T6 declaran que Inicio y Visión de cartera no dan la cifra de Empresas; T4 ya coincide");
        advertencias.Should().NotContain(a => a.StartsWith($"T4 «{CatalogoPilotoOutbound.NombreTenantT4}»"));
    }

    [Fact]
    public void La_autoverificacion_exige_en_T4_la_constante_de_Inicio_y_Vision_de_cartera_y_avisa_solo_si_diverge()
    {
        var prefijo = $"T4 «{CatalogoPilotoOutbound.NombreTenantT4}» · ";
        Informe ConT4(Func<PilotoOutboundAutoverificacion.MedicionTenant, PilotoOutboundAutoverificacion.MedicionTenant> cambio) =>
            fixture.Informe with { Tenants = [.. fixture.Informe.Tenants.Select(t => t.Clave == T4.Clave ? cambio(t) : t)] };

        // Si Inicio volviera a dar el 100 % de «ningún documento», la autoverificación lo dice, con el contador.
        PilotoOutboundAutoverificacion.Discrepancias(ConT4(t => t with { InicioCumplimiento = 100 }))
            .Should().Equal(prefijo + "Inicio · % de cumplimiento: medido 100, esperado 0.");
        PilotoOutboundAutoverificacion.Discrepancias(ConT4(t => t with { VisionCarteraCumplimiento = 100 }))
            .Should().Equal(prefijo + "Visión de cartera · % de cumplimiento: medido 100, esperado 0.");

        // La advertencia sale de comparar los esperados del catálogo, no de un texto fijo.
        var esperadoHoy = CatalogoPilotoOutbound.Esperado(T4)!;
        esperadoHoy.InicioOVisionDeCarteraDivergenDeEmpresa.Should().BeFalse("con la constante a 0, igual que Empresas, no hay divergencia que declarar");
        (esperadoHoy with { CumplimientoInicio = 100, CumplimientoVisionCartera = 100 }).InicioOVisionDeCarteraDivergenDeEmpresa
            .Should().BeTrue("control: con el 100 % antiguo sí divergía de Empresas");
        CatalogoPilotoOutbound.Esperado(T2)!.InicioOVisionDeCarteraDivergenDeEmpresa.Should().BeFalse("control: donde las tres cifras coinciden no se avisa");
    }

    [Fact]
    public void T5_cuatro_Trabajadores_en_catorce_Centros_dan_los_contadores_de_la_matriz()
    {
        var m = fixture.Informe.De(T5);

        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be(
            (3, 1, 0, 0), "MEDIDO: dos Trabajadores bloqueados en el Centro de la periodicidad especial, un vencido y un urgente");
        m.MiTrabajoAlcanceCero.Should().BeFalse();
        m.InicioCumplimiento.Should().Be(95, "MEDIDO: 19 de 20 documentos al día");
        m.VisionCarteraCumplimiento.Should().Be(95);
        m.EmpresaCumplimiento.Should().Be(96, "MEDIDO: 212 de 220 pares; el vencido cuenta en los ocho Centros de su Trabajador");
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((1, 1, 0, 0));
        m.InicioTrabajadoresBloqueados.Should().Be(2);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((220, 0), "MEDIDO: 14 + 12 + 10 + 8 Asignaciones activas × cinco tipos");
        (m.Documentos, m.DocumentosSinPdf).Should().Be((33, 0), "MEDIDO: cuatro Trabajadores × cinco tipos y los 13 de la Empresa propia");
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto).Should().Be((6, 0));

        m.Centros.Should().HaveCount(14);
        m.Centros.Count(c => c is { Estado: EstadoCentro.Vencido, Cumplimiento: 95 }).Should().Be(8, "MEDIDO: los ocho Centros del Trabajador con el vencido");
        m.Centros.Count(c => c is { Estado: EstadoCentro.Urgente, Cumplimiento: 100 }).Should().Be(2);
        // Los cuatro sin nada pendiente de sus Trabajadores: los tiñen los dos certificados mensuales de la Empresa propia.
        m.Centros.Count(c => c is { Estado: EstadoCentro.Proximo, Cumplimiento: 100 }).Should().Be(4);
        salida.WriteLine($"MEDIDO T5: {PilotoOutboundTextos.CentrosPorEstado(m)}");
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
            (13, 4, 2, 0), "MEDIDO: 5 vencidos + 7 «Falta» + 1 requisito bloqueante pendiente; 4 urgentes; 2 próximos; la Visita no añade filas");
        m.MiTrabajoAlcanceCero.Should().BeFalse();
        m.InicioCumplimiento.Should().Be(96, "MEDIDO: 112 de 117 documentos de Trabajador al día");
        m.VisionCarteraCumplimiento.Should().Be(96);
        m.EmpresaCumplimiento.Should().Be(92, "MEDIDO: 145 de 158 pares");
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((5, 4, 2, 0), "MEDIDO: hay Vencido, Urgente y Próximo");
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((158, 7), "MEDIDO: 31 Asignaciones activas; el Centro del desplazamiento exige seis tipos y los demás cinco; siete pares sin documento");
        (m.InicioTrabajadoresBloqueados, m.InicioVisitasUrgentes).Should().Be((1, 0), "MEDIDO: uno de los Faltantes es de un requisito que bloquea el acceso; la Visita queda a más de 48 horas");
        (m.Documentos, m.DocumentosSinPdf).Should().Be(
            (136, 0), "MEDIDO: 117 de Trabajador, tres por cada una de las dos subcontratas y los 13 de la Empresa propia");
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto).Should().Be((9, 0), "MEDIDO: las dos subcontratas no cuentan como Clientes empresariales");
    }

    [Fact]
    public void T6_el_trabajo_pendiente_es_desigual_entre_las_tres_zonas()
    {
        var m = fixture.Informe.De(T6);
        List<(EstadoCentro, int?)> De(ZonaPilotoOutbound zona) =>
            [.. T6.CentrosDe(zona).Select(c => m.Centros.Single(x => x.Nombre == c.Nombre)).Select(c => (c.Estado, c.Cumplimiento))];

        // Barcelona, la peor: ningún Centro al 100 %. El primero es el del desplazamiento: 15 de 18 pares, con dos
        // documentos vencidos y uno que falta, así que está Vencido (Vencido precede a Faltante).
        De(DisenoT6PilotoOutbound.Barcelona).Should().Equal(
            (EstadoCentro.Vencido, 83), (EstadoCentro.Vencido, 80), (EstadoCentro.Faltante, 80), (EstadoCentro.Faltante, 87));
        // Madrid, intermedia: dos Centros con pares incumplidos y dos solo con avisos.
        De(DisenoT6PilotoOutbound.Madrid).Should().Equal(
            (EstadoCentro.Vencido, 90), (EstadoCentro.Urgente, 100), (EstadoCentro.Urgente, 100), (EstadoCentro.Faltante, 87));
        // Santander, casi limpia: los cuatro al 100 %, y solo dos avisos de sus Trabajadores. Los dos Centros sin
        // ninguno quedan «Próximo» por los dos certificados mensuales de la Empresa propia.
        De(DisenoT6PilotoOutbound.Santander).Should().Equal(
            (EstadoCentro.Proximo, 100), (EstadoCentro.Proximo, 100), (EstadoCentro.Urgente, 100), (EstadoCentro.Proximo, 100));
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
                [$"{codigo}-01", $"{codigo}-02", $"{codigo}-03", $"{codigo}-04"], $"MEDIDO: los cuatro Centros de {zona} llevan su zona en el código, sin la clave del Tenant");
        }

        cargosDeLaEmpresaPropia.Where(c => c!.StartsWith("Coordinación documental", StringComparison.Ordinal)).Should().BeEquivalentTo(
            ["Coordinación documental — Barcelona", "Coordinación documental — Madrid", "Coordinación documental — Santander"]);
        cargosDeLaEmpresaPropia.Should().HaveCount(5, "MEDIDO: los dos contactos de toda Empresa propia y los tres de zona");
        contactosDeCentro.Should().HaveCount(12).And.NotContain(
            c => c!.StartsWith("Coordinación documental", StringComparison.Ordinal), "MEDIDO: el contacto interno no se duplica en la agenda de cada Centro");
    }

    [Fact]
    public async Task T6_un_Trabajador_de_Madrid_esta_desplazado_a_un_Centro_de_Barcelona_con_Asignacion_activa_y_Visita_con_fechas_y_le_falta_lo_que_solo_pide_ese_Centro()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        var destino = T6.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[0].Nombre;

        var (trabajadores, asignaciones, visitas, enLaVisita, desplazadoId, leFalta) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T6.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var asignacionesConCentro = await (from a in db.Asignaciones
                                               join c in db.Centros on a.CentroId equals c.Id
                                               select new { a.TrabajadorId, Centro = c.Nombre, a.FechaAlta, a.FechaBaja }).ToListAsync();
            var visitasConCentro = await (from v in db.Visitas
                                          join c in db.Centros on v.CentroId equals c.Id
                                          select new { v.Id, Centro = c.Nombre, v.FechaInicio, v.FechaFin }).ToListAsync();

            // El desplazado es el único con Asignaciones en Centros de dos zonas (la zona es lo que precede a « · »).
            var enDosZonas = asignacionesConCentro.GroupBy(a => a.TrabajadorId)
                .Where(g => g.Select(a => a.Centro.Split(" · ")[0]).Distinct().Count() > 1).Select(g => g.Key).ToList();
            enDosZonas.Should().ContainSingle("MEDIDO: solo un Trabajador trabaja en dos zonas");

            var porCentro = await sp.GetRequiredService<ISender>().Send(
                new CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador.ObtenerDocumentacionPorCentroDeTrabajadorQuery(enDosZonas[0]));

            return (
                await db.Trabajadores.CountAsync(), asignacionesConCentro, visitasConCentro,
                await db.VisitasTrabajadores.Select(x => new { x.VisitaId, x.TrabajadorId }).ToListAsync(), enDosZonas[0],
                porCentro.ToDictionary(
                    c => c.CentroNombre,
                    c => c.Documentos.Where(x => x.Estado == EstadoDocumento.Faltante).Select(x => x.TipoDocumentoNombre).ToList()));
        });

        // Un solo Trabajador, no una copia: siguen siendo 24, y todas las Asignaciones están activas.
        trabajadores.Should().Be(24, "MEDIDO: ocho por zona; el desplazado no es un Trabajador más");
        asignaciones.Select(a => a.TrabajadorId).Distinct().Should().HaveCount(24);
        asignaciones.Should().HaveCount(31).And.OnlyContain(a => a.FechaBaja == null, "MEDIDO: una Asignación con fecha de baja sería inerte para el modelo");

        // Activo en las dos zonas: sus dos Centros de Madrid y, desde D−10, el de Barcelona.
        var delDesplazado = asignaciones.Where(a => a.TrabajadorId == desplazadoId).ToList();
        delDesplazado.Select(a => a.Centro.Split(" · ")[0]).Should().BeEquivalentTo(["Madrid", "Madrid", "Barcelona"]);
        delDesplazado.Single(a => a.Centro == destino).FechaAlta.Should().Be(d.AddDays(-10));

        // La temporalidad la lleva la Visita: con fechas, en el Centro de destino, con él, y a más de 48 horas de D.
        var visita = visitas.Should().ContainSingle().Subject;
        (visita.Centro, visita.FechaInicio, visita.FechaFin).Should().Be((destino, d.AddDays(10), d.AddDays(20)));
        enLaVisita.Should().ContainSingle().Which.Should().Be(new { VisitaId = visita.Id, TrabajadorId = desplazadoId });

        // Le falta exactamente lo que ese Centro pide y los de Madrid no.
        leFalta.Should().HaveCount(3);
        leFalta[destino].Should().Equal(CatalogoPilotoOutbound.CarretillasElevadoras);
        leFalta.Where(p => p.Key != destino).Should().OnlyContain(p => p.Value.Count == 0);
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
    public async Task Ningun_nombre_completo_de_Trabajador_se_repite_en_los_seis_Tenants()
    {
        var escritos = new List<(string Tenant, string NombreCompleto)>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var delTenant = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
                await db.Trabajadores.Select(t => t.Nombre + " " + t.Apellidos).ToListAsync());

            // Los que la base enseña son de los que el catálogo reparte: los de las subcontratas de T1 también.
            var delCatalogo = Enumerable.Range(0, CatalogoPilotoOutbound.TrabajadoresSembrados(tenant))
                .Select(i => IdentidadesPilotoOutbound.Trabajador(tenant, i)).Select(p => $"{p.Nombre} {p.Apellidos}").ToList();
            delTenant.Should().NotBeEmpty().And.BeSubsetOf(delCatalogo, $"MEDIDO: los Trabajadores de {tenant.Clave} llevan los nombres del catálogo");

            escritos.AddRange(delTenant.Select(n => (tenant.Clave, n)));
        }

        escritos.GroupBy(e => e.NombreCompleto).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Tenant))}")
            .Should().BeEmpty("MEDIDO: ningún nombre completo se repite entre los Trabajadores de los seis Tenants");
    }

    [Fact]
    public void Mi_trabajo_suma_entre_T2_y_T6_un_volumen_de_demostracion()
    {
        var filas = new[] { T2, T3, T4, T5, T6 }.ToDictionary(t => t.Clave, t => fixture.Informe.De(t).MiTrabajoFilas);
        salida.WriteLine($"MEDIDO filas de Mi trabajo: {string.Join(", ", filas.Select(p => $"{p.Key} {p.Value}"))}; suma {filas.Values.Sum()}");

        filas.Should().Equal(new Dictionary<string, int> { ["T2"] = 0, ["T3"] = 10, ["T4"] = 19, ["T5"] = 4, ["T6"] = 19 });
        filas.Values.Sum().Should().Be(52);
        filas.Values.Sum().Should().BeInRange(40, 80, "MEDIDO: ni una cola vacía ni una que no se pueda recorrer en la demostración");
    }

    [Fact]
    public async Task La_siembra_no_envia_ni_deja_encolado_ningun_correo()
    {
        fixture.Arnes.Correo.Intentos.Should().BeEmpty("MEDIDO: ni la siembra ni la medición llaman a IEmailService");

        using (var ambito = fixture.Arnes.Servicios.CreateScope())
            ambito.ServiceProvider.GetRequiredService<IEmailService>().Should().BeSameAs(
                fixture.Arnes.Correo, "control positivo: el servicio de correo que resuelve la aplicación es el espía");

        // Lo que deja en la base cualquier envío o aviso de la aplicación (no hay cola de salida de correo),
        // en T1 por un lado y en los demás Tenants del piloto por otro.
        async Task<Dictionary<string, int>> RastrosAsync(IReadOnlyCollection<string> nombres) =>
            await fixture.Arnes.ComoBootstrapAsync(async b =>
            {
                var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
                return new Dictionary<string, int>
                {
                    ["Documentos (control positivo)"] = await b.Documentos.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                    ["Reclamaciones documentales"] = await b.ReclamacionesDocumentales.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                    ["Conversaciones"] = await b.Conversaciones.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                    ["Mensajes"] = await b.Mensajes.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                    ["Mensajes con estado de entrega"] = await b.Mensajes.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId) && e.EstadoEntrega != null),
                    ["Eventos de conversación"] = await b.EventosConversacion.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                    ["Notificaciones a usuarios"] = await b.NotificacionesUsuario.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                };
            });

        var rastros = await RastrosAsync([.. CatalogoPilotoOutbound.NombresTenants.Where(n => n != T1.Nombre)]);
        salida.WriteLine("MEDIDO rastros de envío fuera de T1: " + string.Join(", ", rastros.Select(p => $"{p.Key} {p.Value}")));
        rastros["Documentos (control positivo)"].Should().Be(279, "control positivo: el recuento ve las filas de los Tenants del piloto");
        rastros.Where(p => !p.Key.StartsWith("Documentos", StringComparison.Ordinal)).Should().OnlyContain(p => p.Value == 0);

        // T1 lleva UNA reclamación como registro histórico: su fila, su hilo con el mensaje que salió y su entrada en la
        // cronología. Es lo que queda después de un envío, sembrado sin enviar: el espía de arriba sigue vacío, el
        // mensaje no tiene estado de entrega que un proceso pudiera recoger y no hay ninguna notificación.
        var rastrosDeT1 = await RastrosAsync([T1.Nombre]);
        salida.WriteLine("MEDIDO rastros de envío en T1: " + string.Join(", ", rastrosDeT1.Select(p => $"{p.Key} {p.Value}")));
        rastrosDeT1.Should().Equal(new Dictionary<string, int>
        {
            ["Documentos (control positivo)"] = 885,
            ["Reclamaciones documentales"] = 1,
            ["Conversaciones"] = 1,
            ["Mensajes"] = 1,
            ["Mensajes con estado de entrega"] = 0,
            ["Eventos de conversación"] = 1,
            ["Notificaciones a usuarios"] = 0,
        });
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
            eseDia.Tenants.Where(t => t.Clave != T1.Clave).Should().BeEquivalentTo(
                fixture.Informe.Tenants.Where(t => t.Clave != T1.Clave), $"MEDIDO: lo que se ensaya el día D−5 es lo que se ve el día {dia:yyyy-MM-dd}");

            // T1 tiene lo ÚNICO que depende del día: su Visita por correo empieza en D+1, así que desde D−1 es urgente
            // (una actuación más en Mi trabajo y una Visita urgente en Inicio). Todo lo demás de T1 no se mueve.
            var (ensayo, eseDiaT1) = (fixture.Informe.De(T1), eseDia.De(T1));
            var yaEsUrgente = dia >= fecha.AddDays(-1);
            salida.WriteLine(
                $"MEDIDO T1 el día {dia:yyyy-MM-dd}: actuaciones {eseDiaT1.MiTrabajoActuaciones}, Visitas urgentes {eseDiaT1.InicioVisitasUrgentes}, " +
                $"días hasta la Visita por correo {eseDiaT1.Grande!.Paquete!.DiasDesdeHoy}");
            (eseDiaT1.MiTrabajoActuaciones - ensayo.MiTrabajoActuaciones, eseDiaT1.InicioVisitasUrgentes, eseDiaT1.Grande.Paquete.DiasDesdeHoy)
                .Should().Be(yaEsUrgente ? (1, 1, 1) : (0, 0, 10));
            (eseDiaT1 with
            {
                MiTrabajoActuaciones = ensayo.MiTrabajoActuaciones,
                InicioVisitasUrgentes = ensayo.InicioVisitasUrgentes,
                Grande = eseDiaT1.Grande with { Paquete = eseDiaT1.Grande.Paquete with { DiasDesdeHoy = ensayo.Grande!.Paquete!.DiasDesdeHoy } }
            }).Should().BeEquivalentTo(ensayo, $"MEDIDO: salvo la urgencia de su Visita, T1 es el mismo el día {dia:yyyy-MM-dd}");
        }
    }

    [Fact]
    public void T1_tiene_la_estructura_los_porcentajes_y_el_volumen_del_Tenant_grande()
    {
        CatalogoPilotoOutbound.Tenants.Should().NotContain(
            t => t.Escenario == EscenarioPilotoOutbound.Esqueleto, "MEDIDO: ya no queda ningún Tenant declarado sin sembrar");

        var m = fixture.Informe.De(T1);
        var g = m.Grande!;

        (g.TrabajadoresPropios, g.TrabajadoresDeSubcontrata, g.Subcontratas).Should().Be((150, 25, 3));
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto, m.Centros.Count).Should().Be((12, 0, 28));
        (m.Documentos, m.DocumentosSinPdf).Should().Be(
            (885, 0), "MEDIDO: 854 de Trabajador (175 × 5 menos los 21 que faltan), 14 de la Empresa propia, 8 de los dos Vehículos y 9 de las tres subcontratas");
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((965, 21), "MEDIDO: 193 Asignaciones activas × cinco tipos");
        (m.MiTrabajoPresente, m.MiTrabajoAlcanceCero, m.MiTrabajoNoConsultado).Should().Be((true, false, false));
        m.ContactosDeAgenda.Should().BeGreaterThan(40, "MEDIDO: la Empresa propia, cada Cliente empresarial, cada Centro y cada subcontrata tienen a quién escribir");
        m.ContactosFueraDeLaReglaDeCorreo.Should().Be(0);

        // Lo que el diseño fija como proporción, no como número: se mide, se imprime y se exige dentro de su intervalo.
        salida.WriteLine(
            $"MEDIDO T1: Empresa {m.EmpresaCumplimiento} %, Inicio {m.InicioCumplimiento} %, Visión de cartera {m.VisionCarteraCumplimiento} %, " +
            $"Mi trabajo {m.MiTrabajoFilas} filas (bloqueos {m.MiTrabajoBloqueos}, actuaciones {m.MiTrabajoActuaciones}, próximos {m.MiTrabajoProximos}, " +
            $"seguimiento {m.MiTrabajoSeguimiento}), Centros bloqueados {m.InicioCentrosBloqueados}, Trabajadores bloqueados {m.InicioTrabajadoresBloqueados}, " +
            $"Centros por estado [{string.Join("; ", m.Centros.GroupBy(c => c.Estado).Select(e => $"{e.Key} {e.Count()}"))}], " +
            $"Clientes empresariales con alertas {m.ClientesEmpresarialesConAlertas}");
        m.EmpresaCumplimiento.Should().BeInRange(82, 88, "MEDIDO: el objetivo es un 85 % de pares conformes en la Empresa propia");
        m.InicioCumplimiento.Should().BeInRange(87, 91, "MEDIDO: Inicio cuenta documentos de Trabajador al día, no pares, y no ve los que faltan");
        m.VisionCarteraCumplimiento.Should().BeInRange(87, 91, "MEDIDO: Visión de cartera da la tasa de Inicio");
        m.MiTrabajoFilas.Should().BeInRange(100, 250, "MEDIDO: una cola que hay que priorizar, pero que se puede recorrer");
        m.InicioCentrosBloqueados.Should().BeGreaterThanOrEqualTo(2, "MEDIDO: el de la acreditación Rechazada y el de la vencida en plataforma");
        m.InicioTrabajadoresBloqueados.Should().BeGreaterThanOrEqualTo(3, "MEDIDO: dos con el requisito vencido en un Centro y uno al que le falta en otro");
    }

    [Fact]
    public void T1_tiene_al_menos_una_vez_cada_caso_de_estado_de_la_matriz()
    {
        var casos = fixture.Informe.De(T1).Grande!.CasosDeEstado;
        foreach (var (caso, veces) in casos)
            salida.WriteLine($"MEDIDO T1 · {caso}: {veces}");

        // La lista de § 3.1 de la matriz, escrita a mano.
        casos.Keys.Should().BeEquivalentTo(
        [
            "Documento Vencido", "Documento Urgente", "Documento Próximo", "Documento Sin confirmar", "Documento Sin caducidad",
            "Documento En tolerancia", "Faltante bloqueante", "Faltante no bloqueante",
            "Acreditación externa Pendiente de subir", "Acreditación externa Subida", "Acreditación externa Aceptada",
            "Acreditación externa Rechazada", "Acreditación externa vencida en plataforma",
            "Trabajador bloqueado en un Centro y no en otro", "Trabajador de baja", "Documento de Empresa vencido en la Empresa propia",
            "Subcontrata con documentación propia", "Vehículo con documento vencido", "Visita a menos de 48 horas de la demostración",
            "Gestión pendiente", "Reclamación enviada sin respuesta"
        ]);
        casos.Should().OnlyContain(c => c.Value >= 1);

        // Los que el diseño siembra un número cerrado de veces.
        casos["Acreditación externa Rechazada"].Should().Be(1);
        casos["Acreditación externa vencida en plataforma"].Should().Be(1);
        casos["Documento de Empresa vencido en la Empresa propia"].Should().Be(1, "MEDIDO: exactamente uno, de un tipo que no se exige por defecto");
        // MEDIDO: 7. Eran 2 (la aptitud vencida en Illescas) hasta que el Pendiente en la plataforma bloquea por Centro
        // (2026-10-10): los otros 5 son Trabajadores con un documento sin subir o sin validar en la plataforma de uno de sus
        // Centros y no en otro.
        casos["Trabajador bloqueado en un Centro y no en otro"].Should().Be(7);
        casos["Trabajador de baja"].Should().Be(6);
        casos["Subcontrata con documentación propia"].Should().Be(3);
        casos["Vehículo con documento vencido"].Should().Be(1);
        casos["Visita a menos de 48 horas de la demostración"].Should().Be(1);
        casos["Gestión pendiente"].Should().Be(4);
        casos["Reclamación enviada sin respuesta"].Should().Be(1);
    }

    /// <summary>
    /// Tres casos de estado buscados con la consulta de su pantalla, como la Gestora
    /// CAE: la evaluación de acceso que usan Inicio y el estado de los Centros, la
    /// documentación por Centro de la ficha del Trabajador y el listado de
    /// reclamaciones sin respuesta.
    /// </summary>
    [Fact]
    public async Task T1_el_bloqueo_por_Centro_la_tolerancia_y_la_reclamacion_sin_respuesta_salen_en_sus_pantallas()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        const string conBloqueoPorVencido = "Plataforma logística de Illescas";
        const string elOtroCentroDeLosBloqueados = "Planta de estampación de Martorell";
        const string conBloqueoPorAusente = "Plataforma de cruce de Valladolid";
        const string conTolerancia = "Almacén de expediciones de Tudela";

        var (sinCumplir, aptitudPorCentro, formacionPorCentro, sinRespuesta) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T1.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var sender = sp.GetRequiredService<ISender>();
            var hoy = DiaDeNegocio.Hoy();
            var nombreDelCentro = await db.Centros.ToDictionaryAsync(c => c.Id, c => c.Nombre);
            var nombreDelTipo = await db.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var aptitudId = nombreDelTipo.Single(t => t.Value == CatalogoPilotoOutbound.AptitudMedica).Key;
            var formacionId = nombreDelTipo.Single(t => t.Value == CatalogoPilotoOutbound.FormacionArt19).Key;

            var evaluacion = await sp.GetRequiredService<CaeManager.Application.Centros.IEvaluacionDeAccesoPorCentroService>()
                .EvaluarAsync(null, CancellationToken.None);
            // Solo los requisitos que el Centro marca «bloquea el acceso»: el Pendiente en la plataforma (2026-10-10) también
            // bloquea, pero es otro caso y se mide aparte, abajo.
            var sinCumplir = evaluacion.Requisitos
                .Where(r => r.Resultado.Situacion is not (SituacionDeRequisitoBloqueante.Cumplido or SituacionDeRequisitoBloqueante.PendienteEnPlataforma))
                .Select(r => (r.TrabajadorId, Centro: nombreDelCentro[r.CentroId], Tipo: nombreDelTipo[r.TipoDocumentoId], r.Resultado.Situacion))
                .ToList();
            var centrosConPendiente = evaluacion.Requisitos
                .Where(r => r.Resultado.Situacion == SituacionDeRequisitoBloqueante.PendienteEnPlataforma)
                .Select(r => r.CentroId).ToHashSet();
            var centrosConPlataforma = await db.CanalesGestionDocumental
                .Where(c => c.Tipo == TipoCanalGestion.Plataforma).Select(c => c.CentroId).ToListAsync();
            centrosConPendiente.Should().NotBeEmpty("MEDIDO: T1 siembra acreditaciones sin subir y subidas sin validar");
            centrosConPendiente.Should().BeSubsetOf(centrosConPlataforma, "solo un Centro con plataforma tiene Pendiente en la plataforma");

            // La ficha de cada Trabajador al que la aptitud le bloquea un Centro: su aptitud, Centro a Centro.
            var aptitudPorCentro = new Dictionary<Guid, Dictionary<string, EstadoDocumento>>();
            foreach (var trabajadorId in sinCumplir.Where(r => r.Tipo == CatalogoPilotoOutbound.AptitudMedica).Select(r => r.TrabajadorId).Distinct())
            {
                var ficha = await sender.Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId));
                aptitudPorCentro[trabajadorId] = ficha.ToDictionary(c => c.CentroNombre, c => c.Documentos.Single(x => x.TipoDocumentoId == aptitudId).Estado);
            }

            // El Trabajador del Centro con tolerancia cuya formación está vencida, y cómo la pinta su ficha.
            var conLaFormacionVencida = await (
                from a in db.Asignaciones
                join c in db.Centros on a.CentroId equals c.Id
                join x in db.Documentos on (Guid?)a.TrabajadorId equals x.TrabajadorId
                where a.FechaBaja == null && c.Nombre == conTolerancia && x.TipoDocumentoId == formacionId && x.FechaVencimiento < hoy
                select a.TrabajadorId).SingleAsync();
            var fichaDelVencido = await sender.Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(conLaFormacionVencida));
            var formacionPorCentro = fichaDelVencido.ToDictionary(c => c.CentroNombre, c => c.Documentos.Single(x => x.TipoDocumentoId == formacionId));

            return (sinCumplir, aptitudPorCentro, formacionPorCentro, await sender.Send(new ObtenerReclamacionesSinRespuestaQuery()));
        });

        foreach (var r in sinCumplir)
            salida.WriteLine($"MEDIDO T1 requisito que bloquea sin cumplir: {r.Tipo} en «{r.Centro}», {r.Situacion}");

        // Bloqueado en un Centro y no en el otro: la aptitud vencida solo bloquea donde el Centro la exige para acceder.
        var porAptitud = sinCumplir.Where(r => r.Tipo == CatalogoPilotoOutbound.AptitudMedica).ToList();
        porAptitud.Should().HaveCount(2).And.OnlyContain(r => r.Centro == conBloqueoPorVencido && r.Situacion == SituacionDeRequisitoBloqueante.Vencido);
        aptitudPorCentro.Should().HaveCount(2);
        foreach (var (trabajadorId, estados) in aptitudPorCentro)
        {
            estados.Should().Equal(
                new Dictionary<string, EstadoDocumento> { [conBloqueoPorVencido] = EstadoDocumento.Vencido, [elOtroCentroDeLosBloqueados] = EstadoDocumento.Vencido },
                "MEDIDO: el mismo documento vencido se ve en sus dos Centros");
            sinCumplir.Should().NotContain(
                r => r.TrabajadorId == trabajadorId && r.Centro == elOtroCentroDeLosBloqueados, "MEDIDO: en su otro Centro ese documento no bloquea el acceso");
        }

        // Faltante bloqueante: un documento que falta, en el Centro que lo exige para acceder.
        sinCumplir.Should().Contain(
            r => r.Centro == conBloqueoPorAusente && r.Tipo == CatalogoPilotoOutbound.DocumentoIdentidad && r.Situacion == SituacionDeRequisitoBloqueante.Ausente);

        // En tolerancia: venció en D−12 y su Centro concede 30 días.
        var enTolerancia = formacionPorCentro.Should().ContainSingle().Subject;
        (enTolerancia.Key, enTolerancia.Value.Estado).Should().Be((conTolerancia, EstadoDocumento.EnTolerancia));
        enTolerancia.Value.FechaVencimiento.Should().Be(d.AddDays(-12));
        enTolerancia.Value.EnToleranciaHasta.Should().Be(d.AddDays(18));

        // La reclamación: enviada hace veinte días a la Empresa propia por su documento vencido, y sin respuesta.
        var reclamacion = sinRespuesta.Should().ContainSingle().Subject;
        salida.WriteLine($"MEDIDO T1 reclamación sin respuesta: a «{reclamacion.RazonSocialTitular}», enviada el {reclamacion.FechaEnvioUtc:yyyy-MM-dd}, {reclamacion.DiasTranscurridos} días, {reclamacion.TotalDocumentos} documento");
        (reclamacion.RazonSocialTitular, reclamacion.AmbitoTitular, reclamacion.TotalDocumentos).Should().Be((T1.Nombre, AmbitoAplicacion.Empresa, 1));
        DateOnly.FromDateTime(reclamacion.FechaEnvioUtc).Should().Be(d.AddDays(-20), "MEDIDO: es un registro histórico, anclado a la demostración");
        reclamacion.DiasTranscurridos.Should().BeGreaterThanOrEqualTo(7);

        // Solo medida, sin afirmación: qué filas de la cola de T1 son de un documento de ámbito Empresa, cuántas nombran el
        // «Mutua» vencido de la Empresa propia, y si ese documento entra en el contador de vencidos de Inicio. Es un hecho
        // del producto, que la siembra no fija. La cola es la plana (ObtenerBandejaGestorQuery), la que Mi trabajo agrupa:
        // el KPI de Inicio es un recuento y no dice qué documentos cuenta, así que se compara con los de la base.
        string medida;
        try
        {
            medida = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T1.Nombre, async sp =>
            {
                var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
                var sender = sp.GetRequiredService<ISender>();
                var hoy = DiaDeNegocio.Hoy();
                var mutuaId = await db.TiposDocumento.Where(t => t.Nombre == DisenoT1PilotoOutbound.TipoDeEmpresaVencido).Select(t => t.Id).SingleAsync();
                var documentos = await db.Documentos.Select(x => new { x.Id, x.TipoDocumentoId, x.EmpresaId, x.TrabajadorId, x.FechaVencimiento }).ToListAsync();
                var deEmpresa = documentos.Where(x => x.EmpresaId != null).Select(x => x.Id).ToHashSet();
                var mutuas = documentos.Where(x => x.TipoDocumentoId == mutuaId).Select(x => x.Id).ToHashSet();

                var cola = await sender.Send(new CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor.ObtenerBandejaGestorQuery());
                var kpis = await sender.Send(new CaeManager.Application.Dashboard.Queries.ObtenerKpisDashboardQuery());

                var deDocumentoDeEmpresa = cola.Where(i => i.DocumentoId is { } id && deEmpresa.Contains(id)).ToList();
                var deEmpresaSinDocumento = cola.Where(i => i.DocumentoId == null && i.TrabajadorId == null && i.EmpresaId != null).ToList();
                var queNombranMutua = cola.Where(i => i.TipoDocumentoId == mutuaId || (i.DocumentoId is { } id && mutuas.Contains(id))).ToList();
                var vencidosDeTrabajador = documentos.Count(x => x.TrabajadorId != null && x.FechaVencimiento < hoy);
                var vencidosDeEmpresa = documentos.Count(x => x.EmpresaId != null && x.FechaVencimiento < hoy);

                return
                    $"MEDIDO T1 cola plana: {cola.Count} filas (Mi trabajo, agrupada: {fixture.Informe.De(T1).MiTrabajoFilas}); de un documento de ámbito Empresa " +
                    $"{deDocumentoDeEmpresa.Count} [{string.Join("; ", deDocumentoDeEmpresa.Select(i => $"{i.Tipo} · {i.Titulo}"))}]; de Empresa sin documento " +
                    $"{deEmpresaSinDocumento.Count}; nombran «{DisenoT1PilotoOutbound.TipoDeEmpresaVencido}» {queNombranMutua.Count} " +
                    $"[{string.Join("; ", queNombranMutua.Select(i => $"{i.Tipo} · {i.Titulo}"))}]\n" +
                    $"MEDIDO T1 Inicio: DocumentosVencidos {kpis.DocumentosVencidos}; en la base, con fecha vencida: {vencidosDeTrabajador} de Trabajador y " +
                    $"{vencidosDeEmpresa} de Empresa ({mutuas.Count} «{DisenoT1PilotoOutbound.TipoDeEmpresaVencido}»). El contador coincide con solo los de " +
                    $"Trabajador: {kpis.DocumentosVencidos == vencidosDeTrabajador}; con los de Trabajador más los de Empresa: " +
                    $"{kpis.DocumentosVencidos == vencidosDeTrabajador + vencidosDeEmpresa}";
            });
        }
        catch (Exception ex)
        {
            medida = $"MEDIDO T1 cola plana e Inicio: no se pudo medir ({ex.GetType().Name}: {ex.Message})";
        }

        salida.WriteLine(medida);
    }

    /// <summary>
    /// R13: el Centro que se gestiona por correo, su Visita y lo que viaja en el ZIP,
    /// por las tres consultas de la pantalla de la Visita y por lo que midió la
    /// autoverificación con el servicio que arma ese mismo ZIP.
    /// </summary>
    [Fact]
    public async Task T1_la_Visita_al_Centro_que_se_gestiona_por_correo_lleva_en_su_ZIP_lo_que_ese_Centro_exige()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        const string centroPorCorreo = "Parque fotovoltaico de Almansa";

        var (visita, previa, solicitud, descarga) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(T1.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var sender = sp.GetRequiredService<ISender>();
            var visita = await (from v in db.Visitas
                                join c in db.Centros on v.CentroId equals c.Id
                                select new { v.Id, v.FechaInicio, Centro = c.Nombre }).SingleAsync();

            return (
                visita,
                await sender.Send(new ObtenerDocumentacionVisitaQuery(visita.Id)),
                await sender.Send(new ObtenerSolicitudAccesoCorreoQuery(visita.Id)),
                await sender.Send(new ObtenerPaqueteDocumentalVisitaQuery(visita.Id)));
        });

        (visita.Centro, visita.FechaInicio).Should().Be((centroPorCorreo, d.AddDays(1)), "MEDIDO: la única Visita de T1 empieza al día siguiente de la demostración");

        // La solicitud de acceso sale hacia el canal principal del Centro, que es un correo de la misma regla que la agenda.
        solicitud.EsExitoso.Should().BeTrue();
        salida.WriteLine($"MEDIDO T1 solicitud de acceso por correo: a {solicitud.Valor.Destinatarios} · {solicitud.Valor.Asunto}");
        solicitud.Valor.Destinatarios.Should().Be("ensayo+accesos.parque-fotovoltaico-almansa@destino.example");

        // La comprobación previa: dos Trabajadores con todo en regla, y de la Empresa propia nada que falte. La
        // pantalla pone una fila «Faltante» por cada tipo exigido sin documento, y no lista lo que está confirmado
        // como que no caduca: que no haya ningún Faltante es lo que dice que lo exigido está entero. De los cinco
        // tipos de un Trabajador solo queda sin listar la información de riesgos, el único que no caduca.
        previa.Should().NotBeNull();
        foreach (var t in previa!.Trabajadores)
            salida.WriteLine(
                $"MEDIDO T1 comprobación previa · {t.NombreCompleto}: {t.Documentacion.Documentos.Count} filas " +
                $"[{string.Join("; ", t.Documentacion.Documentos.Select(x => $"{x.TipoDocumentoNombre} {x.Estado}"))}]");
        previa.Trabajadores.Should().HaveCount(2).And.OnlyContain(
            t => t.Documentacion.Documentos.Count > 0 && t.Documentacion.Documentos.All(
                x => x.DocumentoId != null && x.Estado == EstadoDocumento.Vigente),
            "MEDIDO: ningún Faltante ni nada fuera de vigencia; los documentos que no caducan no se listan");
        previa.Trabajadores.Should().OnlyContain(
            t => t.Documentacion.Documentos.Count == 4 && t.Documentacion.Documentos.All(x => x.TipoDocumentoNombre != CatalogoPilotoOutbound.InformacionArt18),
            "cuatro de los cinco tipos exigidos tienen fecha: el documento de identidad también, anotada a mano");
        salida.WriteLine(
            $"MEDIDO T1 comprobación previa · Empresa propia: {previa.Empresa.Documentos.Count} filas " +
            $"[{string.Join("; ", previa.Empresa.Documentos.GroupBy(x => x.Estado).Select(e => $"{e.Key} {e.Count()}"))}]");
        previa.Empresa.Documentos.Should().NotContain(x => x.Estado == EstadoDocumento.Faltante);
        previa.Empresa.Documentos.Where(x => x.Estado == EstadoDocumento.Vencido).Should().ContainSingle(
            "MEDIDO: el único documento vencido de la Empresa propia").Which.TipoDocumentoNombre.Should().Be("Mutua");
        ExigirDocumentosDeEmpresaEnRegla([.. previa.Empresa.Documentos.Where(x => x.Estado != EstadoDocumento.Vencido)]);
        previa.Empresa.Documentos.Should().HaveCount(14, "los trece que se exigen por defecto a una Empresa, todos con fecha, y el «Mutua» vencido");

        // El ZIP que descarga la pantalla.
        descarga.EsExitoso.Should().BeTrue();
        using var zip = new ZipArchive(new MemoryStream(descarga.Valor.Contenido), ZipArchiveMode.Read);
        var entradas = zip.Entries.Select(e => e.FullName).ToList();
        salida.WriteLine($"MEDIDO T1 ZIP «{descarga.Valor.NombreArchivo}», {descarga.Valor.Contenido.Length} bytes: {string.Join(" | ", entradas)}");
        entradas.Count(e => e.StartsWith("Empresa", StringComparison.Ordinal)).Should().Be(13);
        entradas.Count(e => e.StartsWith("Trabajadores", StringComparison.Ordinal)).Should().Be(10);
        entradas.Should().HaveCount(23).And.NotContain(e => e.Contains("Mutua"), "MEDIDO: lo vencido no viaja");

        // Lo que midió la autoverificación, fichero a fichero contra lo exigido.
        var g = fixture.Informe.De(T1).Grande!;
        var p = g.Paquete!;
        g.VisitasACentrosPorCorreo.Should().Be(1);
        (p.Centro, p.DiasDesdeLaDemostracion, p.Trabajadores, p.CorreosDelCanalFueraDeLaRegla).Should().Be((centroPorCorreo, 1, 2, 0));
        p.CorreosDelCanal.Should().Be(solicitud.Valor.Destinatarios);
        (p.ExigidosDeEmpresa, p.PdfDeEmpresa, p.ExigidosDeTrabajador, p.PdfDeTrabajador, p.FicherosEnElZip).Should().Be((13, 13, 10, 10, 23));
        p.Faltan.Should().BeEmpty();
        p.Sobran.Should().BeEmpty();
    }

    /// <summary>
    /// La cuenta opcional de § 3.3: un Usuario de Cliente de T1, sembrado
    /// por el mismo camino que las demás cuentas y ligado a su Cliente empresarial.
    /// Lo que ve se mide y se imprime; no se exige, porque no es parte de la matriz.
    /// </summary>
    [Fact]
    public async Task T1_tiene_un_Usuario_de_Cliente_empresarial_ligado_a_su_primer_Cliente_empresarial()
    {
        var tenantId = await fixture.Arnes.TenantIdAsync(T1.Nombre);
        var email = CuentasPilotoOutbound.Locales.UsuarioDeClienteEmpresarialT1;

        var (tenantDeLaCuenta, roles, clienteEmpresarial) = await fixture.Arnes.EnTenantAsync(tenantId, async (db, sp) =>
        {
            var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = await usuarios.FindByEmailAsync(email) ?? throw new InvalidOperationException("La cuenta no existe.");
            var suCliente = await db.Empresas.Where(e => e.Id == cuenta.ClienteId).Select(e => e.RazonSocial).SingleOrDefaultAsync();
            return (cuenta.TenantId, await usuarios.GetRolesAsync(cuenta), suCliente);
        });

        tenantDeLaCuenta.Should().Be(tenantId, "MEDIDO: la cuenta es del Tenant propietario, no del Operador CAE externo");
        roles.Should().Equal("Cliente");
        clienteEmpresarial.Should().Be("Logística Gorsenta, S.A.");

        try
        {
            var centros = await fixture.Arnes.ComoCuentaAsync(T1.Nombre, email, async sp =>
            {
                using (AmbitoTenantExplicito.Establecer(tenantId))
                    return (await sp.GetRequiredService<ISender>().Send(
                        new CaeManager.Application.Centros.Queries.ObtenerCentros.ObtenerCentrosQuery(null, null, TamanoPagina: 1000))).Elementos;
            });
            salida.WriteLine($"MEDIDO T1 Usuario de Cliente: ve {centros.Count} Centros [{string.Join("; ", centros.Select(c => c.Nombre))}]");
        }
        catch (Exception ex)
        {
            salida.WriteLine($"MEDIDO T1 Usuario de Cliente: el listado de Centros no le responde ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// La comprobación previa de una Visita lista, de la Empresa propia, cada tipo
    /// de documento que se exige a una Empresa. Sin documentos sembrados salían
    /// todos como Faltante, una cifra que no aparece en ninguna otra pantalla.
    /// </summary>
    [Theory]
    [InlineData("T2")]
    [InlineData("T3")]
    [InlineData("T5")]
    [InlineData("T6")]
    public async Task La_comprobacion_previa_de_una_Visita_no_da_por_faltante_ningun_documento_de_la_Empresa_propia(string clave)
    {
        var tenant = CatalogoPilotoOutbound.Tenants.Single(t => t.Clave == clave);
        var tenantId = await fixture.Arnes.TenantIdAsync(tenant.Nombre);
        var sembrada = await fixture.Arnes.EnTenantAsync(
            tenantId, (db, _) => db.Visitas.OrderBy(v => v.FechaInicio).ThenBy(v => v.Id).Select(v => (Guid?)v.Id).FirstOrDefaultAsync());

        // Donde la siembra no deja ninguna Visita, el test crea una, lejos de las 48 horas, y la retira al acabar.
        Guid? delTest = null;
        if (sembrada is null)
            delTest = await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
            {
                var centroId = await db.Centros.OrderBy(c => c.Nombre).Select(c => c.Id).FirstAsync();
                var dia = ArnesPilotoOutbound.FechaDemostracion().AddDays(20);
                var visita = new Visita(centroId, dia, dia, "Visita creada por el test", OrigenVisita.Plataforma);
                db.Visitas.Add(visita);
                await db.SaveChangesAsync();
                return (Guid?)visita.Id;
            });

        try
        {
            var visitaId = (sembrada ?? delTest)!.Value;
            var previa = await fixture.Arnes.ComoGestoraPrimeraEnAsync(
                tenant.Nombre, sp => sp.GetRequiredService<ISender>().Send(new ObtenerDocumentacionVisitaQuery(visitaId)));

            previa.Should().NotBeNull();
            salida.WriteLine(
                $"MEDIDO {clave} comprobación previa ({(sembrada is null ? "Visita del test" : "Visita sembrada")}) · Empresa propia: peor estado " +
                $"{previa!.Empresa.PeorEstado}; {previa.Empresa.Documentos.Count} filas " +
                $"[{string.Join("; ", previa.Empresa.Documentos.GroupBy(x => x.Estado).Select(e => $"{e.Key} {e.Count()}"))}]");
            // La pantalla pone una fila «Faltante» por cada tipo exigido sin documento y no lista lo confirmado como
            // que no caduca: salen los trece que se exigen por defecto a una Empresa, porque los trece tienen fecha.
            previa.Empresa.Documentos.Should().NotContain(
                x => x.Estado == EstadoDocumento.Faltante, "MEDIDO: a la Empresa propia no le falta nada de lo que el Centro exige");
            previa.Empresa.Documentos.Should().HaveCount(13, "ninguno de los trece tipos que se exigen por defecto a una Empresa queda «no caduca»");
            ExigirDocumentosDeEmpresaEnRegla(previa.Empresa.Documentos);
            previa.Empresa.PeorEstado.Should().Be(EstadoDocumento.Proximo);
        }
        finally
        {
            if (delTest is { } id)
                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    db.Visitas.Remove(await db.Visitas.SingleAsync(v => v.Id == id));
                    return await db.SaveChangesAsync();
                });
        }
    }

    // Los dos tipos de documento que vencen solos al mes, escritos a mano.
    private static readonly string[] CertificadosMensuales = ["Certificado de estar al corriente con la Seguridad Social", "ITA"];

    /// <summary>
    /// Lo que la comprobación previa lista de una Empresa propia en regla: cada fila con
    /// su documento, «Próximo» los dos certificados mensuales —un documento mensual
    /// coherente con su tipo no puede estar a más de 31 días de vencer— y Vigente lo demás.
    /// </summary>
    private static void ExigirDocumentosDeEmpresaEnRegla(IReadOnlyList<DocumentoVisitaItemDto> documentos)
    {
        documentos.Should().OnlyContain(x => x.DocumentoId != null);
        documentos.Where(x => x.Estado == EstadoDocumento.Proximo).Select(x => x.TipoDocumentoNombre).Should().BeEquivalentTo(CertificadosMensuales);
        documentos.Where(x => x.Estado != EstadoDocumento.Proximo).Should().NotBeEmpty().And.OnlyContain(x => x.Estado == EstadoDocumento.Vigente);
    }

    /// <summary>
    /// Las dos propiedades de fechas de la autoverificación y la regla de «no caduca»,
    /// leídas directamente de las filas de los seis Tenants: el vencimiento de un Tipo
    /// que vence solo es su emisión más los meses del Tipo, ninguna emisión es futura
    /// —ni lo habría sido el primer día en que se podía sembrar— y solo quedan «no
    /// caduca» los Tipos cuya nota del catálogo dice que no tienen caducidad.
    /// </summary>
    [Fact]
    public async Task Las_fechas_de_cada_documento_son_las_que_el_producto_le_habria_dado_y_solo_cinco_tipos_quedan_sin_caducidad()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        var hoy = DiaDeNegocio.Hoy();
        var primerDiaDeSiembra = d.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias);
        string[] deFechaManualNombrados = ["Certificado de estar al corriente con Hacienda", "Seguro de Responsabilidad Civil + recibo de pago"];
        var (conVencimientoAutomatico, sinCaducidadEnTotal) = (0, 0);

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var filas = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
            {
                var tipos = await db.TiposDocumento.AsNoTracking().ToDictionaryAsync(t => t.Id);
                var documentos = await db.Documentos.AsNoTracking()
                    .Select(x => new { x.TipoDocumentoId, x.FechaEmision, x.FechaVencimiento, x.EstadoVigencia }).ToListAsync();
                return documentos.Select(x => (Tipo: tipos[x.TipoDocumentoId], x.FechaEmision, x.FechaVencimiento, x.EstadoVigencia)).ToList();
            });

            var deTipoQueVenceSolo = filas.Where(f => f.Tipo.FijaVigenciaDesdeLaEmision).ToList();
            var ajenosASuTipo = deTipoQueVenceSolo
                .Where(f => f.FechaVencimiento != CalculadoraEstadoDocumento.CalcularFechaVencimiento(f.FechaEmision, f.Tipo.VigenciaMeses)).ToList();
            var emitidosDespuesDeHoy = filas.Where(f => f.FechaEmision > hoy).ToList();
            var emitidosDespuesDelPrimerDia = filas.Where(f => f.FechaEmision > primerDiaDeSiembra).ToList();
            var sinCaducidad = filas.Where(f => f.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca).ToList();
            var mensuales = deTipoQueVenceSolo.Where(f => f.Tipo.VigenciaMeses == 1).ToList();
            var trimestrales = deTipoQueVenceSolo.Where(f => f.Tipo.VigenciaMeses == 3).ToList();
            conVencimientoAutomatico += deTipoQueVenceSolo.Count;

            salida.WriteLine(
                $"MEDIDO {tenant.Clave}: {filas.Count} documentos, {deTipoQueVenceSolo.Count} de un Tipo que vence solo; con vencimiento ajeno a su Tipo " +
                $"{ajenosASuTipo.Count}; emitidos después de hoy {emitidosDespuesDeHoy.Count}; emitidos después de D−{OpcionesPilotoOutbound.MargenMaximoDias} " +
                $"{emitidosDespuesDelPrimerDia.Count}; emisión más tardía {(filas.Count == 0 ? "(sin documentos)" : $"{filas.Max(f => f.FechaEmision):yyyy-MM-dd}")}; " +
                $"sin caducidad {sinCaducidad.Count} [{string.Join("; ", sinCaducidad.GroupBy(f => f.Tipo.Nombre).Select(g => $"{g.Key} {g.Count()}"))}]; " +
                $"mensuales vencen en [{string.Join(", ", mensuales.Select(f => $"D+{f.FechaVencimiento!.Value.DayNumber - d.DayNumber}"))}]; " +
                $"trimestrales vencen en [{string.Join(", ", trimestrales.Select(f => $"D+{f.FechaVencimiento!.Value.DayNumber - d.DayNumber}"))}]");

            ajenosASuTipo.Select(f => $"{f.Tipo.Nombre}: {f.FechaEmision:yyyy-MM-dd} → {f.FechaVencimiento:yyyy-MM-dd}").Should().BeEmpty(
                $"{tenant.Clave}: el producto no deja crear un documento de un Tipo que vence solo con otro vencimiento que su emisión más los meses del Tipo");
            emitidosDespuesDeHoy.Should().BeEmpty($"{tenant.Clave}: ninguna emisión es futura");
            emitidosDespuesDelPrimerDia.Should().BeEmpty($"{tenant.Clave}: ninguna emisión habría sido futura el primer día en que se podía sembrar");
            sinCaducidad.Select(f => f.Tipo.Nombre).Distinct().Should().BeSubsetOf(
                CatalogoPilotoOutbound.TiposQueNoCaducan, $"{tenant.Clave}: solo quedan «no caduca» los Tipos cuya nota del catálogo lo dice");
            // Por negación y no con OnlyContain, que da rojo ante una colección vacía: el Tenant con todo
            // pendiente no tiene documentos. Que en los demás había de las tres clases lo afirma el control de abajo.
            filas.Where(f => deFechaManualNombrados.Contains(f.Tipo.Nombre) && f.EstadoVigencia != EstadoVigenciaDocumento.VenceEnFecha)
                .Select(f => f.Tipo.Nombre).Should().BeEmpty($"{tenant.Clave}: Hacienda y el seguro llevan su fecha, anotada a mano");
            mensuales.Select(f => f.FechaVencimiento!.Value.DayNumber - d.DayNumber).Where(dias => dias is < 16 or > 21).Should().BeEmpty(
                $"{tenant.Clave}: un mensual está «Próximo», y nunca «Urgente», de D−9 a D");
            trimestrales.Select(f => f.FechaVencimiento!.Value.DayNumber - d.DayNumber).Where(dias => dias <= 30).Should().BeEmpty(
                $"{tenant.Clave}: un trimestral recién emitido sigue Vigente el día de la demostración");

            if (tenant.Escenario == EscenarioPilotoOutbound.TodoPendiente) continue;

            // Control positivo, por Tenant con documentos: había de las tres clases que mirar.
            (mensuales.Count, trimestrales.Count).Should().Be((2, 4), $"{tenant.Clave}: los seis documentos de la Empresa propia que vencen al mes o a los tres meses");
            filas.Where(f => deFechaManualNombrados.Contains(f.Tipo.Nombre) && f.FechaVencimiento != null).Should().HaveCountGreaterThanOrEqualTo(2);
            sinCaducidadEnTotal += sinCaducidad.Count;
        }

        sinCaducidadEnTotal.Should().BeGreaterThan(0, "la información de riesgos de un Trabajador sigue sin caducidad: el estado no desaparece de la siembra");
        conVencimientoAutomatico.Should().BeGreaterThan(500, "control: se miraron los documentos de los seis Tenants, no un conjunto vacío");
        fixture.Informe.Tenants.Should().OnlyContain(
            m => m.DocumentosConVencimientoAjenoASuTipo == 0 && m.DocumentosEmitidosEnElFuturo == 0, "la autoverificación midió lo mismo, por Tenant");
    }

    [Fact]
    public async Task Cada_documento_se_abre_como_PDF_y_solo_tres_pesan_entre_5_MB_y_10_MiB()
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

        tamanos.Should().HaveCount(1164, "MEDIDO: 885 de T1, 77 de T2, 33 de T3, ninguno de T4, 33 de T5 y 136 de T6");
        var pesados = tamanos.Where(t => t.Bytes >= 5_000_000).ToList();
        salida.WriteLine($"MEDIDO PDF pesado: {string.Join(", ", pesados.Select(p => $"{p.Tenant} {p.Bytes} bytes"))}; el mayor de los demás: {tamanos.Where(t => t.Bytes < 5_000_000).Max(t => t.Bytes)} bytes");
        pesados.Select(p => p.Tenant).Should().BeEquivalentTo(["T1", "T1", "T2"], "MEDIDO: uno de la Empresa propia de T2 y dos de la de T1");
        pesados.Should().OnlyContain(p => p.Bytes <= 10 * 1024 * 1024);
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
            fixture.Arnes.FabricaDeAmbitos, cuentas,
            new OpcionesPilotoOutbound(ArnesPilotoOutbound.FechaDemostracion(), ContactosPilotoOutbound.NoEntregables), CancellationToken.None);

        await medicion.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        fixture.Arnes.Servicios.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull();
    }

    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await fixture.Arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(7, "control: el recuento ve los seis Tenants propietarios y el del Operador CAE externo");
        antes["Cuentas del piloto"].Should().Be(6, "MEDIDO: las cinco del equipo y de T1, y el Usuario de Cliente de T1");
        antes["Documentos"].Should().Be(1164);
        antes["Ficheros en el almacén"].Should().Be(1164);

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
        $"{m.ClientesEmpresarialesConAlertas}) | documentos {m.Documentos} (sin PDF {m.DocumentosSinPdf}, con vencimiento ajeno a su Tipo " +
        $"{m.DocumentosConVencimientoAjenoASuTipo}, emitidos en el futuro {m.DocumentosEmitidosEnElFuturo}) | agenda {m.ContactosDeAgenda} " +
        $"(fuera de regla {m.ContactosFueraDeLaReglaDeCorreo})" + (m.Grande is { } g ? " | " + Grande(g) : string.Empty);

    /// <summary>Los Centros de Trabajo del Tenant por estado, con los seis estados aunque alguno no tenga ninguno.</summary>
    public static string CentrosPorEstado(PilotoOutboundAutoverificacion.MedicionTenant m) =>
        $"Centros por estado [{string.Join(", ", Enum.GetValues<EstadoCentro>().Select(e => $"{e} {m.Centros.Count(c => c.Estado == e)}"))}]";

    private static string Grande(PilotoOutboundAutoverificacion.MedicionGrande g) =>
        $"Trabajadores propios {g.TrabajadoresPropios}, de subcontrata {g.TrabajadoresDeSubcontrata}, subcontratas {g.Subcontratas} | casos " +
        $"[{string.Join("; ", g.CasosDeEstado.Select(c => $"{c.Key} {c.Value}"))}] | Visitas a Centros por correo {g.VisitasACentrosPorCorreo}" +
        (g.Paquete is { } p
            ? $" | paquete de «{p.Centro}» a {p.CorreosDelCanal} (fuera de regla {p.CorreosDelCanalFueraDeLaRegla}), D+{p.DiasDesdeLaDemostracion}, " +
              $"{p.Trabajadores} Trabajadores: exigidos de Trabajador {p.ExigidosDeTrabajador} y PDF {p.PdfDeTrabajador}, exigidos de Empresa " +
              $"{p.ExigidosDeEmpresa} y PDF {p.PdfDeEmpresa}, ficheros {p.FicherosEnElZip}, faltan {p.Faltan.Count}, sobran {p.Sobran.Count}"
            : " | sin paquete");
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
            await CorregirComoElProductoAsync(db, documento, DiaDeNegocio.Hoy().AddDays(-20));
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

        // 3. Un documento de T3 que debía estar Vencido queda Vigente: 11 de 20 en vez de 10 de 20.
        var t3 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT3);
        await arnes.EnTenantAsync(t3, async (db, _) =>
        {
            var hoy = DiaDeNegocio.Hoy();
            var documento = await db.Documentos.Where(d => d.FechaVencimiento < hoy).OrderBy(d => d.Id).FirstAsync();
            await CorregirComoElProductoAsync(db, documento, hoy.AddDays(200));
            return await db.SaveChangesAsync();
        });

        var exigir = async () => PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var mensaje = (await exigir.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        salida.WriteLine("MEDIDO " + mensaje);

        var prefijoT3 = $"T3 «{CatalogoPilotoOutbound.NombreTenantT3}» · ";
        mensaje.Should().Contain(prefijoT3 + "Mi trabajo · filas: medido 9, esperado 10.");
        mensaje.Should().Contain(prefijoT3 + "Inicio · % de cumplimiento: medido 55, esperado 50.");
        mensaje.Should().Contain(prefijoT3 + "Visión de cartera · % de cumplimiento: medido 55, esperado 50.");
        mensaje.Should().Contain(prefijoT3 + "Empresas · % de cumplimiento de la Empresa propia: medido 55, esperado 50.");
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
        })).Should().BeGreaterThan(0, "control: la alteración de T5 se escribió (la fila y su auditoría)");

        // 5. En T6, el Centro que exigía el certificado para acceder deja de exigirlo así: nadie queda bloqueado.
        var t6 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT6);
        (await arnes.EnTenantAsync(t6, async (db, _) =>
        {
            var fila = await db.TiposDocumentoCentros.Where(f => f.BloqueaAcceso).SingleAsync();
            fila.Actualizar(fila.Incluido, fila.PeriodicidadEspecialMeses, bloqueaAcceso: false, fila.ArchivoUrl, fila.NombreArchivoOriginal, fila.ToleranciaDias);
            return await db.SaveChangesAsync();
        })).Should().BeGreaterThan(0, "control: la alteración de T6 se escribió (la fila y su auditoría)");

        var conT5yT6 = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        var prefijoT5 = $"T5 «{CatalogoPilotoOutbound.NombreTenantT5}» · ";
        var prefijoT6 = $"T6 «{CatalogoPilotoOutbound.NombreTenantT6}» · ";
        foreach (var linea in conT5yT6.Where(l => l.StartsWith(prefijoT5) || l.StartsWith(prefijoT6))) salida.WriteLine("MEDIDO " + linea);

        conT5yT6.Where(l => l.StartsWith(prefijoT5)).Should().Equal(
            [prefijoT5 + "Mi trabajo · filas: medido 6, esperado 4."], "MEDIDO: dos requisitos bloqueantes pendientes más, y nada más cambia en T5");
        conT5yT6.Where(l => l.StartsWith(prefijoT6)).Should().Equal(
            [prefijoT6 + "Mi trabajo · filas: medido 18, esperado 19.", prefijoT6 + "Inicio · Trabajadores bloqueados: medido 0, esperado 1."],
            "MEDIDO: desaparece el requisito bloqueante pendiente y el Trabajador deja de estar bloqueado; el «Falta» sigue");

        // 6. En T1, un documento de uno de los dos Trabajadores de la Visita por correo pasa a estar vencido: lo vencido
        //    no viaja, así que el ZIP ya no lleva todo lo que su Centro exige.
        var t1 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT1);
        var tipoQueYaNoViaja = await arnes.EnTenantAsync(t1, async (db, _) =>
        {
            var documento = await (from acude in db.VisitasTrabajadores
                                   join x in db.Documentos on (Guid?)acude.TrabajadorId equals x.TrabajadorId
                                   where x.FechaVencimiento != null
                                   orderby x.Id
                                   select x).FirstAsync();
            await CorregirComoElProductoAsync(db, documento, DiaDeNegocio.Hoy().AddDays(-20));
            (await db.SaveChangesAsync()).Should().BeGreaterThan(0, "control: la alteración de T1 se escribió");
            return await db.TiposDocumento.Where(t => t.Id == documento.TipoDocumentoId).Select(t => t.Nombre).SingleAsync();
        });

        var prefijoT1 = $"T1 «{CatalogoPilotoOutbound.NombreTenantT1}» · ";
        var conT1 = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion)).Where(l => l.StartsWith(prefijoT1)).ToList();
        foreach (var linea in conT1) salida.WriteLine("MEDIDO " + linea);

        conT1.Should().Contain(prefijoT1 + "Paquete de la Visita · PDF de Trabajador en el ZIP: medido 9, esperado 10.");
        conT1.Should().Contain(prefijoT1 + "Paquete de la Visita · ficheros en el ZIP: medido 22, esperado 23.");
        conT1.Should().ContainSingle(
            l => l.StartsWith(prefijoT1 + $"Paquete de la Visita · exigido por el Centro: «{tipoQueYaNoViaja}» de ")
                 && l.EndsWith(": medido no está en el ZIP, esperado en el ZIP."),
            "MEDIDO: la autoverificación dice qué documento falta, y de quién");

        // 7. En T5, dos documentos dejan de tener las fechas que el producto les habría dado, sin cambiar de estado: a uno de
        //    un Tipo que vence solo se le mueve la emisión y se le deja el vencimiento, y uno que no caduca pasa a estar
        //    emitido pasado mañana. La emisión futura se escribe por la propiedad de la fila: el dominio no deja corregirla así.
        //    El primero es una entrega de EPI Vigente: ningún Centro de T5 le pone condiciones propias a ese Tipo.
        (await arnes.EnTenantAsync(t5, async (db, _) =>
        {
            var hoy = DiaDeNegocio.Hoy();
            var epi = await db.TiposDocumento.SingleAsync(t => t.Nombre == CatalogoPilotoOutbound.EntregaEpi);
            epi.FijaVigenciaDesdeLaEmision.Should().BeTrue("control: la entrega de EPI vence sola");
            var deTipoQueVenceSolo = await db.Documentos
                .Where(d => d.TipoDocumentoId == epi.Id && d.FechaVencimiento > hoy.AddDays(60)).OrderBy(d => d.Id).FirstAsync();
            deTipoQueVenceSolo.CorregirVigencia(deTipoQueVenceSolo.FechaEmision.AddDays(-40), deTipoQueVenceSolo.Vigencia);

            var queNoCaduca = await db.Documentos.Where(d => d.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca).OrderBy(d => d.Id).FirstAsync();
            db.Entry(queNoCaduca).Property(d => d.FechaEmision).CurrentValue = hoy.AddDays(2);

            return await db.SaveChangesAsync();
        })).Should().BeGreaterThan(0, "control: las dos alteraciones de T5 se escribieron");

        var conFechasAjenas = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion)).Where(l => l.StartsWith(prefijoT5)).ToList();
        foreach (var linea in conFechasAjenas) salida.WriteLine("MEDIDO " + linea);

        conFechasAjenas.Should().BeEquivalentTo(
        [
            prefijoT5 + "Documentos · vencimiento que no es la emisión más los meses de su Tipo: medido 1, esperado 0.",
            prefijoT5 + "Documentos · emisión posterior a hoy: medido 1, esperado 0.",
            prefijoT5 + "Mi trabajo · filas: medido 6, esperado 4."
        ], "la autoverificación nombra el Tenant y las dos propiedades de fechas, y nada más cambia en T5 desde el paso 4");
    }

    /// <summary>
    /// Corrige un documento para que venza <paramref name="haciaElDia"/> —o hasta tres días antes, si ese día no lo
    /// alcanza ninguna emisión— con la emisión de la que el producto sacaría ese vencimiento: así la alteración
    /// solo aparta de la matriz el estado, y no también la coherencia de las fechas con el Tipo.
    /// </summary>
    private static async Task CorregirComoElProductoAsync(
        CaeManager.Infrastructure.Persistence.CaeManagerDbContext db, Documento documento, DateOnly haciaElDia)
    {
        var meses = await db.TiposDocumento.Where(t => t.Id == documento.TipoDocumentoId).Select(t => t.VigenciaMeses).SingleAsync() ?? 12;
        var emision = haciaElDia.AddMonths(-meses);
        documento.CorregirVigencia(emision, VigenciaDocumento.VenceEl(emision.AddMonths(meses)));
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
        conCorreo.DireccionDe("cae.aldrevia").Should().Be("ensayo+cae.aldrevia@destino.example");
        conCorreo.Cumple("ensayo+cae.aldrevia@destino.example").Should().BeTrue();
        conCorreo.Cumple("ensayo@destino.example").Should().BeFalse("sin etiqueta no se sabe a qué contacto se escribió");
        conCorreo.Cumple("otra+cae.aldrevia@destino.example").Should().BeFalse();
        conCorreo.Cumple("cae.aldrevia@caemanager.local").Should().BeFalse();

        var sinCorreo = ContactosPilotoOutbound.Crear(" ", null);
        sinCorreo.Should().Be(ContactosPilotoOutbound.NoEntregables);
        sinCorreo.DireccionDe("cae.aldrevia").Should().Be("cae.aldrevia@caemanager.local");
        sinCorreo.Cumple("ensayo+cae.aldrevia@destino.example").Should().BeFalse();
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
    // T4 no escribe ningún PDF y T3 escribe 33 (20 de Trabajador y 13 de su Empresa propia): el n.º 43 es el décimo de T2.
    private const int PdfEnElQueFalla = 43;
    private const int PdfDeT3 = 33;

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
        completa["Ficheros en el almacén"].Should().Be(1164);

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

    /// <summary>
    /// El lote que sembró esta versión antes de que la siembra llevara la instrucción de tratamiento de IA: los
    /// siete Tenants con todos sus datos y ninguno con instrucción. El asistente falla cerrado —es el estado que
    /// este paso corrige—, la resiembra añade las siete instrucciones sin tocar nada más, repetirla no añade
    /// ninguna, y la retirada se las lleva.
    /// </summary>
    [Fact]
    public async Task A_un_lote_ya_sembrado_sin_instruccion_de_tratamiento_de_IA_la_resiembra_solo_le_anade_la_instruccion_y_la_retirada_la_borra()
    {
        const string Clave = "Instrucciones de tratamiento de IA";
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
        var sinPiloto = await arnes.RecuentoAsync();

        (await arnes.SembrarAsync(configuracion))!.Escribio.Should().BeTrue("control: la primera siembra escribe");
        await arnes.BackfillAsync();
        var completa = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO lote completo: " + Texto(completa));

        // 1. Qué deja la siembra: una por Tenant, vigente, con las versiones Draft de la demo y atribuida a una cuenta del lote.
        var administradorDelOperador = await arnes.ComoBootstrapAsync(b => b.Users.IgnoreQueryFilters()
            .Where(u => u.Email == CuentasPilotoOutbound.Locales.AdministradorOperador).Select(u => u.Id).SingleAsync());
        var sembradas = await InstruccionesDelPilotoAsync(arnes);
        sembradas.Select(i => i.Tenant).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: exactamente una por Tenant del piloto, el Operador CAE externo y los seis propietarios");
        sembradas.Should().OnlyContain(i => i.Vigente, "MEDIDO: todas vigentes");
        sembradas.Should().OnlyContain(
            i => i.Dpa == "Draft-2026-09-03" && i.Anexo == "Draft-2026-09-03",
            "MEDIDO: las versiones son las Draft de la siembra de demo: un borrador, nunca una aceptación real");
        sembradas.Should().OnlyContain(i => i.Origen == OrigenInstruccionTratamientoIa.AltaManualPlataforma, "MEDIDO: el origen de la siembra de demo");
        sembradas.Should().OnlyContain(i => i.RegistradaPor == administradorDelOperador, "MEDIDO: la registra el Administrador del Operador CAE externo del lote");

        // 2. El lote tal como lo dejó la siembra anterior a este paso: sin ninguna instrucción.
        (await arnes.ComoBootstrapAsync(async b =>
        {
            var ids = await IdsDelPilotoAsync(b);
            return await b.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters().Where(i => ids.Contains(i.TenantId)).ExecuteDeleteAsync();
        })).Should().Be(7, "control: se han quitado las siete");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(
            new Dictionary<string, int>(completa) { [Clave] = 0 }, "control: lo único que cambia respecto del lote completo es que no hay instrucciones");

        // 3. Así, el modo Preguntar se niega antes de llamar al proveedor, y la autoverificación lo dice.
        var pregunta = new PreguntarAlAsistenteQuery([new MensajeChatDto(RolMensajeChat.Usuario, "¿Qué vence esta semana?")]);
        var negativa = await arnes.ComoGestoraPrimeraEnAsync(
            CatalogoPilotoOutbound.NombreTenantT1, sp => sp.GetRequiredService<ISender>().Send(pregunta));
        negativa.EsFallido.Should().BeTrue("MEDIDO: sin instrucción en el Tenant en pantalla, el asistente no procesa el mensaje");
        negativa.Error.Codigo.Should().Be("AsistenteIa.SinInstruccion", "MEDIDO: y falla por la instrucción, no por otra cosa");

        var carteraSinInstruccion = await arnes.ComoGestoraPrimeraEnAsync(
            CatalogoPilotoOutbound.NombreTenantT1, sp => sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery()));
        carteraSinInstruccion.SinInstruccion.Select(t => t.Nombre).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: los siete Tenants de la cartera de la Gestora CAE están sin instrucción");
        carteraSinInstruccion.ErrorSiFalta()!.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);

        var discrepancias = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in discrepancias) salida.WriteLine("MEDIDO " + linea);
        discrepancias.Should().HaveCount(14, "MEDIDO: una por Tenant y por cuenta, siete de la Gestora CAE y siete de la Coordinadora CAE");
        discrepancias.Should().OnlyContain(l => l.StartsWith("Asistente IA · "), "control: del lote solo falla el Asistente IA");
        discrepancias.Should().Contain(
            $"Asistente IA · cartera de la Coordinadora CAE · «{CatalogoPilotoOutbound.NombreTenantT1}»: medido sin instrucción de tratamiento de IA " +
            "vigente, esperado con instrucción de tratamiento de IA vigente.");

        // 4. La resiembra: no hay nada que sembrar salvo la instrucción.
        var resiembra = await arnes.SembrarAsync(configuracion);
        resiembra!.Escribio.Should().BeFalse("MEDIDO: añadir la instrucción que faltaba no cuenta como haber escrito el lote");
        resiembra.TenantsConDatosNuevos.Should().BeEmpty();
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: vuelve a haber siete instrucciones y nada más ha cambiado");
        (await InstruccionesDelPilotoAsync(arnes)).Select(i => i.Tenant).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: una por Tenant, no siete en uno");
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));

        var carteraConInstruccion = await arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.Coordinadora,
            sp => sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery()));
        carteraConInstruccion.ErrorSiFalta().Should().BeNull("MEDIDO: la comprobación de cartera pasa para la Coordinadora CAE");
        carteraConInstruccion.ConInstruccion.Should().HaveCount(7, "control: y pasa con los siete en la cartera, no con una cartera vacía");

        // 5. Repetirla no añade ninguna.
        await arnes.SembrarAsync(configuracion);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: idempotente, siguen siendo siete");

        // 6. Una instrucción revocada a propósito en un ensayo no se repone: el Tenant ya tiene la suya, cerrada.
        var t2Id = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT2);
        (await arnes.EnTenantAsync(t2Id, async (db, _) =>
        {
            (await db.InstruccionesTratamientoIaTenantPropietario.SingleAsync()).Revocar("Ensayo del fallo cerrado.", DateTime.UtcNow);
            return await db.SaveChangesAsync();
        })).Should().BeGreaterThan(0, "control: la revocación se guardó");
        await arnes.SembrarAsync(configuracion);
        var trasRevocar = await InstruccionesDelPilotoAsync(arnes);
        trasRevocar.Should().HaveCount(7, "MEDIDO: la siembra no añade otra al Tenant cuya instrucción se revocó");
        trasRevocar.Where(i => !i.Vigente).Select(i => i.Tenant).Should().Equal(
            [CatalogoPilotoOutbound.NombreTenantT2], "MEDIDO: y la revocada sigue revocada");
        PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion)).Should().HaveCount(2, "MEDIDO: la autoverificación lo dice, una vez por cuenta")
            .And.OnlyContain(l => l.StartsWith("Asistente IA · ") && l.Contains($"«{CatalogoPilotoOutbound.NombreTenantT2}»: medido sin instrucción"));

        // 7. La retirada se las lleva con el resto del lote, también la revocada.
        completa[Clave].Should().Be(7, "control positivo: antes de retirar hay siete que borrar");
        (await arnes.RetirarAsync()).Should().HaveCount(7);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto);
        // El recuento del arnés cuenta por los Tenants del piloto que existen, y ya no existe ninguno: daría cero aunque
        // las filas siguieran ahí. Lo que mide la retirada es este, sobre la tabla entera de una base recién creada.
        (await arnes.ComoBootstrapAsync(b => b.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters().CountAsync()))
            .Should().Be(0, "MEDIDO: la retirada no deja ninguna instrucción, tampoco huérfana de un Tenant que ya no existe");
    }

    private sealed record InstruccionSembrada(string Tenant, bool Vigente, string Dpa, string Anexo, OrigenInstruccionTratamientoIa Origen, Guid RegistradaPor);

    private static Task<List<Guid>> IdsDelPilotoAsync(CaeManager.Infrastructure.Persistence.CaeManagerDbContext bootstrap)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        return bootstrap.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
    }

    /// <summary>Las instrucciones de los Tenants del piloto, también las revocadas, leídas con la identidad de bootstrap y sin filtros.</summary>
    private static Task<List<InstruccionSembrada>> InstruccionesDelPilotoAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var nombrePorId = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var ids = nombrePorId.Keys.ToList();
            var filas = await b.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters()
                .Where(i => ids.Contains(i.TenantId)).ToListAsync();

            return filas.Select(i => new InstruccionSembrada(
                nombrePorId[i.TenantId], i.EstaVigente, i.VersionDpaAceptada, i.VersionAnexoSubencargadosAceptada,
                i.OrigenInstruccion, i.RegistradaPorUsuarioId)).ToList();
        });

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
