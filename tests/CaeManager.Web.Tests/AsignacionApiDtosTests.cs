using System.Text.Json;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /api/v1/asignaciones</c> con Clave de Integración del Tenant devuelve
/// <see cref="AsignacionApiListaDto"/>. El contrato JSON conserva <c>clienteId</c> y <c>clienteNombre</c>:
/// es lo publicado, aunque en C# el campo se llame por su concepto canónico.
/// </summary>
public class AsignacionApiDtosTests
{
    [Fact]
    public void La_proyeccion_conserva_los_campos_del_contrato_y_su_orden()
    {
        var (id, trabajadorId, centroId, clienteEmpresarialId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dto = new AsignacionListaDto(
            id, trabajadorId, "Nora Vidal", centroId, "Planta Alfa", clienteEmpresarialId, "Alfa Montajes S.L.",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(AsignacionApiListaDto.DesdeInterno(dto), opciones));

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "trabajadorId", "trabajadorNombre", "centroId", "centroNombre", "clienteId", "clienteNombre", "fechaAlta", "fechaBaja");
        json.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        json.RootElement.GetProperty("trabajadorId").GetGuid().Should().Be(trabajadorId);
        json.RootElement.GetProperty("centroId").GetGuid().Should().Be(centroId);
        json.RootElement.GetProperty("clienteId").GetGuid().Should().Be(clienteEmpresarialId);
        json.RootElement.GetProperty("trabajadorNombre").GetString().Should().Be("Nora Vidal");
        json.RootElement.GetProperty("centroNombre").GetString().Should().Be("Planta Alfa");
        json.RootElement.GetProperty("fechaAlta").GetString().Should().Be("2026-01-01");
        json.RootElement.GetProperty("clienteNombre").GetString().Should().Be("Alfa Montajes S.L.");
        json.RootElement.GetProperty("fechaBaja").GetString().Should().Be("2026-12-31");
    }
}
