using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>/api/v1/trabajadores</c> publica una proyección propia, no el DTO interno del listado: cuando el listado
/// ganó el desglose documental (incidencias y «registrados vigentes»), la forma del JSON público no cambió.
/// Estos casos fijan esa forma, de modo que añadir un campo al DTO interno no lo publique sin decidirlo.
/// </summary>
public class TrabajadorApiDtosTests
{
    private static readonly string[] CamposPublicos = ["id", "nombre", "apellidos", "dni", "empleadorNombre", "estadoDocumental"];

    private static TrabajadorListaDto InternoConDesglose() =>
        new(Guid.NewGuid(), "Nora", "Vidal", "12345678Z", "Montajes Ebro S.L.", EstadoDocumento.Vencido)
        {
            Incidencias = [new IncidenciaDocumentalDto(Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Vencido, new DateOnly(2026, 9, 1))],
            DocumentosRegistrados = 4,
            DocumentosVigentes = 3
        };

    [Fact]
    public void La_proyeccion_publica_tiene_exactamente_los_seis_campos_de_siempre()
    {
        typeof(TrabajadorApiListaDto).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo("Id", "Nombre", "Apellidos", "Dni", "EmpleadorNombre", "EstadoDocumental");
    }

    [Fact]
    public void El_JSON_de_una_fila_no_lleva_el_desglose_documental()
    {
        var interno = InternoConDesglose();
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonSerializer.Serialize(interno, opciones).Should().Contain("incidencias").And.Contain("documentosRegistrados",
            "control positivo: el DTO interno sí lo lleva, así que la ausencia de abajo la produce la proyección");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(TrabajadorApiListaDto.DesdeInterno(interno), opciones));

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(CamposPublicos);
    }

    [Fact]
    public void DesdeInterno_conserva_los_valores_y_la_paginacion()
    {
        var interno = InternoConDesglose();
        var recuentos = new Dictionary<string, int> { ["Vencido"] = 1 };
        var pagina = new ResultadoPaginado<TrabajadorListaDto>([interno], 7, 2, 5)
        {
            RecuentosPorEstado = recuentos,
            TotalSinFiltroDeEstado = 9
        };

        var publico = TrabajadorApiListaDto.DesdeInterno(pagina);

        publico.Elementos.Should().ContainSingle().Which.Should().Be(
            new TrabajadorApiListaDto(interno.Id, "Nora", "Vidal", "12345678Z", "Montajes Ebro S.L.", EstadoDocumento.Vencido));
        (publico.TotalElementos, publico.Pagina, publico.TamanoPagina).Should().Be((7, 2, 5));
        publico.RecuentosPorEstado.Should().BeSameAs(recuentos);
        publico.TotalSinFiltroDeEstado.Should().Be(9);
    }
}
