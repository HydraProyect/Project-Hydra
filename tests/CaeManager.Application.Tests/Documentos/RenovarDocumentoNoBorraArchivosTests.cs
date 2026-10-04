using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Renovar no borra (decisión 7 del diseño del documento efectivo): el archivo del documento anterior es historial y
/// queda en almacenamiento, referenciado por su fila. La garantía es estructural: el handler ya no recibe el servicio de
/// almacenamiento, así que ningún camino suyo puede eliminar un archivo. Un cambio que lo reintroduzca tiene que
/// romper este test y justificarse. (El comportamiento —el anterior conserva archivo, fechas y acreditaciones— lo prueba
/// PostgreSQL real en <c>RenovarDocumentoConcurrenciaTests</c> y <c>AcreditacionDocumentoPlataformaSincronizacionTests</c>.)
/// </summary>
public class RenovarDocumentoNoBorraArchivosTests
{
    [Fact]
    public void El_handler_de_renovar_no_depende_del_almacenamiento_de_archivos()
    {
        var parametros = typeof(RenovarDocumentoCommandHandler).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType).ToList();

        parametros.Should().NotContain(typeof(IFileStorageService),
            "renovar sustituye un documento por otro y deja el archivo anterior en el historial; no borra nada");
        parametros.Should().NotBeEmpty("control positivo: la reflexión sí ve las dependencias del handler");
    }
}
