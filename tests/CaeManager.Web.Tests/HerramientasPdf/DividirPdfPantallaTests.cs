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

    public DividirPdfPantallaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        public IReadOnlyList<ClienteAutorizadoDto> Autorizados { get; set; } = [];
        public IReadOnlyList<EmpresaSelectorDto> Empresas { get; set; } = [new EmpresaSelectorDto(EmpresaId, "Montajes Norte S.L.")];
        public Result<PreparacionDivisionPdf> Preparacion { get; set; } = Result.Exito(DividirPdfPantallaTests.Preparacion);
        public List<CrearDocumentosDesdeDivisionCommand> Creaciones { get; } = [];

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
                    IReadOnlyList<ResultadoTramoCreado> resultados = comando.Tramos
                        .Select(t => new ResultadoTramoCreado(t.PaginaInicial, t.PaginaFinal, EstadoTramoCreado.Creado, Guid.NewGuid(), null))
                        .ToList();
                    return Result.Exito(resultados);
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

    private IRenderedComponent<DividirPdf> Renderizar(string rol = "GestorCae", Guid? tenantId = null, bool disponible = true)
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioFalso(rol));
        Services.AddScoped<ITenantActual>(_ => new TenantFijo(tenantId ?? TenantAId));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<IRasterizadorPaginasPdfService, SinVistaPrevia>();
        Services.AddSingleton<IDisponibilidadHerramientasPdf>(new Disponibilidad(disponible));
        Services.AddSingleton<ILogger<DividirPdf>>(_ => NullLogger<DividirPdf>.Instance);
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddLocalization();
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton(_ => new CuotaSubidaMasiva(Options.Create(new OpcionesSubidaMasiva
        {
            DirectorioTemporal = Path.Combine(Path.GetTempPath(), "talveg-dividir-pdf-tests"),
        })));
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System));

        return Render<DividirPdf>();
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

        cut.WaitForAssertion(() => cut.Find(".aviso-dividir").TextContent.Should().Contain("Elige primero una Empresa gestionada en el selector de la barra lateral"));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
        cut.Find(".zona-bloqueada").TextContent.Should().Contain("Elige una Empresa gestionada para poder subir el PDF");
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

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("El PDF tiene contraseña"));
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
        Pulsar(cut, "Asignar documentos");
        PonerEmision(cut);

        Pulsar(cut, "Volver a dividir");

        cut.WaitForAssertion(() => cut.FindAll("button.tijera-sugerida").Should().HaveCount(1, "el corte retirado sigue retirado"));
        cut.Find("button.tijera[aria-label='Dividir tras la página 3']").Should().NotBeNull();

        Pulsar(cut, "Asignar documentos");

        cut.WaitForAssertion(() => cut.FindAll("input[placeholder=\"dd/mm/aaaa\"]")[0].GetAttribute("value").Should().Be("15/01/2026"));
    }

    [Fact]
    public async Task Un_tramo_omitido_no_se_crea_y_se_puede_restaurar()
    {
        var cut = Renderizar();
        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        await SubirAsync(cut);
        cut.Find("button.tijera[aria-label='Quitar la división tras la página 3']").Click();
        Pulsar(cut, "Asignar documentos");

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
        Pulsar(cut, "Asignar documentos");
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
        Pulsar(cut, "Asignar documentos");
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
        Pulsar(cut, "Asignar documentos");
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
