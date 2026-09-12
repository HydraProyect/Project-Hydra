using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="DrawerGestionDocumento.AbrirCrearParaFaltanteAsync"/> y
/// <see cref="DrawerGestionDocumento.AbrirCrearParaFaltanteEmpresaAsync"/> son
/// la API pública que Documentos.razor y AcordeonAsignacionesCentro.razor
/// invocan vía @ref (ver el comentario normativo de
/// DrawerGestionDocumento.razor.cs) con un Id que Documentos.razor.cs leyó de
/// la query string. Hasta 2026-09-12 lo asignaban directo a
/// _trabajadorId/_empresaId sin comprobar que ese Id estuviera en el catálogo
/// con alcance ya cargado para este usuario — mismo defecto que
/// AltaGuiada.razor.cs corregía por la misma fecha (ver
/// AltaGuiadaResolucionIdentificadoresTests): una coordenada de contexto no es
/// autoridad. Centros.razor.cs y Empresas.razor.cs ya comprobaban esto con
/// <c>.Any(x => x.Id == idEntrante)</c> contra la lista con alcance ya
/// cargada; este componente sigue ahora el mismo patrón.
/// </summary>
public class DrawerGestionDocumentoPreseleccionAlcanceTests : BunitContext
{
    private sealed class MediatorFalso : IMediator
    {
        public required IReadOnlyList<TrabajadorSelectorDto> Trabajadores { get; init; }
        public required IReadOnlyList<EmpresaSelectorDto> Empresas { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => Trabajadores,
                ObtenerEmpresasParaSelectorQuery => Empresas,
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

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

    /// <summary>Ningún test de esta suite sube ni descarta un archivo — si alguno lo hiciera, sería una regresión de camino, no de esta comprobación.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Este test no sube ni descarta ningún archivo; si esto salta, el camino cambió.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada; si esto salta, el camino cambió.");
    }

    private IRenderedComponent<DrawerGestionDocumento> Renderizar(MediatorFalso mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return Render<DrawerGestionDocumento>();
    }

    /// <summary>
    /// _trabajadorId/_empresaId son privados y el markup no distingue "vacío"
    /// de "un Id que no resolvió" (CampoBuscarSelect solo pinta texto para un
    /// Id que SÍ está en sus Opciones) — el propio valor interno es la única
    /// forma de observar la propiedad, igual que Centro360Gen2Tests con
    /// _generacion.
    /// </summary>
    private static string? LeerCampoPrivado(DrawerGestionDocumento instancia, string nombreCampo)
    {
        var campo = typeof(DrawerGestionDocumento).GetField(nombreCampo, BindingFlags.Instance | BindingFlags.NonPublic);
        campo.Should().NotBeNull($"el test necesita leer {nombreCampo} directamente");
        return (string?)campo!.GetValue(instancia);
    }

    [Fact]
    public async Task Un_trabajadorId_ajeno_al_catalogo_cargado_no_preselecciona_nada()
    {
        var trabajadorAjeno = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Trabajadores = [], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(trabajadorAjeno, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_trabajadorId").Should().BeEmpty(
            "un Id que no está en el catálogo con alcance ya cargado no puede quedar preseleccionado");
    }

    [Fact]
    public async Task Un_trabajadorId_presente_en_el_catalogo_cargado_si_se_preselecciona()
    {
        var trabajador = new TrabajadorSelectorDto(Guid.NewGuid(), "Ruiz Peña, Ana", "12345678A", null);
        var cut = Renderizar(new MediatorFalso { Trabajadores = [trabajador], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(trabajador.Id, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_trabajadorId").Should().Be(trabajador.Id.ToString(),
            "un Id que sí está en el catálogo cargado para este alcance debe seguir preseleccionándose, igual que antes del fix");
    }

    [Fact]
    public async Task Un_empresaId_ajeno_al_catalogo_cargado_no_preselecciona_nada()
    {
        var empresaAjena = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Trabajadores = [], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteEmpresaAsync(empresaAjena, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_empresaId").Should().BeEmpty(
            "un Id que no está en el catálogo con alcance ya cargado no puede quedar preseleccionado");
    }

    [Fact]
    public async Task Un_empresaId_presente_en_el_catalogo_cargado_si_se_preselecciona()
    {
        var empresa = new EmpresaSelectorDto(Guid.NewGuid(), "Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Trabajadores = [], Empresas = [empresa] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteEmpresaAsync(empresa.Id, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_empresaId").Should().Be(empresa.Id.ToString(),
            "un Id que sí está en el catálogo cargado para este alcance debe seguir preseleccionándose, igual que antes del fix");
    }
}
