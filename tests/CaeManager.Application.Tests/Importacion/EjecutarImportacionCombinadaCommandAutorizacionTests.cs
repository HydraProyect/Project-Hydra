using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;
using CentrosFalsos = CaeManager.Application.Tests.Centros;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;
using DocumentosFalsos = CaeManager.Application.Tests.Documentos;
using PlantillasFalsas = CaeManager.Application.Tests.Plantillas;
using TrabajadoresFalsos = CaeManager.Application.Tests.Trabajadores;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Restaura en Application el límite que ya expone ImportarCombinado.razor
/// (Administrador). Plan vacío a propósito: la comprobación de rol corta
/// antes de tocar ningún repositorio de escritura, así que
/// <see cref="RelacionEmpresarialRepositorioNoUsado"/> nunca llega a
/// invocarse — solo existe para completar la firma del handler.
/// </summary>
public class EjecutarImportacionCombinadaCommandAutorizacionTests
{
    private static EjecutarImportacionCombinadaCommandHandler Handler(string? rol) => new(
        new ClientesFalsos.EmpresaRepositorioFalso(),
        new RelacionEmpresarialRepositorioNoUsado(),
        new CentrosFalsos.CentroRepositorioFalso(),
        new TrabajadoresFalsos.TrabajadorRepositorioFalso(),
        new PlantillasFalsas.CentrosQueryContextFalso(),
        new DocumentosFalsos.EmpresasQueryContextFalso(),
        new PlantillasFalsas.TrabajadoresQueryContextFalso(),
        new ClientesFalsos.UnitOfWorkFalso(),
        new CurrentUserServiceFalso(Guid.NewGuid(), rol));

    private static PlanImportacionCombinadaDto PlanVacio() => new([], [], [], [], [], []);

    [Fact]
    public async Task Administrador_puede_ejecutar_una_importacion_combinada()
    {
        var resultado = await Handler("Administrador").Handle(
            new EjecutarImportacionCombinadaCommand(PlanVacio(), ReemplazarExistentes: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    [InlineData(null)]
    public async Task Otros_roles_no_pueden_ejecutar_una_importacion_combinada(string? rol)
    {
        var resultado = await Handler(rol).Handle(
            new EjecutarImportacionCombinadaCommand(PlanVacio(), ReemplazarExistentes: false), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Importacion.SoloAdministrador");
    }

    private sealed class RelacionEmpresarialRepositorioNoUsado : IRelacionEmpresarialRepository
    {
        public void Agregar(RelacionEmpresarial relacion) => throw new NotImplementedException();

        public Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(Guid proveedoraId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> AgregarSiNoVigenteAsync(
            Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> CerrarVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(
            IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> CreariaUnCicloAsync(Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
