using System.Text.Json;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class ExtensionAcreditacionesTests
{
    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    public async Task Filtra_solo_el_rechazo_vigente_en_la_extension(bool renovada, bool extension, int esperadas)
    {
        var documentos = new DocumentosQueryContextFalso();
        var centros = new CentrosQueryContextFalso();
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var tipos = new TiposDocumentoQueryContextFalso();
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de Trabajo");
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
        var tipo = new TipoDocumento("Certificado", null, false, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        var documento = Documento.DeCliente(Guid.NewGuid(), tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Ilegible", DateTime.UtcNow);
        documentos.ListaDocumentos.Add(documento);
        documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        documentos.ListaRechazosAcreditacionDocumentoPlataforma.AddRange(acreditacion.HistorialRechazos);
        if (renovada)
        {
            var nuevo = documento.NuevoDelMismoTitular(new DateOnly(2026, 2, 1), VigenciaDocumento.NoCaduca, "corregido.pdf", null);
            documento.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
            documentos.ListaDocumentos.Add(nuevo);
            documentos.ListaAcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(nuevo.Id, canal.Id));
        }
        centros.ListaCentros.Add(centro);
        centros.ListaCanalesGestionDocumental.Add(canal);
        proveedores.ListaProveedores.Add(proveedor);
        tipos.ListaTiposDocumento.Add(tipo);
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(documentos, centros, proveedores,
            new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), tipos, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: extension), CancellationToken.None);

        resultado.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).Should().HaveCount(esperadas);
    }

    [Fact]
    public void El_dto_no_expone_el_dni_del_trabajador()
    {
        var dto = new AcreditacionDrillDownDto(Guid.NewGuid(), Guid.NewGuid(), "Nombre", "Tipo",
            EstadoAcreditacion.PendienteDeSubir, null);
        typeof(AcreditacionDrillDownDto).GetProperty("TrabajadorDni").Should().BeNull();
        JsonSerializer.Serialize(dto).Should().NotContain("TrabajadorDni");
    }
}
