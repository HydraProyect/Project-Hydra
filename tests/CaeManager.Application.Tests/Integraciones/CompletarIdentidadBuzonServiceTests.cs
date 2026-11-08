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

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

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

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

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
    public async Task No_toca_conexiones_que_ya_tienen_identidad_ni_las_deshabilitadas()
    {
        var escenario = new Escenario();
        var conIdentidad = escenario.AgregarCompartida("con@operador.test");
        conIdentidad.FijarIdentidadBuzon("inbox-ya");
        var deshabilitada = escenario.AgregarCompartida("off@operador.test");
        deshabilitada.Deshabilitar();

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        deshabilitada.IdentidadBuzon.Should().BeNull();
    }
}
