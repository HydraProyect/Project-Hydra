using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ObtenerDocumentosGeneradosQueryHandlerTests
{
    private static (PlantillasQueryContextFalso Contexto, PlantillaDocumento Plantilla, PlantillaDocumentoVersion Version) ConstruirPlantilla()
    {
        var contexto = new PlantillasQueryContextFalso();
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        return (contexto, plantilla, version);
    }

    [Fact]
    public async Task Devuelve_los_documentos_generados_con_nombre_de_plantilla_y_trabajador()
    {
        var (contexto, plantilla, version) = ConstruirPlantilla();
        var trabajadoresContext = new TrabajadoresQueryContextFalso();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", "77189989B");
        trabajadoresContext.ListaTrabajadores.Add(trabajador);
        var empresasContext = new EmpresasQueryContextFalso();

        var generado = new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajador.Id);
        contexto.ListaDocumentosGenerados.Add(generado);

        var handler = new ObtenerDocumentosGeneradosQueryHandler(contexto, trabajadoresContext, empresasContext, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        var fila = resultado.Should().ContainSingle().Subject;
        fila.PlantillaNombre.Should().Be("Ficha de riesgos");
        fila.TrabajadorNombreCompleto.Should().Be("Juan Pérez");
    }

    [Fact]
    public async Task Filtra_por_plantilla()
    {
        var (contexto, plantillaUno, versionUno) = ConstruirPlantilla();
        var plantillaDos = new PlantillaDocumento(OrigenPlantilla.Externa, "Otra ficha", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var versionDos = new PlantillaDocumentoVersion(plantillaDos.Id, 1, "url2.pdf", new string('b', 64));
        contexto.ListaPlantillasDocumento.Add(plantillaDos);
        contexto.ListaPlantillasDocumentoVersion.Add(versionDos);

        contexto.ListaDocumentosGenerados.AddRange(
        [
            new DocumentoGenerado(versionUno.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow),
            new DocumentoGenerado(versionDos.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow),
        ]);

        var handler = new ObtenerDocumentosGeneradosQueryHandler(
            contexto, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentosGeneradosQuery(PlantillaDocumentoId: plantillaUno.Id), CancellationToken.None);

        resultado.Should().ContainSingle(d => d.PlantillaNombre == "Ficha de riesgos");
    }

    [Fact]
    public async Task Filtra_por_trabajador()
    {
        var (contexto, _, version) = ConstruirPlantilla();
        var trabajadorBuscado = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorBuscado),
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: Guid.NewGuid()),
        ]);

        var handler = new ObtenerDocumentosGeneradosQueryHandler(
            contexto, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentosGeneradosQuery(TrabajadorId: trabajadorBuscado), CancellationToken.None);

        resultado.Should().ContainSingle(d => d.TrabajadorId == trabajadorBuscado);
    }

    [Fact]
    public async Task Respeta_el_alcance_de_datos_del_usuario()
    {
        var (contexto, _, version) = ConstruirPlantilla();
        var trabajadorVisible = Guid.NewGuid();
        var trabajadorAjeno = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorVisible),
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorAjeno),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajadorVisible]);

        var handler = new ObtenerDocumentosGeneradosQueryHandler(
            contexto, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), alcance);

        var resultado = await handler.Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(d => d.TrabajadorId == trabajadorVisible);
    }
}
