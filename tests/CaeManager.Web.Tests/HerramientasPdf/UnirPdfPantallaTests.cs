using System.Security.Claims;
using Bunit;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.HerramientasPdf.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// La pantalla «Unir PDF» con un mediador falso: lo que aquí se prueba es la lista, su orden, los avisos y la descarga. El
/// alcance de cartera, el rol, los límites y el registro de acceso viven en <see cref="UnirPdfQuery"/> y sus tests.
/// </summary>
public sealed class UnirPdfPantallaTests : BunitContext
{
    private static readonly Guid TenantOrigenId = Guid.NewGuid();
    private static readonly Guid TenantAId = Guid.NewGuid();
    private static readonly Guid TenantBId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();

    private readonly MediadorFalso _mediador = new();
    private readonly BunitJSModuleInterop _descarga;

    public UnirPdfPantallaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _descarga = JSInterop.SetupModule("./js/descarga-pdf-unido.js");
        _descarga.SetupVoid("descargarPdf", _ => true).SetVoidResult();
    }

    private sealed class MediadorFalso : IMediator
    {
        public IReadOnlyList<ClienteAutorizadoDto> Autorizados { get; set; } = [];
        public IReadOnlyList<EmpresaSelectorDto> Empresas { get; set; } = [new EmpresaSelectorDto(EmpresaId, "Montajes Norte S.L.")];
        public IReadOnlyList<DocumentoParaUnirDto> Documentos { get; set; } = [];
        public bool HayMasDocumentos { get; set; }
        public Func<PrepararUnionPdfQuery, Result<InfoPdfParaUnir>> Preparar { get; set; } = _ => Result.Exito(new InfoPdfParaUnir(2, false));
        public Func<UnirPdfQuery, Result<ResultadoUnionPdf>> Unir { get; set; } = q =>
            Result.Exito(new ResultadoUnionPdf(EstadoUnionPdf.Unido, [1, 2, 3], "Montajes-Norte-S.L._unido_2026-11-10.pdf", 4, [], null));
        public Exception? ExcepcionAlUnir { get; set; }
        public List<UnirPdfQuery> Uniones { get; } = [];

        private object Responder(object peticion)
        {
            switch (peticion)
            {
                case ObtenerClientesAutorizadosQuery:
                    return Autorizados;
                case ObtenerEmpresasParaSelectorQuery:
                    return Empresas;
                case PrepararUnionPdfQuery preparar:
                    return Preparar(preparar);
                case ListarDocumentosParaUnirQuery:
                    return Result.Exito(new DocumentosParaUnirDto(Documentos, HayMasDocumentos));
                case UnirPdfQuery unir:
                    Uniones.Add(unir);
                    if (ExcepcionAlUnir is not null) throw ExcepcionAlUnir;
                    return Unir(unir);
                default:
                    throw new NotSupportedException(peticion.GetType().Name);
            }
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)Responder(request));

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

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    /// <summary>Lo que el límite de errores contiene sin mostrarlo: así un fallo de la pantalla no pasa por un simple tiempo de espera agotado.</summary>
    private sealed class AlertasQueAnotan : IAlertaOperativa
    {
        public List<Exception> Excepciones { get; } = [];
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) => Excepciones.Add(excepcion);
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();

        private sealed class Nada : IDisposable
        {
            public void Dispose() { }
        }
    }

    private readonly AlertasQueAnotan _alertas = new();

    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    private IRenderedComponent<UnirPdf> Renderizar(string rol = "GestorCae", Guid? tenantId = null, bool disponible = true)
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioFalso(rol));
        Services.AddScoped<ITenantActual>(_ => new TenantFijo(tenantId ?? TenantAId));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddSingleton<IDisponibilidadHerramientasPdf>(new Disponibilidad(disponible));
        Services.AddSingleton<ILogger<UnirPdf>>(_ => NullLogger<UnirPdf>.Instance);
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddSingleton<IAlertaOperativa>(_alertas);
        Services.AddAuthorizationCore();
        Services.AddLocalization();
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton(_ => new CuotaSubidaMasiva(Options.Create(new OpcionesSubidaMasiva
        {
            DirectorioTemporal = Path.Combine(Path.GetTempPath(), "talveg-unir-pdf-tests"),
        })));
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System));

        return Render<UnirPdf>();
    }

    /// <summary>Espera como <c>WaitForAssertion</c>, pero si el límite de errores contuvo una excepción, falla con ella.</summary>
    private void EsperarSinErrores(IRenderedComponent<UnirPdf> cut, Action asercion)
    {
        try
        {
            cut.WaitForAssertion(asercion);
        }
        catch (Exception) when (_alertas.Excepciones.Count > 0)
        {
            throw new AggregateException(_alertas.Excepciones);
        }
    }

    private static InputFileContent Pdf(string nombre) => InputFileContent.CreateFromBinary("%PDF-1.4 x"u8.ToArray(), nombre);

    private static async Task SubirAsync(IRenderedComponent<UnirPdf> cut, params string[] nombres) =>
        await SubirHastaAsync(cut, nombres.Length, nombres);

    /// <summary>Sube los PDF y espera a que la lista llegue a <paramref name="totalEnLista"/> elementos.</summary>
    private static async Task SubirHastaAsync(IRenderedComponent<UnirPdf> cut, int totalEnLista, params string[] nombres)
    {
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(nombres.Select(Pdf).ToArray()));
        cut.WaitForAssertion(() => cut.FindAll("ol.lista-unir li").Should().HaveCount(totalEnLista));
    }

    private static string[] NombresEnLista(IRenderedComponent<UnirPdf> cut) =>
        cut.FindAll("ol.lista-unir li .nombre-unir strong").Select(e => e.TextContent.Trim()).ToArray();

    private static void PulsarBoton(IRenderedComponent<UnirPdf> cut, string texto)
    {
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == texto));
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).Click();
    }

    private static void PulsarPorEtiqueta(IRenderedComponent<UnirPdf> cut, string etiqueta) =>
        cut.Find($"button[aria-label=\"{etiqueta}\"]").Click();

    private static bool Habilitado(IRenderedComponent<UnirPdf> cut, string texto) =>
        !cut.FindAll("button").First(b => b.TextContent.Trim() == texto).HasAttribute("disabled");

    // ---- Estados de entrada

    [Fact]
    public void Sin_Empresa_seleccionada_se_bloquea_y_no_deja_subir()
    {
        _mediador.Autorizados =
        [
            new ClienteAutorizadoDto(TenantOrigenId, "Operador", EsOrigen: true),
            new ClienteAutorizadoDto(TenantAId, "Tenant A", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(TenantBId, "Tenant B", EsOrigen: false, EsGestionadoPorOperacion: true),
        ];

        var cut = Renderizar(tenantId: TenantOrigenId);

        cut.WaitForAssertion(() => cut.Find(".aviso-unir").TextContent.Should().Contain("Elige primero una Empresa gestionada"));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
        cut.Find(".zona-bloqueada").TextContent.Should().Contain("Elige una Empresa gestionada para poder añadir PDF");
    }

    [Fact]
    public void Con_varias_Empresas_propias_y_sin_selector_lateral_se_elige_en_la_pagina_antes_de_subir()
    {
        _mediador.Empresas = [new EmpresaSelectorDto(EmpresaId, "Montajes Norte S.L."), new EmpresaSelectorDto(Guid.NewGuid(), "Otra Empresa S.L.")];

        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.FindAll(".zona-bloqueada").Should().ContainSingle());
        cut.Find("select").Change(EmpresaId.ToString());

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
    }

    [Fact]
    public void Si_el_plan_no_la_incluye_no_hay_nada_que_unir()
    {
        var cut = Renderizar(disponible: false);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Herramientas PDF no está disponible"));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
    }

    // ---- Lista y orden

    [Fact]
    public async Task Los_PDF_subidos_entran_en_el_orden_de_subida_con_sus_paginas_y_hacen_falta_dos_para_unir()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf");

        Habilitado(cut, "Unir y descargar").Should().BeFalse("con un solo PDF no hay nada que unir");

        await SubirHastaAsync(cut, 2, "b.pdf");

        NombresEnLista(cut).Should().Equal("a.pdf", "b.pdf");
        cut.Find("ol.lista-unir").TextContent.Should().Contain("2 páginas");
        Habilitado(cut, "Unir y descargar").Should().BeTrue();
    }

    [Fact]
    public async Task Subir_y_bajar_cambia_el_orden_que_se_envia_a_unir()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf", "c.pdf");

        PulsarPorEtiqueta(cut, "Bajar a.pdf una posición");
        NombresEnLista(cut).Should().Equal("b.pdf", "a.pdf", "c.pdf");
        PulsarPorEtiqueta(cut, "Subir c.pdf una posición");
        NombresEnLista(cut).Should().Equal("b.pdf", "c.pdf", "a.pdf");

        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => _mediador.Uniones.Should().ContainSingle());

        _mediador.Uniones[0].Entradas.OfType<EntradaUnionSubida>().Select(e => e.NombreArchivo).Should().Equal("b.pdf", "c.pdf", "a.pdf");
        _mediador.Uniones[0].EmpresaGestionadaId.Should().Be(EmpresaId);
        _mediador.Uniones[0].TenantEsperadoId.Should().Be(TenantAId);
        _mediador.Uniones[0].FirmasConfirmadas.Should().BeNull();
    }

    [Fact]
    public async Task El_primero_no_sube_y_el_ultimo_no_baja()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");

        cut.Find("button[aria-label=\"Subir a.pdf una posición\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[aria-label=\"Bajar b.pdf una posición\"]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Quitar_un_PDF_lo_saca_de_la_lista_y_deja_de_poderse_unir_con_uno()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");

        PulsarPorEtiqueta(cut, "Quitar a.pdf");

        NombresEnLista(cut).Should().Equal("b.pdf");
        Habilitado(cut, "Unir y descargar").Should().BeFalse();
    }

    // ---- Subida rechazada

    [Fact]
    public async Task Un_PDF_con_contrasena_se_avisa_con_su_nombre_y_no_entra_en_la_lista()
    {
        _mediador.Preparar = _ => Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.ConContrasena, "x"));
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(Pdf("secreto.pdf")));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("secreto.pdf tiene contraseña"));
        cut.FindAll("ol.lista-unir li").Should().BeEmpty();
    }

    // ---- Firma electrónica

    [Fact]
    public async Task Un_PDF_firmado_se_marca_en_la_lista_y_se_avisa_antes_de_unir()
    {
        _mediador.Preparar = q => Result.Exito(new InfoPdfParaUnir(1, true));
        var cut = Renderizar();
        await SubirAsync(cut, "firmado.pdf", "otro.pdf");

        cut.Find("ol.lista-unir").TextContent.Should().Contain("con firma electrónica");
        cut.Find(".aviso-unir[role=status]").TextContent.Should().Contain("la pierde");
    }

    [Fact]
    public async Task Si_la_union_responde_que_hay_firmas_sin_confirmar_se_pregunta_y_unir_igualmente_reenvia_confirmado()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");
        _mediador.Unir = q => q.FirmasConfirmadas == "HUELLA-1"
            ? Result.Exito(new ResultadoUnionPdf(EstadoUnionPdf.Unido, [1], "unido.pdf", 4, [], null))
            : Result.Exito(ResultadoUnionPdf.ConFirmas([1], "HUELLA-1"));

        PulsarBoton(cut, "Unir y descargar");

        cut.WaitForAssertion(() => cut.Find("[role=alertdialog]").TextContent.Should().Contain("Perderán la firma electrónica").And.Contain("b.pdf"));
        _descarga.Invocations.Should().BeEmpty("sin confirmar no se descarga nada");

        PulsarBoton(cut, "Unir igualmente");

        EsperarSinErrores(cut, () => cut.Find("h1").TextContent.Should().Contain("PDF unido: 4 páginas"));
        _mediador.Uniones.Select(u => u.FirmasConfirmadas).Should().Equal(null, "HUELLA-1");
        _descarga.VerifyInvoke("descargarPdf");
    }

    [Fact]
    public async Task Revisar_la_lista_cierra_el_aviso_sin_unir()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");
        _mediador.Unir = _ => Result.Exito(ResultadoUnionPdf.ConFirmas([0], "HUELLA-1"));

        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => cut.FindAll("[role=alertdialog]").Should().ContainSingle());
        PulsarBoton(cut, "Revisar la lista");

        cut.FindAll("[role=alertdialog]").Should().BeEmpty();
        _mediador.Uniones.Should().ContainSingle();

        // La confirmación no sobrevive al aviso cerrado: unir de nuevo vuelve a preguntar.
        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => _mediador.Uniones.Should().HaveCount(2));
        _mediador.Uniones[1].FirmasConfirmadas.Should().BeNull();
    }

    [Fact]
    public async Task Si_al_confirmar_los_PDF_firmados_ya_no_son_los_del_aviso_se_vuelve_a_preguntar_y_no_se_descarga()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");
        // La segunda respuesta trae otra huella: entre el aviso y la confirmación cambió un PDF firmado.
        var respuestas = new Queue<string>(["HUELLA-1", "HUELLA-2"]);
        _mediador.Unir = q => q.FirmasConfirmadas == "HUELLA-2"
            ? Result.Exito(new ResultadoUnionPdf(EstadoUnionPdf.Unido, [1], "unido.pdf", 4, [], null))
            : Result.Exito(ResultadoUnionPdf.ConFirmas([1], respuestas.Dequeue()));

        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => cut.FindAll("[role=alertdialog]").Should().ContainSingle());
        PulsarBoton(cut, "Unir igualmente");

        cut.WaitForAssertion(() => _mediador.Uniones.Should().HaveCount(2));
        _mediador.Uniones.Select(u => u.FirmasConfirmadas).Should().Equal(null, "HUELLA-1");
        cut.WaitForAssertion(() => cut.FindAll("[role=alertdialog]").Should().ContainSingle("se vuelve a pedir la confirmación"));
        _descarga.Invocations.Should().BeEmpty();

        PulsarBoton(cut, "Unir igualmente");
        cut.WaitForAssertion(() => _mediador.Uniones.Should().HaveCount(3));
        _mediador.Uniones[2].FirmasConfirmadas.Should().Be("HUELLA-2", "se confirma la huella del último aviso, no la del primero");
        EsperarSinErrores(cut, () => cut.Find("h1").TextContent.Should().Contain("PDF unido: 4 páginas"));
        _descarga.VerifyInvoke("descargarPdf");
    }

    [Fact]
    public void Si_hay_mas_Documentos_que_el_tope_del_listado_la_pantalla_lo_dice()
    {
        _mediador.Documentos = [new DocumentoParaUnirDto(Guid.NewGuid(), "Seguro RC", null, new DateOnly(2026, 1, 15), null)];
        _mediador.HayMasDocumentos = true;
        var cut = Renderizar();

        PulsarBoton(cut, "Elegir de Documentos");

        cut.WaitForAssertion(() => cut.Find(".documentos-unir [role=note]").TextContent.Should().Contain("Hay más"));
    }

    [Fact]
    public void Sin_mas_Documentos_que_el_tope_no_hay_aviso_de_recorte()
    {
        _mediador.Documentos = [new DocumentoParaUnirDto(Guid.NewGuid(), "Seguro RC", null, new DateOnly(2026, 1, 15), null)];
        var cut = Renderizar();

        PulsarBoton(cut, "Elegir de Documentos");

        cut.WaitForAssertion(() => cut.Find(".documentos-unir").TextContent.Should().Contain("Seguro RC"));
        cut.FindAll(".documentos-unir [role=note]").Should().BeEmpty();
    }

    // ---- Resultado

    [Fact]
    public async Task Al_unir_se_descarga_se_dice_que_no_se_guarda_y_se_puede_empezar_de_nuevo()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");

        PulsarBoton(cut, "Unir y descargar");

        EsperarSinErrores(cut, () => cut.Find("h1").TextContent.Should().Contain("PDF unido: 4 páginas"));
        var llamada = _descarga.Invocations["descargarPdf"].Should().ContainSingle().Subject;
        llamada.Arguments[0].Should().Be("Montajes-Norte-S.L._unido_2026-11-10.pdf");
        cut.Find("[role=note]").TextContent.Should().Contain("no se ha guardado en TALVEG");

        PulsarBoton(cut, "Unir otros PDF");

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        cut.FindAll("ol.lista-unir li").Should().BeEmpty();
    }

    // ---- Errores de la operación

    [Theory]
    [InlineData(ErroresUnirPdf.TenantCruzado, "ha cambiado")]
    [InlineData(ErroresUnirPdf.EmpresaNoGestionable, "queda fuera de tu cartera")]
    [InlineData(ErroresUnirPdf.RolSinAcceso, "Tu rol no permite")]
    [InlineData(ErroresUnirPdf.SesionPrivilegiadaSinLectura, "sesión privilegiada")]
    [InlineData(ErroresUnirPdf.TotalDemasiadoGrande, "pesan juntos más de 50 MB")]
    [InlineData(ErroresUnirPdf.TotalDemasiadasPaginas, "pasaría de 500 páginas")]
    public async Task Un_fallo_de_la_operacion_se_dice_sin_descargar_nada(string codigo, string texto)
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");
        _mediador.Unir = _ => Result.Fallo<ResultadoUnionPdf>(Error.Crear(codigo, "x"));

        PulsarBoton(cut, "Unir y descargar");

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain(texto));
        _descarga.Invocations.Should().BeEmpty();
        cut.FindAll("ol.lista-unir li").Should().HaveCount(2, "la lista se conserva para poder corregirla");
    }

    [Fact]
    public async Task Un_PDF_rechazado_por_la_union_se_senala_por_su_nombre()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");
        _mediador.Unir = _ => Result.Exito(ResultadoUnionPdf.Rechazada(1, ErroresUnirPdf.ConContrasena));

        PulsarBoton(cut, "Unir y descargar");

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("b.pdf tiene contraseña"));
        _descarga.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_demasiado_grande_cita_el_tope_del_total_y_una_subida_el_suyo()
    {
        // Un Documento almacenado puede pasar de 10 MB: solo se rechaza al pasar de lo que cabe en la unión.
        _mediador.Documentos = [new DocumentoParaUnirDto(Guid.NewGuid(), "Seguro RC", null, new DateOnly(2026, 1, 15), null)];
        var cut = Renderizar();
        await SubirAsync(cut, "carta.pdf");
        PulsarBoton(cut, "Elegir de Documentos");
        cut.WaitForAssertion(() => cut.Find(".documentos-unir").TextContent.Should().Contain("Seguro RC"));
        PulsarBoton(cut, "Añadir");
        cut.WaitForAssertion(() => NombresEnLista(cut).Should().Equal("carta.pdf", "Seguro RC"));

        _mediador.Unir = _ => Result.Exito(ResultadoUnionPdf.Rechazada(1, ErroresUnirPdf.DemasiadoGrande));
        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("Seguro RC pesa más de 50 MB").And.NotContain("10 MB"));

        _mediador.Unir = _ => Result.Exito(ResultadoUnionPdf.Rechazada(0, ErroresUnirPdf.DemasiadoGrande));
        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("carta.pdf pesa más de 10 MB"));
    }

    [Fact]
    public async Task Sin_constancia_del_acceso_a_Documentos_no_se_entrega_el_PDF_y_se_puede_repetir()
    {
        var cut = Renderizar();
        await SubirAsync(cut, "a.pdf", "b.pdf");
        _mediador.ExcepcionAlUnir = new AccesoDocumentoSensibleNoRegistradoException(Guid.NewGuid());

        PulsarBoton(cut, "Unir y descargar");

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("No se ha podido dejar constancia del acceso"));
        _descarga.Invocations.Should().BeEmpty("sin constancia no se entrega el PDF");
        Habilitado(cut, "Unir y descargar").Should().BeTrue();
    }

    // ---- Documentos de la Empresa gestionada

    [Fact]
    public async Task Un_Documento_de_la_Empresa_gestionada_se_anade_por_su_Id_y_se_une_en_su_posicion()
    {
        var seguroId = Guid.NewGuid();
        _mediador.Documentos = [new DocumentoParaUnirDto(seguroId, "Seguro RC", null, new DateOnly(2026, 1, 15), null)];
        var cut = Renderizar();
        await SubirAsync(cut, "carta.pdf");

        PulsarBoton(cut, "Elegir de Documentos");
        cut.WaitForAssertion(() => cut.Find(".documentos-unir").TextContent.Should().Contain("Seguro RC").And.Contain("emitido el 15/01/2026"));
        PulsarBoton(cut, "Añadir");

        cut.WaitForAssertion(() => NombresEnLista(cut).Should().Equal("carta.pdf", "Seguro RC"));
        cut.Find("ol.lista-unir").TextContent.Should().Contain("De Documentos");
        cut.FindAll(".documentos-unir button").Single().HasAttribute("disabled").Should().BeTrue("ya está añadido");

        PulsarBoton(cut, "Unir y descargar");
        cut.WaitForAssertion(() => _mediador.Uniones.Should().ContainSingle());

        _mediador.Uniones[0].Entradas.Should().SatisfyRespectively(
            primera => primera.Should().BeOfType<EntradaUnionSubida>(),
            segunda => segunda.Should().Be(new EntradaUnionDocumento(seguroId)));
    }

    [Fact]
    public void Sin_Documentos_con_archivo_el_panel_lo_dice()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());

        PulsarBoton(cut, "Elegir de Documentos");

        cut.WaitForAssertion(() => cut.Find(".documentos-unir").TextContent.Should().Contain("no tiene documentos con archivo que puedas unir"));
    }
}
