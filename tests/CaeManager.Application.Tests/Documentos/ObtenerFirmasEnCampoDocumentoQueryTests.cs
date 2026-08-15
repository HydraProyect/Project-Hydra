using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerFirmasEnCampoDocumentoQueryTests
{
    private static readonly string Hash = new('a', FirmaEnCampoDocumento.LongitudHashSha256);

    [Fact]
    public async Task Devuelve_lista_vacia_cuando_el_documento_no_tiene_firmas()
    {
        var contexto = new DocumentosQueryContextFalso();
        var handler = new ObtenerFirmasEnCampoDocumentoQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerFirmasEnCampoDocumentoQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Devuelve_las_firmas_del_documento_ordenadas_por_fecha_y_no_las_de_otro_documento()
    {
        var documentoId = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        var masReciente = new FirmaEnCampoDocumento(
            documentoId, Guid.NewGuid(), "Ana López", "CoordinadorCae", new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc), "Sevilla", Hash);
        var masAntigua = new FirmaEnCampoDocumento(
            documentoId, Guid.NewGuid(), "Juan Pérez", "GestorCae", new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), null, Hash);
        var deOtroDocumento = new FirmaEnCampoDocumento(
            Guid.NewGuid(), Guid.NewGuid(), "Otro Firmante", "GestorCae", DateTime.UtcNow, null, Hash);
        contexto.ListaFirmasEnCampoDocumento.AddRange([masReciente, masAntigua, deOtroDocumento]);
        var handler = new ObtenerFirmasEnCampoDocumentoQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerFirmasEnCampoDocumentoQuery(documentoId), CancellationToken.None);

        resultado.Should().HaveCount(2);
        resultado[0].FirmanteNombre.Should().Be("Juan Pérez");
        resultado[1].FirmanteNombre.Should().Be("Ana López");
        resultado[1].Ubicacion.Should().Be("Sevilla");
    }
}
