using Bunit;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using CaeManager.Web.Features.BusquedaGlobal;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// HO-006-01 (REC-006): las siete áreas que hoy solo tenía el menú —
/// /vehiculos, /proyectos, /visitas, /gestiones, /incidencias, /calendario,
/// /comunicaciones— deben alcanzarse también desde la paleta global
/// (Ctrl/Cmd+K, <see cref="BuscadorGlobal"/>), sin pasar por
/// <c>NavMenu.razor</c>. Importa porque REC-007 depende de esto: recortar el
/// menú a 14 entradas sin darles otra puerta las dejaría inalcanzables.
///
/// <para>
/// Fija las siete por nombre en vez de un trinquete de texto sobre las 35
/// rutas <c>@page</c> del repositorio (criterio § 13 riesgo 3 del handoff):
/// medido en el mismo barrido, unas dieciocho rutas más ya carecen hoy de
/// entrada en <see cref="BuscadorGlobal"/> — un trinquete general fallaría
/// de inmediato o exigiría una lista de exclusión de esas dieciocho que
/// nadie mantendría al día, justo el trinquete que todo el mundo acaba
/// desactivando. Fijar las siete, con el nombre del área en cada caso de
/// <see cref="AreasObjetivo"/>, es lo que hace que "retirar el acceso de un
/// área" ponga el rojo nombrando cuál.
/// </para>
///
/// <para>
/// Renderiza el componente real y escribe en su input — no lee la lista
/// privada por reflexión ni por texto — porque una entrada presente en la
/// lista pero con un filtro de sustring que no la alcanza (p.ej. un typo en
/// el nombre) pasaría un test de solo-inspección y fallaría igual para un
/// usuario real.
/// </para>
/// </summary>
public class BuscadorGlobalIrAAreasNuevasTests : BunitContext
{
    public BuscadorGlobalIrAAreasNuevasTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>Mediator falso: recientes vacíos y ninguna entidad — deja ver el grupo "Ir a" sin depender de datos de dominio.</summary>
    private sealed class MediatorBuscadorVacio : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerRecientesQuery => (object)new List<ItemRecienteDto>(),
                BuscarGlobalQuery => new ResultadoBusquedaGlobalDto([], [], [], [], [], []),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            })!);

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

    /// <summary>
    /// Término de búsqueda, título esperado y ruta esperada por área. Los
    /// términos evitan a propósito las vocales acentuadas de cada nombre
    /// ("veh" y no "vehí…") porque el filtro real (<c>string.Contains</c>
    /// con <c>OrdinalIgnoreCase</c>) no pliega acentos: una "í" no coincide
    /// con una "i" suelta.
    /// </summary>
    public static TheoryData<string, string, string> AreasObjetivo => new()
    {
        { "veh", "Ir a Vehículos", "/vehiculos" },
        { "proy", "Ir a Proyectos", "/proyectos" },
        { "visit", "Ir a Visitas", "/visitas" },
        { "gestion", "Ir a Gestiones", "/gestiones" },
        { "incidenc", "Ir a Incidencias", "/incidencias" },
        { "calendari", "Ir a Calendario", "/calendario" },
        { "comunicac", "Ir a Comunicaciones", "/comunicaciones" },
    };

    [Theory]
    [MemberData(nameof(AreasObjetivo))]
    public async Task El_area_aparece_en_el_grupo_Ir_a_de_la_paleta(string termino, string tituloEsperado, string rutaEsperada)
    {
        Services.AddScoped<IMediator>(_ => new MediatorBuscadorVacio());
        Services.AddScoped<BusquedaGlobalService>();

        var cut = Render<BuscadorGlobal>();
        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());

        await cut.Find("input.buscador-input").InputAsync(termino);

        var enlace = cut.FindAll("a.buscador-item")
            .FirstOrDefault(a => a.TextContent.Contains(tituloEsperado, StringComparison.Ordinal));

        enlace.Should().NotBeNull(
            $"«{tituloEsperado}» debe aparecer en el grupo «Ir a» de la paleta al buscar «{termino}» — " +
            $"si desaparece, {rutaEsperada} vuelve a ser alcanzable solo desde el menú (lo que REC-007 no puede permitirse)");
        enlace!.GetAttribute("href").Should().Be(rutaEsperada);
    }

    /// <summary>Control positivo del mismo barrido: una entrada que YA estaba antes de HO-006-01 debe seguir apareciendo, para que los siete verdes de arriba no sean un artefacto del fake.</summary>
    [Fact]
    public async Task Una_entrada_preexistente_sigue_apareciendo_control_positivo()
    {
        Services.AddScoped<IMediator>(_ => new MediatorBuscadorVacio());
        Services.AddScoped<BusquedaGlobalService>();

        var cut = Render<BuscadorGlobal>();
        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());

        await cut.Find("input.buscador-input").InputAsync("client");

        cut.FindAll("a.buscador-item").Should().Contain(a => a.TextContent.Contains("Ir a Clientes", StringComparison.Ordinal));
    }
}
