using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Defecto 2026-09-08 (cierre del vacío-por-filtro, PR #507): el badge de la
/// pestaña "Generados" notificaba <c>_documentosGenerados.Count</c> —la
/// lista YA filtrada del panel— así que aplicar un filtro hacía bajar un
/// número que Plantillas TALVEG.dc.html define como "N documentos generados
/// con avisos, pendientes de revisar", ajeno al filtro de la tabla. Este
/// handler es la mitad de Application del arreglo: no acepta ningún filtro,
/// así que estructuralmente no puede reproducir ese defecto — la otra mitad
/// (que el panel deje de mandar el conteo filtrado) vive en
/// <see cref="CaeManager.Web.Tests.DocumentosGeneradosPanelVacioPorFiltroTests"/>.
/// </summary>
public class ObtenerTotalDocumentosGeneradosConAvisosQueryHandlerTests
{
    private static PlantillaDocumentoVersion CrearVersion(PlantillasQueryContextFalso contexto)
    {
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        return version;
    }

    private static DocumentoGenerado Generado(Guid versionId, EstadoDocumentoGenerado estado, Guid? trabajadorId = null, Guid? empresaId = null) =>
        new(versionId, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorId, empresaId: empresaId, conAvisos: estado == EstadoDocumentoGenerado.GeneradoConAvisos);

    [Fact]
    public async Task Cuenta_solo_los_generados_con_avisos()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.Generado),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos),
        ]);

        var handler = new ObtenerTotalDocumentosGeneradosConAvisosQueryHandler(contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(2);
    }

    [Fact]
    public async Task Sin_avisos_devuelve_cero()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.Add(Generado(version.Id, EstadoDocumentoGenerado.Generado));

        var handler = new ObtenerTotalDocumentosGeneradosConAvisosQueryHandler(contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(0);
    }

    [Fact]
    public async Task Respeta_el_alcance_de_datos_del_usuario()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var trabajadorVisible = Guid.NewGuid();
        var trabajadorAjeno = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajadorVisible),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajadorAjeno),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajadorVisible]);

        var handler = new ObtenerTotalDocumentosGeneradosConAvisosQueryHandler(contexto, alcance);

        var resultado = await handler.Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }
}
