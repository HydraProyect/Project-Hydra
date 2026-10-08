using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

/// <summary>
/// Invariantes de la propuesta de apoyo (ADR-011 § 2.7, enmienda 2026-10-08): se propone el
/// Tenant propietario entero de una operación externa vigente; acepta o rechaza el
/// destinatario y nadie más; quien propone solo retira; solo cambia lo pendiente; y la
/// cartera que la aceptación enlaza nunca lleva la marca de principal.
/// </summary>
public class PropuestaApoyoCarteraTests
{
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Principal = Guid.NewGuid();
    private static readonly Guid Destinatario = Guid.NewGuid();
    private static readonly Guid Tercero = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static AsignacionOperacion OperacionExternaUniversal() =>
        AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora);

    private static PropuestaApoyoCartera Pendiente(AsignacionOperacion? operacion = null) =>
        PropuestaApoyoCartera.Crear(operacion ?? OperacionExternaUniversal(), Principal, Destinatario, null, Ahora);

    private static AsignacionCartera CarteraUniversal(AsignacionOperacion operacion, Guid usuario) =>
        AsignacionCartera.Externa(operacion, usuario, "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);

    [Fact]
    public void Nace_pendiente_con_los_dos_Tenants_de_la_operacion_y_sin_conceder_nada()
    {
        var operacion = OperacionExternaUniversal();

        var propuesta = PropuestaApoyoCartera.Crear(operacion, Principal, Destinatario, null, Ahora);

        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        propuesta.OperadorTenantId.Should().Be(Operador);
        propuesta.PropietarioTenantId.Should().Be(Propietario);
        propuesta.AsignacionOperacionId.Should().Be(operacion.Id);
        propuesta.ProponenteUsuarioId.Should().Be(Principal);
        propuesta.DestinatarioUsuarioId.Should().Be(Destinatario);
        propuesta.CreadaEnUtc.Should().Be(Ahora);
        propuesta.VigenciaHastaPropuesta.Should().BeNull();
        propuesta.AsignacionCarteraId.Should().BeNull();
        propuesta.ResueltaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Guarda_la_fecha_de_fin_propuesta_si_es_futura()
    {
        var hasta = Ahora.AddDays(15);

        var propuesta = PropuestaApoyoCartera.Crear(OperacionExternaUniversal(), Principal, Destinatario, hasta, Ahora);

        propuesta.VigenciaHastaPropuesta.Should().Be(hasta);
    }

    [Fact]
    public void No_admite_una_fecha_de_fin_que_ya_paso()
    {
        var crear = () => PropuestaApoyoCartera.Crear(
            OperacionExternaUniversal(), Principal, Destinatario, Ahora.AddMinutes(-1), Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Nadie_se_propone_un_apoyo_a_si_mismo()
    {
        var crear = () => PropuestaApoyoCartera.Crear(OperacionExternaUniversal(), Principal, Principal, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exige_proponente_y_destinatario()
    {
        var sinProponente = () => PropuestaApoyoCartera.Crear(OperacionExternaUniversal(), Guid.Empty, Destinatario, null, Ahora);
        var sinDestinatario = () => PropuestaApoyoCartera.Crear(OperacionExternaUniversal(), Principal, Guid.Empty, null, Ahora);

        sinProponente.Should().Throw<ArgumentException>();
        sinDestinatario.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_propone_sobre_una_operacion_interna()
    {
        // Una operación interna es el Tenant propietario operándose a sí mismo: no hay
        // Operador CAE externo en cuyo seno proponer nada.
        var interna = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora);

        var crear = () => PropuestaApoyoCartera.Crear(interna, Principal, Destinatario, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_propone_sobre_una_operacion_acotada()
    {
        // El apoyo es una cartera del Tenant entero; sobre una operación parcial sería más
        // ancha que lo que el Operador CAE opera.
        var acotada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()),
            Ahora.AddDays(-30), null, Ahora);

        var crear = () => PropuestaApoyoCartera.Crear(acotada, Principal, Destinatario, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_propone_sobre_una_operacion_que_aun_no_es_vigente()
    {
        var programada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(7), null, Ahora);

        var crear = () => PropuestaApoyoCartera.Crear(programada, Principal, Destinatario, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_destinatario_la_acepta_y_queda_enlazada_la_cartera_de_apoyo()
    {
        var operacion = OperacionExternaUniversal();
        var propuesta = Pendiente(operacion);
        var cartera = CarteraUniversal(operacion, Destinatario);
        var filaHeredada = Guid.NewGuid();

        propuesta.Aceptar(Destinatario, cartera, filaHeredada, Ahora.AddHours(1));

        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Aceptada);
        propuesta.AsignacionCarteraId.Should().Be(cartera.Id);
        propuesta.AsignacionOperadorDelegadoId.Should().Be(filaHeredada);
        propuesta.ResueltaEnUtc.Should().Be(Ahora.AddHours(1));
    }

    [Fact]
    public void Quien_propuso_no_puede_aceptarla_por_el_destinatario()
    {
        // Al revés que en la solicitud de incorporación: aquí resuelve el beneficiario.
        var operacion = OperacionExternaUniversal();
        var propuesta = Pendiente(operacion);

        var aceptar = () => propuesta.Aceptar(Principal, CarteraUniversal(operacion, Destinatario), null, Ahora);

        aceptar.Should().Throw<InvalidOperationException>();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    [Fact]
    public void Un_tercero_no_puede_aceptarla_ni_rechazarla()
    {
        var operacion = OperacionExternaUniversal();
        var propuesta = Pendiente(operacion);

        var aceptar = () => propuesta.Aceptar(Tercero, CarteraUniversal(operacion, Destinatario), null, Ahora);
        var rechazar = () => propuesta.Rechazar(Tercero, Ahora);

        aceptar.Should().Throw<InvalidOperationException>();
        rechazar.Should().Throw<InvalidOperationException>();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    [Fact]
    public void La_cartera_enlazada_nunca_lleva_la_marca_de_principal()
    {
        var operacion = OperacionExternaUniversal();
        var propuesta = Pendiente(operacion);
        var cartera = CarteraUniversal(operacion, Destinatario);
        cartera.DesignarPrincipal();

        var aceptar = () => propuesta.Aceptar(Destinatario, cartera, null, Ahora);

        aceptar.Should().Throw<InvalidOperationException>().WithMessage("*nunca nace con la marca de principal*");
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        propuesta.AsignacionCarteraId.Should().BeNull();
    }

    [Fact]
    public void La_cartera_enlazada_tiene_que_ser_la_del_destinatario_bajo_la_misma_operacion()
    {
        var operacion = OperacionExternaUniversal();
        var propuesta = Pendiente(operacion);

        var deOtraPersona = () => propuesta.Aceptar(Destinatario, CarteraUniversal(operacion, Tercero), null, Ahora);
        var deOtraOperacion = () => propuesta.Aceptar(
            Destinatario, CarteraUniversal(OperacionExternaUniversal(), Destinatario), null, Ahora);

        deOtraPersona.Should().Throw<ArgumentException>();
        deOtraOperacion.Should().Throw<ArgumentException>();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    [Fact]
    public void El_destinatario_la_rechaza_sin_enlazar_nada()
    {
        var propuesta = Pendiente();

        propuesta.Rechazar(Destinatario, Ahora.AddHours(2));

        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Rechazada);
        propuesta.AsignacionCarteraId.Should().BeNull();
        propuesta.ResueltaEnUtc.Should().Be(Ahora.AddHours(2));
    }

    [Fact]
    public void Quien_propuso_no_puede_rechazarla()
    {
        var propuesta = Pendiente();

        var rechazar = () => propuesta.Rechazar(Principal, Ahora);

        rechazar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Quien_propuso_la_retira()
    {
        var propuesta = Pendiente();

        propuesta.Retirar(Principal, Ahora.AddHours(3));

        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Retirada);
        propuesta.ResueltaEnUtc.Should().Be(Ahora.AddHours(3));
    }

    [Fact]
    public void Ni_el_destinatario_ni_un_tercero_pueden_retirarla()
    {
        var propuesta = Pendiente();

        var elDestinatario = () => propuesta.Retirar(Destinatario, Ahora);
        var unTercero = () => propuesta.Retirar(Tercero, Ahora);

        elDestinatario.Should().Throw<InvalidOperationException>();
        unTercero.Should().Throw<InvalidOperationException>();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    [Fact]
    public void Se_anula_con_su_motivo_y_sin_resolutor()
    {
        var propuesta = Pendiente();

        propuesta.Anular(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal, Ahora.AddDays(1));

        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        propuesta.MotivoAnulacion.Should().Be(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal);
        propuesta.ResueltaEnUtc.Should().Be(Ahora.AddDays(1));
    }

    public static TheoryData<string> EstadosFinales => new() { "Aceptada", "Rechazada", "Retirada", "Anulada" };

    [Theory]
    [MemberData(nameof(EstadosFinales))]
    public void Un_estado_final_no_admite_ningun_cambio(string estadoFinal)
    {
        var operacion = OperacionExternaUniversal();
        var propuesta = Pendiente(operacion);
        switch (estadoFinal)
        {
            case "Aceptada": propuesta.Aceptar(Destinatario, CarteraUniversal(operacion, Destinatario), null, Ahora); break;
            case "Rechazada": propuesta.Rechazar(Destinatario, Ahora); break;
            case "Retirada": propuesta.Retirar(Principal, Ahora); break;
            default: propuesta.Anular(MotivoAnulacionPropuestaApoyo.OperacionNoVigente, Ahora); break;
        }

        var estado = propuesta.Estado;
        var aceptar = () => propuesta.Aceptar(Destinatario, CarteraUniversal(operacion, Destinatario), null, Ahora);
        var rechazar = () => propuesta.Rechazar(Destinatario, Ahora);
        var retirar = () => propuesta.Retirar(Principal, Ahora);
        var anular = () => propuesta.Anular(MotivoAnulacionPropuestaApoyo.YaEnCartera, Ahora);

        aceptar.Should().Throw<InvalidOperationException>();
        rechazar.Should().Throw<InvalidOperationException>();
        retirar.Should().Throw<InvalidOperationException>();
        anular.Should().Throw<InvalidOperationException>();
        propuesta.Estado.Should().Be(estado);
    }
}
