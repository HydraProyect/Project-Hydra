using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El paso «Centro» del alta guiada recibe «» cuando «Contrato vigente hasta» está a medias o ilegible, y guardar así crearía
/// el Centro «sin fecha». El paso consume la bandera de <see cref="CampoFecha"/> y no envía el comando mientras sea true.
/// </summary>
public class AltaGuiadaFechaNoValidaTests : BunitContext
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid ClienteId = Guid.NewGuid();

    private readonly MediatorRegistrador _mediador = new();

    public AltaGuiadaFechaNoValidaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorRegistrador : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? respuesta = request switch
            {
                ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                ObtenerEmpresaPorIdQuery => new EmpresaDetalleDto(EmpresaId, "Construcciones Norte", null, DateTime.UtcNow, [ClienteId], Guid.NewGuid()),
                ObtenerClientePorIdQuery => new ClienteDetalleDto(ClienteId, "Industrias Sur", "B10380186", false, null, DateTime.UtcNow, null, Guid.NewGuid()),
                CrearCentroCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
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

    private (IRenderedComponent<AltaGuiada> Cut, NavigationManager Navegacion) RenderizarPasoCentro()
    {
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo($"clientes/alta-guiada?empresaId={EmpresaId}&clienteId={ClienteId}");
        var cut = Render<AltaGuiada>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("3. Centro"));
        return (cut, navegacion);
    }

    private static Task EscribirAsync(IRenderedComponent<AltaGuiada> cut, string etiqueta, string valor) =>
        cut.FindComponents<CampoTexto>().First(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = valor });

    private static Task PulsarAsync(IRenderedComponent<AltaGuiada> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Un_contrato_vigente_hasta_a_medias_no_crea_el_centro_sin_fecha()
    {
        var (cut, _) = RenderizarPasoCentro();
        await EscribirAsync(cut, "Nombre", "Planta de Getafe");
        var contrato = () => cut.Find("input[data-campo-fecha]");

        await contrato().InputAsync(new ChangeEventArgs { Value = "15/03" });
        await PulsarAsync(cut, "Guardar centro");

        _mediador.Enviadas.OfType<CrearCentroCommand>().Should().BeEmpty("el texto a medias llega como «» y se guardaría como «sin fecha»");
        cut.Markup.Should().Contain("no es válida");

        await contrato().InputAsync(new ChangeEventArgs { Value = "15/03/2030" });
        cut.Markup.Should().NotContain("no es válida", "el aviso de un intento anterior ya no es cierto");
        await PulsarAsync(cut, "Guardar centro");

        _mediador.Enviadas.OfType<CrearCentroCommand>().Should().ContainSingle("control positivo: con la fecha entera sí se crea")
            .Which.ContratoVigenteHasta.Should().Be(new DateOnly(2030, 3, 15));
    }

    [Fact]
    public async Task Salir_con_el_contrato_vigente_hasta_a_medias_pregunta()
    {
        var (cut, navegacion) = RenderizarPasoCentro();

        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "15/03" });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }
}
