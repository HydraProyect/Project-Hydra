using System.Text.Json;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /api/v1/clientes</c> devuelve <see cref="ClienteListaDto"/> tal cual. Cuando el listado de Clientes
/// empresariales ganó las incidencias de la fila (que nombran Trabajadores), la forma del JSON público no cambió:
/// esos dos campos no se serializan. Estos casos fijan esa forma, de modo que añadir un campo al record no lo
/// publique sin decidirlo.
///
/// <para>
/// A diferencia de <see cref="TrabajadorApiDtosTests"/>, aquí no hay una proyección pública aparte: tendría que
/// repetir <c>EjecutivoUsuarioId</c>, deuda terminológica que <c>TerminologiaCanonicaTests</c> no deja crecer.
/// Lo que estos casos NO observan es el endpoint: serializan el record con las opciones web por defecto, que
/// son las de Minimal API mientras nadie las cambie.
/// </para>
/// </summary>
public class ClienteApiDtosTests
{
    private static readonly JsonSerializerOptions OpcionesWeb = new(JsonSerializerDefaults.Web);

    /// <summary>Los campos que la API devolvía antes de las incidencias, en su orden.</summary>
    private static readonly string[] CamposPublicos =
    [
        "id", "razonSocial", "cif", "esCritico", "creadoEnUtc", "sinContactoEnAgenda", "ejecutivoUsuarioId", "centros",
        "estadoDocumentalPeor", "estadoDocumentalCantidad"
    ];

    private static IncidenciaClienteDto UnaIncidencia() => new(
        "incidencia-1", EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", Guid.NewGuid(),
        "Nora Vidal", new DateOnly(2026, 9, 1), null);

    private static ClienteListaDto ConIncidencias() =>
        new(Guid.NewGuid(), "Alfa Montajes S.L.", "A48010615", true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SinContactoEnAgenda: true, EjecutivoUsuarioId: Guid.NewGuid(), Centros: 3,
            EstadoDocumentalPeor: EstadoDocumento.Vencido, EstadoDocumentalCantidad: 2)
        {
            Incidencias = [UnaIncidencia()],
            IncidenciasTotales = 2
        };

    [Fact]
    public void El_JSON_de_una_fila_tiene_los_diez_campos_de_siempre_y_no_lleva_incidencias_ni_nombres_de_trabajadores()
    {
        JsonSerializer.Serialize(UnaIncidencia(), OpcionesWeb).Should().Contain("Nora Vidal",
            "control positivo: una incidencia serializada sí lleva el nombre, así que su ausencia de abajo es del record de la fila");

        var publico = JsonSerializer.Serialize(ConIncidencias(), OpcionesWeb);

        using var json = JsonDocument.Parse(publico);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(CamposPublicos);
        publico.Should().NotContain("Nora Vidal").And.NotContain("ncidencias");
    }

    [Fact]
    public void El_JSON_de_la_pagina_tampoco_las_lleva()
    {
        var pagina = new ResultadoPaginado<ClienteListaDto>([ConIncidencias()], 7, 2, 5);

        var publico = JsonSerializer.Serialize(pagina, OpcionesWeb);

        publico.Should().Contain("Alfa Montajes S.L.", "control positivo: la fila sí está");
        publico.Should().NotContain("Nora Vidal").And.NotContain("ncidencias");
    }
}
