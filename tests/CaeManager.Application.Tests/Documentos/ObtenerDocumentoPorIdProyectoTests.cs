using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerDocumentoPorIdProyectoTests
{
    [Fact]
    public async Task Un_Proyecto_eliminado_no_revela_su_documento_ni_provoca_excepcion()
    {
        var proyecto = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Obra", new(2026, 3, 1), null, null);
        proyecto.MarcarComoEliminado(Guid.NewGuid());
        var documento = Documento.DeProyecto(proyecto.Id, Guid.NewGuid(), new(2026, 3, 1), VigenciaDocumento.NoCaduca);
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        // Simula el filtro global: el documento existe, el Proyecto eliminado queda excluido.
        var proyectos = new ProyectosQueryContextFalso();
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            documentos, null!, proyectos, null!, null!, null!, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentoPorIdQuery(documento.Id), default);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Sin_Empresa_ni_otro_propietario_no_revela_el_documento_ni_provoca_excepcion()
    {
        var documento = Documento.DeEmpresa(Guid.NewGuid(), Guid.NewGuid(), new(2026, 3, 1), VigenciaDocumento.NoCaduca);
        typeof(Documento).GetProperty(nameof(Documento.EmpresaId))!.SetValue(documento, null);
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            documentos, null!, null!, null!, null!, null!, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentoPorIdQuery(documento.Id), default);

        resultado.Should().BeNull();
    }
}