using Bunit;
using CaeManager.Application.Tenants.Commands.GuardarLogoTenant;
using CaeManager.Application.Tenants.Commands.RetirarLogoTenant;
using CaeManager.Application.Tenants.Queries.ObtenerLogoOrganizacion;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Configuración → Organización (selector de Tenant beneficiario, lote 1): subir, sustituir y retirar el
/// logo. La pantalla solo ofrece o esconde los botones según <see cref="LogoOrganizacionDto.PuedeEditar"/>;
/// quién escribe lo impone Application (ver LogoTenantTests), y por eso aquí cada variante se prueba con la
/// respuesta de la query y no con roles.
/// </summary>
public class OrganizacionLogoTests : BunitContext
{
    private static readonly Guid TenantId = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");
    private const string Version = "0123456789abcdef";

    private readonly MediadorDeLaPantalla _mediador = new();

    public OrganizacionLogoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
    }

    private IRenderedComponent<OrganizacionLogo> Renderizar(LogoOrganizacionDto? dto)
    {
        _mediador.Organizacion = dto;
        var cut = Render<OrganizacionLogo>();
        cut.WaitForState(() => cut.FindAll(".organizacion-logo-tarjeta").Count == 1 || cut.FindAll(".estado-vacio").Count > 0);
        return cut;
    }

    private static LogoOrganizacionDto Dto(string? version = null, bool puedeEditar = true, bool soporte = false) =>
        new(TenantId, "Norte Servicios", version, puedeEditar, soporte);

    // ── Variantes de la pantalla ────────────────────────────────────────────

    [Fact]
    public void Sin_logo_el_Administrador_ve_las_iniciales_y_la_zona_de_subida_con_la_nota_de_conversion()
    {
        var cut = Renderizar(Dto());

        cut.FindAll("img.organizacion-logo-imagen").Should().BeEmpty();
        cut.Find(".organizacion-logo-sin-logo").TextContent.Should().Contain("NS").And.Contain("iniciales");
        cut.FindComponent<ZonaSoltarArchivo>().Instance.Titulo.Should().Be("Sube el logo");
        cut.FindComponent<ZonaSoltarArchivo>().Instance.FormatosTexto.Should().Be("PNG o JPG, máx. 5 MB");
        cut.FindComponent<ZonaSoltarArchivo>().Instance.Accept.Should().NotContain("svg");
        cut.Find(".organizacion-logo-nota").TextContent.Should().Be("Se convertirá a PNG de 256 px.");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Retirar logo"),
            "sin logo no hay nada que retirar");
    }

    [Fact]
    public void Con_logo_se_previsualiza_a_256_px_con_la_URL_versionada_y_se_ofrece_sustituir_y_retirar()
    {
        var cut = Renderizar(Dto(Version));

        var imagen = cut.Find("img.organizacion-logo-imagen");
        imagen.GetAttribute("src").Should().Be($"/tenants/{TenantId}/logo?v={Version}");
        imagen.GetAttribute("width").Should().Be("256");
        imagen.GetAttribute("height").Should().Be("256");
        imagen.GetAttribute("alt").Should().Be("Logo de Norte Servicios");
        cut.FindComponent<ZonaSoltarArchivo>().Instance.Titulo.Should().Be("Sustituye el logo");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Retirar logo"));
    }

    [Fact]
    public void Quien_no_puede_editar_solo_ve_el_logo_y_una_explicacion()
    {
        var cut = Renderizar(Dto(Version, puedeEditar: false));

        cut.FindAll("img.organizacion-logo-imagen").Should().ContainSingle();
        cut.FindComponents<ZonaSoltarArchivo>().Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Retirar logo"));
        cut.Find(".organizacion-logo-solo-lectura").TextContent.Should()
            .Be("Solo el Administrador de la organización puede cambiar el logo.");
    }

    [Fact]
    public void En_una_sesion_de_soporte_sin_aprovisionamiento_la_explicacion_nombra_la_capacidad()
    {
        var cut = Renderizar(Dto(Version, puedeEditar: false, soporte: true));

        cut.FindComponents<ZonaSoltarArchivo>().Should().BeEmpty();
        cut.Find(".organizacion-logo-solo-lectura").TextContent.Should().Contain("capacidad de aprovisionamiento");
    }

    [Fact]
    public void En_una_sesion_de_soporte_con_aprovisionamiento_ofrece_el_mismo_formulario()
    {
        var cut = Renderizar(Dto(Version, puedeEditar: true, soporte: true));

        cut.FindComponent<ZonaSoltarArchivo>().Instance.Titulo.Should().Be("Sustituye el logo");
        cut.FindAll(".organizacion-logo-solo-lectura").Should().BeEmpty();
    }

    [Fact]
    public void Un_fallo_de_carga_ofrece_reintentar()
    {
        var cut = Renderizar(null);

        cut.FindAll(".organizacion-logo-tarjeta").Should().BeEmpty();
        cut.Markup.Should().Contain("No se pudo cargar la organización");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Reintentar"));
    }

    [Fact]
    public void Ningun_texto_visible_dice_tenant()
    {
        foreach (var dto in new[] { Dto(), Dto(Version), Dto(Version, false), Dto(Version, false, true) })
        {
            var cut = Renderizar(dto);
            // Texto y atributos que lee una persona; la URL del logo (/tenants/{id}/logo) no cuenta.
            var legible = cut.Find(".organizacion-logo").TextContent + " " + string.Join(" ",
                cut.FindAll("[alt],[aria-label],[title]").SelectMany(e => new[]
                {
                    e.GetAttribute("alt"), e.GetAttribute("aria-label"), e.GetAttribute("title")
                }));
            legible.Should().NotContainEquivalentOf("tenant");
        }
    }

    // ── Subir ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Subir_un_archivo_envia_sus_bytes_al_comando_y_recarga()
    {
        var cut = Renderizar(Dto());
        byte[] contenido = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        _mediador.OrganizacionTrasGuardar = Dto(Version);

        await Subir(cut, InputFileContent.CreateFromBinary(contenido, "logo.png", contentType: "image/png"));

        _mediador.Guardados.Should().ContainSingle().Which.ImagenOriginal.Should().Equal(contenido);
        cut.WaitForAssertion(() => cut.FindAll("img.organizacion-logo-imagen").Should().ContainSingle());
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "Logo actualizado");
    }

    [Fact]
    public async Task Un_rechazo_del_servidor_se_muestra_tal_cual_y_no_recarga()
    {
        var cut = Renderizar(Dto());
        _mediador.ResultadoGuardar = Result.Fallo(Error.Crear("LogoTenant.ImagenNoAdmitida", "Formato no admitido (PNG, JPG)."));

        await Subir(cut, InputFileContent.CreateFromBinary([1, 2, 3], "logo.svg", contentType: "image/svg+xml"));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Be("Formato no admitido (PNG, JPG)."));
        cut.FindAll("img.organizacion-logo-imagen").Should().BeEmpty();
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_archivo_de_mas_de_5_MB_se_rechaza_sin_llegar_al_comando()
    {
        var cut = Renderizar(Dto());

        await Subir(cut, InputFileContent.CreateFromBinary(new byte[5 * 1024 * 1024 + 1], "grande.png", contentType: "image/png"));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Be("Máximo 5 MB."));
        _mediador.Guardados.Should().BeEmpty();
    }

    // ── Retirar ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retirar_pide_confirmacion_y_solo_envia_el_comando_al_confirmar()
    {
        var cut = Renderizar(Dto(Version));
        _mediador.OrganizacionTrasGuardar = Dto();

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Retirar logo")).ClickAsync(new MouseEventArgs());
        cut.FindAll(".modal-contenido").Should().ContainSingle("retirar pregunta antes");
        _mediador.Retirados.Should().Be(0);

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Retirar").ClickAsync(new MouseEventArgs());

        _mediador.Retirados.Should().Be(1);
        cut.WaitForAssertion(() => cut.FindAll("img.organizacion-logo-imagen").Should().BeEmpty());
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "Logo retirado");
    }

    [Fact]
    public async Task Cancelar_la_retirada_no_envia_nada()
    {
        var cut = Renderizar(Dto(Version));

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Retirar logo")).ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        _mediador.Retirados.Should().Be(0);
        cut.FindAll("img.organizacion-logo-imagen").Should().ContainSingle();
    }

    private static Task Subir(IRenderedComponent<OrganizacionLogo> cut, InputFileContent archivo) =>
        Task.Run(() => cut.FindComponent<InputFile>().UploadFiles(archivo));

    // ── Doble ───────────────────────────────────────────────────────────────

    private sealed class MediadorDeLaPantalla : IMediator
    {
        public LogoOrganizacionDto? Organizacion { get; set; }
        public LogoOrganizacionDto? OrganizacionTrasGuardar { get; set; }
        public Result ResultadoGuardar { get; set; } = Result.Exito();
        public List<GuardarLogoTenantCommand> Guardados { get; } = [];
        public int Retirados { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerLogoOrganizacionQuery:
                    return Task.FromResult((TResponse)(object)Organizacion!);
                case GuardarLogoTenantCommand guardar:
                    Guardados.Add(guardar);
                    if (ResultadoGuardar.EsExitoso) Organizacion = OrganizacionTrasGuardar;
                    return Task.FromResult((TResponse)(object)ResultadoGuardar);
                case RetirarLogoTenantCommand:
                    Retirados++;
                    Organizacion = OrganizacionTrasGuardar;
                    return Task.FromResult((TResponse)(object)Result.Exito());
                default:
                    throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
