using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

/// <summary>
/// Las invariantes del plano de operación (ADR-011 § 2.7): append-only, la
/// raíz como fallback y no como competidora, y la vigencia semiabierta que hace
/// respondible la pregunta "¿quién era responsable el día X?".
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
}
