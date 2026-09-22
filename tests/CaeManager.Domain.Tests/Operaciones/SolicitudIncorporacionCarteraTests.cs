using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

/// <summary>
/// Invariantes de la solicitud de incorporación a cartera (contrato de Mi
/// trabajo Gen2 multi-Tenant § 13): se pide un Tenant propietario entero que el
/// Operador CAE ya opera, nadie resuelve la suya, solo se resuelve lo pendiente
/// y solo se revoca lo aceptado.
/// </summary>
public class SolicitudIncorporacionCarteraTests
{
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Gestor = Guid.NewGuid();
    private static readonly Guid Coordinador = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static AsignacionOperacion OperacionExternaUniversal() =>
        AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora);

    private static SolicitudIncorporacionCartera Pendiente(AsignacionOperacion? operacion = null) =>
        SolicitudIncorporacionCartera.Crear(operacion ?? OperacionExternaUniversal(), Gestor, "Llevo sus centros", Ahora);

    private static AsignacionCartera CarteraUniversal(AsignacionOperacion operacion, Guid usuario) =>
        AsignacionCartera.Externa(operacion, usuario, "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);

    [Fact]
    public void Nace_pendiente_con_los_dos_Tenants_de_la_operacion()
    {
        var operacion = OperacionExternaUniversal();

        var solicitud = SolicitudIncorporacionCartera.Crear(operacion, Gestor, "  Llevo sus centros  ", Ahora);

        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
        solicitud.OperadorTenantId.Should().Be(Operador);
        solicitud.PropietarioTenantId.Should().Be(Propietario);
        solicitud.AsignacionOperacionId.Should().Be(operacion.Id);
        solicitud.SolicitanteUsuarioId.Should().Be(Gestor);
        solicitud.Mensaje.Should().Be("Llevo sus centros");
        solicitud.CreadaEnUtc.Should().Be(Ahora);
        solicitud.ResueltaPorUsuarioId.Should().BeNull();
        solicitud.AsignacionCarteraId.Should().BeNull();
    }

    [Fact]
    public void No_se_pide_la_cartera_de_una_operacion_interna()
    {
        // Una operación interna es el Tenant propietario operándose a sí mismo:
        // no hay Operador CAE externo en cuyo seno pedir nada.
        var interna = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora);

        var crear = () => SolicitudIncorporacionCartera.Crear(interna, Gestor, "Hola", Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_pide_la_cartera_de_una_operacion_acotada_a_un_cliente()
    {
        // Se pide el Tenant propietario entero; una operación parcial daría una
        // cartera universal más ancha que lo que el Operador CAE opera.
        var acotada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()),
            Ahora.AddDays(-30), null, Ahora);

        var crear = () => SolicitudIncorporacionCartera.Crear(acotada, Gestor, "Hola", Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_pide_la_cartera_de_una_operacion_que_aun_no_es_vigente()
    {
        var programada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(7), null, Ahora);

        var crear = () => SolicitudIncorporacionCartera.Crear(programada, Gestor, "Hola", Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_pide_la_cartera_de_una_operacion_cerrada()
    {
        var cerrada = OperacionExternaUniversal();
        cerrada.Cerrar(MotivoCierreAsignacion.Revocada, Ahora);

        var crear = () => SolicitudIncorporacionCartera.Crear(cerrada, Gestor, "Hola", Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void El_mensaje_es_obligatorio(string? mensaje)
    {
        var crear = () => SolicitudIncorporacionCartera.Crear(OperacionExternaUniversal(), Gestor, mensaje!, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_mensaje_admite_hasta_mil_caracteres_y_ni_uno_mas()
    {
        var justo = new string('a', SolicitudIncorporacionCartera.LongitudMaximaMensaje);
        var largo = justo + "a";

        SolicitudIncorporacionCartera.Crear(OperacionExternaUniversal(), Gestor, justo, Ahora)
            .Mensaje.Should().HaveLength(1000);
        var crear = () => SolicitudIncorporacionCartera.Crear(OperacionExternaUniversal(), Gestor, largo, Ahora);
        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void La_solicitud_exige_solicitante()
    {
        var crear = () => SolicitudIncorporacionCartera.Crear(OperacionExternaUniversal(), Guid.Empty, "Hola", Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Aceptar_enlaza_la_cartera_y_registra_quien_resolvio()
    {
        var operacion = OperacionExternaUniversal();
        var solicitud = Pendiente(operacion);
        var cartera = CarteraUniversal(operacion, Gestor);
        var filaHeredada = Guid.NewGuid();

        solicitud.Aceptar(Coordinador, cartera, filaHeredada, Ahora.AddHours(1));

        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
        solicitud.ResueltaPorUsuarioId.Should().Be(Coordinador);
        solicitud.ResueltaEnUtc.Should().Be(Ahora.AddHours(1));
        solicitud.AsignacionCarteraId.Should().Be(cartera.Id);
        solicitud.AsignacionOperadorDelegadoId.Should().Be(filaHeredada);
    }

    [Fact]
    public void Nadie_acepta_su_propia_solicitud()
    {
        var operacion = OperacionExternaUniversal();
        var solicitud = Pendiente(operacion);

        var aceptar = () => solicitud.Aceptar(Gestor, CarteraUniversal(operacion, Gestor), null, Ahora);

        aceptar.Should().Throw<InvalidOperationException>();
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
    }

    [Fact]
    public void Nadie_rechaza_su_propia_solicitud()
    {
        var solicitud = Pendiente();

        var rechazar = () => solicitud.Rechazar(Gestor, Ahora);

        rechazar.Should().Throw<InvalidOperationException>();
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
    }

    [Fact]
    public void Aceptar_rechaza_una_cartera_de_otro_usuario()
    {
        var operacion = OperacionExternaUniversal();
        var solicitud = Pendiente(operacion);

        var aceptar = () => solicitud.Aceptar(Coordinador, CarteraUniversal(operacion, Guid.NewGuid()), null, Ahora);

        aceptar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Aceptar_rechaza_una_cartera_de_otra_operacion()
    {
        var solicitud = Pendiente();
        var otra = OperacionExternaUniversal();

        var aceptar = () => solicitud.Aceptar(Coordinador, CarteraUniversal(otra, Gestor), null, Ahora);

        aceptar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Aceptar_rechaza_una_cartera_acotada()
    {
        var operacion = OperacionExternaUniversal();
        var solicitud = Pendiente(operacion);
        var acotada = AsignacionCartera.Externa(
            operacion, Gestor, "GestorCae", AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora, null, Ahora);

        var aceptar = () => solicitud.Aceptar(Coordinador, acotada, null, Ahora);

        aceptar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_solicitud_resuelta_no_se_vuelve_a_resolver()
    {
        // Dos Coordinadores CAE ven la misma solicitud: el segundo que llega no
        // puede deshacer ni repetir lo que hizo el primero.
        var operacion = OperacionExternaUniversal();
        var aceptada = Pendiente(operacion);
        aceptada.Aceptar(Coordinador, CarteraUniversal(operacion, Gestor), null, Ahora);
        var rechazada = Pendiente();
        rechazada.Rechazar(Coordinador, Ahora);

        var otroCoordinador = Guid.NewGuid();
        ((Action)(() => aceptada.Rechazar(otroCoordinador, Ahora))).Should().Throw<InvalidOperationException>();
        ((Action)(() => aceptada.Aceptar(otroCoordinador, CarteraUniversal(operacion, Gestor), null, Ahora)))
            .Should().Throw<InvalidOperationException>();
        ((Action)(() => rechazada.Rechazar(otroCoordinador, Ahora))).Should().Throw<InvalidOperationException>();
        ((Action)(() => rechazada.Anular(MotivoAnulacionSolicitudCartera.YaEnCartera, Ahora)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Anular_cierra_sin_resolutor_y_con_motivo()
    {
        var solicitud = Pendiente();

        solicitud.Anular(MotivoAnulacionSolicitudCartera.OperacionNoVigente, Ahora);

        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Anulada);
        solicitud.MotivoAnulacion.Should().Be(MotivoAnulacionSolicitudCartera.OperacionNoVigente);
        solicitud.ResueltaPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Solo_se_revoca_una_solicitud_aceptada()
    {
        var pendiente = Pendiente();
        var rechazada = Pendiente();
        rechazada.Rechazar(Coordinador, Ahora);

        ((Action)(() => pendiente.Revocar(Coordinador, Ahora))).Should().Throw<InvalidOperationException>();
        ((Action)(() => rechazada.Revocar(Coordinador, Ahora))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void El_solicitante_puede_revocar_la_suya_y_queda_registrado()
    {
        // Revocar no es resolver: el Gestor CAE puede renunciar a la cartera
        // que obtuvo. Quién más puede revocar lo decide el Command.
        var operacion = OperacionExternaUniversal();
        var solicitud = Pendiente(operacion);
        solicitud.Aceptar(Coordinador, CarteraUniversal(operacion, Gestor), null, Ahora);

        solicitud.Revocar(Gestor, Ahora.AddDays(3));

        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Revocada);
        solicitud.RevocadaPorUsuarioId.Should().Be(Gestor);
        solicitud.RevocadaEnUtc.Should().Be(Ahora.AddDays(3));
        ((Action)(() => solicitud.Revocar(Coordinador, Ahora))).Should().Throw<InvalidOperationException>();
    }
}
