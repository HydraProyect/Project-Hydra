using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Common;
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

    private readonly DocumentosQueryContextFalso _documentos = new();

    private ObtenerTotalDocumentosGeneradosConAvisosQueryHandler Handler(PlantillasQueryContextFalso contexto, AlcanceDatosServiceFalso alcance) =>
        new(contexto, _documentos, new ProyectosQueryContextFalso(), alcance);

    /// <summary>Con su Documento real (legible), para que solo decidan las coordenadas del registro.</summary>
    private DocumentoGenerado Generado(Guid versionId, EstadoDocumentoGenerado estado, Guid? trabajadorId = null, Guid? empresaId = null)
    {
        var documento = trabajadorId is { } trabajador
            ? Documento.DeTrabajador(trabajador, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf")
            : Documento.DeEmpresa(empresaId ?? Guid.NewGuid(), Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        return new(versionId, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorId, empresaId: empresaId,
            conAvisos: estado == EstadoDocumentoGenerado.GeneradoConAvisos);
    }

    /// <summary>Generado con avisos desde una plantilla de ámbito Cliente sin Centro: sin Trabajador, Empresa ni Centro.</summary>
    private DocumentoGenerado GeneradoDeClienteEmpresarial(Guid versionId, Guid clienteEmpresarialId)
    {
        var documento = Documento.DeCliente(clienteEmpresarialId, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        return new(versionId, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, conAvisos: true);
    }

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

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso()).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(2);
    }

    [Fact]
    public async Task Sin_avisos_devuelve_cero()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.Add(Generado(version.Id, EstadoDocumentoGenerado.Generado));

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso()).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

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

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    [Fact]
    public async Task No_cuenta_el_generado_sin_Trabajador_ni_Empresa_de_un_Cliente_empresarial_fuera_de_la_Cartera()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var clienteDeCartera = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            GeneradoDeClienteEmpresarial(version.Id, clienteDeCartera),
            GeneradoDeClienteEmpresarial(version.Id, Guid.NewGuid()),
        ]);
        var gestorConCarteraLimitada = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteDeCartera]);

        var resultado = await Handler(contexto, gestorConCarteraLimitada).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    [Fact]
    public async Task Con_acceso_total_cuenta_todos_los_generados_con_avisos()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.AddRange(
        [
            GeneradoDeClienteEmpresarial(version.Id, Guid.NewGuid()),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos),
        ]);

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true))
            .Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(2);
    }
}
