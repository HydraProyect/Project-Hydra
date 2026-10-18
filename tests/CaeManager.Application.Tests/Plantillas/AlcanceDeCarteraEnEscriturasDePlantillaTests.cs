using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;
using CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Agregar una versión, guardar sus elementos y confirmarla exigen la Asignación de Cartera sobre la plantilla, con
/// el mismo criterio con el que CrearPlantillaDocumentoCommandHandler decide si se puede crear: una plantilla atada a
/// un Centro o a un Cliente empresarial fuera del alcance responde igual que una que no existe, y no cambia nada.
/// Una plantilla general del Tenant (sin Centro ni Cliente empresarial) sigue al alcance de todos.
/// </summary>
public class AlcanceDeCarteraEnEscriturasDePlantillaTests
{
    public enum Escritura { AgregarVersion, GuardarElementos, Confirmar }

    public enum Ancla { Centro, ClienteEmpresarial, CentroDentroYClienteFuera }

    private readonly Guid _centroDeMiCartera = Guid.NewGuid();
    private readonly Guid _clienteDeMiCartera = Guid.NewGuid();
    private readonly PlantillaDocumentoRepositorioFalso _plantillas = new();
    private readonly PlantillaDocumentoVersionRepositorioFalso _versiones = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private AlcanceDatosServiceFalso GestorConCarteraExplicita() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera], centroIdsVisibles: [_centroDeMiCartera]);

    private (PlantillaDocumento Plantilla, PlantillaDocumentoVersion Version) Sembrar(Guid? centroId, Guid? clienteId)
    {
        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid(),
            centroId: centroId, clienteId: clienteId);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url-falsa.pdf", new string('a', 64));
        version.EstablecerElementos([new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo")]);
        _plantillas.Agregar(plantilla);
        _versiones.Agregar(version);
        return (plantilla, version);
    }

    private (Guid? CentroId, Guid? ClienteId) FueraDeCartera(Ancla ancla) => ancla switch
    {
        Ancla.Centro => (Guid.NewGuid(), null),
        Ancla.ClienteEmpresarial => (null, Guid.NewGuid()),
        _ => (_centroDeMiCartera, Guid.NewGuid()),
    };

    private async Task<Result> EjecutarAsync(Escritura escritura, IAlcanceDatosService alcance, PlantillaDocumento plantilla, PlantillaDocumentoVersion version)
    {
        switch (escritura)
        {
            case Escritura.AgregarVersion:
                var resultado = await new AgregarVersionPlantillaCommandHandler(
                        _plantillas, _versiones, new FileStorageServiceFalso(), alcance, _unitOfWork,
                        NullLogger<AgregarVersionPlantillaCommandHandler>.Instance)
                    .Handle(new AgregarVersionPlantillaCommand(plantilla.Id, [1, 2, 3], "nueva.pdf"), CancellationToken.None);
                return resultado.EsExitoso ? Result.Exito() : Result.Fallo(resultado.Error);
            case Escritura.GuardarElementos:
                return await new GuardarElementosPlantillaCommandHandler(_versiones, _plantillas, alcance, _unitOfWork)
                    .Handle(new GuardarElementosPlantillaCommand(version.Id, [ElementoDto]), CancellationToken.None);
            default:
                return await new ConfirmarPlantillaDocumentoVersionCommandHandler(
                        _versiones, _plantillas, new CurrentUserServiceFalso(Guid.NewGuid()), new FileStorageServiceFalso(),
                        new ExtractorSinCampos(), alcance, _unitOfWork)
                    .Handle(new ConfirmarPlantillaDocumentoVersionCommand(version.Id), CancellationToken.None);
        }
    }

    private static string CodigoDeNoEncontrada(Escritura escritura) =>
        escritura == Escritura.AgregarVersion ? "Plantilla.NoEncontrada" : "Plantilla.VersionNoEncontrada";

    private static readonly ElementoPlantillaEntradaDto ElementoDto =
        new(TipoElementoPlantilla.Texto, 1, 10, 20, 100, 15, "CIF", FuenteDatoPlantilla.EmpresaCif, null, null, false, null, "campoCif");

    public static TheoryData<Escritura, Ancla> FueraDeAlcance()
    {
        var datos = new TheoryData<Escritura, Ancla>();
        foreach (var escritura in Enum.GetValues<Escritura>())
            foreach (var ancla in Enum.GetValues<Ancla>())
                datos.Add(escritura, ancla);
        return datos;
    }

    [Theory]
    [MemberData(nameof(FueraDeAlcance))]
    public async Task Un_Gestor_con_cartera_explicita_no_escribe_en_una_plantilla_fuera_de_ella_y_recibe_lo_mismo_que_si_no_existiera(
        Escritura escritura, Ancla ancla)
    {
        var (centroId, clienteId) = FueraDeCartera(ancla);
        var (plantilla, version) = Sembrar(centroId, clienteId);

        var resultado = await EjecutarAsync(escritura, GestorConCarteraExplicita(), plantilla, version);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigoDeNoEncontrada(escritura), "fuera de cartera no se delata que la plantilla existe");
        _versiones.Lista.Should().ContainSingle("no nace ninguna versión nueva");
        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Borrador);
        plantilla.VersionActualId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(Escritura.AgregarVersion)]
    [InlineData(Escritura.GuardarElementos)]
    [InlineData(Escritura.Confirmar)]
    public async Task Un_Gestor_con_cartera_explicita_escribe_en_una_plantilla_de_su_cartera(Escritura escritura)
    {
        var (plantilla, version) = Sembrar(_centroDeMiCartera, _clienteDeMiCartera);

        var resultado = await EjecutarAsync(escritura, GestorConCarteraExplicita(), plantilla, version);

        resultado.EsExitoso.Should().BeTrue();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(Escritura.AgregarVersion)]
    [InlineData(Escritura.GuardarElementos)]
    [InlineData(Escritura.Confirmar)]
    public async Task Un_Gestor_con_cartera_explicita_escribe_en_una_plantilla_general_del_Tenant(Escritura escritura)
    {
        var (plantilla, version) = Sembrar(centroId: null, clienteId: null);

        var resultado = await EjecutarAsync(escritura, GestorConCarteraExplicita(), plantilla, version);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(Escritura.AgregarVersion)]
    [InlineData(Escritura.GuardarElementos)]
    [InlineData(Escritura.Confirmar)]
    public async Task Sin_restriccion_de_cartera_escribe_en_cualquier_plantilla(Escritura escritura)
    {
        var (plantilla, version) = Sembrar(Guid.NewGuid(), Guid.NewGuid());

        var resultado = await EjecutarAsync(escritura, new AlcanceDatosServiceFalso(tieneAccesoTotal: true), plantilla, version);

        resultado.EsExitoso.Should().BeTrue();
    }

    private sealed class ExtractorSinCampos : IExtractorCamposAcroFormService
    {
        public IReadOnlyList<CampoAcroFormDetectado> Extraer(byte[] pdfOriginal) => [];
    }
}
