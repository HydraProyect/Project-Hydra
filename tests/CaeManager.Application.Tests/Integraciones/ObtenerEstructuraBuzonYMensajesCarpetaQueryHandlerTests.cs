using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerEstructuraBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerMensajesCarpeta;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Regresión del mismo defecto que <c>EnviarMensajeNuevoCommandHandlerTests
/// .No_envia_desde_el_buzon_personal_de_otro_gestor_aunque_se_le_pase_su_Id</c>
/// cierra para el envío: sin <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/>,
/// estas dos consultas de LECTURA solo comprobaban la cartera de Cliente
/// (<c>ClienteOpcionalVisibleAsync</c>), que un buzón personal ajeno también
/// supera porque su <c>ClienteId</c> es null — igual que el genérico del
/// tenant. Cualquier Gestor CAE con acceso a Comunicaciones podía leer la
/// estructura de carpetas y el historial de mensajes del buzón personal de
/// otro gestor pasando su <c>ConexionIntegracionId</c> a mano (hallazgo Codex
/// 2026-09-12, previo al rediseño Gen 2 del Buzón).
/// </summary>
public class ObtenerEstructuraBuzonYMensajesCarpetaQueryHandlerTests
{
    private static ConexionIntegracion CrearBuzonPersonalDeOtroGestor(
        out ConexionIntegracionRepositorioFalso conexionRepositorio, out CredencialIntegracionRepositorioFalso credencialRepositorio)
    {
        var conexion = new ConexionIntegracion(
            "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
        conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion.Verificada());
        credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        return conexion;
    }

    [Fact]
    public async Task ObtenerEstructuraBuzonQuery_no_lee_las_carpetas_del_buzon_personal_de_otro_gestor()
    {
        var conexion = CrearBuzonPersonalDeOtroGestor(out var conexionRepositorio, out var credencialRepositorio);
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var handler = new ObtenerEstructuraBuzonQueryHandler(
            conexionRepositorio, accesoGraph, graphClient,
            new AlcanceDatosServiceFalso(conexionIntegracionVisible: false), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ObtenerEstructuraBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
    }

    [Fact]
    public async Task ObtenerMensajesCarpetaQuery_no_lee_los_mensajes_del_buzon_personal_de_otro_gestor()
    {
        var conexion = CrearBuzonPersonalDeOtroGestor(out var conexionRepositorio, out var credencialRepositorio);
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var handler = new ObtenerMensajesCarpetaQueryHandler(
            conexionRepositorio, accesoGraph, graphClient,
            new AlcanceDatosServiceFalso(conexionIntegracionVisible: false), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ObtenerMensajesCarpetaQuery(conexion.Id, "carpeta-1"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
    }

    [Fact]
    public async Task ObtenerEstructuraBuzonQuery_permite_al_propio_dueno_leer_su_buzon_personal()
    {
        var conexion = CrearBuzonPersonalDeOtroGestor(out var conexionRepositorio, out var credencialRepositorio);
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var handler = new ObtenerEstructuraBuzonQueryHandler(
            conexionRepositorio, accesoGraph, graphClient,
            new AlcanceDatosServiceFalso(conexionIntegracionVisible: true), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ObtenerEstructuraBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task ObtenerMensajesCarpetaQuery_permite_al_propio_dueno_leer_su_buzon_personal()
    {
        var conexion = CrearBuzonPersonalDeOtroGestor(out var conexionRepositorio, out var credencialRepositorio);
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var handler = new ObtenerMensajesCarpetaQueryHandler(
            conexionRepositorio, accesoGraph, graphClient,
            new AlcanceDatosServiceFalso(conexionIntegracionVisible: true), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ObtenerMensajesCarpetaQuery(conexion.Id, "carpeta-1"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    /// <summary>Un buzón compartido se lee como lo que es: la consulta pasa a Graph el buzón de la Conexión, no el de quien consintió.</summary>
    [Fact]
    public async Task Las_dos_consultas_leen_un_buzon_compartido_por_su_propia_direccion()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "piloto@tenant.com", "Buzón piloto", Guid.NewGuid().ToString(), Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion.Verificada());
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);

        var estructura = await new ObtenerEstructuraBuzonQueryHandler(
                conexionRepositorio, accesoGraph, graphClient, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso())
            .Handle(new ObtenerEstructuraBuzonQuery(conexion.Id), CancellationToken.None);
        var mensajes = await new ObtenerMensajesCarpetaQueryHandler(
                conexionRepositorio, accesoGraph, graphClient, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso())
            .Handle(new ObtenerMensajesCarpetaQuery(conexion.Id, "carpeta-1"), CancellationToken.None);

        estructura.EsExitoso.Should().BeTrue();
        mensajes.EsExitoso.Should().BeTrue();
        graphClient.BuzonesUsados.Should().HaveCount(2)
            .And.OnlyContain(b => b == new BuzonGraph("piloto@tenant.com", true));
    }

    /// <summary>Sin identidad verificada en la ventana, la dirección puede ser hoy otro buzón: ninguna consulta lo lee.</summary>
    [Fact]
    public async Task Las_dos_consultas_no_leen_un_buzon_sin_identidad_verificada()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "piloto@tenant.com", "Buzón piloto", Guid.NewGuid().ToString(), Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), graphClient);

        var estructura = await new ObtenerEstructuraBuzonQueryHandler(
                conexionRepositorio, accesoGraph, graphClient, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso())
            .Handle(new ObtenerEstructuraBuzonQuery(conexion.Id), CancellationToken.None);
        var mensajes = await new ObtenerMensajesCarpetaQueryHandler(
                conexionRepositorio, accesoGraph, graphClient, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso())
            .Handle(new ObtenerMensajesCarpetaQuery(conexion.Id, "carpeta-1"), CancellationToken.None);

        estructura.EsFallido.Should().BeTrue();
        estructura.Error.Codigo.Should().Be(ErroresBuzonSinVerificar.Codigo);
        mensajes.EsFallido.Should().BeTrue();
        mensajes.Error.Codigo.Should().Be(ErroresBuzonSinVerificar.Codigo);
        graphClient.BuzonesUsados.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerEstructuraBuzonQuery_sigue_leyendo_el_buzon_general_del_tenant()
    {
        var conexion = new ConexionIntegracion("cae@tenant.com", "Buzón general");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion.Verificada());
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var handler = new ObtenerEstructuraBuzonQueryHandler(
            conexionRepositorio, accesoGraph, graphClient, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ObtenerEstructuraBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }
}
