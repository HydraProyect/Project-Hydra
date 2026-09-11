using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La lista de Empresas tiene que distinguir <b>«aún no hay empresas»</b> de
/// <b>«ninguna coincide con el filtro»</b>: son dos situaciones distintas y
/// llevan a acciones opuestas. Ofrecer «crea la primera» a quien acaba de
/// teclear mal una razón social es mandarlo a duplicar una que ya existe.
///
/// <para>
/// Hasta 2026-09-07 se pintaba el mismo <c>EstadoVacio</c> en los dos casos —
/// el propio mockup «Empresas TALVEG.dc.html» lo marcaba como HUECO EN EL
/// CÓDIGO. Es el mismo defecto que <see cref="UsuariosBusquedaTests"/> ya
/// cierra para Usuarios, con una diferencia importante: allí el filtrado es en
/// memoria y aquí es de servidor, así que la copia NO puede afirmar que existan
/// empresas dadas de alta — la consulta solo devuelve el total ya filtrado.
/// </para>
/// </summary>
public class EmpresasVacioPorFiltroTests : BunitContext
{
    /// <summary>
    /// La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.
    /// Ese módulo queda fuera de lo que este test observa — mismo criterio que
    /// <see cref="AtajosGlobalesTests"/>.
    /// </summary>
    public EmpresasVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>La página lanza dos consultas por el mismo IMediator: el perfil de vocabulario y la lista.</summary>
    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<EmpresaListaDto> Empresas { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasQuery q => (object)new ResultadoPaginado<EmpresaListaDto>(
                    Empresas, Empresas.Count, q.Pagina, q.TamanoPagina),
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <param name="busqueda">Valor del filtro de texto que llega por la URL (?q=).</param>
    /// <param name="estado">Valor del filtro documental que llega por la URL (?estado=).</param>
    private IRenderedComponent<Empresas> Renderizar(string? busqueda = null, string? estado = null,
        params EmpresaListaDto[] empresas)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Empresas = empresas });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());

        // Los filtros son [SupplyParameterFromQuery]: no se pasan como
        // parámetros de componente —Blazor lo rechaza explícitamente— sino
        // navegando a la URI que los lleva, igual que en el producto.
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(busqueda)) partes.Add("q=" + Uri.EscapeDataString(busqueda));
        if (!string.IsNullOrWhiteSpace(estado)) partes.Add("estado=" + Uri.EscapeDataString(estado));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(partes.Count == 0 ? "empresas" : "empresas?" + string.Join('&', partes));

        return Render<Empresas>();
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_crear_la_primera_empresa()
    {
        var cut = Renderizar(busqueda: "Refrielectric");

        cut.Markup.Should().Contain("Ninguna empresa con este filtro");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay empresas",
            "decir «crea la primera» a quien busca algo que no aparece lo manda a duplicar una empresa existente");
    }

    [Fact]
    public void Sin_resultados_y_con_filtro_documental_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: nameof(CaeManager.Domain.Documentos.EstadoDocumento.Vencido));

        cut.Markup.Should().Contain("Ninguna empresa con este filtro");
        cut.Markup.Should().NotContain("Aún no hay empresas");
    }

    [Fact]
    public void La_copia_no_afirma_que_existan_empresas_dadas_de_alta()
    {
        var cut = Renderizar(busqueda: "Refrielectric");

        // Anclar primero que el estado SÍ se ha pintado. Sin esta línea el test
        // pasa aunque la rama no se active nunca —una aserción de ausencia sin
        // barrera es verde vacío—, y se descubrió justo así: al anular la rama
        // por mutación, este test siguió en verde cuando debía caer.
        cut.Markup.Should().Contain("Ninguna empresa con este filtro");

        // El filtrado es de servidor: la consulta devuelve el total YA filtrado,
        // así que la pantalla no sabe cuántas empresas hay sin filtro. Afirmarlo
        // sería mentir con cero empresas y un filtro escrito.
        cut.Markup.Should().NotContain("Hay empresas dadas de alta");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_crear_la_primera()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay empresas");
        cut.Markup.Should().Contain("Crea la primera para poder dar de alta trabajadores.");
        cut.Markup.Should().NotContain("Ninguna empresa con este filtro");
    }

    [Fact]
    public void Los_filtros_activos_se_ven_como_chips_con_su_valor()
    {
        var cut = Renderizar(busqueda: "Refrielectric",
            estado: nameof(CaeManager.Domain.Documentos.EstadoDocumento.Vencido));

        var chips = cut.FindAll(".chip-filtro");
        chips.Should().HaveCount(2, "hay dos filtros puestos: la búsqueda y el estado documental");
        cut.Markup.Should().Contain("Refrielectric");
        cut.Markup.Should().Contain("Vencido",
            "el chip muestra el rótulo del catálogo, no el valor crudo del enum");
    }

    /// <summary>
    /// La cabecera y la fila tienen que llevar el MISMO número de celdas, o la
    /// rejilla se desalinea en cuanto una fila no pinta alguna. Es justo lo que
    /// pasaba con las detecciones: eran un badge condicional.
    /// </summary>
    [Fact]
    public void La_cabecera_de_columnas_y_la_fila_tienen_el_mismo_numero_de_celdas()
    {
        var cut = Renderizar(empresas: new EmpresaListaDto(Guid.NewGuid(), "Montajes Ebro S.L.", "B-48.220.917", DateTime.UtcNow));

        var cabecera = cut.Find(".cabecera-columnas-empresas");
        cabecera.TextContent.Should().Contain("Razón social").And.Contain("CIF")
            .And.Contain("Cumplimiento").And.Contain("Documentación").And.Contain("Detecciones");

        var fila = cut.Find(".tarjeta-fila-acordeon-cabecera");
        fila.Children.Length.Should().Be(cabecera.Children.Length,
            "cabecera y fila comparten la misma definición de rejilla: si no coinciden las celdas, las columnas no cuadran");
    }

    [Fact]
    public void Una_empresa_sin_detecciones_conserva_su_celda_para_no_desalinear_la_fila()
    {
        var cut = Renderizar(empresas: new EmpresaListaDto(Guid.NewGuid(), "Montajes Ebro S.L.", "B-48.220.917", DateTime.UtcNow));

        cut.FindAll(".badge-deteccion").Should().BeEmpty("esta empresa no tiene detecciones pendientes");
        cut.FindAll(".celda-sin-deteccion").Should().HaveCount(1,
            "la celda se reserva igual: es lo que mantiene la rejilla cuadrada de fila en fila");
    }

    /// <summary>
    /// «Detección de trabajadores» solo se ofrece cuando hay alguna pendiente —
    /// misma condición que ya gobierna el badge. Ofrecerla siempre sería un
    /// cambio de producto que nadie ha decidido.
    /// </summary>
    [Fact]
    public void Sin_detecciones_pendientes_el_menu_de_fila_no_ofrece_la_deteccion()
    {
        var cut = Renderizar(empresas: new EmpresaListaDto(
            Guid.NewGuid(), "Montajes Ebro S.L.", "B-48.220.917", DateTime.UtcNow, null, null, 0));

        // MenuAcciones no pinta sus ítems hasta que se abre: sin este clic el
        // test comprobaría una ausencia contra un menú cerrado, que es verde
        // vacío — daría lo mismo que el ítem existiera o no.
        cut.Find(".menu-acciones-disparador").Click();

        cut.Markup.Should().Contain("Abrir Empresa 360",
            "el menú abierto es la barrera que hace válida la comprobación siguiente");
        cut.Markup.Should().NotContain("Detección de trabajadores");
    }

    [Fact]
    public void Con_detecciones_pendientes_el_menu_de_fila_si_las_ofrece()
    {
        var cut = Renderizar(empresas: new EmpresaListaDto(
            Guid.NewGuid(), "Aislamientos Nervión S.L.", "B-48.111.222", DateTime.UtcNow, null, null, 3));

        cut.Markup.Should().Contain("3 detecciones", "el badge de la celda sigue estando");

        cut.Find(".menu-acciones-disparador").Click();
        cut.Markup.Should().Contain("Detección de trabajadores");
    }

    [Fact]
    public void Sin_filtros_no_se_pinta_ningun_chip()
    {
        var cut = Renderizar(empresas: new EmpresaListaDto(Guid.NewGuid(), "Montajes Ebro S.L.", "B-48.220.917", DateTime.UtcNow));

        cut.FindAll(".chip-filtro").Should().BeEmpty();
        cut.FindAll(".chips-filtros").Should().BeEmpty(
            "la barra de chips entera sobra cuando no hay nada que quitar");
    }
}
