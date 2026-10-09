using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

/// <summary>
/// Las invariantes del plano de operación (ADR-011 § 2.7): inmutabilidad de lo
/// que define la responsabilidad, la raíz como fallback y no como competidora,
/// la vigencia semiabierta que hace respondible la pregunta "¿quién era
/// responsable el día X?" y la marca de principal de una cartera.
/// </summary>
public class AsignacionResponsabilidadTests
{
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void La_raiz_es_interna_universal_y_sin_fecha_de_fin()
    {
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora);

        raiz.EsRaiz.Should().BeTrue();
        raiz.EsOperacionInterna.Should().BeTrue();
        raiz.Ambito.EsUniversal.Should().BeTrue();
        raiz.VigenciaHasta.Should().BeNull();
        raiz.Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    [Fact]
    public void Una_operacion_interna_universal_se_rechaza_porque_eso_es_la_raiz()
    {
        // Si se admitiera, un tenant podría tener dos "todo lo mío" internos
        // compitiendo, que es justo el conflicto permanente que la regla de la
        // raíz evita.
        var crear = () => AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_operacion_externa_exige_un_operador_distinto_del_propietario()
    {
        var crear = () => AsignacionOperacion.Externa(
            Propietario, Propietario, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_asignacion_que_empieza_en_el_futuro_nace_programada()
    {
        // Es la ventana de traspaso: el operador entrante ve lo que va a
        // heredar sin responder todavía del ámbito, y sin ocupar el índice
        // único que impediría convivir con quien aún responde.
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            Ahora.AddDays(7), null, Ahora);

        operacion.Estado.Should().Be(EstadoAsignacion.Programada);
        operacion.EstaVigenteEn(Ahora).Should().BeFalse();
    }

    [Fact]
    public void Activar_solo_vale_desde_programada()
    {
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        var activar = () => operacion.Activar();

        activar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cerrar_es_final_y_no_se_reabre()
    {
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        operacion.Cerrar(MotivoCierreAsignacion.Revocada, Ahora);

        operacion.Estado.Should().Be(EstadoAsignacion.Cerrada);
        operacion.MotivoCierre.Should().Be(MotivoCierreAsignacion.Revocada);
        // Ni reactivar ni volver a cerrar: para operar otra vez se abre otra
        // fila, y así el histórico conserva las dos etapas por separado.
        operacion.Invoking(o => o.Reactivar()).Should().Throw<InvalidOperationException>();
        operacion.Invoking(o => o.Cerrar(MotivoCierreAsignacion.Revocada, Ahora)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cerrar_adelanta_el_fin_de_vigencia_pero_nunca_lo_alarga()
    {
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            Ahora.AddDays(-10), Ahora.AddDays(10), Ahora);

        operacion.Cerrar(MotivoCierreAsignacion.Transferida, Ahora);

        // Si el cierre respetara el fin futuro, una consulta histórica sobre
        // mañana devolvería como responsable a alguien que ya no lo era.
        operacion.VigenciaHasta.Should().Be(Ahora);
        operacion.EstaVigenteEn(Ahora.AddDays(5)).Should().BeFalse();
    }

    [Fact]
    public void La_vigencia_es_semiabierta_incluye_el_inicio_y_excluye_el_fin()
    {
        var desde = Ahora;
        var hasta = Ahora.AddDays(1);
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, hasta, Ahora);

        operacion.EstaVigenteEn(desde).Should().BeTrue();
        operacion.EstaVigenteEn(hasta.AddTicks(-1)).Should().BeTrue();
        operacion.EstaVigenteEn(hasta).Should().BeFalse();
    }

    [Fact]
    public void Una_vigente_con_fecha_de_fin_pasada_se_reconoce_como_expirada()
    {
        // Lo que busca el job de expiración: sin cerrarla seguiría ocupando el
        // índice único de responsabilidad y bloquearía a su sustituta.
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            Ahora.AddDays(-10), Ahora.AddDays(-1), Ahora.AddDays(-10));

        operacion.HaExpiradoEn(Ahora).Should().BeTrue();
        operacion.Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    [Fact]
    public void Una_vigencia_que_termina_antes_de_empezar_se_rechaza()
    {
        var crear = () => AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            Ahora, Ahora.AddDays(-1), Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_cartera_hereda_propietario_y_operador_de_su_operacion()
    {
        // No se copian del llamante: la FK compuesta contra la clave alternativa
        // de la operación los ata en la base de datos, así que tomarlos de otro
        // sitio sería un error que la BD rechazaría.
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        var cartera = AsignacionCartera.Externa(
            operacion, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);

        cartera.PropietarioTenantId.Should().Be(Propietario);
        cartera.OperadorTenantId.Should().Be(Operador);
        cartera.AsignacionOperacionId.Should().Be(operacion.Id);
    }

    [Fact]
    public void Una_cartera_externa_exige_rol_explicito()
    {
        // Sin rol propio, el usuario se llevaría al workspace ajeno el rol que
        // tiene en su propio tenant — el fallo que el rol efectivo corrigió.
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        var crear = () => AsignacionCartera.Externa(
            operacion, Guid.NewGuid(), "  ", AmbitoAsignacion.Universal, Ahora, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_cartera_interna_no_puede_colgar_de_una_operacion_externa_ni_al_reves()
    {
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora, Ahora);
        var externa = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);

        var internaSobreExterna = () => AsignacionCartera.Interna(
            externa, Guid.NewGuid(), AmbitoAsignacion.Universal, Ahora, null, Ahora);
        var externaSobreInterna = () => AsignacionCartera.Externa(
            raiz, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);

        internaSobreExterna.Should().Throw<ArgumentException>();
        externaSobreInterna.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_puede_colgar_una_cartera_de_una_operacion_cerrada()
    {
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora, null, Ahora);
        operacion.Cerrar(MotivoCierreAsignacion.Revocada, Ahora);

        var crear = () => AsignacionCartera.Externa(
            operacion, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    // ---------- D-7: la cartera de un Gestor CAE es siempre el Tenant entero ----------

    [Fact]
    public void Una_cartera_externa_no_se_reparte_por_Cliente_empresarial()
    {
        var operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

        var crear = () => AsignacionCartera.Externa(
            operacion, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()),
            Ahora.AddDays(-1), null, Ahora);

        crear.Should().Throw<ArgumentException>().WithParameterName("ambito");
    }

    [Fact]
    public void Una_cartera_interna_no_se_reparte_por_Cliente_empresarial()
    {
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddDays(-1), Ahora);

        var crear = () => AsignacionCartera.Interna(
            raiz, Guid.NewGuid(), AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora.AddDays(-1), null, Ahora);

        crear.Should().Throw<ArgumentException>().WithParameterName("ambito");
    }

    [Fact]
    public void Lo_acotado_a_un_Cliente_empresarial_vive_en_la_operacion_y_la_cartera_es_universal_bajo_ella()
    {
        // Control positivo: el rechazo es de la CARTERA, no del ámbito en general. La operación acotada sigue
        // siendo representable y una cartera universal cuelga de ella.
        var clienteId = Guid.NewGuid();
        var acotada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteId),
            Ahora.AddDays(-1), null, Ahora);

        var cartera = AsignacionCartera.Externa(
            acotada, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

        acotada.AmbitoRelacionClienteId.Should().Be(clienteId);
        cartera.AmbitoRelacionClienteId.Should().BeNull();
        cartera.Ambito.EsUniversal.Should().BeTrue();
    }

    [Fact]
    public void El_ambito_distingue_lo_universal_de_lo_acotado_y_marca_las_dimensiones_diferidas()
    {
        var clienteId = Guid.NewGuid();

        AmbitoAsignacion.Universal.EsUniversal.Should().BeTrue();
        AmbitoAsignacion.Universal.UsaDimensionesDiferidas.Should().BeFalse();

        var deCliente = AmbitoAsignacion.DeRelacionCliente(clienteId);
        deCliente.EsUniversal.Should().BeFalse();
        deCliente.RelacionClienteId.Should().Be(clienteId);
        deCliente.UsaDimensionesDiferidas.Should().BeFalse();

        // Las tres dimensiones que F1 no habilita quedan marcadas para que el
        // alta pueda rechazarlas: existen como columnas, no como capacidad.
        new AmbitoAsignacion(CentroId: Guid.NewGuid()).UsaDimensionesDiferidas.Should().BeTrue();
        new AmbitoAsignacion(TrabajadorId: Guid.NewGuid()).UsaDimensionesDiferidas.Should().BeTrue();
        new AmbitoAsignacion(ProyectoId: Guid.NewGuid()).UsaDimensionesDiferidas.Should().BeTrue();
    }

    // ── Marca de principal (ADR-011 § 2.7, enmienda 2026-10-08) ───────────

    private static AsignacionOperacion OperacionExterna() => AsignacionOperacion.Externa(
        Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

    private static AsignacionCartera CarteraExterna(string rol) => AsignacionCartera.Externa(
        OperacionExterna(), Guid.NewGuid(), rol, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

    [Fact]
    public void Una_cartera_nace_sin_la_marca_de_principal()
    {
        CarteraExterna("GestorCae").EsPrincipal.Should().BeFalse();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    public void Una_cartera_del_Tenant_entero_de_Gestor_CAE_o_Coordinador_CAE_puede_ser_la_principal(string rol)
    {
        var cartera = CarteraExterna(rol);

        cartera.DesignarPrincipal();
        cartera.DesignarPrincipal();

        cartera.EsPrincipal.Should().BeTrue();
        cartera.Rol.Should().Be(rol, "la marca no cambia el rol");
        cartera.Ambito.EsUniversal.Should().BeTrue("ni el ámbito");
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("gestorcae")]
    public void Una_cartera_de_otro_rol_nunca_es_la_principal(string rol)
    {
        var cartera = CarteraExterna(rol);

        cartera.Invoking(c => c.DesignarPrincipal()).Should().Throw<InvalidOperationException>();
        cartera.EsPrincipal.Should().BeFalse();
    }

    [Fact]
    public void Una_cartera_interna_sin_rol_propio_puede_ser_la_principal()
    {
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora);
        var cartera = AsignacionCartera.Interna(
            raiz, Guid.NewGuid(), AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

        cartera.DesignarPrincipal();

        cartera.EsPrincipal.Should().BeTrue("en una operación interna vale el rol de Identity");
    }

    [Fact]
    public void Una_cartera_interna_con_rol_propio_que_no_es_de_gestion_no_es_la_principal()
    {
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora);
        var cartera = AsignacionCartera.Interna(
            raiz, Guid.NewGuid(), AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora, rol: "Consulta");

        cartera.Invoking(c => c.DesignarPrincipal()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_cartera_que_no_es_del_Tenant_entero_no_es_la_principal()
    {
        var cartera = AsignacionCartera.Externa(
            OperacionExterna(), Guid.NewGuid(), "GestorCae",
            new AmbitoAsignacion(null, Guid.NewGuid(), null, null), Ahora.AddDays(-1), null, Ahora);

        cartera.Invoking(c => c.DesignarPrincipal()).Should().Throw<InvalidOperationException>();
        cartera.EsPrincipal.Should().BeFalse();
    }

    [Fact]
    public void Una_cartera_cerrada_no_puede_ser_la_principal()
    {
        var cartera = CarteraExterna("GestorCae");
        cartera.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, Ahora);

        cartera.Invoking(c => c.DesignarPrincipal()).Should().Throw<InvalidOperationException>();
        cartera.EsPrincipal.Should().BeFalse();
    }

    [Fact]
    public void Cerrar_la_cartera_principal_apaga_la_marca()
    {
        var cartera = CarteraExterna("GestorCae");
        cartera.DesignarPrincipal();

        cartera.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, Ahora);

        cartera.Estado.Should().Be(EstadoAsignacion.Cerrada);
        cartera.EsPrincipal.Should().BeFalse("una cartera cerrada no es la principal de nada");
    }

    // ── D-9: quién era el principal cuando la cascada cerró la operación ──

    [Fact]
    public void El_cierre_por_cascada_recuerda_que_era_la_principal_y_la_cartera_cerrada_sigue_sin_serlo()
    {
        var cartera = CarteraExterna("GestorCae");
        cartera.DesignarPrincipal();

        cartera.CerrarPorCascadaDeLaOperacion(MotivoCierreAsignacion.Revocada, Ahora);

        cartera.EraPrincipalAlCerrarsePorCascada.Should().BeTrue("es lo que la reactivación lee para devolverle la marca");
        cartera.EsPrincipal.Should().BeFalse("el dato es histórico: una cartera cerrada no cuenta como principal vivo");
        cartera.Estado.Should().Be(EstadoAsignacion.Cerrada);
        cartera.MotivoCierre.Should().Be(MotivoCierreAsignacion.Revocada);
    }

    [Fact]
    public void El_cierre_por_cascada_de_una_cartera_de_apoyo_no_la_recuerda_como_principal()
    {
        var cartera = CarteraExterna("GestorCae");

        cartera.CerrarPorCascadaDeLaOperacion(MotivoCierreAsignacion.Revocada, Ahora);

        cartera.EraPrincipalAlCerrarsePorCascada.Should().BeFalse();
    }

    [Theory]
    [InlineData(MotivoCierreAsignacion.RetiradaPorElOperador)]
    [InlineData(MotivoCierreAsignacion.Revocada)]
    [InlineData(MotivoCierreAsignacion.Expirada)]
    public void La_retirada_individual_y_la_expiracion_de_la_principal_no_dejan_ese_dato(MotivoCierreAsignacion motivo)
    {
        var cartera = CarteraExterna("GestorCae");
        cartera.DesignarPrincipal();

        cartera.Cerrar(motivo, Ahora);

        cartera.EraPrincipalAlCerrarsePorCascada.Should().BeFalse(
            "ahí el principal se releva en el acto: no hay nada que restaurar al reactivar la delegación");
    }

    [Fact]
    public void Una_cartera_ya_cerrada_no_se_puede_cerrar_otra_vez_por_cascada_para_fabricar_el_dato()
    {
        var cartera = CarteraExterna("GestorCae");
        cartera.DesignarPrincipal();
        cartera.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, Ahora);

        cartera.Invoking(c => c.CerrarPorCascadaDeLaOperacion(MotivoCierreAsignacion.Revocada, Ahora))
            .Should().Throw<InvalidOperationException>();
        cartera.EraPrincipalAlCerrarsePorCascada.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Una_cartera_suspendida_conserva_la_marca_y_una_suspendida_puede_recibirla(bool marcarAntes)
    {
        var cartera = CarteraExterna("GestorCae");
        if (marcarAntes) cartera.DesignarPrincipal();

        cartera.Suspender();
        if (!marcarAntes) cartera.DesignarPrincipal();

        cartera.EsPrincipal.Should().BeTrue("solo el cierre apaga la marca; por eso el índice único cuenta las suspendidas");
    }

    [Fact]
    public void Dejar_de_ser_principal_apaga_la_marca_y_no_toca_nada_mas()
    {
        var cartera = CarteraExterna("CoordinadorCae");
        cartera.DesignarPrincipal();

        cartera.DejarDeSerPrincipal();
        cartera.DejarDeSerPrincipal();

        cartera.EsPrincipal.Should().BeFalse();
        cartera.Estado.Should().Be(EstadoAsignacion.Vigente, "la cartera sigue viva: pierde la marca, no el acceso");
        cartera.Ambito.EsUniversal.Should().BeTrue();
        cartera.Rol.Should().Be("CoordinadorCae");
    }
}
