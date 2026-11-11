using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.RepararMarcaHueco;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Codex, ronda 19, hallazgo alto: la acción Reparar de Conexiones enviaba Desconectar, que bajo el cerrojo desconecta
/// la conexión si la encuentra Habilitada o ConError. Si otra sesión la reactivaba después de cargar la fila, Reparar la
/// desconectaba. Reparar es ahora un comando propio que solo registra la marca de una conexión que sigue Deshabilitada.
/// </summary>
public class RepararMarcaHuecoCommandHandlerTests
{
    /// <summary>Cuenta los descartes y, si se le dice, deja en el repositorio la versión de la base (otra instancia).</summary>
    private sealed class DescarteContado(Action? alDescartar = null) : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes()
        {
            Veces++;
            alDescartar?.Invoke();
        }
    }

    private sealed class LoggerCaptura : ILogger<RepararMarcaHuecoCommandHandler>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    private static RepararMarcaHuecoCommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexiones, IUnitOfWork unitOfWork, CerrojoVerificacionBuzonFalso? cerrojo = null,
        AlcanceDatosServiceFalso? alcance = null, ILogger<RepararMarcaHuecoCommandHandler>? logger = null,
        DescarteContado? descarte = null)
    {
        cerrojo ??= new CerrojoVerificacionBuzonFalso();
        return new RepararMarcaHuecoCommandHandler(
            conexiones, alcance ?? new AlcanceDatosServiceFalso(), cerrojo,
            new ReparacionMarcaHuecoService(conexiones, unitOfWork, cerrojo, descarte ?? new DescarteContado(), NullLogger<ReparacionMarcaHuecoService>.Instance),
            logger ?? NullLogger<RepararMarcaHuecoCommandHandler>.Instance);
    }

    /// <summary>Buzón compartido Deshabilitado con <paramref name="porRegistrarDesdeUtc"/> por registrar.</summary>
    private static (ConexionIntegracion Conexion, ConexionIntegracionRepositorioFalso Conexiones) Deshabilitada(DateTime? porRegistrarDesdeUtc)
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "cae@operador.test", "Buzón CAE", "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        conexion.Deshabilitar(porRegistrarDesdeUtc);
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        return (conexion, conexiones);
    }

    [Fact]
    public async Task Una_reactivacion_entre_la_carga_y_el_cerrojo_no_se_desconecta_ni_se_toca()
    {
        var porRegistrar = DateTime.UtcNow.AddMinutes(-10);
        var (conexion, conexiones) = Deshabilitada(porRegistrar);
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        ConexionIntegracion? reactivada = null;
        // Otra sesión reactivó la conexión después de que Conexiones cargara la fila Deshabilitada.
        cerrojo.AlTomar = id => reactivada = conexiones.SustituirEnBase(id, c => c.MarcarIdentidadVerificada(DateTime.UtcNow));
        var handler = CrearHandler(conexiones, unitOfWork, cerrojo);

        var resultado = await handler.Handle(new RepararMarcaHuecoCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(RepararMarcaHuecoCommandHandler.CodigoNoDeshabilitada);
        cerrojo.Pedidos.Should().Equal([conexion.Id], "control: el cerrojo sí se tomó");
        reactivada!.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "Reparar nunca desconecta");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Repara_la_marca_de_una_deshabilitada_sin_cambiar_su_estado()
    {
        var porRegistrar = DateTime.UtcNow.AddMinutes(-10);
        var (conexion, conexiones) = Deshabilitada(porRegistrar);
        conexion.MarcaHuecoPorRegistrarDesdeUtc.Should().Be(porRegistrar, "control");
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, unitOfWork);

        var resultado = await handler.Handle(new RepararMarcaHuecoCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexion.HuecoPendienteDesdeUtc.Should().Be(porRegistrar);
        conexion.MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_nada_por_registrar_devuelve_exito_sin_guardar()
    {
        var (conexion, conexiones) = Deshabilitada(porRegistrarDesdeUtc: null);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, unitOfWork);

        var resultado = await handler.Handle(new RepararMarcaHuecoCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    [Fact]
    public async Task Si_la_reparacion_no_llega_a_la_base_devuelve_un_fallo_visible_y_lo_registra_como_critico()
    {
        var porRegistrar = DateTime.UtcNow.AddMinutes(-10);
        var (conexion, conexiones) = Deshabilitada(porRegistrar);
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("base caída") };
        var logger = new LoggerCaptura();
        // Tras cada descarte se lee la versión de la base, sin lo que el intento dejó en memoria.
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = conexiones.SustituirEnBase(conexion.Id, c => c.Deshabilitar(porRegistrar)));
        var handler = CrearHandler(conexiones, unitOfWork, logger: logger, descarte: descarte);

        var resultado = await handler.Handle(new RepararMarcaHuecoCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada);
        descarte.Veces.Should().Be(ReparacionMarcaHuecoService.IntentosGuardado, "cada intento parte de la base");
        conexiones.Conexiones[0].Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexiones.Conexiones[0].MarcaHuecoPorRegistrarDesdeUtc.Should().Be(porRegistrar, "sigue guardada para la próxima reparación");
        logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Mensaje.Should().Contain(conexion.Id.ToString());
    }

    [Fact]
    public async Task Con_el_cerrojo_ocupado_pide_volver_a_intentarlo_sin_guardar()
    {
        var (conexion, conexiones) = Deshabilitada(DateTime.UtcNow.AddMinutes(-10));
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.Ocupadas.Add(conexion.Id);
        var handler = CrearHandler(conexiones, unitOfWork, cerrojo);

        var resultado = await handler.Handle(new RepararMarcaHuecoCommand(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoVerificacionEnCurso);
        unitOfWork.VecesGuardado.Should().Be(0);
        conexion.MarcaHuecoPorRegistrarDesdeUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Una_conexion_no_visible_no_se_repara()
    {
        var (conexion, conexiones) = Deshabilitada(DateTime.UtcNow.AddMinutes(-10));
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        var handler = CrearHandler(conexiones, unitOfWork, cerrojo,
            new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [conexion.Id]));

        var resultado = await handler.Handle(new RepararMarcaHuecoCommand(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        cerrojo.Pedidos.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
