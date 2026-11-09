using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class RenovacionSuscripcionesWebhookServiceTests
{
    private const string Directorio = "11111111-2222-3333-4444-555555555555";
    private static readonly TimeSpan Ventana = TimeSpan.FromHours(48);
    private static readonly Error FalloDefinitivo = Error.Crear("Integraciones.Microsoft365.ErrorApi", "La suscripción ya no existe.");

    /// <summary>
    /// Base de datos simulada: el descarte suelta lo pendiente y, si se le dice, sustituye una conexión por la versión
    /// que dejó en la base el otro proceso (la que provocó la colisión). Sin esto, recargar devolvería la misma instancia
    /// ya mutada y el test no distinguiría entre reaplicar y no haber perdido nada.
    /// </summary>
    private sealed class Escenario
    {
        public ConexionIntegracionRepositorioFalso Conexiones { get; } = new();
        public SuscripcionWebhookRepositorioFalso Suscripciones { get; } = new();
        public Microsoft365GraphClientFalso Graph { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public DescarteQueRecargaDeLaBase Descarte { get; }

        /// <summary>Colisiones de concurrencia que quedan por lanzar, una por guardado.</summary>
        public int ColisionesPendientes { get; set; }

        public Escenario() => Descarte = new DescarteQueRecargaDeLaBase(this);

        public (ConexionIntegracion Conexion, SuscripcionWebhook Suscripcion) Agregar(string direccion, string graphId, DateTime expiracionUtc)
        {
            var conexion = Compartida(direccion);
            Conexiones.Agregar(conexion);
            var suscripcion = new SuscripcionWebhook(conexion.Id, graphId, "estado-cliente", expiracionUtc);
            Suscripciones.Agregar(suscripcion);
            return (conexion, suscripcion);
        }

        public RenovacionSuscripcionesWebhookService Servicio() => new(
            Suscripciones, Conexiones,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), Graph), Graph,
            new UnitOfWorkConColisiones(this), Descarte,
            NullLogger<RenovacionSuscripcionesWebhookService>.Instance);

        /// <summary>Lo que otro proceso escribió en la base y se carga al recargar tras la colisión.</summary>
        public Func<ConexionIntegracion, ConexionIntegracion>? VersionEnLaBase { get; set; }

        /// <summary>Otra escritura del otro proceso que se ve al recargar (p. ej. Reactivar sustituyó la suscripción).</summary>
        public Action? AlDescartar { get; set; }
    }

    private sealed class UnitOfWorkConColisiones(Escenario escenario) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (escenario.ColisionesPendientes > 0)
            {
                escenario.ColisionesPendientes--;
                throw new DbUpdateConcurrencyException("Otro proceso cambió la versión de la conexión.");
            }

            return escenario.UnitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class DescarteQueRecargaDeLaBase(Escenario escenario) : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes()
        {
            Veces++;
            escenario.AlDescartar?.Invoke();
            if (escenario.VersionEnLaBase is not { } version)
                return;

            for (var i = 0; i < escenario.Conexiones.Conexiones.Count; i++)
                escenario.Conexiones.Conexiones[i] = version(escenario.Conexiones.Conexiones[i]);
        }
    }

    private static ConexionIntegracion Compartida(string direccion, Guid? id = null)
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(direccion, "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-" + direccion);
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        if (id is { } fijado)
            typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(conexion, fijado);
        return conexion;
    }

    /// <summary>
    /// La colisión de una conexión no se lleva por delante a las demás del Tenant (cada una se guarda por separado), y
    /// su propia observación no se pierde: se recarga la versión del otro proceso y se vuelve a aplicar, sin volver a
    /// llamar a Graph.
    /// </summary>
    [Fact]
    public async Task Una_colision_reaplica_el_ConError_y_la_marca_sobre_la_conexion_recargada_sin_tocar_las_demas()
    {
        var escenario = new Escenario();
        var expiracion = DateTime.UtcNow.AddHours(-2);
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", expiracion);
        var (sana, suscripcionSana) = escenario.Agregar("sana@operador.test", "sub-sana", DateTime.UtcNow.AddHours(10));
        escenario.Graph.ErroresRenovacion["sub-rota"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;

        // El otro proceso (por ejemplo, la ingesta) guardó otra cosa en la conexión rota mientras se hablaba con Graph.
        escenario.VersionEnLaBase = actual => actual.Id == rota.Id ? Compartida("rota@operador.test", rota.Id) : actual;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == rota.Id);
        recargada.Should().NotBeSameAs(rota, "control: el descarte cargó la versión de la base");
        recargada.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        recargada.UltimoError.Should().Be(FalloDefinitivo.Mensaje);
        recargada.HuecoPendienteDesdeUtc.Should().Be(expiracion, "la suscripción se perdió en su expiración (disparador b)");

        sana.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        suscripcionSana.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-rota", "sub-sana"], "reaplicar no vuelve a llamar a Graph");
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "la rota tras la colisión y la sana, cada una por su lado");
        escenario.Descarte.Veces.Should().Be(1);
    }

    /// <summary>Sin colisiones, cada conexión se guarda por separado: un guardado por conexión.</summary>
    [Fact]
    public async Task Sin_colisiones_guarda_una_vez_por_conexion()
    {
        var escenario = new Escenario();
        escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(10));
        escenario.Agregar("b@operador.test", "sub-b", DateTime.UtcNow.AddHours(10));

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.UnitOfWork.VecesGuardado.Should().Be(2);
        escenario.Descarte.Veces.Should().Be(0);
    }

    /// <summary>
    /// Agotados los intentos, la conexión queda para el ciclo siguiente (registrado), pero las demás del Tenant se
    /// renuevan igual: ya no se revierte el lote entero.
    /// </summary>
    [Fact]
    public async Task Colisiones_seguidas_agotan_los_intentos_de_una_conexion_y_no_impiden_renovar_las_demas()
    {
        var escenario = new Escenario();
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", DateTime.UtcNow.AddHours(-2));
        var (_, suscripcionSana) = escenario.Agregar("sana@operador.test", "sub-sana", DateTime.UtcNow.AddHours(10));
        escenario.Graph.ErroresRenovacion["sub-rota"] = FalloDefinitivo;
        escenario.ColisionesPendientes = RenovacionSuscripcionesWebhookService.IntentosGuardado;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.Descarte.Veces.Should().Be(RenovacionSuscripcionesWebhookService.IntentosGuardado,
            "una por cada reintento y otra al rendirse, para no arrastrar la mutación a la siguiente conexión");
        suscripcionSana.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.UnitOfWork.VecesGuardado.Should().Be(1, "solo la sana");
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-rota", "sub-sana"]);
        rota.Estado.Should().Be(EstadoConexionIntegracion.ConError, "en memoria se aplicó; en la base no llegó a guardarse");
    }

    /// <summary>Si al recargar la conexión resulta Deshabilitada (Desconectar entretanto), no se marca ni se mueve.</summary>
    [Fact]
    public async Task Si_la_conexion_recargada_esta_Deshabilitada_no_se_reaplica_nada()
    {
        var escenario = new Escenario();
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", DateTime.UtcNow.AddHours(-2));
        escenario.Graph.ErroresRenovacion["sub-rota"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;
        escenario.VersionEnLaBase = actual =>
        {
            var desconectada = Compartida("rota@operador.test", actual.Id);
            desconectada.Deshabilitar();
            return desconectada;
        };

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == rota.Id);
        recargada.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        recargada.UltimoError.Should().BeNull();
        recargada.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Ronda 11 (C3): Graph rechazó de forma definitiva la suscripción antigua, pero antes de guardarlo Reactivar creó
    /// otra. Al reaplicar tras la colisión, la observación es de una suscripción que ya no es la de la conexión: no la
    /// pasa a ConError ni marca una pérdida que no corresponde a la suscripción vigente.
    /// </summary>
    [Fact]
    public async Task Un_fallo_reaplicado_sobre_una_suscripcion_sustituida_se_descarta_sin_mutar_nada()
    {
        var escenario = new Escenario();
        var (conexion, suscripcion) = escenario.Agregar("a@operador.test", "sub-vieja", DateTime.UtcNow.AddHours(-2));
        escenario.Graph.ErroresRenovacion["sub-vieja"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;
        var expiracionNueva = DateTime.UtcNow.AddDays(3);
        escenario.VersionEnLaBase = actual => Compartida("a@operador.test", actual.Id);
        escenario.AlDescartar = () => suscripcion.ActualizarTrasRenovacion("sub-nueva", expiracionNueva);

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == conexion.Id);
        recargada.Should().NotBeSameAs(conexion, "control: el descarte cargó la versión de la base");
        recargada.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        recargada.UltimoError.Should().BeNull();
        recargada.HuecoPendienteDesdeUtc.Should().BeNull();
        suscripcion.GraphSubscriptionId.Should().Be("sub-nueva");
        suscripcion.FechaExpiracionUtc.Should().Be(expiracionNueva);
    }

    /// <summary>Una renovación con éxito que colisiona se reaplica a la suscripción recargada, sin volver a Graph.</summary>
    [Fact]
    public async Task Una_renovacion_que_colisiona_se_reaplica_a_la_suscripcion()
    {
        var escenario = new Escenario();
        var (_, suscripcion) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(10));
        escenario.ColisionesPendientes = 1;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        suscripcion.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-a"]);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_conexion_Deshabilitada_no_se_renueva_ni_se_marca()
    {
        var escenario = new Escenario();
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(-2));
        conexion.Deshabilitar();

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.Graph.SuscripcionesRenovadas.Should().BeEmpty();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }
}
