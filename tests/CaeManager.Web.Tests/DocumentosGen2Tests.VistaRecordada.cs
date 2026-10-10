using System.Text.Json;
using System.Web;
using Bunit;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

namespace CaeManager.Web.Tests;

/// <summary>
/// La vista recordada (<see cref="VistaRecordadaDeListado"/>) conectada al listado principal de Documentos,
/// con el arnés de esta clase. La pieza va bajo la pestaña del listado: <c>Pestana</c> y los enlaces
/// profundos no son vista. Los mismos casos de las otras páginas están en <see cref="VistaRecordadaEnListadosTests"/>.
/// </summary>
public partial class DocumentosGen2Tests
{
    /// <summary>El mediador responde además a los tres casos de uso de la vista recordada.</summary>
    private static MediadorControlado ConVistaRecordada(string? valoresJson)
    {
        var mediador = ConOpcionesDeFiltro();
        mediador.Interceptar = p => p switch
        {
            ObtenerVistaRecordadaQuery => Task.FromResult<object?>(valoresJson),
            GuardarVistaRecordadaCommand or OlvidarVistaRecordadaCommand => Task.FromResult<object?>(Result.Exito()),
            _ => null,
        };
        return mediador;
    }

    private Dictionary<string, string> ParametrosDeLaUrl()
    {
        var consulta = HttpUtility.ParseQueryString(new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query);
        return consulta.AllKeys.ToDictionary(k => k!, k => consulta[k]!);
    }

    [Fact]
    public void Sin_parametros_restaura_la_vista_recordada_con_toda_su_lista_blanca()
    {
        var recordada = new Dictionary<string, string>
        {
            ["q"] = "Salas",
            ["Estado"] = "Vencido",
            ["Ambito"] = nameof(AmbitoAplicacion.Trabajador),
            ["Tipo"] = TipoSeguroId.ToString(),
            ["Plataforma"] = PlataformaNalandaId.ToString(),
            ["orden"] = "emision-desc",
        };
        var mediador = ConVistaRecordada(JsonSerializer.Serialize(recordada));

        var (cut, _) = Renderizar(mediador);

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(recordada));
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().Equal([new ObtenerVistaRecordadaQuery(PantallasConVistaRecordada.Documentos)]);
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(q =>
            q.Busqueda == "Salas" && q.Ambito == AmbitoAplicacion.Trabajador
            && q.TipoDocumentoId == TipoSeguroId && q.ProveedorPlataformaCaeId == PlataformaNalandaId
            && q.OrdenarPor == nameof(DocumentoListaDto.FechaEmision) && q.Descendente && q.Pagina == 1));
        UltimaConsultaDeDocumentos(mediador).Estados.Should().Equal(EstadoDocumento.Vencido);
    }

    /// <summary>Con cualquier parámetro manda la URL, y la pestaña es uno: un enlace a otra pestaña no restaura nada.</summary>
    [Fact]
    public void Un_enlace_a_otra_pestana_no_lee_ni_restaura_la_vista_recordada()
    {
        var mediador = ConVistaRecordada("{\"Estado\":\"Vencido\"}");

        var (cut, _) = Renderizar(mediador, url: "documentos?pestana=reclamaciones");

        Pestana(cut, "Reclamaciones").GetAttribute("aria-selected").Should().Be("true", "barrera: la pestaña activa es la del enlace");
        ParametrosDeLaUrl().Should().BeEquivalentTo(new Dictionary<string, string> { ["pestana"] = "reclamaciones" });
        mediador.Enviadas.Should().NotBeEmpty("control: la página sí habló con el mediador");
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().BeEmpty("con parámetros manda la URL, y la pieza va bajo la pestaña del listado");
    }

    [Fact]
    public async Task Restablecer_vista_limpia_la_url_y_olvida_lo_recordado()
    {
        var mediador = ConVistaRecordada(null);
        var (cut, _) = Renderizar(mediador,
            url: $"documentos?q=Salas&Estado=Vencido&Ambito=Trabajador&Tipo={TipoSeguroId}&Plataforma={PlataformaNalandaId}&orden=emision-desc");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).TipoDocumentoId.Should().Be(TipoSeguroId, "control: la página se abre con la vista de la URL"));
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().BeEmpty("con parámetros manda la URL: no se lee lo recordado");

        await cut.Find("button.restablecer-vista-barra").ClickAsync(new MouseEventArgs());

        ParametrosDeLaUrl().Should().BeEmpty("«Restablecer vista» quita todos los parámetros de vista, el orden incluido");
        mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().Equal([new OlvidarVistaRecordadaCommand(PantallasConVistaRecordada.Documentos)]);
        mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty("la vista de inicio se olvida, no se guarda vacía");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(q =>
            q.Busqueda == null && q.Ambito == null && q.Estados == null && q.TipoDocumentoId == null && q.ProveedorPlataformaCaeId == null
            && q.OrdenarPor == ObtenerDocumentosQuery.OrdenPorSeveridad && !q.Descendente,
            "vuelve el orden de inicio: por severidad"));
    }

    [Theory]
    [InlineData("vencimiento-desc", nameof(DocumentoListaDto.FechaVencimiento), true)]
    [InlineData("emision", nameof(DocumentoListaDto.FechaEmision), false)]
    [InlineData("ambito-desc", nameof(DocumentoListaDto.Ambito), true)]
    [InlineData("estado", nameof(DocumentoListaDto.Estado), false)]
    public void El_orden_de_la_url_ordena_la_consulta(string orden, string propiedad, bool descendente)
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: $"documentos?orden={orden}");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(q =>
            q.OrdenarPor == propiedad && q.Descendente == descendente));
    }

    /// <summary>Sin un orden válido manda el de inicio por severidad (#1215): el orden en la URL no lo sustituye.</summary>
    [Fact]
    public void Un_orden_que_no_existe_se_ignora_y_queda_el_de_inicio_por_severidad()
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?orden=inventada-desc");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(q =>
            q.OrdenarPor == ObtenerDocumentosQuery.OrdenPorSeveridad && !q.Descendente));
    }

    /// <summary>La otra dirección: sin el orden en la URL la vista recordada no tendría qué recordar.</summary>
    [Fact]
    public async Task Ordenar_por_una_columna_lo_escribe_en_la_url()
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro());
        ParametrosDeLaUrl().Should().NotContainKey("orden", "control: el orden de inicio no viaja");

        await FijarSentidoDocumentoFase1(cut, mediador, "Tipo de documento", descendente: true);

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().Contain("orden", "tipo-desc"));
    }

    /// <summary>«Exportar esta vista» sale en el mismo orden que se ve, venga de un clic o de la URL.</summary>
    [Fact]
    public void El_orden_de_la_url_viaja_tambien_en_exportar_esta_vista()
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?orden=tipo-desc");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).OrdenarPor.Should().Be(nameof(DocumentoListaDto.TipoDocumentoNombre)));

        cut.Find(".cabecera-pagina .menu-acciones-disparador").Click();

        cut.FindAll(".cabecera-pagina a[href^='/documentos/exportar.xlsx']").Select(a => a.GetAttribute("href"))
            .Should().Contain(h => h!.Contains($"orden={nameof(DocumentoListaDto.TipoDocumentoNombre)}") && h.Contains("desc=true"));
    }
}
