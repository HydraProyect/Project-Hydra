using CaeManager.Application.Plantillas;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// El recorte en la base de <see cref="AlcanceDeDocumentosGenerados.AcotarALaCarteraAsync"/> (hallazgo de Codex, ronda 2:
/// el contador materializaba sin cota los generados del Tenant propietario). Se prueba solo, sin la puerta en memoria
/// detrás, porque juntas cada una tapa el fallo de la otra: aquí cae cualquier coordenada que el recorte deje de mirar.
/// </summary>
public class AcotarDocumentosGeneradosALaCarteraTests
{
    private readonly Guid _trabajador = Guid.NewGuid();
    private readonly Guid _empresa = Guid.NewGuid();
    private readonly Guid _centro = Guid.NewGuid();
    private readonly Guid _clienteEmpresarial = Guid.NewGuid();

    private static Guid AgregarVersion(PlantillasQueryContextFalso contexto, Guid? clienteEmpresarial = null, Guid? centro = null)
    {
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual,
            Guid.NewGuid(), clienteId: clienteEmpresarial, centroId: centro);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        return version.Id;
    }

    private static DocumentoGenerado Generado(Guid versionId, Guid? trabajador, Guid? empresa, Guid? centro) =>
        new(versionId, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajador, empresaId: empresa, centroId: centro);

    private AlcanceDatosServiceFalso GestorConCarteraLimitada() => new(
        tieneAccesoTotal: false, clienteIdsVisibles: [_clienteEmpresarial], trabajadorIdsVisibles: [_trabajador],
        empresaIdsVisibles: [_empresa], centroIdsVisibles: [_centro]);

    [Fact]
    public async Task Deja_fuera_cada_coordenada_que_la_Cartera_no_alcanza_y_conserva_lo_que_si()
    {
        var contexto = new PlantillasQueryContextFalso();
        var general = AgregarVersion(contexto);
        var deCartera = AgregarVersion(contexto, clienteEmpresarial: _clienteEmpresarial, centro: _centro);
        var deOtroCliente = AgregarVersion(contexto, clienteEmpresarial: Guid.NewGuid());
        var deOtroCentro = AgregarVersion(contexto, centro: Guid.NewGuid());

        var dentro = Generado(general, _trabajador, _empresa, _centro);
        var sinCoordenadas = Generado(general, null, null, null);
        var dePlantillaDeCartera = Generado(deCartera, _trabajador, null, null);
        var trabajadorFuera = Generado(general, Guid.NewGuid(), _empresa, _centro);
        var empresaFuera = Generado(general, _trabajador, Guid.NewGuid(), _centro);
        var centroFuera = Generado(general, _trabajador, _empresa, Guid.NewGuid());
        var plantillaDeOtroCliente = Generado(deOtroCliente, _trabajador, null, null);
        var plantillaDeOtroCentro = Generado(deOtroCentro, _trabajador, null, null);
        contexto.ListaDocumentosGenerados.AddRange(
            [dentro, sinCoordenadas, dePlantillaDeCartera, trabajadorFuera, empresaFuera, centroFuera, plantillaDeOtroCliente, plantillaDeOtroCentro]);

        var acotada = await GestorConCarteraLimitada().AcotarALaCarteraAsync(contexto.DocumentosGenerados, contexto);

        acotada.Select(d => d.Id).ToList().Should().BeEquivalentTo([dentro.Id, sinCoordenadas.Id, dePlantillaDeCartera.Id]);
    }

    [Fact]
    public async Task El_generado_de_una_version_que_ya_no_se_lee_no_se_recorta_por_plantilla()
    {
        var contexto = new PlantillasQueryContextFalso();
        var generado = Generado(Guid.NewGuid(), _trabajador, null, null);
        contexto.ListaDocumentosGenerados.Add(generado);

        var acotada = await GestorConCarteraLimitada().AcotarALaCarteraAsync(contexto.DocumentosGenerados, contexto);

        acotada.Select(d => d.Id).Should().ContainSingle().Which.Should().Be(generado.Id);
    }

    [Fact]
    public async Task Con_acceso_total_no_recorta_nada()
    {
        var contexto = new PlantillasQueryContextFalso();
        var deOtroCliente = AgregarVersion(contexto, clienteEmpresarial: Guid.NewGuid());
        contexto.ListaDocumentosGenerados.AddRange(
            [Generado(deOtroCliente, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Generado(deOtroCliente, null, null, null)]);

        var acotada = await new AlcanceDatosServiceFalso(tieneAccesoTotal: true).AcotarALaCarteraAsync(contexto.DocumentosGenerados, contexto);

        acotada.Should().HaveCount(2);
    }
}
