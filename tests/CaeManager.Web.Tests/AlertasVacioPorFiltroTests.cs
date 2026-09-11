using Bunit;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Alertas es <b>la única</b> de las nueve listas del defecto sistémico que
/// filtra <b>en memoria</b>: <c>ObtenerAlertasQuery</c> no acepta filtros,
/// <c>_alertas</c> trae todo y <c>AlertasFiltradas</c> recorta encima. De ahí
/// dos cosas que no valen para ninguna de las otras ocho:
///
/// <list type="bullet">
/// <item>esta pantalla <b>sí sabe</b> cuántas alertas quedan fuera del filtro,
/// así que su copia puede decir un número sin mentir — donde el filtrado es de
/// servidor, afirmarlo sería inventar;</item>
/// <item>y el recuento es exacto por construcción: si ninguna coincide con el
/// estado elegido, todas las que hay están en otros estados.</item>
/// </list>
///
/// <para>
/// Sin la rama, filtrar por «Próximo» y no encontrar nada decía <i>«Nada que
/// reclamar — todos los documentos están vigentes o fuera del umbral de
/// aviso»</i>, que puede ser justo lo contrario de lo que pasa: puede haber una
/// docena vencidos a la vista, en la misma pantalla, un clic más allá.
/// </para>
/// </summary>
public class AlertasVacioPorFiltroTests : BunitContext
{
    /// <summary>La columna Vencimiento usa TextoFechaCopiable, que importa clipboard.js al pintarse; el JavaScript queda fuera.</summary>
    public AlertasVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorConAlertas(IReadOnlyList<AlertaDto> alertas) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAlertasQuery => alertas,
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

    private static AlertaDto Alerta(EstadoDocumento estado) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Reconocimiento médico",
        new DateOnly(2026, 10, 1), estado, ArchivoUrl: null, CentroNombre: "Centro Zorrotzaurre");

    /// <param name="estado">Valor del filtro que llega por la URL (?Estado=).</param>
    private IRenderedComponent<Features.Alertas.Pages.Alertas> Renderizar(
        string? estado = null, params AlertaDto[] alertas)
    {
        Services.AddScoped<IMediator>(_ => new MediatorConAlertas(alertas));
        Services.AddScoped<ToastService>();

        // [SupplyParameterFromQuery]: se navega a la URI, no se pasa como parámetro.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(estado is null ? "alertas" : "alertas?Estado=" + Uri.EscapeDataString(estado));

        return Render<Features.Alertas.Pages.Alertas>();
    }

    [Fact]
    public void Con_vencidos_a_la_vista_filtrar_por_proximo_no_dice_que_todo_esta_vigente()
    {
        var cut = Renderizar(estado: nameof(EstadoDocumento.Proximo),
            Alerta(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Vencido));

        cut.Markup.Should().Contain("Ninguna alerta con este estado");
        cut.Markup.Should().Contain("Quitar el filtro");
        cut.Markup.Should().NotContain("Todos los documentos están vigentes",
            "hay dos vencidos: decir que todo está vigente es exactamente lo contrario de la verdad");
    }

    /// <summary>
    /// El número no es adorno: es lo que distingue a esta pantalla de las otras
    /// ocho. Como filtra en memoria, puede afirmarlo — y tiene que ser el total
    /// real, no una aproximación.
    /// </summary>
    [Fact]
    public void La_copia_dice_cuantas_hay_en_otros_estados_porque_aqui_si_se_sabe()
    {
        var cut = Renderizar(estado: nameof(EstadoDocumento.Proximo),
            Alerta(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Faltante), Alerta(EstadoDocumento.Urgente));

        cut.Markup.Should().Contain("Ninguna alerta con este estado", "es la barrera de este caso");
        cut.Markup.Should().Contain("Hay 3 documento(s) que reclamar en otros estados.");
    }

    [Fact]
    public void Sin_ninguna_alerta_y_con_filtro_puesto_no_se_promete_un_recuento_de_cero()
    {
        var cut = Renderizar(estado: nameof(EstadoDocumento.Proximo));

        // Sin ninguna alerta en absoluto, "nada que reclamar" es cierto y es
        // además la buena noticia que el usuario quiere leer. Decir "ninguna
        // con este estado, hay 0 en otros" sería técnicamente cierto y
        // completamente inútil.
        cut.Markup.Should().Contain("Nada que reclamar");
        cut.Markup.Should().NotContain("Ninguna alerta con este estado");
        cut.Markup.Should().NotContain("Hay 0 documento(s)");
    }

    [Fact]
    public void Sin_filtro_y_sin_alertas_sigue_dando_la_buena_noticia()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Nada que reclamar");
        cut.Markup.Should().Contain("Todos los documentos están vigentes o fuera del umbral de aviso.");
        cut.Markup.Should().NotContain("Ninguna alerta con este estado");
    }

    [Fact]
    public void Quitar_el_filtro_devuelve_la_lista_completa()
    {
        var cut = Renderizar(estado: nameof(EstadoDocumento.Proximo), Alerta(EstadoDocumento.Vencido));
        cut.Markup.Should().Contain("Ninguna alerta con este estado", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().NotContain("Ninguna alerta con este estado");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Reconocimiento médico");
    }

    [Fact]
    public void Con_alertas_del_estado_filtrado_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(estado: nameof(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Vencido));

        cut.Markup.Should().NotContain("Ninguna alerta con este estado");
        cut.Markup.Should().NotContain("Nada que reclamar");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Reconocimiento médico");
    }
}
