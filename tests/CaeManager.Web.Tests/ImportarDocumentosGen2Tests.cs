using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PaginaImportacion = CaeManager.Web.Features.Importacion.Pages.Importacion;

namespace CaeManager.Web.Tests;

public class ImportarDocumentosGen2Tests : BunitContext
{
    private sealed class MediadorHistorialVacio : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerHistorialImportacionesQuery)
                return Task.FromResult((TResponse)(object)(IReadOnlyList<HistorialImportacionDto>)[]);

            throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
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

    private sealed class AlmacenUsuariosVacio : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() => new NotSupportedException("El historial está vacío.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public void Dispose() { }
    }

    private readonly AlmacenUsuariosVacio _usuarios = new();

    private IRenderedComponent<PaginaImportacion> RenderizarAsistenteDocumentos(bool entradaDocumental = true)
    {
        Services.AddScoped<IMediator, MediadorHistorialVacio>();
        Services.AddScoped<ToastService>();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddLogging();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            _usuarios, null!, null!, null!, null!, null!, null!, null!, null!));
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            entradaDocumental ? "importacion?plantilla=documentos&flujo=documentos" : "importacion?plantilla=documentos");

        return Render<PaginaImportacion>();
    }

    [Fact]
    public void La_ruta_de_Documentos_redirige_al_asistente_y_las_dos_siguen_exigiendo_Administrador()
    {
        Render<ImportarDocumentos>();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(
            "/importacion?plantilla=documentos&flujo=documentos", "la ruta documental marca explícitamente su entrada");

        foreach (var pagina in new[] { typeof(ImportarDocumentos), typeof(PaginaImportacion) })
            pagina.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Should().ContainSingle().Which.Roles.Should().Be(Roles.Administrador);
    }

    [Fact]
    public void La_query_de_Documentos_abre_el_paso_de_archivo_con_su_contexto()
    {
        var cut = RenderizarAsistenteDocumentos();

        cut.Markup.Should().Contain("Importar documentos");
        cut.Markup.Should().Contain("Ciclo documental");
        cut.FindAll(".paso-importacion-actual").Should().ContainSingle().Which.TextContent.Should().Contain("Analizar");
        cut.FindAll("button").Select(Texto).Should().NotContain("Continuar con Documentos");
        cut.FindAll(".contexto-plantilla-documentos").Should().ContainSingle(
            "la query de la ruta canónica abre directamente el flujo de documentos");
        cut.FindAll(".tarjeta-alcance-documentos").Should().HaveCount(2,
            "la interfaz documental debe ser alcanzable desde la query, sin un parámetro de componente");
    }

    [Fact]
    public void La_query_de_plantilla_Documentos_conserva_el_paso_uno_y_la_preseleccion()
    {
        var cut = RenderizarAsistenteDocumentos(entradaDocumental: false);

        cut.FindAll(".paso-importacion-actual").Should().ContainSingle().Which.TextContent.Should().Contain("Elegir plantilla");
        cut.Find("[role=radiogroup] [data-plantilla='documentos'] [role=radio]")
            .GetAttribute("aria-checked").Should().Be("true");
        cut.FindAll(".contexto-plantilla-documentos").Should().BeEmpty();
    }

    [Fact]
    public void La_query_de_Documentos_no_cambia_el_asistente_integrado_en_Configuracion()
    {
        Services.AddScoped<IMediator, MediadorHistorialVacio>();
        Services.AddScoped<ToastService>();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddLogging();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            _usuarios, null!, null!, null!, null!, null!, null!, null!, null!));
        Services.GetRequiredService<NavigationManager>().NavigateTo("importacion?plantilla=documentos&flujo=documentos");

        var cut = Render<PaginaImportacion>(p => p.Add(x => x.IntegradaEnConfiguracion, true));

        cut.FindAll(".paso-importacion-actual").Should().ContainSingle().Which.TextContent.Should().Contain("Elegir plantilla");
        cut.FindAll(".contexto-plantilla-documentos").Should().BeEmpty();
        cut.FindAll(".tarjeta-plantilla-seleccionada").Should().ContainSingle()
            .Which.GetAttribute("data-plantilla").Should().Be("documentos");
    }

    [Fact]
    public void El_alcance_de_Documentos_expone_solo_las_garantias_del_comando()
    {
        var cut = RenderizarAsistenteDocumentos();

        var tarjetas = cut.FindAll(".tarjeta-alcance-documentos");
        tarjetas.Should().HaveCount(2, "el control positivo confirma que el asistente dibujó ambas tarjetas");

        var hace = tarjetas.Single(t => Texto(t.QuerySelector("h3")!) == "Qué hace").QuerySelectorAll("li").Select(Texto);
        hace.Should().HaveCount(3, "el control positivo confirma que se observan las tres promesas");
        hace.Should().Contain("Crea los documentos nuevos de trabajador, con fecha de emisión y vencimiento calculado.");
        hace.Should().Contain("Cruza el DNI con los trabajadores dados de alta y el nombre con el catálogo de tipos.");
        hace.Should().Contain("Deja constancia de cada fila que no entra, con su motivo, en el reporte.");

        var noHace = tarjetas.Single(t => Texto(t.QuerySelector("h3")!) == "No hace").QuerySelectorAll("li").Select(Texto);
        noHace.Should().HaveCount(3, "el control positivo confirma que se observan las tres limitaciones");
        noHace.Should().Contain("No da de alta trabajadores ni tipos de documento.");
        noHace.Should().Contain("No adjunta PDF: el archivo se aporta después con subida múltiple.");
        noHace.Should().Contain("No admite fecha de vencimiento: se calcula desde la emisión y la vigencia del tipo.");
    }

    private static string Texto(IElement elemento) => Regex.Replace(elemento.TextContent, @"\s+", " ").Trim();
}
