using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// El Id reservado de un Documento es un detalle interno del reparto de adjuntos: quien llama desde fuera de esta capa no
/// debe poder elegir el Id con el que nace un Documento, ni por código ni por una petición serializada.
/// </summary>
public class DocumentoIdReservadoNoEsContratoTests
{
    private const string Propiedad = nameof(CrearDocumentoCommand.DocumentoIdReservado);

    [Theory]
    [InlineData(typeof(CrearDocumentoCommand))]
    [InlineData(typeof(RenovarDocumentoCommand))]
    public void Solo_esta_capa_lo_fija_y_no_es_parametro_del_comando(Type comando)
    {
        var propiedad = comando.GetProperty(Propiedad)!;

        propiedad.SetMethod!.IsPublic.Should().BeFalse();
        propiedad.GetCustomAttribute<JsonIgnoreAttribute>().Should().NotBeNull();
        comando.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.Name)
            .Should().NotContain(Propiedad);
    }

    [Fact]
    public void Una_peticion_serializada_no_puede_elegir_el_Id_al_crear()
    {
        var json = $$"""{"TipoDocumentoId":"{{Guid.NewGuid()}}","FechaEmision":"2026-10-01","{{Propiedad}}":"{{Guid.NewGuid()}}"}""";

        JsonSerializer.Deserialize<CrearDocumentoCommand>(json)!.DocumentoIdReservado.Should().BeNull();
    }

    [Fact]
    public void Una_peticion_serializada_no_puede_elegir_el_Id_al_renovar()
    {
        var json = $$"""{"Id":"{{Guid.NewGuid()}}","FechaEmision":"2026-10-01","{{Propiedad}}":"{{Guid.NewGuid()}}"}""";

        JsonSerializer.Deserialize<RenovarDocumentoCommand>(json)!.DocumentoIdReservado.Should().BeNull();
    }

    [Fact]
    public void El_Id_reservado_no_sale_al_serializar_el_comando()
    {
        var comando = new RenovarDocumentoCommand(Guid.NewGuid(), new DateOnly(2026, 10, 1), null, null, null)
        {
            DocumentoIdReservado = Guid.NewGuid()
        };

        JsonSerializer.Serialize(comando).Should().NotContain(Propiedad);
    }
}
