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

    [Fact]
    public async Task No_reactiva_el_buzon_personal_de_otro_gestor_aunque_se_le_pase_su_Id()
    {
        // Mismo hallazgo que EnviarMensajeNuevoCommandHandlerTests: un buzón
        // personal tiene ClienteId null, igual que el genérico del tenant —
        // sin ConexionIntegracionVisibleAsync, la cartera de Cliente no lo
        // protege de un tercero que conozca/adivine su Id.
        var conexionPersonal = new ConexionIntegracion(
            "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
        conexionPersonal.MarcarConError("fallo");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexionPersonal);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReactivarConexionCommandHandler(
            conexionRepositorio, new AlcanceDatosServiceFalso(conexionIntegracionVisible: false), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexionPersonal.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexionPersonal.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
