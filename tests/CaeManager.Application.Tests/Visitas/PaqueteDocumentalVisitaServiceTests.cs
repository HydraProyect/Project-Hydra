using System.IO.Compression;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Regla del propietario (2026-09-20) para el zip que sale hacia el Cliente
/// empresarial: «se envían todos los documentos vigentes, y uno de cada uno. Si hay
/// cinco reconocimientos médicos o cinco EPIs vigentes por algún error, solo se
/// tiene que mandar el más actual y el de mayor vigencia. No se envía todos los
/// documentos vencidos ni nada de eso.»
///
/// Antes de este cambio el servicio filtraba solo por «tiene archivo», así que
/// viajaban los vencidos y todas las copias repetidas (el colisionador de nombres
/// las apilaba como «X (2)», «X (3)»). Cada test observa el CONTENIDO del zip, no el
/// recuento del correo: es lo que llega al tercero.
/// </summary>
public class PaqueteDocumentalVisitaServiceTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly VisitasQueryContextFalso _visitas = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly FileStorageServiceFalso _almacenamiento = new();
    private readonly LoggerCaptura _logger = new();

    private readonly Empresa _empresa = new("Contratista Demo SL", "B12345674");
    private readonly Centro _centro;
    private readonly Visita _visita;
    private readonly Conversacion _conversacion = new("Solicitud de visita");
    private readonly TipoDocumento _reconocimiento = NuevoTipo("Reconocimiento médico", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _epi = NuevoTipo("Entrega de EPI", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _formacion = NuevoTipo("Formación 60h", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _seguro = NuevoTipo("Seguro RC", AmbitoAplicacion.Empresa);
    private readonly Trabajador _ana;
    private readonly Trabajador _luis;

    public PaqueteDocumentalVisitaServiceTests()
    {
        _centro = new Centro(Guid.NewGuid(), _empresa.Id, "Nave Norte");
        _visita = new Visita(_centro.Id, Hoy.AddDays(1), Hoy.AddDays(2), null);
        _ana = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Garcia", "12345678Z");
        _luis = Trabajador.DeEmpresa(_empresa.Id, "Luis", "Perez", "77189989B");

        _visitas.ListaVisitas.Add(_visita);
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(_visita.Id, _ana.Id));
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(_visita.Id, _luis.Id));
        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.Add(_empresa);
        _trabajadores.ListaTrabajadores.AddRange([_ana, _luis]);
        _tipos.ListaTiposDocumento.AddRange([_reconocimiento, _epi, _formacion, _seguro]);
        _conversaciones.Conversaciones.Add(_conversacion);
    }

    [Fact]
    public async Task No_envia_el_documento_vencido_y_si_los_vigentes()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-400), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-1)), contenido: "vencido");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-30), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(300)), contenido: "epi-vigente");

        await GenerarAsync();

        var zip = LeerZip();
        zip.Should().ContainSingle().Which.Value.Should().Be("epi-vigente");
        zip.Keys.Should().NotContain(n => n.Contains("Reconocimiento"), "el vencido nunca viaja «por si acaso»");
    }

    [Fact]
    public async Task Un_documento_que_vence_hoy_todavia_es_vigente_y_uno_que_vencio_ayer_no()
    {
        // Frontera exacta de «Vencido» (FechaVencimiento < hoy): un mutante <= dejaría fuera el de hoy.
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-365), vigencia: VigenciaDocumento.VenceEl(Hoy), contenido: "vence-hoy");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-365), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-1)), contenido: "vencio-ayer");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("vence-hoy");
    }

    [Fact]
    public async Task Los_estados_proximo_y_urgente_siguen_siendo_vigentes()
    {
        // Vencen en 3 y 25 días: caen en Urgente/Próximo con los umbrales por defecto y aun así se envían.
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-300), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(3)), contenido: "urgente");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-300), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(25)), contenido: "proximo");

        await GenerarAsync();

        LeerZip().Values.Should().BeEquivalentTo("urgente", "proximo");
    }

    [Fact]
    public async Task Entre_varias_copias_vigentes_del_mismo_tipo_envia_solo_la_de_mayor_vigencia()
    {
        // Cinco reconocimientos vigentes «por algún error»: la de mayor vigencia gana aunque NO sea la de emisión más reciente.
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-50), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(100)), contenido: "v100");
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-40), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(200)), contenido: "v200-la-mayor");
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-10), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(150)), contenido: "v150-mas-reciente");
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-60), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(50)), contenido: "v50");
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-70), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(60)), contenido: "v60");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("v200-la-mayor");
    }

    [Fact]
    public async Task A_igual_vigencia_gana_la_emision_mas_reciente()
    {
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-90), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(100)), contenido: "emision-antigua");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-5), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(100)), contenido: "emision-reciente");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-45), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(100)), contenido: "emision-media");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("emision-reciente");
    }

    [Fact]
    public async Task Con_copias_vencidas_y_vigentes_del_mismo_tipo_envia_la_vigente()
    {
        // La vencida está emitida DESPUÉS que la vigente: no puede ganar por «más actual».
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-200), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(165)), contenido: "vigente-antigua");
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-20), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-2)), contenido: "vencida-reciente");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("vigente-antigua");
    }

    [Fact]
    public async Task La_seleccion_es_independiente_por_titular_y_por_ambito()
    {
        // Mismo tipo, dos trabajadores distintos: cada uno conserva el suyo. Y el de la empresa, aparte.
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "ana-rm");
        DocumentoDeTrabajador(_luis, _reconocimiento, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "luis-rm");
        DocumentoDeEmpresa(_seguro, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "empresa-seguro");

        await GenerarAsync();

        LeerZip().Values.Should().BeEquivalentTo("ana-rm", "luis-rm", "empresa-seguro");
    }

    [Fact]
    public async Task Un_documento_confirmado_como_que_no_caduca_se_envia()
    {
        DocumentoDeTrabajador(_ana, _formacion, emision: Hoy.AddDays(-900), vigencia: VigenciaDocumento.NoCaduca, contenido: "formacion-60h");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-900), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-1)), contenido: "epi-vencido");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("formacion-60h");
    }

    [Fact]
    public async Task No_caduca_confirmado_cuenta_como_vigencia_maxima_frente_a_una_copia_con_fecha()
    {
        DocumentoDeTrabajador(_ana, _formacion, emision: Hoy.AddDays(-10), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(500)), contenido: "con-fecha");
        DocumentoDeTrabajador(_ana, _formacion, emision: Hoy.AddDays(-900), vigencia: VigenciaDocumento.NoCaduca, contenido: "sin-caducidad");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("sin-caducidad");
    }

    // --- Vigencia sin confirmar (FechaVencimiento sin anotar) ---
    //
    // Antes, «sin fecha» valía DateOnly.MaxValue en el orden: una copia a la que
    // nadie había anotado el vencimiento ganaba a otra con vigencia comprobada.
    // Sin confirmar no es «no caduca»: nunca desplaza a una copia confirmada.

    [Fact]
    public async Task Una_copia_sin_vigencia_confirmada_no_desplaza_a_una_con_vigencia_comprobada()
    {
        // La sin confirmar es la de emisión más reciente: ni por eso gana.
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-300), VigenciaDocumento.VenceEl(Hoy.AddDays(65)), "vigencia-comprobada");
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-1), VigenciaDocumento.SinConfirmar, "sin-confirmar");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("vigencia-comprobada");
        _logger.Entradas.Should().NotContain(e => e.Mensaje.Contains("sin vigencia confirmada"),
            "se envió una copia confirmada: no hay nada sin confirmar que avisar");
    }

    [Fact]
    public async Task Una_copia_sin_vigencia_confirmada_no_desplaza_a_una_confirmada_como_que_no_caduca()
    {
        DocumentoDeTrabajador(_ana, _formacion, Hoy.AddDays(-900), VigenciaDocumento.NoCaduca, "no-caduca");
        DocumentoDeTrabajador(_ana, _formacion, Hoy.AddDays(-1), VigenciaDocumento.SinConfirmar, "sin-confirmar");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("no-caduca");
    }

    [Fact]
    public async Task Si_solo_hay_copias_sin_confirmar_se_envia_la_mas_reciente_y_queda_registrado()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-200), VigenciaDocumento.SinConfirmar, "sin-confirmar-antigua");
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-20), VigenciaDocumento.SinConfirmar, "sin-confirmar-reciente");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("sin-confirmar-reciente");
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning
                && e.Mensaje.Contains("sin vigencia confirmada") && e.Mensaje.Contains(_reconocimiento.Id.ToString()),
            "lo que viaja sin vigencia confirmada tiene que constar, con su tipo");
        _logger.Entradas.Should().NotContain(e => e.Mensaje.Contains("Ana") || e.Mensaje.Contains("Garcia"),
            "el log lleva identificadores, no el nombre del trabajador");
    }

    [Fact]
    public async Task Frente_a_una_copia_vencida_se_envia_la_sin_confirmar()
    {
        // Sin confirmar no es vencido: no se sabe que no sirva. La vencida es más reciente y aun así no viaja.
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-400), VigenciaDocumento.SinConfirmar, "sin-confirmar");
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(-2)), "vencida-reciente");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("sin-confirmar");
    }

    [Fact]
    public async Task Un_tipo_con_solo_copias_vencidas_no_se_envia_y_queda_registrado()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-500), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-30)), contenido: "vencido-1");
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-700), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-200)), contenido: "vencido-2");
        DocumentoDeTrabajador(_ana, _epi, emision: Hoy.AddDays(-10), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(100)), contenido: "epi-vigente");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("epi-vigente");
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains(_reconocimiento.Id.ToString()),
            "el tipo omitido por vencido tiene que constar, con su identificador, para que nadie lo eche en falta a ciegas");
        _logger.Entradas.Should().NotContain(e => e.Mensaje.Contains("Ana") || e.Mensaje.Contains("Garcia"),
            "el log lleva identificadores, no el nombre del trabajador");
    }

    [Fact]
    public async Task Si_todo_esta_vencido_no_se_genera_paquete_ni_mensaje()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, emision: Hoy.AddDays(-500), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-30)), contenido: "vencido");
        DocumentoDeEmpresa(_seguro, emision: Hoy.AddDays(-500), vigencia: VigenciaDocumento.VenceEl(Hoy.AddDays(-1)), contenido: "seguro-vencido");

        await GenerarAsync();

        _conversacion.Mensajes.Should().BeEmpty("un zip sin nada vigente no se manda al cliente");
        _almacenamiento.ArchivosGuardados.Should().Be(2, "solo los dos originales sembrados; no se guardó ningún zip");
        _logger.Entradas.Should().Contain(e => e.Nivel == LogLevel.Warning);
    }

    [Fact]
    public async Task El_correo_anuncia_los_documentos_enviados_no_los_existentes()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-50), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "a");
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-40), VigenciaDocumento.VenceEl(Hoy.AddDays(200)), "b");
        DocumentoDeTrabajador(_ana, _epi, Hoy.AddDays(-500), VigenciaDocumento.VenceEl(Hoy.AddDays(-5)), "vencido");
        DocumentoDeEmpresa(_seguro, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "seguro");

        await GenerarAsync();

        _conversacion.Mensajes.Should().ContainSingle().Which.CuerpoHtml.Should().Contain("(2 documento(s))");
    }

    [Fact]
    public async Task Si_el_archivo_de_la_mejor_copia_no_se_puede_abrir_envia_la_siguiente_vigente()
    {
        // Storage inconsistente: la de mayor vigencia apunta a un objeto que ya no existe.
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(400)), "irrelevante", archivoInexistente: true);
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-30), VigenciaDocumento.VenceEl(Hoy.AddDays(200)), "copia-de-reserva");
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-90), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "otra-copia");

        await GenerarAsync();

        LeerZip().Should().ContainSingle("sigue siendo uno por titular y tipo").Which.Value.Should().Be("copia-de-reserva");
    }

    [Fact]
    public async Task La_copia_de_reserva_nunca_es_una_vencida()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(400)), "irrelevante", archivoInexistente: true);
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-500), VigenciaDocumento.VenceEl(Hoy.AddDays(-3)), "vencida-legible");
        DocumentoDeTrabajador(_ana, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "epi-vigente");

        await GenerarAsync();

        LeerZip().Should().ContainSingle().Which.Value.Should().Be("epi-vigente");
    }

    [Fact]
    public async Task El_correo_cuenta_lo_adjuntado_aunque_un_archivo_no_se_pueda_abrir()
    {
        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(400)), "irrelevante", archivoInexistente: true);
        DocumentoDeTrabajador(_ana, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "epi");
        DocumentoDeEmpresa(_seguro, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "seguro");

        await GenerarAsync();

        _conversacion.Mensajes.Should().ContainSingle().Which.CuerpoHtml.Should().Contain("(2 documento(s))");
    }

    private async Task GenerarAsync()
    {
        var servicio = new PaqueteDocumentalVisitaService(
            _visitas, _centros, _documentos, _tipos, _empresas, _trabajadores, _conversaciones, _almacenamiento, _logger);

        await servicio.GenerarYEnviarAsync(_visita.Id, _conversacion.Id);
    }

    /// <summary>Nombre de entrada → contenido del archivo original, para saber QUÉ copia viajó.</summary>
    private Dictionary<string, string> LeerZip()
    {
        var mensaje = _conversacion.Mensajes.Should().ContainSingle("el paquete se adjunta en un único mensaje").Subject;
        var adjunto = mensaje.Adjuntos.Should().ContainSingle().Subject;

        using var flujo = _almacenamiento.AbrirAsync(adjunto.ArchivoUrl).GetAwaiter().GetResult();
        using var zip = new ZipArchive(flujo, ZipArchiveMode.Read);

        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var lector = new StreamReader(e.Open());
            return lector.ReadToEnd();
        });
    }

    private void DocumentoDeTrabajador(
        Trabajador trabajador, TipoDocumento tipo, DateOnly emision, VigenciaDocumento vigencia, string contenido, bool archivoInexistente = false) =>
        _documentos.ListaDocumentos.Add(Documento.DeTrabajador(
            trabajador.Id, tipo.Id, emision, vigencia, archivoInexistente ? "no-existe-en-almacenamiento.pdf" : Guardar(contenido)));

    private void DocumentoDeEmpresa(TipoDocumento tipo, DateOnly emision, VigenciaDocumento vigencia, string contenido) =>
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, tipo.Id, emision, vigencia, Guardar(contenido)));

    private string Guardar(string contenido) =>
        _almacenamiento.GuardarAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contenido)), "doc.pdf").GetAwaiter().GetResult();

    private static TipoDocumento NuevoTipo(string nombre, AmbitoAplicacion ambito) =>
        new(nombre, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambito);

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }

    private sealed class LoggerCaptura : ILogger<PaqueteDocumentalVisitaService>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }
}
