using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class CompletarIdentidadBuzonServiceTests
{
    private const string Directorio = "11111111-2222-3333-4444-555555555555";

    private sealed class Escenario
    {
        public IntegracionesQueryContextFalso QueryContext { get; } = new();
        public ConexionIntegracionRepositorioFalso Conexiones { get; } = new();
        public ReclamacionBuzonIntegracionRepositorioFalso Reclamaciones { get; } = new();
        public Microsoft365GraphClientFalso Graph { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        public ConexionIntegracion AgregarCompartida(string direccion)
        {
            var conexion = ConexionIntegracion.CrearBuzonCompartido(direccion, "Buzón CAE", Directorio, Guid.NewGuid());
            QueryContext.ConexionesLista.Add(conexion);
            Conexiones.Agregar(conexion);
            Reclamaciones.Reclamar(new ReclamacionBuzonIntegracion(direccion, null, Guid.NewGuid(), conexion.Id, DateTime.UtcNow));
            return conexion;
        }

        public CompletarIdentidadBuzonService Servicio() => new(
            QueryContext, Conexiones, Reclamaciones,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), Graph), Graph, UnitOfWork,
            NullLogger<CompletarIdentidadBuzonService>.Instance);
    }

    [Fact]
    public async Task Completa_la_identidad_de_una_conexion_antigua_y_de_su_reclamacion()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        escenario.Graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-cae";

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().Equal(conexion.Id);
        conexion.IdentidadBuzon.Should().Be("inbox-cae");
        escenario.Reclamaciones.Reclamaciones.Should().ContainSingle().Which.IdentidadBuzon.Should().Be("inbox-cae");
        escenario.Reclamaciones.VecesGuardado.Should().Be(1);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);

        // El compartido se lee con el token de aplicación de su directorio, no con el de una persona.
        escenario.Graph.TokensDeIdentidad.Should().Equal("token-aplicacion-falso");
    }

    /// <summary>
    /// La identidad choca con la reclamación de otro Tenant: el mismo buzón está conectado allí con otra dirección
    /// (un alias). Esta conexión deja de ingerir; la del otro Tenant no se toca.
    /// </summary>
    [Fact]
    public async Task Si_la_identidad_ya_esta_reclamada_por_otra_direccion_la_conexion_pasa_a_ConError()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("alias@operador.test");
        escenario.Reclamaciones.BuzonYaReclamado = true;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty("la renovación no debe rehabilitarla");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonDuplicado);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_lectura_fallida_no_fija_nada_ni_marca_la_conexion()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear("Integraciones.Microsoft365.TiempoAgotado", "timeout");

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.IdentidadBuzon.Should().BeNull();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        escenario.Reclamaciones.VecesGuardado.Should().Be(0);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_conexion_con_la_misma_identidad_sale_verificada()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarConError("fallo transitorio de un ciclo anterior");
        escenario.Graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-cae";

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().Equal(conexion.Id);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError, "rehabilitar es cosa de la renovación");
    }

    /// <summary>El directorio reasignó la dirección a otro buzón: la conexión deja de ingerir y no sale verificada.</summary>
    [Fact]
    public async Task Una_direccion_que_responde_con_otro_buzon_pasa_a_ConError()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        conexion.FijarIdentidadBuzon("inbox-original");
        escenario.Graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-de-otro-buzon";

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
        conexion.IdentidadBuzon.Should().Be("inbox-original");
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task No_mira_las_conexiones_deshabilitadas()
    {
        var escenario = new Escenario();
        var deshabilitada = escenario.AgregarCompartida("off@operador.test");
        deshabilitada.Deshabilitar();

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        deshabilitada.IdentidadBuzon.Should().BeNull();
    }
}
