using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ReactivarConexionCommandHandlerTests
{
    [Fact]
    public async Task Rehabilita_una_conexion_con_error_y_limpia_el_ultimo_error()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.MarcarConError("El refresh token ha expirado.");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReactivarConexionCommandHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Rechaza_una_conexion_que_no_existe()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReactivarConexionCommandHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_una_conexion_fuera_de_la_cartera_visible()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE", clienteId: Guid.NewGuid());
        conexion.MarcarConError("fallo");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReactivarConexionCommandHandler(
            conexionRepositorio, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError, "un cliente fuera de la cartera visible no debe poder tocar la conexión");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
