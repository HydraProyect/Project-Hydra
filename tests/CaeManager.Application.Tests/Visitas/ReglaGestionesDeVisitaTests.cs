using CaeManager.Application.Visitas.AltaDeVisita;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Qué Gestiones nacen al crear una Visita (decisión de Chris, 2026-11-19): una por Trabajador y Tipo de documento que el
/// Centro le exige y que le falta o caduca antes de la fecha de fin de la Visita; nunca una por Documento exigido.
/// </summary>
public class ReglaGestionesDeVisitaTests
{
    private static readonly DateOnly Fin = new(2026, 12, 10);
    private static readonly Guid Centro = Guid.NewGuid();

    private static readonly TrabajadorEnPlan Ana = new(Guid.NewGuid(), "Ana García", TipoContratacion.CuentaAjena, AsignadoAlCentro: true);
    private static readonly TrabajadorEnPlan Bruno = new(Guid.NewGuid(), "Bruno Pérez", TipoContratacion.CuentaAjena, AsignadoAlCentro: false);
    private static readonly TrabajadorEnPlan Carla = new(Guid.NewGuid(), "Carla Ruiz", TipoContratacion.Autonomo, AsignadoAlCentro: false);

    private static readonly TipoEnPlan Formacion = new(Guid.NewGuid(), "Formación PRL", ObligatorioPorDefecto: true, TiposContratacion.Todos);
    private static readonly TipoEnPlan Epis = new(Guid.NewGuid(), "Entrega de EPI", ObligatorioPorDefecto: true, TiposContratacion.CuentaAjena);
    private static readonly TipoEnPlan Opcional = new(Guid.NewGuid(), "Carné de carretillero", ObligatorioPorDefecto: false, TiposContratacion.Todos);

    private static DocumentoEnPlan Doc(TrabajadorEnPlan t, TipoEnPlan tipo, VigenciaDocumento vigencia, DateOnly? emision = null) =>
        new(t.TrabajadorId, tipo.TipoDocumentoId, new DocumentoParaAcceso(vigencia, emision ?? new DateOnly(2026, 1, 1)));

    private static PlanAltaVisitaDto Calcular(
        IReadOnlyList<TrabajadorEnPlan> trabajadores, IReadOnlyList<TipoEnPlan> tipos, IReadOnlyList<DocumentoEnPlan>? documentos = null,
        Dictionary<(Guid, Guid), bool>? incluido = null, Dictionary<Guid, int?>? periodicidad = null,
        HashSet<(Guid, Guid)>? pendientes = null, bool conGestionCae = true) =>
        ReglaGestionesDeVisita.Calcular(
            trabajadores, tipos, incluido ?? [], periodicidad ?? [], documentos ?? [], pendientes ?? [], Centro, conGestionCae, Fin);

    [Fact]
    public void Asigna_solo_a_quien_no_tiene_Asignacion_activa_al_Centro()
    {
        var plan = Calcular([Ana, Bruno], []);

        plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(Bruno.TrabajadorId);
        plan.Gestiones.Should().BeEmpty();
    }

    [Fact]
    public void Nace_una_Gestion_por_Tipo_que_falta_y_ninguna_por_el_que_esta_vigente()
    {
        var plan = Calcular([Ana], [Formacion, Epis], [Doc(Ana, Epis, VigenciaDocumento.VenceEl(Fin.AddDays(30)))]);

        plan.Gestiones.Should().ContainSingle()
            .Which.Should().Be(new GestionPrevistaDto(Ana.TrabajadorId, Ana.Nombre, Formacion.TipoDocumentoId, Formacion.Nombre, MotivoGestionPrevista.Falta));
    }

    [Fact]
    public void Un_Documento_que_caduca_antes_del_fin_de_la_Visita_da_Gestion_y_uno_que_caduca_el_mismo_dia_no()
    {
        var antes = Calcular([Ana], [Formacion], [Doc(Ana, Formacion, VigenciaDocumento.VenceEl(Fin.AddDays(-1)))]);
        var elMismoDia = Calcular([Ana], [Formacion], [Doc(Ana, Formacion, VigenciaDocumento.VenceEl(Fin))]);

        antes.Gestiones.Should().ContainSingle().Which.Motivo.Should().Be(MotivoGestionPrevista.CaducaAntesDelFin);
        elMismoDia.Gestiones.Should().BeEmpty();
    }

    [Fact]
    public void Varios_Documentos_del_mismo_Tipo_dan_una_sola_Gestion_y_basta_uno_que_cubra_la_Visita()
    {
        var caducados = Calcular([Ana], [Formacion],
            [Doc(Ana, Formacion, VigenciaDocumento.VenceEl(Fin.AddDays(-10))), Doc(Ana, Formacion, VigenciaDocumento.VenceEl(Fin.AddDays(-2)))]);
        var unoCubre = Calcular([Ana], [Formacion],
            [Doc(Ana, Formacion, VigenciaDocumento.VenceEl(Fin.AddDays(-10))), Doc(Ana, Formacion, VigenciaDocumento.NoCaduca)]);

        caducados.Gestiones.Should().ContainSingle();
        unoCubre.Gestiones.Should().BeEmpty();
    }

    [Fact]
    public void La_periodicidad_especial_del_Centro_manda_sobre_la_fecha_del_Documento()
    {
        // Vence en 2027, pero el Centro lo pide renovado cada 6 meses desde la emisión: para este Centro vence el 2026-07-01.
        var documento = Doc(Ana, Formacion, VigenciaDocumento.VenceEl(new DateOnly(2027, 6, 1)), emision: new DateOnly(2026, 1, 1));

        var plan = Calcular([Ana], [Formacion], [documento], periodicidad: new() { [Formacion.TipoDocumentoId] = 6 });

        plan.Gestiones.Should().ContainSingle().Which.Motivo.Should().Be(MotivoGestionPrevista.CaducaAntesDelFin);
    }

    [Fact]
    public void Lo_que_el_Centro_no_exige_no_da_Gestion()
    {
        // Opcional no es obligatorio de serie; Formación está excluida en este Centro; Epis solo se pide de serie a cuenta ajena.
        var plan = Calcular([Carla], [Opcional, Formacion, Epis],
            incluido: new() { [(Formacion.TipoDocumentoId, Centro)] = false });

        plan.Gestiones.Should().BeEmpty();
    }

    [Fact]
    public void Una_fila_explicita_del_Centro_exige_lo_que_de_serie_no_se_pide()
    {
        var plan = Calcular([Carla], [Opcional], incluido: new() { [(Opcional.TipoDocumentoId, Centro)] = true });

        plan.Gestiones.Should().ContainSingle().Which.TipoDocumentoId.Should().Be(Opcional.TipoDocumentoId);
    }

    [Fact]
    public void Un_Centro_sin_gestion_CAE_no_exige_nada_pero_si_asigna()
    {
        var plan = Calcular([Bruno], [Formacion], conGestionCae: false);

        plan.Gestiones.Should().BeEmpty();
        plan.Asignaciones.Should().ContainSingle();
    }

    [Fact]
    public void Quien_solapa_con_una_Asignacion_cerrada_sale_en_Solapadas_y_no_en_Asignaciones()
    {
        var dani = new TrabajadorEnPlan(Guid.NewGuid(), "Dani Soler", TipoContratacion.CuentaAjena, AsignadoAlCentro: false, SolapaConAsignacionCerrada: true);

        var plan = Calcular([Ana, Bruno, dani], []);

        plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(Bruno.TrabajadorId);
        plan.Solapadas.Should().ContainSingle().Which.Should().Be(new AsignacionPrevistaDto(dani.TrabajadorId, dani.Nombre));
    }

    [Fact]
    public void Situacion_de_asignacion_con_la_semantica_DEC19_de_CrearAsignaciones()
    {
        var hoy = new DateOnly(2026, 11, 19);

        ReglaGestionesDeVisita.SituacionAsignacion([], hoy).Should().Be((false, false), "sin filas: se asigna");
        ReglaGestionesDeVisita.SituacionAsignacion([new(hoy.AddDays(-30), null)], hoy)
            .Should().Be((true, false), "una fila activa es «asignado»");
        ReglaGestionesDeVisita.SituacionAsignacion([new(hoy.AddDays(-30), hoy.AddDays(1))], hoy)
            .Should().Be((false, true), "cerrada con baja posterior a hoy: solapa");
        ReglaGestionesDeVisita.SituacionAsignacion([new(hoy.AddDays(-30), hoy)], hoy)
            .Should().Be((false, false), "la baja es exclusiva: una baja hoy ya no solapa con un alta hoy");
        ReglaGestionesDeVisita.SituacionAsignacion([new(hoy.AddDays(5), hoy.AddDays(5))], hoy)
            .Should().Be((false, false), "rango vacío (alta == baja): no ocupó ni un día");
        ReglaGestionesDeVisita.SituacionAsignacion([new(hoy.AddDays(-30), hoy.AddDays(1)), new(hoy.AddDays(-1), null)], hoy)
            .Should().Be((true, false), "estar asignado manda sobre solapar");
    }

    [Fact]
    public void No_duplica_una_Gestion_Pendiente_del_mismo_Trabajador_Centro_y_Tipo()
    {
        var plan = Calcular([Ana, Bruno], [Formacion], pendientes: [(Ana.TrabajadorId, Formacion.TipoDocumentoId)]);

        plan.Gestiones.Select(g => g.TrabajadorId).Should().Equal(Bruno.TrabajadorId);
    }
}
