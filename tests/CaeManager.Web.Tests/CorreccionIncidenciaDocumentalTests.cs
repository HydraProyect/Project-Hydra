using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Corregir una incidencia documental sin salir del listado (Listados 3/7): qué
/// abre <see cref="CorreccionIncidenciaDocumental"/> según lo que la incidencia
/// trae, y que no monta el formulario hasta que hace falta.
///
/// <para>
/// No prueba que se guarde: guardar es del formulario que ya existía
/// (<see cref="DrawerGestionDocumento"/>) y de sus comandos, con sus propios
/// tests. Aquí solo la bifurcación renovar / dar de alta / nada.
/// </para>
/// </summary>
public class CorreccionIncidenciaDocumentalTests : BunitContext
{
    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public IReadOnlyList<TrabajadorSelectorDto> Trabajadores { get; init; } = [];
        public IReadOnlyList<EmpresaSelectorDto> Empresas { get; init; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(request switch
            {
                // Sin documento que devolver: basta para observar QUÉ se pidió abrir.
                ObtenerDocumentoPorIdQuery => null!,
                ObtenerTrabajadoresParaSelectorQuery => (object)Trabajadores,
                ObtenerEmpresasParaSelectorQuery => Empresas,
                ObtenerVehiculosParaSelectorQuery => (IReadOnlyList<VehiculoSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
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

    private IRenderedComponent<CorreccionIncidenciaDocumental> Renderizar(MediatorFalso mediador)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        this.ConServiciosDelFormularioDeDocumento();
        Services.AddLocalization();

        return Render<CorreccionIncidenciaDocumental>();
    }

    [Theory]
    // documento, tipo, trabajador, empresa → ¿hay algo que abrir?
    [InlineData(true, false, false, false, true)]   // hay Documento: se renueva, lo demás sobra
    [InlineData(false, true, true, false, true)]    // falta el de un Trabajador
    [InlineData(false, true, false, true, true)]    // falta el de una Empresa
    [InlineData(false, true, false, false, false)]  // tipo sin propietario: no se sabe de quién
    [InlineData(false, false, true, false, false)]  // propietario sin tipo: no se sabe qué
    [InlineData(false, false, false, false, false)]
    public void Es_corregible_solo_si_hay_documento_o_tipo_con_propietario(
        bool conDocumento, bool conTipo, bool conTrabajador, bool conEmpresa, bool esperado)
    {
        Guid? Id(bool presente) => presente ? Guid.NewGuid() : null;

        CorreccionIncidenciaDocumental
            .EsCorregible(Id(conDocumento), Id(conTipo), Id(conTrabajador), Id(conEmpresa))
            .Should().Be(esperado);
    }

    [Fact]
    public void No_monta_el_formulario_hasta_el_primer_uso()
    {
        var mediador = new MediatorFalso();

        var cut = Renderizar(mediador);

        cut.Markup.Trim().Should().BeEmpty("un listado que no corrige nada no carga el formulario ni sus catálogos");
        mediador.Enviadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_documento_abre_su_renovacion()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        var documentoId = Guid.NewGuid();

        await cut.InvokeAsync(() => cut.Instance.AbrirAsync(documentoId, Guid.NewGuid(), Guid.NewGuid(), null));

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(documentoId, "con Documento se renueva ese, no se da de alta otro");
    }

    [Fact]
    public async Task Sin_documento_abre_el_alta_del_que_falta_con_el_trabajador_y_el_tipo()
    {
        var trabajadorId = Guid.NewGuid();
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);

        await cut.InvokeAsync(() => cut.Instance.AbrirAsync(null, Guid.NewGuid(), trabajadorId, null));

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty("no hay Documento que renovar");
        mediador.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().NotBeEmpty(
            "el alta carga el catálogo de Trabajadores con alcance para preseleccionar al de la incidencia");
        cut.Markup.Should().Contain("Nuevo documento");
    }

    [Fact]
    public async Task Sin_nada_que_abrir_no_monta_ni_pide_nada()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);

        await cut.InvokeAsync(() => cut.Instance.AbrirAsync(null, null, Guid.NewGuid(), null));

        mediador.Enviadas.Should().BeEmpty();
        cut.Markup.Trim().Should().BeEmpty();
    }
}

/// <summary>
/// Lo que <see cref="DrawerGestionDocumento"/> inyecta además del mediador. Lo necesita cualquier
/// test que pulse una incidencia en un listado, porque ese clic acaba montando el formulario.
/// Ninguno de estos tests sube ni convierte archivos: si uno de los dos dobles salta, el camino cambió.
/// </summary>
internal static class ServiciosDelFormularioDeDocumentoDePrueba
{
    public static void ConServiciosDelFormularioDeDocumento(this BunitContext ctx)
    {
        ctx.Services.AddScoped<IFileStorageService, AlmacenQueNadieDebeTocar>();
        ctx.Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        ctx.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    private sealed class AlmacenQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no sube ni descarta ningún archivo.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada.");
    }
}
