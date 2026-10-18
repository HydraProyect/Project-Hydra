using Bunit;
using CaeManager.Application.Empresas.Commands.EliminarDelegacionEmpresaUOficio;
using CaeManager.Application.Empresas.Commands.GuardarOficioEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Delegación y Oficio por Empresa en la interfaz (decisión de Chris, 2026-10-18), por rol: quien escribe (Gestor CAE) ve
/// añadir, editar y eliminar y el «+ Crear oficio»; Consulta lee el catálogo y no ve ninguna acción de escritura. Eliminar
/// algo con Trabajadores asignados enseña el rechazo del comando sin cerrar el diálogo.
/// </summary>
public class DelegacionYOficioEmpresaUiTests : BunitContext
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid OficioId = Guid.NewGuid();

    private readonly MediatorFalso _mediador = new();

    public DelegacionYOficioEmpresaUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _mediador.Catalogo = new CatalogoTrabajadoresEmpresaDto(
            [new DelegacionEmpresaDto(Guid.NewGuid(), "Sevilla", "Ana Ruiz", "ana@example.test", "600000000", Guid.NewGuid())],
            [new OficioEmpresaDto(OficioId, "Soldador", Guid.NewGuid())]);
    }

    private sealed class MediatorFalso : IMediator
    {
        public CatalogoTrabajadoresEmpresaDto Catalogo { get; set; } = CatalogoTrabajadoresEmpresaDto.Vacio;
        public Result ResultadoEliminar { get; set; } = Result.Exito();
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? respuesta = request switch
            {
                ObtenerCatalogoTrabajadoresEmpresaQuery => Catalogo,
                EliminarDelegacionEmpresaUOficioCommand => ResultadoEliminar,
                GuardarOficioEmpresaCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private void Registrar(string rol)
    {
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
    }

    private IRenderedComponent<CatalogoTrabajadoresEmpresaCaja> RenderizarCaja(string rol)
    {
        Registrar(rol);
        var cut = Render<CatalogoTrabajadoresEmpresaCaja>(p => p.Add(x => x.EmpresaId, EmpresaId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sevilla"));
        return cut;
    }

    [Fact]
    public void Gestor_ve_las_dos_listas_con_anadir_editar_y_eliminar()
    {
        var cut = RenderizarCaja(Roles.GestorCae);

        cut.Markup.Should().Contain("Delegaciones").And.Contain("Oficios").And.Contain("Soldador")
            .And.Contain("Reclamaciones: Ana Ruiz · ana@example.test · 600000000");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should()
            .Equal("Añadir", "Editar", "Eliminar", "Añadir", "Editar", "Eliminar");
    }

    [Fact]
    public void Consulta_lee_el_catalogo_y_no_ve_ninguna_accion_de_escritura()
    {
        var cut = RenderizarCaja(Roles.Consulta);

        cut.Markup.Should().Contain("Sevilla").And.Contain("Soldador");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Eliminar_algo_con_trabajadores_asignados_muestra_el_rechazo_y_deja_el_dialogo_abierto()
    {
        _mediador.ResultadoEliminar = Result.Fallo(Error.Crear("Catalogo.EnUso",
            "No se puede eliminar este oficio: 2 trabajadores lo tienen asignado."));
        var cut = RenderizarCaja(Roles.GestorCae);

        cut.Find("button[aria-label='Eliminar «Soldador»']").Click();
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Eliminar").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No se puede eliminar este oficio: 2 trabajadores lo tienen asignado."));
        cut.Markup.Should().Contain("¿Eliminar el oficio «Soldador»?");
        _mediador.Enviadas.OfType<EliminarDelegacionEmpresaUOficioCommand>().Should().ContainSingle()
            .Which.Catalogo.Should().Be(CatalogoTrabajadoresEmpresa.OficioEmpresa);
    }

    [Fact]
    public void Gestor_puede_crear_un_oficio_desde_el_selector_y_queda_elegido()
    {
        Registrar(Roles.GestorCae);
        string? elegido = null;
        var cut = Render<SelectorDelegacionYOficio>(p => p
            .Add(x => x.EmpresaId, EmpresaId)
            .Add(x => x.OficioIdChanged, v => elegido = v));
        cut.WaitForAssertion(() => cut.FindAll("option").Select(o => o.TextContent).Should().Contain("Sevilla"));

        cut.Find("input[role=combobox]").Input("Electricista");
        cut.Find("li.selector-entidad-opcion-crear").TextContent.Trim().Should().Be("+ Crear oficio «Electricista»");
        cut.Find("li.selector-entidad-opcion-crear").Click();

        cut.WaitForAssertion(() => elegido.Should().NotBeNull());
        _mediador.Enviadas.OfType<GuardarOficioEmpresaCommand>().Should().ContainSingle()
            .Which.Should().Be(new GuardarOficioEmpresaCommand(EmpresaId, null, "Electricista", null));
    }

    [Fact]
    public void Consulta_no_ve_la_opcion_de_crear_oficio_en_el_selector()
    {
        Registrar(Roles.Consulta);
        var cut = Render<SelectorDelegacionYOficio>(p => p.Add(x => x.EmpresaId, EmpresaId));
        cut.WaitForAssertion(() => cut.FindAll("option").Select(o => o.TextContent).Should().Contain("Sevilla"));

        cut.Find("input[role=combobox]").Input("Electricista");

        cut.FindAll("li.selector-entidad-opcion-crear").Should().BeEmpty();
    }

    [Fact]
    public void El_selector_ofrece_los_oficios_del_catalogo_y_la_delegacion()
    {
        Registrar(Roles.GestorCae);
        var cut = Render<SelectorDelegacionYOficio>(p => p
            .Add(x => x.EmpresaId, EmpresaId)
            .Add(x => x.OficioId, OficioId.ToString()));

        cut.WaitForAssertion(() => cut.Find("input[role=combobox]").GetAttribute("value").Should().Be("Soldador"));
        cut.FindAll("option").Select(o => o.TextContent).Should().Equal("Sin delegación", "Sevilla");
    }
}
