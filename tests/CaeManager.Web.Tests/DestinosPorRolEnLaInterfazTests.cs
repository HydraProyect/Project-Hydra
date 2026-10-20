using Bunit;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.AtajosGlobales;
using CaeManager.Web.Features.BusquedaGlobal;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La paleta Ctrl/Cmd+K («Ir a») y los atajos «g + letra» no ofrecen a un rol un destino que su página le deniega:
/// un Gestor CAE no debe encontrarse «acceso denegado» desde algo que la propia interfaz le puso delante. Estos
/// tests renderizan los componentes reales con la sesión de cada rol; la regla de qué destino admite cada rol
/// está en <c>AdmisionDeDestinosTests</c>.
/// </summary>
public class BuscadorGlobalIrAPorRolTests : BunitContext
{
    public BuscadorGlobalIrAPorRolTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorBuscadorVacio : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerRecientesQuery => (object)new List<ItemRecienteDto>(),
                BuscarGlobalQuery => new ResultadoBusquedaGlobalDto([], [], [], []),
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

    /// <summary>Abre la paleta con la sesión de <paramref name="rol"/> y escribe «ir a»: los títulos del grupo «Ir a».</summary>
    private async Task<List<string>> DestinosOfrecidosAsync(string rol)
    {
        Services.AddScoped<IMediator>(_ => new MediatorBuscadorVacio());
        Services.AddScoped<BusquedaGlobalService>();
        Services.AddScoped<CaeManager.Application.Common.ITenantActual, TenantActualDePaletaFalso>();
        AddAuthorization().SetAuthorized("usuaria-de-prueba").SetRoles(rol);

        var cut = Render<BuscadorGlobal>();
        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());
        await cut.Find("input.buscador-input").InputAsync("ir a");

        return [.. cut.FindAll("a.buscador-item").Select(a => a.TextContent)];
    }

    [Fact]
    public async Task El_Gestor_CAE_no_ve_destinos_de_direccion_ni_de_administracion()
    {
        var destinos = await DestinosOfrecidosAsync(Roles.GestorCae);

        string[] ocultos =
        [
            "Cuadro de dirección", "Facturación", "Auditoría", "Roles", "Usuarios", "Configuración",
            "Claves API", "Tipos de documento", "Importación", "Integraciones", "Retención", "Delegaciones",
            "Indicadores de IA y BPO", "Visión de cartera",
        ];
        foreach (var titulo in ocultos)
            destinos.Should().NotContain(d => d.Contains($"Ir a {titulo}", StringComparison.Ordinal),
                $"el rol Gestor CAE no tiene «{titulo}»: ofrecérselo lo dejaría en una pantalla de acceso denegado");
    }

    [Fact]
    public async Task El_Gestor_CAE_sigue_viendo_sus_destinos_de_trabajo()
    {
        var destinos = await DestinosOfrecidosAsync(Roles.GestorCae);

        foreach (var titulo in new[] { "Clientes", "Empresas", "Trabajadores", "Documentos", "Visitas", "Alertas", "Mi trabajo", "Comunicaciones" })
            destinos.Should().Contain(d => d.Contains($"Ir a {titulo}", StringComparison.Ordinal), $"«{titulo}» es de su trabajo diario");
    }

    [Fact]
    public async Task El_Administrador_ve_los_destinos_de_administracion()
    {
        var destinos = await DestinosOfrecidosAsync(Roles.Administrador);

        foreach (var titulo in new[] { "Facturación", "Auditoría", "Roles", "Usuarios", "Configuración", "Claves API" })
            destinos.Should().Contain(d => d.Contains($"Ir a {titulo}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task El_rol_Cliente_solo_ve_los_destinos_de_su_menu()
    {
        var destinos = await DestinosOfrecidosAsync(Roles.Cliente);

        destinos.Should().Contain(d => d.Contains("Ir a Empresas", StringComparison.Ordinal));
        destinos.Should().Contain(d => d.Contains("Ir a Documentos", StringComparison.Ordinal));
        destinos.Should().NotContain(d => d.Contains("Ir a Clientes", StringComparison.Ordinal));
        destinos.Should().NotContain(d => d.Contains("Ir a Mi trabajo", StringComparison.Ordinal));
    }
}

public class AtajosGlobalesPorRolTests : BunitContext
{
    public AtajosGlobalesPorRolTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private (IRenderedComponent<AtajosGlobales> Cut, NavigationManager Navegacion) Con(string rol)
    {
        AddAuthorization().SetAuthorized("usuaria-de-prueba").SetRoles(rol);
        return (Render<AtajosGlobales>(), Services.GetRequiredService<NavigationManager>());
    }

    [Theory]
    [InlineData("b")]
    [InlineData("m")]
    public async Task Consulta_no_navega_a_Mi_trabajo_que_su_pantalla_le_deniega(string tecla)
    {
        var (cut, navegacion) = Con(Roles.Consulta);
        var uriInicial = navegacion.Uri;

        await cut.InvokeAsync(() => cut.Instance.IrA(tecla));

        navegacion.Uri.Should().Be(uriInicial);
    }

    [Fact]
    public async Task Consulta_si_navega_a_los_destinos_que_su_rol_admite()
    {
        var (cut, navegacion) = Con(Roles.Consulta);

        await cut.InvokeAsync(() => cut.Instance.IrA("p"));

        navegacion.Uri.Should().EndWith("/proyectos");
    }

    [Theory]
    [InlineData("b", "/bandeja")]
    [InlineData("m", "/mi-trabajo")]
    [InlineData("c", "/clientes")]
    public async Task El_Gestor_CAE_navega_a_sus_destinos(string tecla, string ruta)
    {
        var (cut, navegacion) = Con(Roles.GestorCae);

        await cut.InvokeAsync(() => cut.Instance.IrA(tecla));

        navegacion.Uri.Should().EndWith(ruta);
    }

    [Theory]
    [InlineData("c")]
    [InlineData("b")]
    [InlineData("m")]
    public async Task El_rol_Cliente_no_navega_a_destinos_que_su_menu_no_tiene(string tecla)
    {
        var (cut, navegacion) = Con(Roles.Cliente);
        var uriInicial = navegacion.Uri;

        await cut.InvokeAsync(() => cut.Instance.IrA(tecla));

        navegacion.Uri.Should().Be(uriInicial);
    }

    [Theory]
    [InlineData("e", "/empresas")]
    [InlineData("t", "/trabajadores")]
    [InlineData("d", "/documentos")]
    public async Task El_rol_Cliente_conserva_los_atajos_de_su_menu(string tecla, string ruta)
    {
        var (cut, navegacion) = Con(Roles.Cliente);

        await cut.InvokeAsync(() => cut.Instance.IrA(tecla));

        navegacion.Uri.Should().EndWith(ruta);
    }

    [Fact]
    public async Task La_chuleta_de_Consulta_no_anuncia_los_atajos_que_no_hacen_nada()
    {
        var (cut, _) = Con(Roles.Consulta);
        await cut.InvokeAsync(() => cut.Instance.AlternarAyuda());
        var teclas = cut.FindAll("kbd").Select(k => k.TextContent).ToList();

        teclas.Should().Contain("g p");
        teclas.Should().NotContain("g b");
        teclas.Should().NotContain("g m");
    }
}

