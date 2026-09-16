using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Plataforma;
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
///
/// Usa la implementación REAL de <see cref="IAutorizacionEscrituraEfectiva"/>,
/// no un doble, con <see cref="SesionPrivilegiadaAusente"/> (siempre sin
/// sesión): así estos tests también cubren la rama de rol de ese servicio, y
/// la rama de Aprovisionamiento la cubre su propio fichero de tests.
/// </summary>
public class RegistrarHistorialImportacionCommandAutorizacionTests
{
    private static RegistrarHistorialImportacionCommandHandler Handler(
        string? rol, HistorialImportacionRepositorioFalso repositorio, ClientesFalsos.UnitOfWorkFalso unitOfWork)
    {
        var currentUser = new CurrentUserServiceFalso(Guid.NewGuid(), rol);
        var autorizacion = new AutorizacionEscrituraEfectiva(
            currentUser, new SesionPrivilegiadaAusente(), new TenantActualFalso());
        return new(repositorio, unitOfWork, currentUser, autorizacion);
    }

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

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
