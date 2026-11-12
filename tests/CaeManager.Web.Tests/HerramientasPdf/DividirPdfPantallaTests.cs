using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.HerramientasPdf.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Los pasos del asistente con un mediador falso: lo que aquí se prueba es la pantalla, no el alta. Autorización, alcance y
/// guardas viven en <see cref="CrearDocumentosDesdeDivisionCommand"/> y sus tests.
/// </summary>
public sealed class DividirPdfPantallaTests : BunitContext
{
    private static readonly Guid TenantOrigenId = Guid.NewGuid();
    private static readonly Guid TenantAId = Guid.NewGuid();
    private static readonly Guid TenantBId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid HaciendaId = Guid.NewGuid();
    private static readonly Guid AnaId = Guid.NewGuid();
    private static readonly Guid FormacionId = Guid.NewGuid();

    private static readonly CatalogoDivisionPdf Catalogo = new(
        new EmpresaDivisionPdf(EmpresaId, "Montajes Norte S.L.", null), [],
        [new TrabajadorDivisionPdf(AnaId, "Ana", "Pérez", null, EmpresaId)], [],
        [
            new TipoDocumentoDivisionPdf(HaciendaId, "Certificado de Hacienda", AmbitoAplicacion.Empresa, false, null, []),
            new TipoDocumentoDivisionPdf(FormacionId, "Formación PRL", AmbitoAplicacion.Trabajador, false, null, []),
        ]);

    private static readonly PreparacionDivisionPdf Preparacion = new(
        4, false, true,
        [
            new TramoDetectado(1, 2, HaciendaId, "Certificado de Hacienda", ClaseTitular.EmpresaGestionada, EmpresaId, "Montajes Norte S.L.", MotivoDeteccion.CifEmpresaGestionada, 1),
            new TramoDetectado(3, 3, FormacionId, "Formación PRL", ClaseTitular.Trabajador, AnaId, "Ana Pérez", MotivoDeteccion.Nombre, 3),
            new TramoDetectado(4, 4, null, null, null, null, null, MotivoDeteccion.SinTexto, null),
        ],
        Catalogo);

    private readonly MediadorFalso _mediador = new();
    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System);

    /// <summary>
    /// Las vistas previas leen el original página a página, cada una en su <c>Task.Run</c>, y sueltan su reserva fuera del
    /// renderizador. <c>WaitForAssertion</c> solo vuelve a mirar cuando el componente se renderiza, así que si la reserva se
    /// suelta después del último renderizado no la ve nunca, dé el plazo que dé: se espera sondeando el presupuesto.
    /// </summary>
    private static readonly TimeSpan PlazoVistasPrevias = TimeSpan.FromSeconds(15);

    private static async Task EsperarSinReservaAsync(Func<long> reservado, string motivo)
    {
        var plazo = DateTime.UtcNow + PlazoVistasPrevias;
        while (reservado() > 0 && DateTime.UtcNow < plazo)
            await Task.Delay(20);
        reservado().Should().Be(0, motivo);
    }

    public DividirPdfPantallaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        public IReadOnlyList<ClienteAutorizadoDto> Autorizados { get; set; } = [];
        public IReadOnlyList<EmpresaSelectorDto> Empresas { get; set; } = [new EmpresaSelectorDto(EmpresaId, "Montajes Norte S.L.")];
        public Result<PreparacionDivisionPdf> Preparacion { get; set; } = Result.Exito(DividirPdfPantallaTests.Preparacion);
        public List<CrearDocumentosDesdeDivisionCommand> Creaciones { get; } = [];
        public List<ExtraerTramoPdfQuery> Extracciones { get; } = [];
        public int? PaginaQueFallaAlExtraer { get; set; }
        public string CodigoFalloExtraccion { get; set; } = "Prueba.Extraccion";

        /// <summary>Si está, la extracción pasa por la de verdad (<see cref="ExtraccionTramoPdf"/>) con este divisor.</summary>
        public IDivisorPdf? Divisor { get; set; }

        /// <summary>Se llama al extraer cada tramo, con la reserva de la pantalla ya hecha.</summary>
        public Action? AlExtraer { get; set; }

        /// <summary>Si está, marca el lote como cortado por un proceso aparte que no consta terminado.</summary>
        public bool LoteCortadoConEntradaRetenida { get; set; }

        /// <summary>Si está, el resultado de cada tramo de Guardar.</summary>
        public Func<TramoACrear, ResultadoTramoCreado>? ResultadoDe { get; set; }

        private object Responder(object peticion)
        {
            switch (peticion)
            {
                case ObtenerClientesAutorizadosQuery:
                    return Autorizados;
                case ObtenerEmpresasParaSelectorQuery:
                    return Empresas;
                case PrepararDivisionPdfQuery:
                    return Preparacion;
                case CrearDocumentosDesdeDivisionCommand comando:
                    Creaciones.Add(comando);
                    // Cortado, el tramo queda sin preparar y con la entrada retenida, como lo deja el handler real. Con
                    // Indice, como la pantalla lo envía, solo ese tramo.
                    var pedidos = comando.Indice is { } indice ? comando.Tramos.Skip(indice).Take(1) : comando.Tramos;
                    IReadOnlyList<ResultadoTramoCreado> resultados = pedidos
                        .Select(t => ResultadoDe?.Invoke(t) ?? (LoteCortadoConEntradaRetenida
                            ? new ResultadoTramoCreado(t.PaginaInicial, t.PaginaFinal, EstadoTramoCreado.Rechazado, null, "Sin preparar.", EntradaRetenida: true)
                            : new ResultadoTramoCreado(t.PaginaInicial, t.PaginaFinal, EstadoTramoCreado.Creado, Guid.NewGuid(), null)))
                        .ToList();
                    return Result.Exito(resultados);
                case ExtraerTramoPdfQuery extraccion:
                    Extracciones.Add(extraccion);
                    AlExtraer?.Invoke();
                    if (Divisor is { } divisor)
                    {
                        var extraido = ExtraccionTramoPdf.ExtraerAsync(divisor, extraccion.ContenidoPdf, extraccion.PaginaInicial,
                            extraccion.PaginaFinal, NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();
                        return extraido.EsExitoso
                            ? Result.Exito(new TramoPdfExtraido($"paquete-pag-{extraccion.PaginaInicial}-{extraccion.PaginaFinal}.pdf", extraido.Valor))
                            : Result.Fallo<TramoPdfExtraido>(extraido.Error);
                    }

                    return extraccion.PaginaInicial == PaginaQueFallaAlExtraer
                        ? Result.Fallo<TramoPdfExtraido>(Error.Crear(CodigoFalloExtraccion, "No se pudo extraer."))
                        : Result.Exito(new TramoPdfExtraido($"paquete-pag-{extraccion.PaginaInicial}-{extraccion.PaginaFinal}.pdf", [1]));
                default:
                    throw new NotSupportedException(peticion.GetType().Name);
            }
        }

        /// <summary>Si está, el lote de Guardar no responde hasta que se complete.</summary>
        public TaskCompletionSource? CreacionRetenida { get; set; }

        /// <summary>El token con el que la pantalla envió el lote de Guardar.</summary>
        public CancellationToken? TokenCreacion { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is CrearDocumentosDesdeDivisionCommand)
                TokenCreacion = cancellationToken;
            return request is CrearDocumentosDesdeDivisionCommand && CreacionRetenida is { } retenida
                ? ResponderTrasAsync<TResponse>(request, retenida.Task)
                : Task.FromResult((TResponse)Responder(request));
        }

        private async Task<TResponse> ResponderTrasAsync<TResponse>(object peticion, Task espera)
        {
            await espera;
            return (TResponse)Responder(peticion);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioFalso(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    /// <summary>El Tenant activo que lee la pantalla, cambiable a mitad de una descarga como hace el selector lateral.</summary>
    private sealed class TenantCambiable(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    private sealed class SinVistaPrevia : IRasterizadorPaginasPdfService
    {
        public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default) =>
            Result.Fallo<byte[]>(Error.Crear("Prueba.SinVista", "Sin vista previa en esta prueba."));
    }

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    /// <summary>Cuenta las reservas que pide la pantalla y las pasa al presupuesto de verdad.</summary>
    private sealed class PresupuestoContado(IPresupuestoMemoriaDocumentos real) : IPresupuestoMemoriaDocumentos
    {
        public int Peticiones { get; private set; }

        public long Maximo => real.Maximo;

        public ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default)
        {
            Peticiones++;
            return real.ReservarAsync(bytes, cancellationToken);
        }

        public bool TryReservar(long bytes, out ReservaMemoriaDocumentos reserva)
        {
            Peticiones++;
            return real.TryReservar(bytes, out reserva);
        }
    }

    /// <summary>
    /// Un divisor que abre bien y lanza al extraer un tramo, como el proceso aparte cuando no se puede arrancar o se rompe su
    /// tubería: lo que depende del PDF llega tipado en un <c>Result</c>, así que lo que lanza es un fallo de la instancia.
    /// </summary>
    private sealed class DivisorQueLanza(int paginaQueLanza) : IDivisorPdf
    {
        public Task<Result<IPdfParaDividir>> AbrirAsync(byte[] contenidoPdf, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito<IPdfParaDividir>(new Abierto(paginaQueLanza)));

        private sealed class Abierto(int paginaQueLanza) : IPdfParaDividir
        {
            public int NumeroPaginas => 4;
            public bool EstaFirmado => false;

            public Task<Result<byte[]>> ExtraerPaginasAsync(int paginaInicial, int paginaFinal, long tamanoMaximoBytes,
                CancellationToken cancellationToken) => paginaInicial == paginaQueLanza
                ? throw new InvalidOperationException("PDFsharp no pudo copiar la página.")
                : Task.FromResult(Result.Exito(new byte[] { 1 }));

            public void Dispose()
            {
            }
        }
    }

    private IRenderedComponent<DividirPdf> Renderizar(
        string rol = "GestorCae", Guid? tenantId = null, bool disponible = true, IPresupuestoMemoriaDocumentos? presupuesto = null,
        ITenantActual? tenant = null, ILogger<DividirPdf>? logger = null)
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioFalso(rol));
        Services.AddScoped<ITenantActual>(_ => tenant ?? new TenantFijo(tenantId ?? TenantAId));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<IRasterizadorPaginasPdfService, SinVistaPrevia>();
        Services.AddSingleton<IDisponibilidadHerramientasPdf>(new Disponibilidad(disponible));
        Services.AddSingleton<ILogger<DividirPdf>>(_ => logger ?? NullLogger<DividirPdf>.Instance);
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddLocalization();
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton(_ => new CuotaSubidaMasiva(Options.Create(new OpcionesSubidaMasiva
        {
            DirectorioTemporal = Path.Combine(Path.GetTempPath(), "talveg-dividir-pdf-tests"),
        })));
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(presupuesto ?? _presupuesto);
        Services.TryAddSingleton(TimeProvider.System);

        return Render<DividirPdf>();
    }

    [Fact]
    public async Task Tras_dividir_se_elige_entre_guardar_en_TALVEG_y_descargar()
    {
        var cut = Renderizar();
        await SubirAsync(cut);

        cut.Find(".salida-dividir").TextContent.Should().Contain("guardar en TALVEG").And.Contain("descargar cada documento como un PDF aparte");
        var botones = cut.FindAll(".barra-acciones-dividir button").Select(b => b.TextContent.Trim()).ToList();
        botones.Should().Contain(["Descargar 3 PDF", "Guardar en TALVEG"]);
    }

    [Fact]
    public async Task Descargar_entrega_cada_tramo_como_un_PDF_aparte_y_no_crea_ningun_Documento()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".salida-descargada").TextContent.Should().Contain("PDF enviados al navegador: 3. No se ha guardado nada en TALVEG."));
        _mediador.Extracciones.Select(e => (e.PaginaInicial, e.PaginaFinal)).Should().Equal((1, 2), (3, 3), (4, 4));
        _mediador.Extracciones.Should().OnlyContain(e => e.EmpresaGestionadaId == EmpresaId && e.NombreOriginal == "paquete.pdf");
        descarga.Invocations.Select(i => i.Arguments[0]).Should().Equal("paquete-pag-1-2.pdf", "paquete-pag-3-3.pdf", "paquete-pag-4-4.pdf");
        _mediador.Creaciones.Should().BeEmpty("descargar no guarda nada en TALVEG");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Guardar en TALVEG", "después de descargar se puede seguir guardando");
        _presupuesto.Reservado.Should().Be(0, "cada reserva se suelta al entregar su tramo");
    }

    [Fact]
    public async Task Si_la_pantalla_se_desecha_durante_la_descarga_la_reserva_se_devuelve()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        var pendiente = descarga.SetupVoid("descargarPdf", _ => true);
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");
        cut.WaitForAssertion(() => pendiente.Invocations.Should().ContainSingle());
        _presupuesto.Reservado.Should().BeGreaterThan(0, "el tramo en curso tiene su reserva mientras el navegador lo recibe");

        await DisposeComponentsAsync();

        var plazo = DateTime.UtcNow.AddSeconds(5);
        while (_presupuesto.Reservado > 0 && DateTime.UtcNow < plazo)
            await Task.Delay(20);
        _presupuesto.Reservado.Should().Be(0, "la descarga se corta con la pantalla y nadie más devolvería la reserva");
    }

    [Fact]
    public async Task Si_un_tramo_no_se_puede_extraer_se_corta_la_serie_y_se_dice_cual()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.PaginaQueFallaAlExtraer = 3;
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("No se han podido descargar las páginas 3 a 3, ni las siguientes."));
        _mediador.Extracciones.Select(e => e.PaginaInicial).Should().Equal(1, 3);
        descarga.Invocations.Should().ContainSingle();
        cut.Find(".salida-descargada").TextContent.Should().Contain("PDF enviados al navegador: 1.");
    }

    [Fact]
    public async Task Si_el_navegador_no_recibe_un_tramo_se_avisa_sin_dar_la_descarga_por_hecha()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetException(new Microsoft.JSInterop.JSException("el navegador no pudo"));
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("No se han podido descargar las páginas 1 a 2"));
        cut.FindAll(".salida-descargada").Should().BeEmpty();
        _mediador.Creaciones.Should().BeEmpty();
        _presupuesto.Reservado.Should().Be(0, "un fallo del navegador también suelta la reserva");
    }

    [Fact]
    public async Task Descargar_pide_la_extraccion_con_el_Tenant_del_inicio_de_la_pantalla()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => _mediador.Extracciones.Should().HaveCount(3));
        _mediador.Extracciones.Should().OnlyContain(e => e.TenantEsperadoId == TenantAId);
    }

    [Fact]
    public async Task Si_el_Tenant_del_selector_cambia_a_mitad_de_la_descarga_se_dice_que_cambio_la_Empresa()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.PaginaQueFallaAlExtraer = 3;
        _mediador.CodigoFalloExtraccion = CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado;
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("La Empresa del selector ha cambiado desde que subiste el PDF."));
        cut.Find(".aviso-error").TextContent.Should().NotContain("Inténtalo de nuevo", "reintentar con otro Tenant no sirve");
        cut.Find(".aviso-error").TextContent.Should().Contain("Se han descargado 1 de 3 PDF y los demás no")
            .And.NotContain("No se ha creado nada", "el primer tramo ya salió al navegador");
        _mediador.Extracciones.Select(e => e.PaginaInicial).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Si_un_tramo_es_demasiado_grande_se_dice_asi_y_no_se_invita_a_reintentar()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.PaginaQueFallaAlExtraer = 3;
        _mediador.CodigoFalloExtraccion = ErroresDivisorPdf.DemasiadoGrande;
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("Las páginas 3 a 3 ocupan demasiado al separarlas"));
        cut.Find(".aviso-error").TextContent.Should().NotContain("Inténtalo de nuevo")
            .And.Contain("divide en tramos más pequeños o guárdalas en TALVEG, que admite documentos de hasta 10 MB.");
        cut.Find(".salida-descargada").TextContent.Should().Contain("PDF enviados al navegador: 1.");
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_Tenant_cambia_antes_del_primer_tramo_se_dice_que_no_se_descargo_ninguno()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.PaginaQueFallaAlExtraer = 1;
        _mediador.CodigoFalloExtraccion = CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado;
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain(
            "La Empresa del selector ha cambiado desde que subiste el PDF. No se ha descargado ninguno. Vuelve a subirlo."));
        descarga.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_proceso_aparte_no_puede_con_un_tramo_se_dice_que_el_PDF_es_demasiado_complejo_sin_invitar_a_reintentar()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.PaginaQueFallaAlExtraer = 1;
        _mediador.CodigoFalloExtraccion = ErroresDivisorPdf.DemasiadoComplejo;
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("El PDF es demasiado complejo para procesarlo"));
        cut.Find(".aviso-error").TextContent.Should().Contain("Volver a intentarlo no lo arregla").And.NotContain("Inténtalo de nuevo");
        descarga.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_Empresa_sale_de_la_cartera_a_mitad_de_la_descarga_se_dice_asi_y_cuanto_salio_sin_invitar_a_reintentar()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.PaginaQueFallaAlExtraer = 3;
        _mediador.CodigoFalloExtraccion = ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable;
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain(
            "La Empresa ya no existe o ha salido de tu cartera. Se han descargado 1 de 3 PDF y los demás no."));
        cut.Find(".aviso-error").TextContent.Should().NotContain("Inténtalo de nuevo");
    }

    [Fact]
    public async Task Si_el_Tenant_activo_cambia_mientras_se_extrae_un_tramo_ese_tramo_no_se_entrega()
    {
        // La extracción responde bien: lo que corta la entrega es la comprobación de la pantalla contra el Tenant activo.
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var tenant = new TenantCambiable(TenantAId);
        _mediador.AlExtraer = () =>
        {
            if (_mediador.Extracciones.Count == 2)
                tenant.TenantId = TenantBId;
        };
        var cut = Renderizar(tenant: tenant);
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("La Empresa del selector ha cambiado desde que subiste el PDF."));
        descarga.Invocations.Select(i => i.Arguments[0]).Should().Equal(["paquete-pag-1-2.pdf"], "el tramo extraído tras el cambio no sale");
        _mediador.Extracciones.Select(e => e.PaginaInicial).Should().Equal(1, 3);
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_Tenant_activo_cambia_mientras_se_importa_el_modulo_de_descarga_no_se_entrega_ningun_tramo()
    {
        // La carrera de la importación JS: el Tenant cambia mientras el navegador carga el módulo. La extracción de esta
        // prueba no mira el Tenant, así que lo único que corta la entrega es la comprobación de la pantalla tras esa espera.
        var tenant = new TenantCambiable(TenantAId);
        var descarga = JSInterop.SetupModule(invocacion =>
        {
            var esElModulo = invocacion.Arguments.Count > 0 && Equals(invocacion.Arguments[0], "./js/descarga-pdf-unido.js");
            if (esElModulo)
                tenant.TenantId = TenantBId;
            return esElModulo;
        });
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var cut = Renderizar(tenant: tenant);
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("La Empresa del selector ha cambiado desde que subiste el PDF."));
        descarga.Invocations.Should().BeEmpty("ningún tramo sale tras cambiar el Tenant durante la importación");
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_proceso_aparte_no_consta_terminado_al_extraer_la_reserva_del_tramo_no_se_devuelve()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.AlExtraer = () => throw new EntradaPdfRetenidaException();
        var cut = Renderizar();
        await SubirAsync(cut);
        await EsperarSinReservaAsync(() => _presupuesto.Reservado, "las vistas previas ya soltaron su reserva");

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("Ahora mismo no hay espacio para preparar las páginas 1 a 2"));
        descarga.Invocations.Should().BeEmpty();
        // Exacta: la del primer tramo (original de 3 bytes), ni una parte ni la de otra reserva que se quedara colgada.
        _presupuesto.Reservado.Should().Be(LimitesDivisionPdf.MemoriaExtraccionTramo(3),
            "el proceso aparte puede seguir referenciando el original: la reserva entera del tramo en curso no se devuelve");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Si_el_lote_de_Guardar_se_corta_con_la_entrada_retenida_la_reserva_del_original_no_se_devuelve(bool retenida)
    {
        _mediador.LoteCortadoConEntradaRetenida = retenida;
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        await EsperarSinReservaAsync(() => _presupuesto.Reservado, "las vistas previas ya soltaron su reserva");
        cut.Find("button.tijera[aria-label='Quitar la división tras la página 3']").Click();
        Pulsar(cut, "Guardar en TALVEG");
        PonerEmision(cut);
        Pulsar(cut, "Siguiente documento");
        cut.WaitForAssertion(() => cut.FindAll(".marca-omitir input").Should().NotBeEmpty());
        cut.Find(".marca-omitir input").Change(true);
        Pulsar(cut, "Revisar y crear");

        Pulsar(cut, "Crear 1 documento");

        // El resultado del lote se muestra también cuando se cortó.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(retenida ? "0 documentos creados" : "1 documento creado"));
        _mediador.Creaciones.Should().ContainSingle();
        _presupuesto.Reservado.Should().Be(retenida ? LimitesDivisionPdf.MemoriaCreacionTramos(3) : 0,
            retenida
                ? "el proceso aparte que no consta terminado puede seguir referenciando el original: su reserva entera se retiene"
                : "con el lote completo, la reserva del original se devuelve");
    }

    private sealed class RegistroEspia : ILogger<DividirPdf>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Nivel, string Mensaje)> Entradas { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Enqueue((logLevel, formatter(state, exception)));
    }

    private async Task<IRenderedComponent<DividirPdf>> EmpezarLoteDeGuardarRetenidoAsync(ILogger<DividirPdf>? logger = null)
    {
        _mediador.CreacionRetenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Renderizar(logger: logger);
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        await EsperarSinReservaAsync(() => _presupuesto.Reservado, "las vistas previas ya soltaron su reserva");
        cut.Find("button.tijera[aria-label='Quitar la división tras la página 3']").Click();
        Pulsar(cut, "Guardar en TALVEG");
        PonerEmision(cut);
        Pulsar(cut, "Siguiente documento");
        cut.WaitForAssertion(() => cut.FindAll(".marca-omitir input").Should().NotBeEmpty());
        cut.Find(".marca-omitir input").Change(true);
        Pulsar(cut, "Revisar y crear");
        Pulsar(cut, "Crear 1 documento");
        cut.WaitForAssertion(() => _mediador.TokenCreacion.Should().NotBeNull("el lote ya se envió"));
        return cut;
    }

    [Fact]
    public async Task Salir_de_la_pantalla_a_mitad_del_lote_de_Guardar_no_lo_corta()
    {
        var registro = new RegistroEspia();
        await EmpezarLoteDeGuardarRetenidoAsync(registro);

        await DisposeComponentsAsync();

        _mediador.TokenCreacion!.Value.IsCancellationRequested.Should().BeFalse(
            "el resultado parcial solo viviría en la pantalla: cortado, la persona no vería lo creado y lo duplicaría al repetir");
        _mediador.CreacionRetenida!.SetResult();
        await EsperarSinReservaAsync(() => _presupuesto.Reservado, "el lote termina tras salir y devuelve la reserva del original");
        _mediador.Creaciones.Should().ContainSingle().Which.Indice.Should().Be(0,
            "la pantalla envía un Command por tramo (CreacionDivisionPdfPorTramos), no el lote entero, para no retener la puerta");

        // La pantalla ya desechada no toca su estado ni sus recursos dispuestos: solo deja el resultado en el registro.
        var plazo = DateTime.UtcNow.AddSeconds(5);
        while (!registro.Entradas.Any(e => e.Mensaje.Contains("pantalla ya cerrada")) && DateTime.UtcNow < plazo)
            await Task.Delay(20);
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Information
            && e.Mensaje.Contains("pantalla ya cerrada: páginas 1-") && e.Mensaje.Contains("Creado, Documento "));
    }

    [Fact]
    public async Task Tras_salir_de_la_pantalla_el_registro_dice_de_cada_tramo_sus_paginas_su_desenlace_y_su_codigo()
    {
        // Sin nada del PDF ni del titular: las páginas, el estado y el código bastan para saber qué se creó y qué no.
        _mediador.ResultadoDe = t => new ResultadoTramoCreado(t.PaginaInicial, t.PaginaFinal, EstadoTramoCreado.Rechazado, null,
            "Texto que no va al registro.", CodigoError: "Prueba.Rechazo");
        var registro = new RegistroEspia();
        await EmpezarLoteDeGuardarRetenidoAsync(registro);

        await DisposeComponentsAsync();
        _mediador.CreacionRetenida!.SetResult();

        var plazo = DateTime.UtcNow.AddSeconds(5);
        while (!registro.Entradas.Any(e => e.Mensaje.Contains("pantalla ya cerrada")) && DateTime.UtcNow < plazo)
            await Task.Delay(20);
        var entrada = registro.Entradas.Should().ContainSingle(e => e.Mensaje.Contains("pantalla ya cerrada")).Subject;
        entrada.Nivel.Should().Be(LogLevel.Warning);
        entrada.Mensaje.Should().Contain("páginas 1-").And.Contain("Rechazado (Prueba.Rechazo)")
            .And.NotContain("Texto que no va al registro").And.NotContain("paquete");
    }

    [Fact]
    public async Task Al_cerrarse_el_circuito_el_lote_de_Guardar_se_corta()
    {
        await EmpezarLoteDeGuardarRetenidoAsync();

        await Services.GetRequiredService<PuertaAccesoDatos>().CerrarAsync(TimeSpan.FromSeconds(1));

        _mediador.TokenCreacion!.Value.IsCancellationRequested.Should().BeTrue(
            "con el circuito cerrándose, el lote tiene que terminar antes de que su DbContext se disponga");
        _mediador.CreacionRetenida!.SetResult();
    }

    [Fact]
    public async Task Si_el_divisor_lanza_al_extraer_un_tramo_se_corta_la_serie_y_se_dice_cual_sin_tumbar_la_pantalla()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        _mediador.Divisor = new DivisorQueLanza(paginaQueLanza: 3);
        var cut = Renderizar();
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        // Su propio aviso: lo que lanza el divisor es de la instancia, y la pantalla no dice que el PDF esté dañado.
        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should()
            .Contain("Un fallo interno de TALVEG, no del PDF, ha impedido descargar las páginas 3 a 3 y las siguientes."));
        _mediador.Extracciones.Select(e => e.PaginaInicial).Should().Equal(1, 3);
        descarga.Invocations.Should().ContainSingle();
        cut.Find(".salida-descargada").TextContent.Should().Contain("PDF enviados al navegador: 1.");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Guardar en TALVEG", "la pantalla sigue viva tras el fallo");
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task La_reserva_de_un_tramo_cubre_la_extraccion_y_se_reduce_al_tramo_sin_soltarse_entre_medias()
    {
        var contado = new PresupuestoContado(_presupuesto);
        var reservadoAlExtraer = new List<long>();
        _mediador.AlExtraer = () => reservadoAlExtraer.Add(_presupuesto.Reservado);
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        var pendiente = descarga.SetupVoid("descargarPdf", _ => true);
        var cut = Renderizar(presupuesto: contado);
        await SubirAsync(cut);
        await EsperarSinReservaAsync(() => _presupuesto.Reservado, "las vistas previas ya soltaron su reserva");
        var peticionesAntes = contado.Peticiones;

        Pulsar(cut, "Descargar 3 PDF");
        cut.WaitForAssertion(() => pendiente.Invocations.Should().ContainSingle());

        reservadoAlExtraer.Should().Equal([LimitesDivisionPdf.MemoriaExtraccionTramo(3)],
            "mientras se extrae coexisten la copia del original, lo que PDFsharp construye y las copias del guardado");
        _presupuesto.Reservado.Should().Be(1, "mientras el navegador recibe el tramo solo queda reservado el tramo");
        (contado.Peticiones - peticionesAntes).Should().Be(1,
            "la reserva de la extracción se reduce al tramo: soltarla y pedir otra dejaría el tramo vivo fuera del presupuesto");
    }

    [Fact]
    public async Task Durante_la_descarga_no_se_pueden_tocar_los_cortes()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        var pendiente = descarga.SetupVoid("descargarPdf", _ => true);
        var cut = Renderizar();
        await SubirAsync(cut);
        var antes = cut.FindAll("button.tijera").Select(b => b.GetAttribute("aria-pressed")).ToList();

        Pulsar(cut, "Descargar 3 PDF");
        cut.WaitForAssertion(() => pendiente.Invocations.Should().ContainSingle());
        cut.FindAll("button.tijera")[0].Click();
        pendiente.SetVoidResult();

        cut.WaitForAssertion(() => cut.Find(".salida-descargada").TextContent.Should().Contain("PDF enviados al navegador: 3."));
        cut.FindAll("button.tijera").Select(b => b.GetAttribute("aria-pressed")).Should().Equal(antes);
        _mediador.Extracciones.Select(e => (e.PaginaInicial, e.PaginaFinal)).Should().Equal((1, 2), (3, 3), (4, 4));
    }

    [Fact]
    public async Task Si_el_temporal_del_original_desaparece_se_dice_que_tramo_no_salio()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var cut = Renderizar();
        await SubirAsync(cut);
        await EsperarSinReservaAsync(() => _presupuesto.Reservado, "las vistas previas ya leyeron el original");
        var proceso = Services.GetRequiredService<CuotaSubidaMasiva>().DirectorioProceso;
        foreach (var directorio in Directory.GetDirectories(proceso))
            foreach (var fichero in Directory.GetFiles(directorio))
                File.Delete(fichero);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain("No se han podido descargar las páginas 1 a 2, ni las siguientes."));
        _mediador.Extracciones.Should().BeEmpty();
        descarga.Invocations.Should().BeEmpty();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_espacio_en_el_presupuesto_se_dice_que_tramo_no_salio()
    {
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var corto = new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { EsperaMaxima = TimeSpan.FromMilliseconds(100) }), TimeProvider.System);
        var cut = Renderizar(presupuesto: corto);
        await SubirAsync(cut);
        await EsperarSinReservaAsync(() => corto.Reservado, "las vistas previas ya soltaron su reserva");
        // Una reserva ajena de todo el presupuesto: la del primer tramo espera y agota el plazo.
        using var ocupado = await corto.ReservarAsync(corto.Maximo);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should()
            .Contain("Ahora mismo no hay espacio para preparar las páginas 1 a 2, ni las siguientes."));
        _mediador.Extracciones.Should().BeEmpty();
    }

    private const string AvisoExcedePresupuesto = "Este PDF es demasiado grande para separarlo con la memoria que tiene asignada este servidor.";

    private static PresupuestoMemoriaDocumentos PresupuestoDe(long maximo) => new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = maximo, EsperaMaxima = TimeSpan.FromMilliseconds(100) }),
        TimeProvider.System);

    [Fact]
    public async Task Descargar_con_un_presupuesto_menor_que_el_pico_de_un_tramo_avisa_sin_extraer_nada()
    {
        // MEDIO de la ronda 4: la reserva se recortaba al presupuesto entero y el tramo salía sin su pico cubierto.
        var descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
        var corto = PresupuestoDe(LimitesDivisionPdf.MemoriaExtraccionTramo(3) - 1);
        var cut = Renderizar(presupuesto: corto);
        await SubirAsync(cut);

        Pulsar(cut, "Descargar 3 PDF");

        cut.WaitForAssertion(() => cut.Find(".aviso-error").TextContent.Should().Contain(AvisoExcedePresupuesto));
        _mediador.Extracciones.Should().BeEmpty();
        descarga.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guardar_sin_sitio_en_el_presupuesto_para_su_reserva_y_la_del_cifrado_avisa_sin_crear_nada(bool cabeLaDeLaPantalla)
    {
        // MEDIO de la ronda 4: con su reserva recortada no cubre su pico, y si solo cabe la suya, el cifrado de cada tramo
        // esperaría a una reserva que la propia pantalla retiene.
        var maximo = cabeLaDeLaPantalla
            ? LimitesDivisionPdf.MemoriaGuardarTramos(3) - 1
            : LimitesDivisionPdf.MemoriaCreacionTramos(3) - 1;
        var cut = Renderizar(presupuesto: PresupuestoDe(maximo));
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        cut.Find("button.tijera[aria-label='Quitar la división tras la página 3']").Click();
        Pulsar(cut, "Guardar en TALVEG");
        PonerEmision(cut);
        Pulsar(cut, "Siguiente documento");
        cut.WaitForAssertion(() => cut.FindAll(".marca-omitir input").Should().NotBeEmpty());
        cut.Find(".marca-omitir input").Change(true);
        Pulsar(cut, "Revisar y crear");

        Pulsar(cut, "Crear 1 documento");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(AvisoExcedePresupuesto));
        _mediador.Creaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task El_rotulo_dice_Empresa_y_no_Empresa_gestionada()
    {
        var cut = Renderizar();
        await SubirAsync(cut);

        cut.Find(".cabecera-dividir").TextContent.Should().Contain("Empresa:").And.NotContain("gestionada");
        Pulsar(cut, "Guardar en TALVEG");
        cut.WaitForAssertion(() => cut.FindAll("label").Should().Contain(l => l.TextContent.Trim() == "Empresa", "«Es de» ofrece «Empresa», no «Empresa gestionada»"));
        cut.Markup.Should().NotContain("Empresa gestionada");
    }

    private static async Task SubirAsync(IRenderedComponent<DividirPdf> cut)
    {
        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "paquete.pdf")));
        cut.WaitForAssertion(() => cut.FindAll("button.tijera").Should().NotBeEmpty());
    }

    private static void Pulsar(IRenderedComponent<DividirPdf> cut, string texto)
    {
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == texto));
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).Click();
    }

    private static void PonerEmision(IRenderedComponent<DividirPdf> cut)
    {
        cut.WaitForAssertion(() => cut.FindAll("input[placeholder=\"dd/mm/aaaa\"]").Should().NotBeEmpty());
        cut.FindAll("input[placeholder=\"dd/mm/aaaa\"]")[0].Input("15/01/2026");
        // CampoTexto retiene 300 ms lo tecleado y lo entrega al perder el foco, que en el navegador va antes del clic.
        cut.FindAll("input[placeholder=\"dd/mm/aaaa\"]")[0].Blur();
    }

    [Fact]
    public void Sin_Empresa_seleccionada_se_bloquea_con_el_aviso_de_la_maqueta()
    {
        _mediador.Autorizados =
        [
            new ClienteAutorizadoDto(TenantOrigenId, "Operador", EsOrigen: true),
            new ClienteAutorizadoDto(TenantAId, "Tenant A", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(TenantBId, "Tenant B", EsOrigen: false, EsGestionadoPorOperacion: true),
        ];

        var cut = Renderizar(tenantId: TenantOrigenId);

        cut.WaitForAssertion(() => cut.Find(".aviso-dividir").TextContent.Should().Contain("Elige primero una Empresa en el selector de la barra lateral"));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
        cut.Find(".zona-bloqueada").TextContent.Should().Contain("Elige una Empresa para poder subir el PDF");
    }

    [Fact]
    public void Si_el_Tenant_del_selector_lateral_no_coincide_con_ninguna_Empresa_se_elige_en_la_pagina()
    {
        _mediador.Autorizados =
        [
            new ClienteAutorizadoDto(TenantOrigenId, "Operador", EsOrigen: true),
            new ClienteAutorizadoDto(TenantAId, "Tenant A", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(TenantBId, "Tenant B", EsOrigen: false, EsGestionadoPorOperacion: true),
        ];
        _mediador.Empresas = [new EmpresaSelectorDto(EmpresaId, "Montajes Norte S.L."), new EmpresaSelectorDto(Guid.NewGuid(), "Otra Empresa S.L.")];

        var cut = Renderizar(tenantId: TenantAId);

        cut.WaitForAssertion(() => cut.FindAll("select").Should().ContainSingle());
        cut.FindAll(".aviso-dividir").Should().BeEmpty();
    }

    [Fact]
    public void Con_varias_Empresas_propias_y_sin_selector_lateral_se_elige_en_la_pagina_antes_de_subir()
    {
        var otra = new EmpresaSelectorDto(Guid.NewGuid(), "Otra Empresa S.L.");
        _mediador.Empresas = [new EmpresaSelectorDto(EmpresaId, "Montajes Norte S.L."), otra];

        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.FindAll(".zona-bloqueada").Should().ContainSingle());
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();

        cut.Find("select").Change(EmpresaId.ToString());

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        cut.Find(".cabecera-dividir").TextContent.Should().Contain("Montajes Norte S.L.").And.NotContain("barra lateral");
    }

    [Fact]
    public void Consulta_no_puede_empezar_a_subir()
    {
        var cut = Renderizar(rol: "Consulta");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("solo lectura"));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
    }

    [Fact]
    public void Si_el_plan_no_la_incluye_no_hay_nada_que_subir()
    {
        var cut = Renderizar(disponible: false);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Herramientas PDF no está disponible"));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_PDF_con_contrasena_se_avisa_y_no_avanza()
    {
        _mediador.Preparacion = Result.Fallo<PreparacionDivisionPdf>(Error.Crear(ErroresDivisorPdf.ConContrasena, "x"));
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1], "cerrado.pdf")));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("El PDF está protegido o cifrado y no se puede dividir así. Ábrelo y guárdalo de nuevo como PDF sin protección (por ejemplo, imprimiéndolo a PDF)"));
        cut.Markup.Should().NotContain("Quítala", "un PDF con solo contraseña de propietario no tiene contraseña que quitar");
        cut.FindAll("button.tijera").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_PDF_con_una_estructura_demasiado_grande_se_avisa_con_su_motivo_y_no_avanza()
    {
        _mediador.Preparacion = Result.Fallo<PreparacionDivisionPdf>(Error.Crear(ErroresDivisorPdf.DemasiadoComplejo, "x"));
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1], "bomba.pdf")));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("estructura interna demasiado grande para dividirlo"));
        cut.FindAll("button.tijera").Should().BeEmpty();
    }

    [Fact]
    public async Task El_primer_paso_enseña_cortes_propuestos_y_el_motivo_sin_porcentajes()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());

        await SubirAsync(cut);

        cut.FindAll("button.tijera").Should().HaveCount(3);
        cut.FindAll("button.tijera-sugerida").Should().HaveCount(2);
        var detectado = cut.Find(".detectado").TextContent;
        detectado.Should().Contain("CIF leído en la pág. 1").And.Contain("Nombre leído en la pág. 3").And.Contain("Sin identificar");
        detectado.Should().NotContain("%");
    }

    [Fact]
    public async Task Un_PDF_firmado_solo_avisa()
    {
        _mediador.Preparacion = Result.Exito(Preparacion with { EstaFirmado = true });
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());

        await SubirAsync(cut);

        cut.Markup.Should().Contain("Este PDF está firmado");
        cut.FindAll("button.tijera").Should().HaveCount(3);
    }

    [Fact]
    public async Task Volver_atras_no_pierde_los_cortes_ni_lo_escrito()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);

        cut.Find("button.tijera[aria-label='Quitar la división tras la página 3']").Click();
        cut.WaitForAssertion(() => cut.FindAll("button.tijera-sugerida").Should().HaveCount(1));
        Pulsar(cut, "Guardar en TALVEG");
        PonerEmision(cut);

        Pulsar(cut, "Volver a dividir");

        cut.WaitForAssertion(() => cut.FindAll("button.tijera-sugerida").Should().HaveCount(1, "el corte retirado sigue retirado"));
        cut.Find("button.tijera[aria-label='Dividir tras la página 3']").Should().NotBeNull();

        Pulsar(cut, "Guardar en TALVEG");

        cut.WaitForAssertion(() => cut.FindAll("input[placeholder=\"dd/mm/aaaa\"]")[0].GetAttribute("value").Should().Be("15/01/2026"));
    }

    [Fact]
    public async Task Un_tramo_omitido_no_se_crea_y_se_puede_restaurar()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        cut.Find("button.tijera[aria-label='Quitar la división tras la página 3']").Click();
        Pulsar(cut, "Guardar en TALVEG");

        PonerEmision(cut);
        Pulsar(cut, "Siguiente documento");
        cut.WaitForAssertion(() => cut.FindAll(".marca-omitir input").Should().NotBeEmpty());
        cut.Find(".marca-omitir input").Change(true);
        Pulsar(cut, "Revisar y crear");

        cut.WaitForAssertion(() => cut.Find(".fila-omitida").TextContent.Should().Contain("No se crea"));
        Pulsar(cut, "Crear 1 documento");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1 documento creado"));
        var comando = _mediador.Creaciones.Should().ContainSingle().Subject;
        comando.Tramos.Select(t => (t.PaginaInicial, t.PaginaFinal)).Should().Equal((1, 2));
        comando.EmpresaGestionadaId.Should().Be(EmpresaId);
        comando.TenantEsperadoId.Should().Be(TenantAId);
        comando.NombreOriginal.Should().Be("paquete.pdf");
    }

    [Fact]
    public async Task Restaurar_un_tramo_omitido_lo_devuelve_a_la_revision()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        Pulsar(cut, "Guardar en TALVEG");
        cut.WaitForAssertion(() => cut.FindAll(".marca-omitir input").Should().NotBeEmpty());
        cut.Find(".marca-omitir input").Change(true);
        Pulsar(cut, "Siguiente documento");
        Pulsar(cut, "Siguiente documento");
        Pulsar(cut, "Revisar y crear");

        Pulsar(cut, "Restaurar");

        cut.WaitForAssertion(() => cut.FindAll(".fila-omitida").Should().BeEmpty());
    }

    [Fact]
    public async Task Sin_datos_suficientes_no_hay_nada_que_crear_y_no_se_inventa_un_alta()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        Pulsar(cut, "Guardar en TALVEG");
        Pulsar(cut, "Siguiente documento");
        Pulsar(cut, "Siguiente documento");
        Pulsar(cut, "Revisar y crear");

        cut.WaitForAssertion(() => cut.FindAll("button").First(b => b.TextContent.Contains("No hay documentos que crear")).HasAttribute("disabled").Should().BeTrue());
        cut.FindAll(".falta-dato").Should().HaveCount(3);
        _mediador.Creaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tramo_sin_identificar_se_asigna_a_mano()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        Pulsar(cut, "Guardar en TALVEG");
        Pulsar(cut, "Siguiente documento");
        Pulsar(cut, "Siguiente documento");

        cut.WaitForAssertion(() => cut.Find(".cabecera-tramo").TextContent.Should().Contain("Documento 3 de 3"));
        cut.FindAll(".es-de input")[2].Change(true);
        cut.WaitForAssertion(() => cut.Find(".campos-tramo").TextContent.Should().Contain("Elige un Trabajador"));
        cut.FindAll(".campos-tramo select")[1].Change(AnaId.ToString());
        cut.WaitForAssertion(() => cut.FindAll(".campos-tramo select")[1].GetAttribute("value").Should().Be(AnaId.ToString()));
        cut.FindAll(".campos-tramo select")[2].Change(FormacionId.ToString());
        cut.WaitForAssertion(() => cut.FindAll(".campos-tramo select")[2].GetAttribute("value").Should().Be(FormacionId.ToString()));
        PonerEmision(cut);
        cut.WaitForAssertion(() => cut.Find(".campos-tramo").TextContent.Should().NotContain("Falta:"));
        Pulsar(cut, "Revisar y crear");

        Pulsar(cut, "Crear 1 documento");

        cut.WaitForAssertion(() => _mediador.Creaciones.Should().ContainSingle());
        _mediador.Creaciones[0].Tramos.Should().ContainSingle().Which.Should().Be(
            new TramoACrear(4, 4, ClaseTitular.Trabajador, AnaId, FormacionId, new DateOnly(2026, 1, 15), null, false));
    }
}
