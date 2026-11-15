using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Inbound.ContratosAcceso;

public class ContratoAccesoTests
{
    private static readonly DateOnly Inicio = new(2026, 3, 1);
    private static readonly DateOnly Fin = new(2026, 3, 31);
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc);

    private static ContratoAcceso Contrato(DateOnly? fin = null, string? notas = null, bool pendiente = false) =>
        ContratoAcceso.Crear(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Inicio, fin, notas, pendienteDeConfirmar: pendiente);

    /// <summary>Un contrato con fin contratado en el estado pedido, llegando a él por las transiciones válidas.</summary>
    private static ContratoAcceso EnEstado(EstadoContratoAcceso estado)
    {
        var pendiente = estado is EstadoContratoAcceso.PendienteDeConfirmar or EstadoContratoAcceso.Rechazado;
        var contrato = Contrato(Fin, pendiente: pendiente);
        switch (estado)
        {
            case EstadoContratoAcceso.Rechazado: contrato.RechazarPorTenantContratista(); break;
            case EstadoContratoAcceso.Suspendido: contrato.Suspender(); break;
            case EstadoContratoAcceso.Finalizado: contrato.Finalizar(Inicio.AddDays(5)); break;
            case EstadoContratoAcceso.Cancelado: contrato.Cancelar(Inicio.AddDays(-1), tieneActividad: false); break;
        }
        contrato.Estado.Should().Be(estado);
        return contrato;
    }

    public static TheoryData<EstadoContratoAcceso> TodosLosEstados()
    {
        var datos = new TheoryData<EstadoContratoAcceso>();
        foreach (var estado in Enum.GetValues<EstadoContratoAcceso>())
            datos.Add(estado);
        return datos;
    }

    [Fact]
    public void Crear_conserva_las_coordenadas_y_nace_abierto_y_activo()
    {
        var cliente = Guid.NewGuid();
        var centro = Guid.NewGuid();
        var contratista = Guid.NewGuid();

        var contrato = ContratoAcceso.Crear(cliente, centro, contratista, Inicio, Fin, "Mantenimiento anual");

        contrato.ClienteEmpresarialId.Should().Be(cliente);
        contrato.CentroId.Should().Be(centro);
        contrato.EmpresaContratistaId.Should().Be(contratista);
        contrato.FechaInicio.Should().Be(Inicio);
        contrato.FechaFin.Should().Be(Fin);
        contrato.Notas.Should().Be("Mantenimiento anual");
        contrato.EstaAbierto.Should().BeTrue();
        contrato.FechaCierreReal.Should().BeNull();
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
        contrato.Condiciones.Should().Be(CondicionesContratoAcceso.SinFijar);
        contrato.HistorialVigencia.Should().BeEmpty();
    }

    [Fact]
    public void Crear_pendiente_de_confirmar_nace_abierto_y_sin_dar_acceso()
    {
        var contrato = Contrato(Fin, pendiente: true);

        contrato.Estado.Should().Be(EstadoContratoAcceso.PendienteDeConfirmar);
        contrato.EstaAbierto.Should().BeTrue();
        contrato.VigenteEn(Inicio).Should().BeFalse("X-1: hasta confirmar no da acceso");
        CoincideConLaExpresion(contrato, Inicio);
    }

    [Fact]
    public void Crear_con_condiciones_las_guarda()
    {
        var condiciones = new CondicionesContratoAcceso(
            TipoContratoAcceso.Obra, PropiaActividadContrato.Si, 2, 10, 1, "  Aforo de la nave  ");

        var contrato = ContratoAcceso.Crear(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Inicio, Fin, null, condiciones);

        contrato.Tipo.Should().Be(TipoContratoAcceso.Obra);
        contrato.PropiaActividad.Should().Be(PropiaActividadContrato.Si);
        contrato.NivelMaxSubcontrata.Should().Be(2);
        contrato.CupoTrabajadores.Should().Be(10);
        contrato.CupoSubcontratas.Should().Be(1);
        contrato.MotivoCupo.Should().Be("Aforo de la nave");
        contrato.Condiciones.Should().Be(condiciones);
    }

    [Fact]
    public void Crear_exige_las_tres_coordenadas()
    {
        var id = Guid.NewGuid();

        FluentActions.Invoking(() => ContratoAcceso.Crear(Guid.Empty, id, Guid.NewGuid(), Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("clienteEmpresarialId");
        FluentActions.Invoking(() => ContratoAcceso.Crear(id, Guid.Empty, Guid.NewGuid(), Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("centroId");
        FluentActions.Invoking(() => ContratoAcceso.Crear(id, Guid.NewGuid(), Guid.Empty, Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("empresaContratistaId");
    }

    [Fact]
    public void La_Empresa_contratista_no_puede_ser_el_propio_Cliente_empresarial()
    {
        var cliente = Guid.NewGuid();

        FluentActions.Invoking(() => ContratoAcceso.Crear(cliente, Guid.NewGuid(), cliente, Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("empresaContratistaId");
    }

    [Fact]
    public void El_fin_no_puede_ser_anterior_al_inicio_al_crear_ni_al_actualizar()
    {
        FluentActions.Invoking(() => Contrato(Inicio.AddDays(-1)))
            .Should().Throw<ArgumentException>().WithParameterName("fechaFin");

        var contrato = Contrato(Fin);
        FluentActions.Invoking(() => contrato.Actualizar(Inicio.AddDays(-1), null, Usuario, Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("fechaFin");
        contrato.FechaFin.Should().Be(Fin, "un cambio rechazado no toca el contrato");
        contrato.HistorialVigencia.Should().BeEmpty();

        // El mismo día de inicio sí vale: un contrato de un día.
        Contrato(Inicio).FechaFin.Should().Be(Inicio);
    }

    [Fact]
    public void Las_notas_no_pasan_del_maximo()
    {
        var limite = new string('a', ContratoAcceso.LongitudMaximaNotas);
        Contrato(notas: limite).Notas.Should().Be(limite);

        FluentActions.Invoking(() => Contrato(notas: limite + "a"))
            .Should().Throw<ArgumentException>().WithParameterName("notas");
        FluentActions.Invoking(() => Contrato().Actualizar(null, limite + "a", Usuario, Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("notas");
    }

    [Fact]
    public void Actualizar_cambia_el_fin_y_las_notas_y_deja_el_cambio_de_fin_en_el_historial()
    {
        var contrato = Contrato(Fin, "Antes");

        contrato.Actualizar(null, "Después", Usuario, Ahora);

        contrato.FechaFin.Should().BeNull();
        contrato.Notas.Should().Be("Después");
        var cambio = contrato.HistorialVigencia.Should().ContainSingle().Subject;
        cambio.ContratoAccesoId.Should().Be(contrato.Id);
        cambio.FechaFinAnterior.Should().Be(Fin);
        cambio.FechaFinNueva.Should().BeNull();
        cambio.Origen.Should().Be(OrigenCambioVigencia.Edicion);
        cambio.RegistradoPorUsuarioId.Should().Be(Usuario);
        cambio.RegistradoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void Actualizar_sin_cambiar_el_fin_no_escribe_historial()
    {
        var contrato = Contrato(Fin, "Antes");

        contrato.Actualizar(Fin, "Después", Usuario, Ahora);

        contrato.Notas.Should().Be("Después");
        contrato.HistorialVigencia.Should().BeEmpty();
    }

    [Fact]
    public void Un_cambio_de_fin_exige_quien_lo_hace()
    {
        var contrato = Contrato(Fin);

        FluentActions.Invoking(() => contrato.Actualizar(Fin.AddDays(1), null, Guid.Empty, Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("registradoPorUsuarioId");
        contrato.FechaFin.Should().Be(Fin);
        contrato.HistorialVigencia.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoContratoAcceso.Activo)]
    [InlineData(EstadoContratoAcceso.Suspendido)]
    public void Renovar_amplia_el_fin_sin_cambiar_de_estado_y_lo_deja_en_el_historial(EstadoContratoAcceso estado)
    {
        var contrato = EnEstado(estado);
        var nuevoFin = Fin.AddMonths(6);

        contrato.Renovar(nuevoFin, Usuario, Ahora);

        contrato.FechaFin.Should().Be(nuevoFin);
        contrato.Estado.Should().Be(estado, "Prorrogado no es un estado: es Renovar (duda 3)");
        var cambio = contrato.HistorialVigencia.Should().ContainSingle().Subject;
        cambio.FechaFinAnterior.Should().Be(Fin);
        cambio.FechaFinNueva.Should().Be(nuevoFin);
        cambio.Origen.Should().Be(OrigenCambioVigencia.Renovacion);
    }

    [Fact]
    public void Renovar_dos_veces_deja_dos_entradas_encadenadas()
    {
        var contrato = Contrato(Fin);

        contrato.Renovar(Fin.AddMonths(1), Usuario, Ahora);
        contrato.Renovar(Fin.AddMonths(2), Usuario, Ahora.AddDays(1));

        contrato.HistorialVigencia.Select(h => (h.FechaFinAnterior, h.FechaFinNueva)).Should().Equal(
            (Fin, Fin.AddMonths(1)), (Fin.AddMonths(1), Fin.AddMonths(2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Renovar_a_un_fin_no_posterior_lanza(int dias)
    {
        var contrato = Contrato(Fin);

        FluentActions.Invoking(() => contrato.Renovar(Fin.AddDays(dias), Usuario, Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("nuevaFechaFin");
        contrato.FechaFin.Should().Be(Fin);
        contrato.HistorialVigencia.Should().BeEmpty();
    }

    [Fact]
    public void Renovar_un_contrato_indefinido_lanza()
    {
        var contrato = Contrato();

        FluentActions.Invoking(() => contrato.Renovar(Fin, Usuario, Ahora)).Should().Throw<InvalidOperationException>();
        contrato.FechaFin.Should().BeNull();
        contrato.HistorialVigencia.Should().BeEmpty();
    }

    // ── Máquina de estados: la matriz entera, transición por transición ────────────────────────────────────────────

    /// <summary>Las transiciones válidas de cada acción; cualquier otro estado de origen lanza y no toca el contrato.</summary>
    private static readonly Dictionary<string, (EstadoContratoAcceso[] Desde, EstadoContratoAcceso Hacia, Action<ContratoAcceso> Accion)> Acciones = new()
    {
        ["Confirmar"] = ([EstadoContratoAcceso.PendienteDeConfirmar], EstadoContratoAcceso.Activo, c => c.ConfirmarPorTenantContratista()),
        ["Rechazar"] = ([EstadoContratoAcceso.PendienteDeConfirmar], EstadoContratoAcceso.Rechazado, c => c.RechazarPorTenantContratista()),
        ["Suspender"] = ([EstadoContratoAcceso.Activo], EstadoContratoAcceso.Suspendido, c => c.Suspender()),
        ["Reanudar"] = ([EstadoContratoAcceso.Suspendido], EstadoContratoAcceso.Activo, c => c.Reanudar()),
        ["Finalizar"] = ([EstadoContratoAcceso.Activo, EstadoContratoAcceso.Suspendido], EstadoContratoAcceso.Finalizado,
            c => c.Finalizar(Inicio.AddDays(10))),
        ["Cancelar"] = ([EstadoContratoAcceso.PendienteDeConfirmar, EstadoContratoAcceso.Activo], EstadoContratoAcceso.Cancelado,
            c => c.Cancelar(Inicio.AddDays(-1), tieneActividad: false)),
    };

    public static TheoryData<string, EstadoContratoAcceso> AccionPorEstado()
    {
        var datos = new TheoryData<string, EstadoContratoAcceso>();
        foreach (var accion in Acciones.Keys)
            foreach (var estado in Enum.GetValues<EstadoContratoAcceso>())
                datos.Add(accion, estado);
        return datos;
    }

    [Theory]
    [MemberData(nameof(AccionPorEstado))]
    public void Cada_accion_solo_sale_de_sus_estados_permitidos(string accion, EstadoContratoAcceso desde)
    {
        var (permitidos, hacia, ejecutar) = Acciones[accion];
        var contrato = EnEstado(desde);
        var cierreAntes = contrato.FechaCierreReal;

        if (permitidos.Contains(desde))
        {
            ejecutar(contrato);
            contrato.Estado.Should().Be(hacia);
        }
        else
        {
            FluentActions.Invoking(() => ejecutar(contrato)).Should().Throw<InvalidOperationException>();
            contrato.Estado.Should().Be(desde, "una transición rechazada no toca el contrato");
            contrato.FechaCierreReal.Should().Be(cierreAntes);
        }
    }

    [Theory]
    [MemberData(nameof(TodosLosEstados))]
    public void Renovar_solo_desde_Activo_o_Suspendido(EstadoContratoAcceso desde)
    {
        var contrato = EnEstado(desde);
        var accion = () => contrato.Renovar(Fin.AddMonths(1), Usuario, Ahora);

        if (desde is EstadoContratoAcceso.Activo or EstadoContratoAcceso.Suspendido)
            accion();
        else
        {
            accion.Should().Throw<InvalidOperationException>();
            contrato.FechaFin.Should().Be(Fin);
            contrato.HistorialVigencia.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(EstadoContratoAcceso.PendienteDeConfirmar, true)]
    [InlineData(EstadoContratoAcceso.Activo, true)]
    [InlineData(EstadoContratoAcceso.Suspendido, true)]
    [InlineData(EstadoContratoAcceso.Finalizado, false)]
    [InlineData(EstadoContratoAcceso.Cancelado, false)]
    [InlineData(EstadoContratoAcceso.Rechazado, false)]
    public void EstaAbierto_solo_en_los_estados_no_finales(EstadoContratoAcceso estado, bool abierto) =>
        EnEstado(estado).EstaAbierto.Should().Be(abierto);

    [Theory]
    [InlineData(EstadoContratoAcceso.PendienteDeConfirmar, false)]
    [InlineData(EstadoContratoAcceso.Activo, true)]
    [InlineData(EstadoContratoAcceso.Suspendido, false)]
    [InlineData(EstadoContratoAcceso.Finalizado, true)]
    [InlineData(EstadoContratoAcceso.Cancelado, false)]
    [InlineData(EstadoContratoAcceso.Rechazado, false)]
    public void Solo_Activo_y_Finalizado_dan_acceso_en_su_periodo(EstadoContratoAcceso estado, bool vigente)
    {
        var contrato = EnEstado(estado);

        // El día de inicio está dentro del periodo de todos, también del Finalizado (cierre a los cinco días).
        contrato.VigenteEn(Inicio).Should().Be(vigente);
        CoincideConLaExpresion(contrato, Inicio);
    }

    [Fact]
    public void Finalizar_dos_veces_lanza()
    {
        var contrato = Contrato();
        contrato.Finalizar(Inicio.AddDays(5));

        FluentActions.Invoking(() => contrato.Finalizar(Inicio.AddDays(6)))
            .Should().Throw<InvalidOperationException>();
        contrato.FechaCierreReal.Should().Be(Inicio.AddDays(5));
    }

    [Fact]
    public void Finalizar_antes_del_inicio_lanza()
    {
        var contrato = Contrato();

        FluentActions.Invoking(() => contrato.Finalizar(Inicio.AddDays(-1)))
            .Should().Throw<ArgumentException>().WithParameterName("fechaCierre");
        contrato.EstaAbierto.Should().BeTrue();
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);

        // El mismo día de inicio sí vale.
        contrato.Finalizar(Inicio);
        contrato.FechaCierreReal.Should().Be(Inicio);
        contrato.Estado.Should().Be(EstadoContratoAcceso.Finalizado);
    }

    [Fact]
    public void Cancelar_el_dia_de_inicio_o_despues_lanza_porque_ya_empezo()
    {
        var contrato = Contrato(Fin);

        FluentActions.Invoking(() => contrato.Cancelar(Inicio, tieneActividad: false)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => contrato.Cancelar(Inicio.AddDays(1), tieneActividad: false)).Should().Throw<InvalidOperationException>();
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
    }

    [Fact]
    public void Cancelar_con_actividad_lanza()
    {
        var contrato = Contrato(Fin);

        FluentActions.Invoking(() => contrato.Cancelar(Inicio.AddDays(-1), tieneActividad: true))
            .Should().Throw<InvalidOperationException>();
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
    }

    [Fact]
    public void Un_contrato_cancelado_no_da_acceso_tampoco_desde_su_inicio()
    {
        var contrato = EnEstado(EstadoContratoAcceso.Cancelado);

        contrato.VigenteEn(Inicio).Should().BeFalse();
        contrato.VigenteEn(Fin).Should().BeFalse();
        contrato.FechaCierreReal.Should().BeNull("Cancelar no es cerrar: no hubo periodo");
        CoincideConLaExpresion(contrato, Fin);
    }

    [Fact]
    public void Un_contrato_reanudado_vuelve_a_dar_acceso()
    {
        var contrato = EnEstado(EstadoContratoAcceso.Suspendido);

        contrato.Reanudar();

        contrato.VigenteEn(Inicio).Should().BeTrue();
        CoincideConLaExpresion(contrato, Inicio);
    }

    // ── Cierre de la versión anterior durante el relevo azul/verde ───────────────────────────────────────────────────

    /// <summary>
    /// Lo que deja el <c>Cerrar</c> de la versión anterior a B-05: el cierre real escrito y la columna <c>Estado</c> en su
    /// DEFAULT 'Activo' (o el estado que tuviera), porque esa versión no la conoce.
    /// </summary>
    private static ContratoAcceso CerradoPorLaVersionAnterior(EstadoContratoAcceso estado = EstadoContratoAcceso.Activo)
    {
        var contrato = EnEstado(estado);
        typeof(ContratoAcceso).GetProperty(nameof(ContratoAcceso.FechaCierreReal))!.SetValue(contrato, Inicio.AddDays(5));
        contrato.Estado.Should().Be(estado, "la versión anterior no toca la columna Estado");
        return contrato;
    }

    [Theory]
    [InlineData(EstadoContratoAcceso.Activo)]
    [InlineData(EstadoContratoAcceso.Suspendido)]
    public void Un_contrato_cerrado_por_la_version_anterior_es_Finalizado_a_efectos_del_dominio(EstadoContratoAcceso guardado)
    {
        var contrato = CerradoPorLaVersionAnterior(guardado);

        contrato.EstadoEfectivo.Should().Be(EstadoContratoAcceso.Finalizado);
        contrato.EstaAbierto.Should().BeFalse();
        contrato.VigenteEn(Inicio.AddDays(5)).Should().Be(guardado == EstadoContratoAcceso.Activo, "da acceso hasta el cierre, como antes");
        contrato.VigenteEn(Inicio.AddDays(6)).Should().BeFalse("y nunca después del cierre");
        CoincideConLaExpresion(contrato, Inicio.AddDays(5));
    }

    [Fact]
    public void Un_contrato_cerrado_por_la_version_anterior_no_admite_ninguna_transicion()
    {
        foreach (var (nombre, (_, _, ejecutar)) in Acciones)
        {
            var contrato = CerradoPorLaVersionAnterior();
            FluentActions.Invoking(() => ejecutar(contrato)).Should().Throw<InvalidOperationException>(nombre);
            contrato.Estado.Should().Be(EstadoContratoAcceso.Activo, nombre);
            contrato.FechaCierreReal.Should().Be(Inicio.AddDays(5), nombre);
        }

        var otro = CerradoPorLaVersionAnterior();
        FluentActions.Invoking(() => otro.Renovar(Fin.AddMonths(1), Usuario, Ahora)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => otro.Actualizar(Fin.AddMonths(1), null, Usuario, Ahora)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => otro.Actualizar(Fin, "Notas", Usuario, Ahora)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => otro.FijarCondiciones(CondicionesContratoAcceso.SinFijar)).Should().Throw<InvalidOperationException>();
        otro.FechaFin.Should().Be(Fin);
        otro.HistorialVigencia.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoContratoAcceso.PendienteDeConfirmar)]
    [InlineData(EstadoContratoAcceso.Cancelado)]
    [InlineData(EstadoContratoAcceso.Rechazado)]
    public void Un_cierre_de_la_version_anterior_en_un_estado_sin_periodo_no_lo_convierte_en_Finalizado(EstadoContratoAcceso guardado)
    {
        var contrato = CerradoPorLaVersionAnterior(guardado);

        contrato.EstadoEfectivo.Should().Be(guardado);
        contrato.EstaAbierto.Should().BeFalse();
        contrato.VigenteEn(Inicio).Should().BeFalse("ni Pendiente, ni Cancelado, ni Rechazado dan acceso nunca");
        CoincideConLaExpresion(contrato, Inicio);
        foreach (var (nombre, (_, _, ejecutar)) in Acciones)
            FluentActions.Invoking(() => ejecutar(contrato)).Should().Throw<InvalidOperationException>(nombre);
        contrato.Estado.Should().Be(guardado);
    }

    [Theory]
    [MemberData(nameof(TodosLosEstados))]
    public void Sin_cierre_real_el_estado_efectivo_es_el_guardado(EstadoContratoAcceso estado)
    {
        var contrato = EnEstado(estado);

        contrato.EstadoEfectivo.Should().Be(estado);
    }

    // ── Guardas de Editar y Fijar condiciones ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EstadoContratoAcceso.PendienteDeConfirmar, true, false)]
    [InlineData(EstadoContratoAcceso.Activo, true, true)]
    [InlineData(EstadoContratoAcceso.Suspendido, true, false)]
    [InlineData(EstadoContratoAcceso.Finalizado, false, false)]
    [InlineData(EstadoContratoAcceso.Cancelado, false, false)]
    [InlineData(EstadoContratoAcceso.Rechazado, false, false)]
    public void Editar_las_notas_solo_en_un_contrato_abierto_y_el_fin_solo_en_uno_Activo(
        EstadoContratoAcceso estado, bool notas, bool fin)
    {
        var soloNotas = EnEstado(estado);
        var editarNotas = () => soloNotas.Actualizar(Fin, "Nueva nota", Usuario, Ahora);
        if (notas)
        {
            editarNotas();
            soloNotas.Notas.Should().Be("Nueva nota");
        }
        else
        {
            editarNotas.Should().Throw<InvalidOperationException>();
            soloNotas.Notas.Should().BeNull();
        }

        foreach (var nuevoFin in new DateOnly?[] { Fin.AddMonths(2), Fin.AddDays(-5), null })
        {
            var contrato = EnEstado(estado);
            var cambiarFin = () => contrato.Actualizar(nuevoFin, null, Usuario, Ahora);
            if (fin)
            {
                cambiarFin();
                contrato.FechaFin.Should().Be(nuevoFin);
            }
            else
            {
                cambiarFin.Should().Throw<InvalidOperationException>("un contrato que no está Activo no cambia su fin al editarlo: la vía es Renovar");
                contrato.FechaFin.Should().Be(Fin);
                contrato.HistorialVigencia.Should().BeEmpty();
            }
        }
    }

    [Theory]
    [InlineData(EstadoContratoAcceso.PendienteDeConfirmar, true)]
    [InlineData(EstadoContratoAcceso.Activo, true)]
    [InlineData(EstadoContratoAcceso.Suspendido, false)]
    [InlineData(EstadoContratoAcceso.Finalizado, false)]
    [InlineData(EstadoContratoAcceso.Cancelado, false)]
    [InlineData(EstadoContratoAcceso.Rechazado, false)]
    public void FijarCondiciones_solo_en_un_contrato_Pendiente_de_confirmar_o_Activo(EstadoContratoAcceso estado, bool permitido)
    {
        var contrato = EnEstado(estado);
        var condiciones = new CondicionesContratoAcceso(TipoContratoAcceso.Obra, null, 2, 5, null, "Aforo");
        var fijar = () => contrato.FijarCondiciones(condiciones);

        if (permitido)
        {
            fijar();
            contrato.Condiciones.Should().Be(condiciones);
        }
        else
        {
            fijar.Should().Throw<InvalidOperationException>();
            contrato.Condiciones.Should().Be(CondicionesContratoAcceso.SinFijar);
        }
    }

    // ── Acción de negocio en la auditoría (plan § 7) ─────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> AccionDeAuditoria() => new()
    {
        { "Renovar", RegistroAuditoria.AccionRenovado },
        { "Cancelar", RegistroAuditoria.AccionCancelado },
        { "Finalizar", RegistroAuditoria.AccionFinalizado },
        { "FijarCondiciones", RegistroAuditoria.AccionCondicionesFijadas },
    };

    [Theory]
    [MemberData(nameof(AccionDeAuditoria))]
    public void Cada_accion_de_negocio_pide_su_accion_de_auditoria_una_sola_vez(string accion, string esperada)
    {
        var contrato = Contrato(Fin);
        var auditable = (IAccionAuditoriaPropia)contrato;
        auditable.ConsumirAccionAuditoria().Should().BeNull("el alta es «Creado», sin acción propia");

        switch (accion)
        {
            case "Renovar": contrato.Renovar(Fin.AddMonths(1), Usuario, Ahora); break;
            case "Cancelar": contrato.Cancelar(Inicio.AddDays(-1), tieneActividad: false); break;
            case "Finalizar": contrato.Finalizar(Inicio.AddDays(5)); break;
            case "FijarCondiciones": contrato.FijarCondiciones(CondicionesContratoAcceso.SinFijar); break;
        }

        auditable.ConsumirAccionAuditoria().Should().Be(esperada);
        auditable.ConsumirAccionAuditoria().Should().BeNull("la marca es de un solo uso");
    }

    [Fact]
    public void Editar_y_una_transicion_rechazada_no_piden_accion_propia()
    {
        var contrato = Contrato(Fin);
        var auditable = (IAccionAuditoriaPropia)contrato;

        contrato.Actualizar(Fin.AddDays(1), "Notas", Usuario, Ahora);
        auditable.ConsumirAccionAuditoria().Should().BeNull("Editar queda como «Modificado»");

        contrato.Finalizar(Inicio.AddDays(5));
        auditable.ConsumirAccionAuditoria();
        FluentActions.Invoking(() => contrato.Renovar(Fin.AddMonths(1), Usuario, Ahora)).Should().Throw<InvalidOperationException>();
        auditable.ConsumirAccionAuditoria().Should().BeNull();
    }

    // ── Vigencia por fechas ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, false)] // el día antes del inicio
    [InlineData(0, true)]   // el día de inicio
    [InlineData(30, true)]  // el día de fin
    [InlineData(31, false)] // el día después del fin
    public void VigenteEn_cuenta_el_inicio_y_el_fin_como_vigentes(int diasDesdeInicio, bool vigente)
    {
        var contrato = Contrato(Fin);
        var fecha = Inicio.AddDays(diasDesdeInicio);

        contrato.VigenteEn(fecha).Should().Be(vigente);
        CoincideConLaExpresion(contrato, fecha);
    }

    [Fact]
    public void VigenteEn_con_fin_indefinido_sigue_vigente_en_un_futuro_lejano()
    {
        var contrato = Contrato();
        var lejano = new DateOnly(2099, 12, 31);

        contrato.VigenteEn(lejano).Should().BeTrue();
        contrato.VigenteEn(Inicio.AddDays(-1)).Should().BeFalse();
        CoincideConLaExpresion(contrato, lejano);
    }

    [Fact]
    public void VigenteEn_cuenta_el_dia_de_cierre_y_no_el_siguiente_aunque_el_fin_contratado_no_haya_llegado()
    {
        var contrato = Contrato(Fin);
        var cierre = Inicio.AddDays(10);

        contrato.Finalizar(cierre);

        contrato.VigenteEn(cierre).Should().BeTrue();
        contrato.VigenteEn(cierre.AddDays(1)).Should().BeFalse();
        contrato.VigenteEn(Fin).Should().BeFalse();
        CoincideConLaExpresion(contrato, cierre);
        CoincideConLaExpresion(contrato, cierre.AddDays(1));
    }

    [Fact]
    public void Un_contrato_eliminado_no_esta_vigente()
    {
        var contrato = Contrato();

        contrato.MarcarComoEliminado(Guid.NewGuid());

        contrato.VigenteEn(Inicio).Should().BeFalse();
        CoincideConLaExpresion(contrato, Inicio);
    }

    // ── Condiciones: tipo, propia actividad, nivel y cupos ───────────────────────────────────────────────────────────

    [Fact]
    public void FijarCondiciones_sustituye_las_anteriores_de_una_vez()
    {
        var contrato = ContratoAcceso.Crear(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Inicio, Fin, null,
            new CondicionesContratoAcceso(TipoContratoAcceso.Fijo, PropiaActividadContrato.No, 1, 5, null, "Turno de noche"));

        contrato.FijarCondiciones(CondicionesContratoAcceso.SinFijar);

        contrato.Condiciones.Should().Be(CondicionesContratoAcceso.SinFijar);
        contrato.MotivoCupo.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CondicionesContratoAcceso.NivelSubcontrataMaximo)]
    public void El_nivel_de_subcontrata_admite_de_cero_al_maximo(int nivel) =>
        new CondicionesContratoAcceso(null, null, nivel, null, null, null).NivelMaxSubcontrata.Should().Be(nivel);

    [Theory]
    [InlineData(-1)]
    [InlineData(CondicionesContratoAcceso.NivelSubcontrataMaximo + 1)]
    public void El_nivel_de_subcontrata_fuera_de_rango_lanza(int nivel) =>
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, nivel, null, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("nivelMaxSubcontrata");

    [Fact]
    public void Un_cupo_negativo_lanza()
    {
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, -1, null, "x"))
            .Should().Throw<ArgumentException>().WithParameterName("cupoTrabajadores");
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, null, -1, "x"))
            .Should().Throw<ArgumentException>().WithParameterName("cupoSubcontratas");
    }

    [Fact]
    public void El_cupo_cero_es_un_cupo_y_exige_motivo() =>
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, 0, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("motivoCupo");

    [Theory]
    [InlineData(5, null)]
    [InlineData(null, 2)]
    public void Con_cupo_el_motivo_es_obligatorio(int? trabajadores, int? subcontratas)
    {
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, trabajadores, subcontratas, null))
            .Should().Throw<ArgumentException>().WithParameterName("motivoCupo");
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, trabajadores, subcontratas, "   "))
            .Should().Throw<ArgumentException>().WithParameterName("motivoCupo");
    }

    [Fact]
    public void Sin_cupo_no_hay_motivo() =>
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, null, null, "Sin sentido"))
            .Should().Throw<ArgumentException>().WithParameterName("motivoCupo");

    [Fact]
    public void El_motivo_del_cupo_no_pasa_del_maximo()
    {
        var limite = new string('m', CondicionesContratoAcceso.LongitudMaximaMotivoCupo);
        new CondicionesContratoAcceso(null, null, null, 1, null, limite).MotivoCupo.Should().Be(limite);

        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, null, null, 1, null, limite + "m"))
            .Should().Throw<ArgumentException>().WithParameterName("motivoCupo");
    }

    [Fact]
    public void Un_tipo_o_una_propia_actividad_fuera_del_enum_lanza()
    {
        FluentActions.Invoking(() => new CondicionesContratoAcceso((TipoContratoAcceso)99, null, null, null, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("tipo");
        FluentActions.Invoking(() => new CondicionesContratoAcceso(null, (PropiaActividadContrato)99, null, null, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("propiaActividad");
    }

    // ── Vínculo con el Tenant de la Empresa contratista ─────────────────────────────────────────────────────────────

    [Fact]
    public void VincularTenantContratista_se_escribe_una_sola_vez()
    {
        var contrato = Contrato();
        var tenantContratista = Guid.NewGuid();

        contrato.TenantContratistaId.Should().BeNull("un contrato nace sin vincular");
        contrato.VincularTenantContratista(tenantContratista);
        contrato.TenantContratistaId.Should().Be(tenantContratista);

        FluentActions.Invoking(() => contrato.VincularTenantContratista(Guid.NewGuid()))
            .Should().Throw<InvalidOperationException>("cambiar el vínculo redirigiría la lectura autorizada");
        FluentActions.Invoking(() => contrato.VincularTenantContratista(tenantContratista))
            .Should().Throw<InvalidOperationException>("tampoco se repite con el mismo valor");
        contrato.TenantContratistaId.Should().Be(tenantContratista);
    }

    [Fact]
    public void VincularTenantContratista_rechaza_el_vacio_y_el_propio_Tenant_del_contrato()
    {
        var contrato = Contrato();
        var propio = Guid.NewGuid();
        typeof(CaeManager.Domain.Common.EntidadConTenant)
            .GetProperty(nameof(CaeManager.Domain.Common.EntidadConTenant.TenantId))!.SetValue(contrato, propio);

        FluentActions.Invoking(() => contrato.VincularTenantContratista(Guid.Empty))
            .Should().Throw<ArgumentException>().WithParameterName("tenantContratistaId");
        FluentActions.Invoking(() => contrato.VincularTenantContratista(propio))
            .Should().Throw<ArgumentException>().WithParameterName("tenantContratistaId");
        contrato.TenantContratistaId.Should().BeNull("un vínculo rechazado no toca el contrato");
    }

    /// <summary>La expresión que traducen las consultas y el método en memoria son el mismo criterio.</summary>
    private static void CoincideConLaExpresion(ContratoAcceso contrato, DateOnly fecha) =>
        ContratoAcceso.EstaVigenteEn(fecha).Compile()(contrato).Should().Be(contrato.VigenteEn(fecha),
            "EstaVigenteEn y VigenteEn tienen que decir lo mismo el {0}", fecha);
}
