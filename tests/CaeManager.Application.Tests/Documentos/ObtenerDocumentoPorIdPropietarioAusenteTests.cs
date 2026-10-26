using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerDocumentoPorIdPropietarioAusenteTests
{
    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador)]
    [InlineData(AmbitoAplicacion.Empresa)]
    [InlineData(AmbitoAplicacion.Cliente)]
    [InlineData(AmbitoAplicacion.Vehiculo)]
    public async Task Sin_entidad_propietaria_con_acceso_total_no_revela_el_documento_ni_provoca_excepcion(
        AmbitoAplicacion ambito)
    {
        var tipo = new TipoDocumento("Certificado", 12, true, 1, ambito,
            RequisitoDocumental.Si, NaturalezaJuridica.PracticaSector);
        var propietarioId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 3, 1);
        var documento = ambito switch
        {
            AmbitoAplicacion.Trabajador => Documento.DeTrabajador(propietarioId, tipo.Id, fecha, VigenciaDocumento.NoCaduca),
            AmbitoAplicacion.Empresa => Documento.DeEmpresa(propietarioId, tipo.Id, fecha, VigenciaDocumento.NoCaduca),
            AmbitoAplicacion.Cliente => Documento.DeCliente(propietarioId, tipo.Id, fecha, VigenciaDocumento.NoCaduca),
            AmbitoAplicacion.Vehiculo => Documento.DeVehiculo(propietarioId, tipo.Id, fecha, VigenciaDocumento.NoCaduca),
            _ => throw new ArgumentOutOfRangeException(nameof(ambito))
        };
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);
        // El Documento y su tipo existen; la entidad propietaria queda fuera de la consulta.
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            documentos, new EmpresasQueryContextFalso(), null!, tipos,
            new TrabajadoresQueryContextFalso(), new VehiculosVacios(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        var resultado = await handler.Handle(new ObtenerDocumentoPorIdQuery(documento.Id), default);

        resultado.Should().BeNull();
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }
}
