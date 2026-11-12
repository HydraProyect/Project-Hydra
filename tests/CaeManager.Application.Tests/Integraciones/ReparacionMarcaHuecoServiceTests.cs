using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Codex, ronda 18: el ciclo diario de suscripciones repara la marca del hueco que una desconexión dejó por registrar en
/// la conexión, con el cerrojo de cada conexión y aislando sus fallos.
/// </summary>
public class ReparacionMarcaHuecoServiceTests
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

    /// <summary>La versión de la base: Deshabilitada con la marca por registrar, en otra instancia con el mismo Id.</summary>
    private static ConexionIntegracion VersionDeLaBase(Guid id)
    {
        var copia = DeshabilitadaConMarcaPorRegistrar();
        typeof(CaeManager.Domain.Common.Entity).GetProperty(nameof(CaeManager.Domain.Common.Entity.Id))!.SetValue(copia, id);
        return copia;
    }

    private static readonly DateTime Comienzo = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    private static ConexionIntegracion DeshabilitadaConMarcaPorRegistrar()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.Deshabilitar(Comienzo);
        return conexion;
    }

    private static ReparacionMarcaHuecoService Crear(
        ConexionIntegracionRepositorioFalso conexiones, IUnitOfWork unitOfWork, CerrojoVerificacionBuzonFalso cerrojo,
        DescarteContado descarte) =>
        new(conexiones, unitOfWork, cerrojo, descarte, NullLogger<ReparacionMarcaHuecoService>.Instance);

    [Fact]
    public async Task El_ciclo_registra_la_marca_por_registrar_de_cada_conexion_con_su_cerrojo()
    {
        var pendiente = DeshabilitadaConMarcaPorRegistrar();
        var limpia = new ConexionIntegracion("otro@operador.test", "Otro");
        limpia.Deshabilitar();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(pendiente);
        conexiones.Agregar(limpia);
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        var unitOfWork = new UnitOfWorkFalso();

        await Crear(conexiones, unitOfWork, cerrojo, new DescarteContado()).RepararDelTenantActualAsync(CancellationToken.None);

        cerrojo.Pedidos.Should().Equal([pendiente.Id], "solo la que tiene algo por registrar, y bajo su cerrojo");
        pendiente.HuecoPendienteDesdeUtc.Should().Be(Comienzo);
        pendiente.MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull();
        limpia.HuecoPendienteDesdeUtc.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Con_el_cerrojo_ocupado_la_deja_para_el_ciclo_siguiente()
    {
        var pendiente = DeshabilitadaConMarcaPorRegistrar();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(pendiente);
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.Ocupadas.Add(pendiente.Id);
        var unitOfWork = new UnitOfWorkFalso();

        await Crear(conexiones, unitOfWork, cerrojo, new DescarteContado()).RepararDelTenantActualAsync(CancellationToken.None);

        unitOfWork.VecesGuardado.Should().Be(0);
        pendiente.MarcaHuecoPorRegistrarDesdeUtc.Should().Be(Comienzo, "sigue guardada para el ciclo siguiente");
    }

    [Fact]
    public async Task Un_fallo_de_una_conexion_no_impide_reparar_las_demas()
    {
        var rota = DeshabilitadaConMarcaPorRegistrar();
        var sana = DeshabilitadaConMarcaPorRegistrar();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(rota);
        conexiones.Agregar(sana);
        var cerrojo = new CerrojoVerificacionBuzonFalso { AlTomar = id => { if (id == rota.Id) throw new InvalidOperationException("base caída"); } };
        var unitOfWork = new UnitOfWorkFalso();
        var descarte = new DescarteContado();

        await Crear(conexiones, unitOfWork, cerrojo, descarte).RepararDelTenantActualAsync(CancellationToken.None);

        descarte.Veces.Should().Be(1, "lo rastreado de la rota no viaja con la siguiente");
        sana.HuecoPendienteDesdeUtc.Should().Be(Comienzo);
        sana.MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull();
        rota.MarcaHuecoPorRegistrarDesdeUtc.Should().Be(Comienzo);
    }

    [Fact]
    public async Task Si_ningun_intento_llega_a_la_base_devuelve_false_tras_descartar_cada_uno()
    {
        var pendiente = DeshabilitadaConMarcaPorRegistrar();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(pendiente);
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("base caída") };
        // Tras cada descarte se lee la versión de la base, sin lo que el intento dejó en memoria.
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = VersionDeLaBase(pendiente.Id));

        var reparada = await Crear(conexiones, unitOfWork, new CerrojoVerificacionBuzonFalso(), descarte).RepararBajoCerrojoAsync(pendiente.Id);

        reparada.Should().BeFalse();
        descarte.Veces.Should().Be(ReparacionMarcaHuecoService.IntentosGuardado);
        conexiones.Conexiones[0].MarcaHuecoPorRegistrarDesdeUtc.Should().Be(Comienzo, "sigue en la base");
    }

    [Fact]
    public async Task Sin_nada_por_registrar_no_guarda()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.Deshabilitar();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();

        var reparada = await Crear(conexiones, unitOfWork, new CerrojoVerificacionBuzonFalso(), new DescarteContado()).RepararBajoCerrojoAsync(conexion.Id);

        reparada.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
