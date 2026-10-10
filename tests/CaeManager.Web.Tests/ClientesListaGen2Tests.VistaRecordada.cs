using System.Text.Json;
using System.Web;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La vista recordada (<see cref="VistaRecordadaDeListado"/>) conectada a Clientes, con el arnés de esta
/// clase porque el filtro «Gestor CAE» solo vale si el directorio visible lo ofrece. Los mismos casos de
/// las otras páginas están en <see cref="VistaRecordadaEnListadosTests"/>.
/// </summary>
public partial class ClientesListaGen2Tests
{
    /// <summary>El mediador responde a los tres casos de uso de la vista recordada; lo demás, como siempre.</summary>
    private static MediatorFalso ConVistaRecordada(string? valoresJson) => new()
    {
        Retener = p => p switch
        {
            ObtenerVistaRecordadaQuery => Task.FromResult<object>(valoresJson!),
            GuardarVistaRecordadaCommand or OlvidarVistaRecordadaCommand => Task.FromResult<object>(Result.Exito()),
            _ => null,
        },
    };

    private Dictionary<string, string> ParametrosDeLaUrl()
    {
        var consulta = HttpUtility.ParseQueryString(new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query);
        return consulta.AllKeys.ToDictionary(k => k!, k => consulta[k]!);
    }

    [Fact]
    public void Sin_parametros_restaura_la_vista_recordada_con_toda_su_lista_blanca()
    {
        var marta = GestorCae("Marta Ibarra");
        var recordada = new Dictionary<string, string>
        {
            ["q"] = "Refri",
            ["critico"] = "true",
            ["gestor"] = marta.Id.ToString(),
            ["estado"] = "Vencido",
            ["orden"] = "cliente-desc",
        };
        var mediador = ConVistaRecordada(JsonSerializer.Serialize(recordada));

        var cut = Renderizar(mediador, "clientes", gestores: [marta]);

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(recordada));
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().Equal([new ObtenerVistaRecordadaQuery(PantallasConVistaRecordada.Clientes)]);
        cut.WaitForAssertion(() => UltimaConsulta(mediador).Should().Match<ObtenerClientesQuery>(q =>
            q.Busqueda == "Refri" && q.SoloCriticos == true && q.EjecutivoUsuarioId == marta.Id
            && q.OrdenarPor == nameof(ClienteListaDto.RazonSocial) && q.Descendente && q.Pagina == 1));
        UltimaConsulta(mediador).EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);
    }

    /// <summary>Lo recordado no es autoridad: como con la URL, no se filtra por alguien a quien la pantalla no puede nombrar.</summary>
    [Fact]
    public void Un_Gestor_CAE_recordado_que_el_directorio_visible_ya_no_ofrece_no_se_restaura()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = ConVistaRecordada($"{{\"q\":\"Refri\",\"gestor\":\"{Guid.NewGuid()}\",\"estado\":\"Inventado\"}}");

        var cut = Renderizar(mediador, "clientes", gestores: [marta]);

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(new Dictionary<string, string> { ["q"] = "Refri" },
            "ni el Gestor CAE que el directorio no ofrece ni un estado que la franja no conoce llegan a la URL"));
        UltimaConsulta(mediador).Should().Match<ObtenerClientesQuery>(q => q.Busqueda == "Refri" && q.EjecutivoUsuarioId == null);
    }

    [Fact]
    public async Task Restablecer_vista_limpia_la_url_y_olvida_lo_recordado()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = ConVistaRecordada(null);
        var cut = Renderizar(mediador, $"clientes?q=Refri&critico=true&gestor={marta.Id}&estado=Vencido&orden=cliente-desc", gestores: [marta]);
        UltimaConsulta(mediador).EjecutivoUsuarioId.Should().Be(marta.Id, "control: la página se abre con la vista de la URL");
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().BeEmpty("con parámetros manda la URL: no se lee lo recordado");

        await cut.Find("button.restablecer-vista-barra").ClickAsync(new MouseEventArgs());

        ParametrosDeLaUrl().Should().BeEmpty("«Restablecer vista» quita todos los parámetros de vista, el orden incluido");
        mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().Equal([new OlvidarVistaRecordadaCommand(PantallasConVistaRecordada.Clientes)]);
        mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty("la vista de inicio se olvida, no se guarda vacía");
        cut.WaitForAssertion(() => UltimaConsulta(mediador).Should().Match<ObtenerClientesQuery>(q =>
            q.Busqueda == null && q.SoloCriticos != true && q.EjecutivoUsuarioId == null
            && q.OrdenarPor == nameof(ClienteListaDto.EstadoDocumentalPeor) && !q.Descendente,
            "vuelve el orden de fábrica: por estado documental"));
        UltimaConsulta(mediador).EstadosDocumentales.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task El_orden_de_la_url_ordena_la_consulta_y_la_exportacion()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A."), Cliente("Frigoríficos Arcos S.A.") } };

        var cut = Renderizar(mediador, "clientes?orden=cliente-desc");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).Should().Match<ObtenerClientesQuery>(q =>
            q.OrdenarPor == nameof(ClienteListaDto.RazonSocial) && q.Descendente));
        NombresDeLasFilas(cut).Should().Equal("Refrielectric S.A.", "Frigoríficos Arcos S.A.");

        // «Exportar esta vista» sale en el mismo orden que se ve, venga de un clic o de la URL.
        await cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Single().ClickAsync(new MouseEventArgs());
        cut.FindAll("header.cabecera-pagina a.menu-acciones-item").First().GetAttribute("href")
            .Should().StartWith("/clientes/exportar.xlsx?")
            .And.Contain($"orden={nameof(ClienteListaDto.RazonSocial)}").And.Contain("desc=true");
    }

    [Fact]
    public void Un_orden_que_no_existe_se_ignora()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } };

        var cut = Renderizar(mediador, "clientes?orden=inventada-desc");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).Should().Match<ObtenerClientesQuery>(q =>
            q.OrdenarPor == nameof(ClienteListaDto.EstadoDocumentalPeor) && !q.Descendente,
            "sin un orden válido la rejilla nace con el de fábrica: por estado documental"));
    }

    /// <summary>La otra dirección: sin el orden en la URL la vista recordada no tendría qué recordar.</summary>
    [Fact]
    public async Task Ordenar_por_una_columna_lo_escribe_en_la_url()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } };
        var cut = Renderizar(mediador, "clientes");
        ParametrosDeLaUrl().Should().NotContainKey("orden", "control: el orden de fábrica no viaja");

        await cut.FindAll("thead th button.col-title").Single(b => b.TextContent.Trim() == "Razón social").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().Contain("orden", "cliente"));
        UltimaConsulta(mediador).Should().Match<ObtenerClientesQuery>(q => q.OrdenarPor == nameof(ClienteListaDto.RazonSocial) && !q.Descendente);
    }
}
