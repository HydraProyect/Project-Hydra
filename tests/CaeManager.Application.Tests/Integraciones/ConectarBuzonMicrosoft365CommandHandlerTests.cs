using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ConectarBuzonMicrosoft365CommandHandlerTests
{
    private static ConectarBuzonMicrosoft365CommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio,
        CredencialIntegracionRepositorioFalso credencialRepositorio,
        SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        ClienteRepositorioFalso clienteRepositorio,
        AlcanceDatosServiceFalso alcanceDatos,
        Microsoft365GraphClientFalso graphClient,
        UnitOfWorkFalso unitOfWork) =>
        new(conexionRepositorio, credencialRepositorio, suscripcionRepositorio, clienteRepositorio, alcanceDatos, graphClient, unitOfWork);

    [Fact]
    public async Task Conecta_un_buzon_del_propio_tenant_y_persiste_conexion_credencial_y_suscripcion()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var suscripcionRepositorio = new SuscripcionWebhookRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, credencialRepositorio, suscripcionRepositorio,
            new ClienteRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle(c => c.BuzonEmail == "cae@tenant.com");
        credencialRepositorio.Credenciales.Should().ContainSingle(c => c.RefreshToken == "refresh-token");
        suscripcionRepositorio.Suscripciones.Should().ContainSingle();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Rechaza_un_clienteId_fuera_de_la_cartera()
    {
        var clienteAjeno = new Cliente("Empresa Ajena", "B12345674", esCritico: false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(clienteAjeno);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(),
            clienteRepositorio, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]),
            new Microsoft365GraphClientFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@cliente.com", "Buzón CAE", clienteAjeno.Id, "access-token", "refresh-token", "https://hydra.local"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
        conexionRepositorio.Conexiones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_persiste_nada_si_la_creacion_de_la_suscripcion_en_Graph_falla()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var graphClient = new Microsoft365GraphClientFalso { FallaCreacionSuscripcion = true };
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(),
            new ClienteRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient, unitOfWork);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
