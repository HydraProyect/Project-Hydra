using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Aunque este comando "solo" escribe una fila de historial, es alcanzable por
/// MediatR con cualquier rol de escritura — sin este guard, un rol distinto de
/// Administrador podría inyectar un registro de historial fabricado sin haber
/// ejecutado ninguna importación real. Mismo límite que las 4 páginas de
/// importación (Administrador).
/// </summary>
public class RegistrarHistorialImportacionCommandAutorizacionTests
{
    private static RegistrarHistorialImportacionCommandHandler Handler(
        string? rol, HistorialImportacionRepositorioFalso repositorio, ClientesFalsos.UnitOfWorkFalso unitOfWork) =>
        new(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), rol));

    private static RegistrarHistorialImportacionCommand Comando() =>
        new("clientes", "archivo.xlsx", Exitosa: true, TotalCreados: 10, TotalAdvertencias: 0, TotalOmitidos: 0, MensajeError: null);

    [Fact]
    public async Task Administrador_puede_registrar_el_historial_de_una_importacion()
    {
        var repositorio = new HistorialImportacionRepositorioFalso();
        var unitOfWork = new ClientesFalsos.UnitOfWorkFalso();

        var resultado = await Handler("Administrador", repositorio, unitOfWork).Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Registros.Should().ContainSingle();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    [InlineData(null)]
    public async Task Otros_roles_no_pueden_registrar_el_historial_de_una_importacion(string? rol)
    {
        var repositorio = new HistorialImportacionRepositorioFalso();
        var unitOfWork = new ClientesFalsos.UnitOfWorkFalso();

        var resultado = await Handler(rol, repositorio, unitOfWork).Handle(Comando(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Importacion.SoloAdministrador");
        repositorio.Registros.Should().BeEmpty();
    }
}
