using Bunit;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class DocumentosGen2Tests
{
    /// <summary>
    /// Con ?q=… y ?Estado=… en la URL, el listado nunca se pide sin filtro. OnInitializedAsync deja
    /// <c>_resolviendoEmpresa</c> en false antes de esperar sus consultas de apoyo (filtros guardados, opciones de
    /// filtro) y ComponentBase no corre OnParametersSet (donde los filtros se leen de la URL) hasta que termina: el
    /// primer render monta la rejilla con esas consultas en vuelo, y su proveedor pedía el listado entero. Misma
    /// carrera que Vehículos (#1621); aquí se retiene la consulta de filtros guardados para fijarla.
    /// </summary>
    [Fact]
    public void Con_filtros_en_la_url_y_las_consultas_de_apoyo_en_vuelo_la_lista_nunca_se_pide_sin_filtro()
    {
        var filtradoPorLaUrl = Documento("Certificado con filtro");
        var mediador = new MediadorControlado
        {
            Documentos = q => CoincideConLaUrl(q) ? [filtradoPorLaUrl] : [Documento("Certificado sin filtro")],
        };
        var apoyoLiberado = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is ObtenerFiltrosGuardadosQuery ? apoyoLiberado.Task : null;

        var (cut, _) = Renderizar(mediador,
            url: $"documentos?q=Salas&Estado=Vencido&Ambito=Trabajador&Tipo={TipoSeguroId}&orden=vencimiento-desc");
        cut.InvokeAsync(() => apoyoLiberado.SetResult(Array.Empty<FiltroGuardadoDto>()));

        // Espera a que la lista pinte algo (con o sin filtro) para que el fallo sea del filtro y no de un tiempo agotado.
        cut.WaitForState(() => cut.Markup.Contains("Certificado ", StringComparison.Ordinal));

        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().OnlyContain(
            q => CoincideConLaUrl(q), "los filtros y el orden de la URL se leen antes de la primera carga");
        cut.Markup.Should().NotContain("Certificado sin filtro");
    }

    private bool CoincideConLaUrl(ObtenerDocumentosQuery q) =>
        q.Busqueda == "Salas" && q.Estados != null && q.Estados.SequenceEqual(new[] { EstadoDocumento.Vencido })
        && q.Ambito == AmbitoAplicacion.Trabajador && q.TipoDocumentoId == TipoSeguroId
        && q.OrdenarPor == nameof(DocumentoListaDto.FechaVencimiento) && q.Descendente;
}
