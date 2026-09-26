using Bunit;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
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
/// P1-E2b: el asistente de alta guiada crea cada entidad al guardar su paso, así que lo
/// que se pierde al salir es solo lo escrito en el paso en curso. Con algo escrito, salir
/// pregunta; sin nada, o recién guardado, no. «Cancelar» y «Terminar aquí» son salidas
/// decididas y no preguntan.
/// </summary>
public class AltaGuiadaAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid ClienteId = Guid.NewGuid();

    public AltaGuiadaAvisoCambiosSinGuardarTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                ObtenerEmpresaPorIdQuery => new EmpresaDetalleDto(EmpresaId, "Construcciones Norte", null, DateTime.UtcNow, [ClienteId], Guid.NewGuid()),
                ObtenerClientePorIdQuery => new ClienteDetalleDto(ClienteId, "Industrias Sur", "B10380186", false, null, DateTime.UtcNow, null, Guid.NewGuid()),
                CrearEmpresaCommand => Result.Exito(EmpresaId),
                CrearCentroCommand => Result.Exito(Guid.NewGuid()),
                CrearTrabajadorCommand => Result.Exito(Guid.NewGuid()),
                CrearAsignacionCommand => Result.Exito(Guid.NewGuid()),
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

    private (IRenderedComponent<AltaGuiada> Cut, NavigationManager Navegacion) Renderizar(string query, string pasoEsperado)
    {
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo(string.IsNullOrEmpty(query) ? "clientes/alta-guiada" : $"clientes/alta-guiada?{query}");
        var cut = Render<AltaGuiada>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(pasoEsperado));
        return (cut, navegacion);
    }

    private static Task EscribirAsync(IRenderedComponent<AltaGuiada> cut, string etiqueta, string valor) =>
        cut.FindComponents<CampoTexto>().First(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = valor });

    private static Task PulsarAsync(IRenderedComponent<AltaGuiada> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Salir_con_la_empresa_a_medias_pregunta()
    {
        var (cut, navegacion) = Renderizar("", "1. Empresa");
        await EscribirAsync(cut, "Razón social", "Construcciones Norte");

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Sin_escribir_nada_salir_no_pregunta()
    {
        var (cut, navegacion) = Renderizar("", "1. Empresa");

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el asistente recién abierto no tiene nada que perder");
    }

    [Fact]
    public async Task Cancelar_con_la_empresa_a_medias_sale_sin_preguntar()
    {
        var (cut, navegacion) = Renderizar("", "1. Empresa");
        await EscribirAsync(cut, "Razón social", "Construcciones Norte");

        await PulsarAsync(cut, "Cancelar");

        navegacion.Uri.Should().EndWith("/clientes", "«Cancelar» es una decisión explícita de salir");
        cut.FindAll(".modal-pie button").Should().BeEmpty();
    }

    [Fact]
    public async Task Lo_ya_creado_no_cuenta_como_cambio_en_el_paso_siguiente()
    {
        var (cut, navegacion) = Renderizar("", "1. Empresa");
        await EscribirAsync(cut, "Razón social", "Construcciones Norte");
        await PulsarAsync(cut, "Guardar y continuar");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ya creada"));

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion,
            "la Empresa ya está guardada y en el paso del Cliente empresarial no se ha escrito nada");
    }

    [Fact]
    public async Task Salir_con_el_cliente_empresarial_a_medias_pregunta()
    {
        var (cut, navegacion) = Renderizar($"empresaId={EmpresaId}", "Ya creada");
        await EscribirAsync(cut, "Identificación fiscal", "B10380186");

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Salir_con_el_centro_a_medias_pregunta_y_tras_guardarlo_no()
    {
        var (cut, navegacion) = Renderizar($"empresaId={EmpresaId}&clienteId={ClienteId}", "3. Centro");
        await EscribirAsync(cut, "Nombre", "Planta de Getafe");

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await PulsarAsync(cut, "Guardar centro");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("creado."));

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el centro ya está guardado y el formulario vacío es su nuevo punto de partida");
    }

    [Fact]
    public async Task Terminar_aqui_con_un_trabajador_a_medias_no_pregunta_al_salir()
    {
        var (cut, navegacion) = Renderizar($"empresaId={EmpresaId}&clienteId={ClienteId}", "3. Centro");
        await EscribirAsync(cut, "Nombre", "Planta de Getafe");
        await PulsarAsync(cut, "Guardar centro");
        await PulsarAsync(cut, "Continuar a Trabajadores");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("4. Trabajadores"));
        await EscribirAsync(cut, "DNI / NIE", "12345678Z");

        await PulsarAsync(cut, "Terminar aquí");

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion,
            "«Terminar aquí» abre la ficha de la Empresa por decisión explícita: lo que abre (?ctx=) no pregunta");
    }
}
