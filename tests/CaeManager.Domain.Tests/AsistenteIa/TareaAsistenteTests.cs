using System.Reflection;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Auditoria;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.AsistenteIa;

/// <summary>
/// Invariantes de la Tarea del asistente de flujos: nada se ejecuta sin plan
/// confirmado, lo que falta queda como borrador en el paso, y los textos y los
/// actores se guardan cada uno en su campo.
/// </summary>
public class TareaAsistenteTests
{
    private static readonly DateTime Ahora = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Persona = Guid.NewGuid();

    private static TareaAsistente NuevaTarea(Guid? persona = null) =>
        new(persona ?? Persona, null, Guid.NewGuid(), TipoViaAccesoAuditoria.Normal, null, Ahora);

    private static DefinicionPasoTareaAsistente PasoListo(string orden = "alta_centro") =>
        new(orden, """{"nombre":"Centro Norte"}""", "Alta del Centro Norte", [], []);

    private static DefinicionPasoTareaAsistente PasoConDatoPendiente() =>
        new("alta_trabajador_y_asignacion_a_centro", """{"trabajador":"Ana Ruiz"}""", "Alta de Ana Ruiz", ["dni"], []);

    private static TareaAsistente TareaConPlanListo(out Guid pasoId)
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoListo()], asistidoPorIa: true, Ahora);
        pasoId = tarea.Pasos.Single().Id;
        return tarea;
    }

    // ── Nada se ejecuta sin plan confirmado ────────────────────────────────

    [Fact]
    public void Registrar_la_ejecucion_de_un_paso_sin_plan_confirmado_se_rechaza_y_el_paso_no_cambia()
    {
        var tarea = TareaConPlanListo(out var pasoId);

        var ejecutar = () => tarea.RegistrarPasoEjecutado(pasoId, Guid.NewGuid(), Ahora);

        ejecutar.Should().Throw<InvalidOperationException>().WithMessage("*plan confirmado*");
        tarea.Pasos.Single().Estado.Should().Be(EstadoPasoTareaAsistente.Listo);
        tarea.Pasos.Single().EjecutadoEnUtc.Should().BeNull();
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanListo);
    }

    [Fact]
    public void Registrar_un_fallo_de_ejecucion_sin_plan_confirmado_tambien_se_rechaza()
    {
        var tarea = TareaConPlanListo(out var pasoId);

        var fallar = () => tarea.RegistrarPasoFallido(pasoId, "error", Ahora);

        fallar.Should().Throw<InvalidOperationException>();
        tarea.Pasos.Single().Estado.Should().Be(EstadoPasoTareaAsistente.Listo);
    }

    [Fact]
    public void Ningun_metodo_publico_del_paso_cambia_su_estado_solo_la_tarea_lo_mueve()
    {
        // Si un método público del paso pudiera confirmarlo o ejecutarlo, un
        // llamador saltaría la comprobación de plan confirmado que hace la
        // tarea. Lo que queda público son lecturas.
        var publicos = typeof(PasoTareaAsistente)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        publicos.Should().BeEmpty();
        typeof(PasoTareaAsistente).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Should().BeEmpty();
        typeof(PasoTareaAsistente).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty("un paso solo nace dentro de TareaAsistente.GuardarPlan");
    }

    [Fact]
    public void Tras_confirmar_el_plan_el_paso_se_ejecuta_y_la_tarea_termina()
    {
        var tarea = TareaConPlanListo(out var pasoId);
        var resultado = Guid.NewGuid();

        tarea.ConfirmarPlan(Persona, null, Ahora);
        tarea.RegistrarPasoEjecutado(pasoId, resultado, Ahora.AddSeconds(1));

        var paso = tarea.Pasos.Single();
        paso.Estado.Should().Be(EstadoPasoTareaAsistente.Ejecutado);
        paso.ConfirmadoEnUtc.Should().Be(Ahora);
        paso.EntidadResultadoId.Should().Be(resultado);
        tarea.Estado.Should().Be(EstadoTareaAsistente.Terminada);
    }

    [Fact]
    public void Registrar_dos_veces_la_misma_ejecucion_es_idempotente_y_un_resultado_distinto_se_rechaza()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoListo(), PasoListo("alta_cliente_empresarial")], true, Ahora);
        tarea.ConfirmarPlan(Persona, null, Ahora);
        var primero = tarea.Pasos[0].Id;
        var resultado = Guid.NewGuid();

        tarea.RegistrarPasoEjecutado(primero, resultado, Ahora);
        tarea.RegistrarPasoEjecutado(primero, resultado, Ahora.AddMinutes(5));

        tarea.Pasos[0].EjecutadoEnUtc.Should().Be(Ahora);
        tarea.Estado.Should().Be(EstadoTareaAsistente.Confirmada);
        var otroResultado = () => tarea.RegistrarPasoEjecutado(primero, Guid.NewGuid(), Ahora);
        otroResultado.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_paso_fallido_se_puede_reintentar()
    {
        var tarea = TareaConPlanListo(out var pasoId);
        tarea.ConfirmarPlan(Persona, null, Ahora);

        tarea.RegistrarPasoFallido(pasoId, "El Centro no respondió", Ahora);
        tarea.Pasos.Single().Estado.Should().Be(EstadoPasoTareaAsistente.Fallido);
        tarea.Pasos.Single().MotivoFallo.Should().Be("El Centro no respondió");

        tarea.RegistrarPasoEjecutado(pasoId, null, Ahora.AddMinutes(1));
        tarea.Pasos.Single().Estado.Should().Be(EstadoPasoTareaAsistente.Ejecutado);
        tarea.Pasos.Single().MotivoFallo.Should().BeNull();
    }

    // ── Confirmación ───────────────────────────────────────────────────────

    [Fact]
    public void El_plan_no_se_confirma_mientras_un_paso_siga_en_borrador()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoListo(), PasoConDatoPendiente()], true, Ahora);

        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanEnBorrador);
        var confirmar = () => tarea.ConfirmarPlan(Persona, null, Ahora);
        confirmar.Should().Throw<InvalidOperationException>();
        tarea.Pasos.Should().NotContain(p => p.Estado == EstadoPasoTareaAsistente.Confirmado);
        tarea.PlanConfirmadoEnUtc.Should().BeNull();
    }

    [Fact]
    public void Un_aviso_bloqueante_deja_el_paso_en_borrador_y_una_advertencia_no()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan(
        [
            new("alta_centro", "{}", null, [], [new(GravedadAvisoPasoTareaAsistente.Advertencia, "La fecha es de ayer", "desde")]),
            new("alta_cliente_empresarial", "{}", null, [], [new(GravedadAvisoPasoTareaAsistente.Bloqueante, "La letra del DNI no corresponde", "dni")]),
        ], true, Ahora);

        tarea.Pasos[0].Estado.Should().Be(EstadoPasoTareaAsistente.Listo);
        tarea.Pasos[1].Estado.Should().Be(EstadoPasoTareaAsistente.Borrador);
        tarea.Pasos[1].Avisos.Should().ContainSingle()
            .Which.Should().Be(new AvisoPasoTareaAsistente(GravedadAvisoPasoTareaAsistente.Bloqueante, "La letra del DNI no corresponde", "dni"));
    }

    [Fact]
    public void Cuando_llega_el_dato_que_faltaba_el_borrador_se_reanuda_y_el_plan_queda_listo()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoConDatoPendiente()], true, Ahora);
        var paso = tarea.Pasos.Single();
        paso.CamposPendientes.Should().Equal("dni");

        tarea.ActualizarPasoEnBorrador(paso.Id, """{"trabajador":"Ana Ruiz","dni":"[DOC_1]"}""", "Alta de Ana Ruiz", [], [], Ahora);

        paso.Estado.Should().Be(EstadoPasoTareaAsistente.Listo);
        paso.CamposPendientes.Should().BeEmpty();
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanListo);
    }

    [Fact]
    public void Solo_la_persona_propietaria_confirma_su_plan_y_queda_como_Actor_real_separado_del_Usuario_simulado()
    {
        var tarea = TareaConPlanListo(out _);

        var otraPersona = () => tarea.ConfirmarPlan(Guid.NewGuid(), null, Ahora);
        otraPersona.Should().Throw<InvalidOperationException>();

        var simulado = Guid.NewGuid();
        tarea.ConfirmarPlan(Persona, simulado, Ahora);

        tarea.PlanConfirmadoPorActorRealUsuarioId.Should().Be(Persona);
        tarea.PlanConfirmadoComoUsuarioSimuladoId.Should().Be(simulado);
        tarea.PlanConfirmadoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void Un_plan_confirmado_ya_no_se_modifica()
    {
        var tarea = TareaConPlanListo(out var pasoId);
        tarea.ConfirmarPlan(Persona, null, Ahora);

        tarea.Invoking(t => t.GuardarPlan([PasoListo()], true, Ahora)).Should().Throw<InvalidOperationException>();
        tarea.Invoking(t => t.ActualizarPasoEnBorrador(pasoId, "{}", null, [], [], Ahora)).Should().Throw<InvalidOperationException>();
        tarea.Invoking(t => t.DescartarPaso(pasoId, Ahora)).Should().Throw<InvalidOperationException>();
        tarea.Invoking(t => t.ConfirmarPlan(Persona, null, Ahora)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Descartar_un_paso_antes_de_confirmar_lo_saca_del_plan_y_un_plan_sin_pasos_vivos_no_se_confirma()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoListo(), PasoConDatoPendiente()], true, Ahora);

        tarea.DescartarPaso(tarea.Pasos[1].Id, Ahora);
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanListo);

        tarea.DescartarPaso(tarea.Pasos[0].Id, Ahora);
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanEnBorrador);
        tarea.Invoking(t => t.ConfirmarPlan(Persona, null, Ahora)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Descartar_la_tarea_conserva_lo_ya_ejecutado_y_descarta_lo_demas()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoListo(), PasoListo("alta_cliente_empresarial")], true, Ahora);
        tarea.ConfirmarPlan(Persona, null, Ahora);
        tarea.RegistrarPasoEjecutado(tarea.Pasos[0].Id, Guid.NewGuid(), Ahora);

        tarea.Descartar(Ahora);

        tarea.Estado.Should().Be(EstadoTareaAsistente.Descartada);
        tarea.Pasos[0].Estado.Should().Be(EstadoPasoTareaAsistente.Ejecutado);
        tarea.Pasos[1].Estado.Should().Be(EstadoPasoTareaAsistente.Descartado);
        tarea.Invoking(t => t.RegistrarPasoEjecutado(t.Pasos[1].Id, null, Ahora)).Should().Throw<InvalidOperationException>();
        tarea.Invoking(t => t.AgregarTurnoDePersona("otra cosa", null, Ahora)).Should().Throw<InvalidOperationException>();
        tarea.Invoking(t => t.Descartar(Ahora)).Should().Throw<InvalidOperationException>();
    }

    // ── Conversación y textos ─────────────────────────────────────────────

    [Fact]
    public void El_texto_original_y_el_enmascarado_se_guardan_cada_uno_en_su_campo_y_el_original_tal_cual()
    {
        var tarea = NuevaTarea();
        const string original = "  Alta de Ana Ruiz, DNI 12345678Z, en el Centro Norte ";
        const string enmascarado = "Alta de Ana Ruiz, DNI [DOC_1], en el Centro Norte";

        var turno = tarea.AgregarTurnoDePersona(original, enmascarado, Ahora);
        var respuesta = tarea.AgregarTurnoDeAsistente("¿Desde qué fecha?", null, Ahora);

        turno.TextoOriginal.Should().Be(original);
        turno.TextoEnmascarado.Should().Be(enmascarado);
        turno.Autor.Should().Be(AutorTurnoTareaAsistente.Persona);
        turno.Numero.Should().Be(1);
        respuesta.Autor.Should().Be(AutorTurnoTareaAsistente.Asistente);
        respuesta.TextoEnmascarado.Should().BeNull();
        respuesta.Numero.Should().Be(2);
    }

    [Fact]
    public void Un_turno_vacio_o_demasiado_largo_se_rechaza()
    {
        var tarea = NuevaTarea();

        tarea.Invoking(t => t.AgregarTurnoDePersona("   ", null, Ahora)).Should().Throw<ArgumentException>();
        tarea.Invoking(t => t.AgregarTurnoDePersona(new string('a', TurnoTareaAsistente.LongitudMaximaTexto + 1), null, Ahora))
            .Should().Throw<ArgumentException>();
        tarea.Invoking(t => t.AgregarTurnoDePersona("hola", " ", Ahora)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("{no es json")]
    [InlineData("")]
    public void Los_datos_de_un_paso_tienen_que_ser_un_objeto_JSON(string datos)
    {
        var tarea = NuevaTarea();

        tarea.Invoking(t => t.GuardarPlan([new("alta_centro", datos, null, [], [])], true, Ahora))
            .Should().Throw<ArgumentException>();
        tarea.Pasos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alta Centro")]
    [InlineData("alta-centro")]
    public void Un_paso_nombra_la_orden_por_su_identificador_estable(string orden)
    {
        var tarea = NuevaTarea();

        tarea.Invoking(t => t.GuardarPlan([new(orden, "{}", null, [], [])], true, Ahora))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Guardar_un_plan_nuevo_sustituye_al_anterior_y_marca_la_asistencia_por_IA()
    {
        var tarea = NuevaTarea();
        tarea.GuardarPlan([PasoListo(), PasoListo("alta_cliente_empresarial")], asistidoPorIa: true, Ahora);

        tarea.GuardarPlan([PasoConDatoPendiente()], asistidoPorIa: false, Ahora);

        tarea.Pasos.Should().ContainSingle();
        tarea.Pasos.Single().Posicion.Should().Be(1);
        tarea.Pasos.Single().AsistidoPorIa.Should().BeFalse();
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanEnBorrador);
    }

    // ── Propiedad y vía ───────────────────────────────────────────────────

    [Fact]
    public void Una_sesion_privilegiada_no_abre_tareas_del_asistente()
    {
        var crear = () => new TareaAsistente(Persona, null, Guid.NewGuid(), TipoViaAccesoAuditoria.SesionPrivilegiada, Guid.NewGuid(), Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(TipoViaAccesoAuditoria.Normal, true)]
    [InlineData(TipoViaAccesoAuditoria.OperacionDelegada, false)]
    [InlineData(TipoViaAccesoAuditoria.Desconocida, false)]
    public void La_via_tiene_que_ser_coherente_con_su_identificador(TipoViaAccesoAuditoria via, bool conIdentificador)
    {
        // Normal con identificador, delegada sin su Asignación de Operación o
        // vía desconocida: ninguna describe cómo opera la persona el Tenant.
        var crear = () => new TareaAsistente(Persona, null, null, via, conIdentificador ? Guid.NewGuid() : null, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_tarea_de_operacion_delegada_guarda_el_tenant_de_origen_y_la_asignacion()
    {
        var tenantOrigen = Guid.NewGuid();
        var asignacionOperacion = Guid.NewGuid();

        var tarea = new TareaAsistente(Persona, null, tenantOrigen, TipoViaAccesoAuditoria.OperacionDelegada, asignacionOperacion, Ahora);

        tarea.ActorRealUsuarioId.Should().Be(Persona);
        tarea.UsuarioSimuladoId.Should().BeNull();
        tarea.TenantOrigenId.Should().Be(tenantOrigen);
        tarea.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.OperacionDelegada);
        tarea.ViaAccesoId.Should().Be(asignacionOperacion);
        tarea.Estado.Should().Be(EstadoTareaAsistente.Conversando);
    }
}
