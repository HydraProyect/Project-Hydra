using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ConectarBuzonMicrosoft365CommandHandlerTests
{
    private static ConectarBuzonMicrosoft365CommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio,
        CredencialIntegracionRepositorioFalso credencialRepositorio,
        SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        EmpresaRepositorioFalso clienteRepositorio,
        AlcanceDatosServiceFalso alcanceDatos,
        Microsoft365GraphClientFalso graphClient,
        UnitOfWorkFalso unitOfWork,
        DirectorioUsuariosServiceFalso? directorioUsuarios = null) =>
        new(conexionRepositorio, credencialRepositorio, suscripcionRepositorio, clienteRepositorio, alcanceDatos,
            directorioUsuarios ?? new DirectorioUsuariosServiceFalso(), graphClient, unitOfWork);

    [Fact]
    public async Task Conecta_un_buzon_del_propio_tenant_y_persiste_conexion_credencial_y_suscripcion()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var suscripcionRepositorio = new SuscripcionWebhookRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, credencialRepositorio, suscripcionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(), unitOfWork);

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
        var clienteAjeno = Empresa.CrearComoCliente("Empresa Ajena", "B12345674", false, null, null);
        var clienteRepositorio = new EmpresaRepositorioFalso();
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
    public async Task Conecta_un_buzon_personal_de_un_gestor()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var gestorId = Guid.NewGuid();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(),
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "gestor@arcosspa.com", "Buzón personal", ClienteId: null, "access-token", "refresh-token", "https://hydra.local",
                GestorPropietarioId: gestorId),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle(c => c.GestorPropietarioId == gestorId);
    }

    [Fact]
    public async Task Rechaza_un_gestorPropietarioId_no_visible_en_el_tenant()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(),
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(), unitOfWork,
            new DirectorioUsuariosServiceFalso(esVisible: false));

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "gestor@arcosspa.com", "Buzón personal", ClienteId: null, "access-token", "refresh-token", "https://hydra.local",
                GestorPropietarioId: Guid.NewGuid()),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.GestorNoVisible");
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
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient, unitOfWork);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
