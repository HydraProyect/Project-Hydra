using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

public class ObtenerBandejaGestorQueryHandlerTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 15);
    private const int HorasAvisoVisita = 48;
    private const int HorasCriticasVisita = 24;

    private static AlertaDto Alerta(EstadoDocumento estado, DateOnly? fecha = null) => new(
        DocumentoId: Guid.NewGuid(), TrabajadorId: Guid.NewGuid(), TrabajadorNombre: "Ana García",
        TipoDocumentoId: Guid.NewGuid(), TipoDocumentoNombre: "Apto médico", FechaVencimiento: fecha,
        Estado: estado, ArchivoUrl: null, CentroNombre: "Centro Norte");

    private static RevisionIaDocumentoDto Revision() => new(
        Id: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), PropietarioNombre: "Luis Pérez",
        TipoDocumentoNombre: "EPIs", ConfianzaGeneral: 60, TipoDetectado: null,
        FechaEmisionDetectada: null, Motivo: "Confianza baja", CreadaEnUtc: DateTime.UtcNow);

    private static DocumentacionBloqueantePendienteDto Requisito() => new(
        CentroId: Guid.NewGuid(), CentroNombre: "Centro Sur", TrabajadorId: Guid.NewGuid(), TrabajadorNombre: "Ana García",
        TipoDocumentoId: Guid.NewGuid(), TipoDocumentoNombre: "PSS firmado");

    private static VisitaListaDto Visita(NivelUrgenciaVisita nivel, DateOnly? fechaInicio = null) => new(
        Id: Guid.NewGuid(), CentroId: Guid.NewGuid(), CentroNombre: "Centro Este",
        ClienteId: Guid.NewGuid(), ClienteRazonSocial: "Cliente Este S.A.",
        EmpresaId: Guid.NewGuid(), EmpresaRazonSocial: "Empresa Este S.L.",
        FechaInicio: fechaInicio ?? Hoy.AddDays(1), FechaFin: fechaInicio ?? Hoy.AddDays(1),
        TotalTrabajadores: 2, DocumentacionCompleta: true, NotificadoCliente: false,
        Origen: OrigenVisita.Plataforma, NivelUrgencia: nivel);

    private static SugerenciaVisitaCorreoPendienteDto Sugerencia(DateOnly? fechaInicioSugerida) => new(
        Id: Guid.NewGuid(), CentroId: Guid.NewGuid(), CentroNombre: "Centro Oeste",
        FechaInicioSugerida: fechaInicioSugerida, Resumen: "Pide entrar mañana con dos operarios",
        Canal: CanalConversacion.Correo, CreadaEnUtc: DateTime.UtcNow);

    private static DeteccionPendienteDto Deteccion(TipoDeteccion tipo = TipoDeteccion.Nuevo) => new(
        Id: Guid.NewGuid(), EmpresaId: Guid.NewGuid(), EmpresaRazonSocial: "Empresa Centro S.L.",
        Tipo: tipo, NombreCompleto: "Marta Ruiz", CreadaEnUtc: DateTime.UtcNow);

    private static ProveedorAcreditacionesDto PendientePlataforma(
        EstadoAcreditacion estado = EstadoAcreditacion.PendienteDeSubir, string proveedorNombre = "Dokify",
        string? motivoRechazo = null, Guid? trabajadorId = null, Guid? empresaId = null, Guid? centroId = null,
        Guid? tipoDocumentoId = null) => new(
        ProveedorPlataformaCaeId: Guid.NewGuid(), ProveedorNombre: proveedorNombre, ProveedorCodigo: "dokify",
        Clientes: [new ClienteAcreditacionesDto(
            ClienteId: Guid.NewGuid(), ClienteNombre: "Cliente Norte S.A.",
            Documentos: [new AcreditacionDrillDownDto(
                AcreditacionId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), PropietarioNombre: "Iker Etxeberria",
                TipoDocumentoNombre: "Formación 60h", Estado: estado, UltimoMotivoRechazo: motivoRechazo,
                TrabajadorId: empresaId is null ? trabajadorId ?? Guid.NewGuid() : null, EmpresaId: empresaId,
                CentroId: centroId, TipoDocumentoId: tipoDocumentoId)]) ]);

    private static IReadOnlyList<ItemBandejaDto> Fusionar(
        IReadOnlyList<AlertaDto>? alertas = null,
        IReadOnlyList<RevisionIaDocumentoDto>? revisiones = null,
        IReadOnlyList<DocumentacionBloqueantePendienteDto>? requisitos = null,
        IReadOnlyList<VisitaListaDto>? visitasUrgentes = null,
        IReadOnlyList<SugerenciaVisitaCorreoPendienteDto>? sugerenciasVisita = null,
        IReadOnlyList<DeteccionPendienteDto>? detecciones = null,
        IReadOnlyList<ProveedorAcreditacionesDto>? pendientesPlataforma = null) =>
        ObtenerBandejaGestorQueryHandler.Fusionar(
            alertas ?? [], revisiones ?? [], requisitos ?? [], visitasUrgentes ?? [], sugerenciasVisita ?? [], detecciones ?? [],
            pendientesPlataforma ?? [],
            Hoy, HorasAvisoVisita, HorasCriticasVisita);

    /// <summary>
    /// Pendiente en la plataforma (2026-10-10): un Trabajador bloqueado porque su documento bloqueante está SIN SUBIR a la
    /// plataforma del Centro no es una fila más de la cola cuando ya sale como su acreditación pendiente de subir. La
    /// fila de requisito solo se quita si la acreditación es del mismo Trabajador, del mismo Centro y del mismo tipo.
    /// </summary>
    [Fact]
    public void Un_bloqueo_por_pendiente_de_subir_no_duplica_la_fila_de_la_acreditacion_del_mismo_Trabajador_Centro_y_tipo()
    {
        var bloqueo = Requisito() with { Situacion = SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma };

        var resultado = Fusionar(
            requisitos: [bloqueo, Requisito()],
            pendientesPlataforma: [PendientePlataforma(
                EstadoAcreditacion.PendienteDeSubir, trabajadorId: bloqueo.TrabajadorId, centroId: bloqueo.CentroId,
                tipoDocumentoId: bloqueo.TipoDocumentoId)]);

        resultado.Where(i => i.Tipo == TipoItemBandeja.RequisitoPendiente).Should().ContainSingle(
            "solo el requisito ausente; el pendiente de subir ya está en la cola como su acreditación");
        resultado.Where(i => i.Tipo == TipoItemBandeja.PlataformaPendiente).Should().ContainSingle("control positivo: la acreditación sí sale");
    }

    /// <summary>
    /// Si la acreditación pendiente NO es la de este bloqueo (otro Trabajador, otro Centro u otro tipo; o no hay ninguna
    /// porque la fuente de acreditaciones usa el alcance de gestión y no el de lectura), el bloqueo conserva su fila: si
    /// no, el Trabajador bloqueado desaparecería de la Bandeja. Rompe cualquier emparejamiento más laxo que Trabajador,
    /// Centro y tipo.
    /// </summary>
    [Theory]
    [InlineData("otro Trabajador")]
    [InlineData("otro Centro")]
    [InlineData("otro tipo")]
    [InlineData("sin acreditación")]
    public void Un_bloqueo_por_pendiente_de_subir_sin_la_acreditacion_que_lo_cubre_conserva_su_fila_de_requisito(string caso)
    {
        var bloqueo = Requisito() with { Situacion = SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma };
        IReadOnlyList<ProveedorAcreditacionesDto> acreditaciones = caso == "sin acreditación" ? [] :
        [
            PendientePlataforma(
                EstadoAcreditacion.PendienteDeSubir,
                trabajadorId: caso == "otro Trabajador" ? Guid.NewGuid() : bloqueo.TrabajadorId,
                centroId: caso == "otro Centro" ? Guid.NewGuid() : bloqueo.CentroId,
                tipoDocumentoId: caso == "otro tipo" ? Guid.NewGuid() : bloqueo.TipoDocumentoId)
        ];

        var resultado = Fusionar(requisitos: [bloqueo], pendientesPlataforma: acreditaciones);

        resultado.Should().ContainSingle(i => i.Tipo == TipoItemBandeja.RequisitoPendiente, caso);
    }

    /// <summary>
    /// El requisito de Empresa bloquea a sus Trabajadores por un documento de la Empresa: lo cubre la acreditación de ESA
    /// Empresa en ese Centro y tipo (sin Trabajador), y no la de otra Empresa.
    /// </summary>
    [Fact]
    public void Un_bloqueo_de_Empresa_pendiente_de_subir_lo_cubre_solo_la_acreditacion_de_esa_Empresa()
    {
        var empresa = Guid.NewGuid();
        var bloqueo = Requisito() with
        {
            Ambito = AmbitoAplicacion.Empresa,
            EmpresaId = empresa,
            EmpresaNombre = "Montajes Ebro S.L.",
            Situacion = SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma
        };

        var deOtraEmpresa = Fusionar(requisitos: [bloqueo], pendientesPlataforma:
            [PendientePlataforma(EstadoAcreditacion.PendienteDeSubir, empresaId: Guid.NewGuid(), centroId: bloqueo.CentroId, tipoDocumentoId: bloqueo.TipoDocumentoId)]);
        var deLaEmpresa = Fusionar(requisitos: [bloqueo], pendientesPlataforma:
            [PendientePlataforma(EstadoAcreditacion.PendienteDeSubir, empresaId: empresa, centroId: bloqueo.CentroId, tipoDocumentoId: bloqueo.TipoDocumentoId)]);

        deOtraEmpresa.Should().ContainSingle(i => i.Tipo == TipoItemBandeja.RequisitoPendiente, "la acreditación es de otra Empresa");
        deLaEmpresa.Should().NotContain(i => i.Tipo == TipoItemBandeja.RequisitoPendiente, "la cubre la acreditación de su Empresa");
        deLaEmpresa.Should().ContainSingle(i => i.Tipo == TipoItemBandeja.PlataformaPendiente);
    }

    /// <summary>
    /// Subido y sin validar NO sale en la cola como acreditación (está esperando a la plataforma: seguimiento, no cola), así
    /// que el bloqueo que causa conserva su fila de requisito: si no, el Trabajador bloqueado desaparecería de la Bandeja.
    /// </summary>
    [Fact]
    public void Un_bloqueo_por_documento_subido_sin_validar_conserva_su_fila_de_requisito()
    {
        var resultado = Fusionar(
            requisitos: [Requisito() with { Situacion = SituacionDeRequisitoBloqueante.SinValidarEnPlataforma }],
            pendientesPlataforma: [PendientePlataforma(EstadoAcreditacion.Subida)]);

        resultado.Should().ContainSingle(i => i.Tipo == TipoItemBandeja.RequisitoPendiente,
            "la acreditación subida no entra en la cola, así que el requisito es la única fila de ese bloqueo");
        resultado.Should().NotContain(i => i.Tipo == TipoItemBandeja.PlataformaPendiente,
            "control: la acreditación subida no genera fila propia");
    }

    [Fact]
    public void Excluye_las_alertas_en_estado_Proximo()
    {
        var resultado = Fusionar(alertas: [Alerta(EstadoDocumento.Proximo), Alerta(EstadoDocumento.Vencido)]);

        resultado.Should().ContainSingle();
        resultado[0].Tipo.Should().Be(TipoItemBandeja.Vencido);
    }

    // El Id de la fila es su @key en Mi trabajo y en Bandeja: dos filas hermanas con el mismo Id
    // matan el circuito de Blazor («Attempting to return wrong pooled instance»). Medido en el E2E
    // del Coordinador CAE: el mismo Trabajador, asignado a dos Centros, sin el mismo Tipo de
    // documento en ninguno, daba dos alertas Faltante con Id alerta-{Trabajador}-{Tipo}.
    [Fact]
    public void Un_mismo_documento_que_falta_en_dos_Centros_del_Trabajador_da_dos_filas_con_Id_distinto()
    {
        var trabajador = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        AlertaDto Faltante(Guid centro, string nombreCentro) => new(
            DocumentoId: null, TrabajadorId: trabajador, TrabajadorNombre: "Ana García",
            TipoDocumentoId: tipo, TipoDocumentoNombre: "Apto médico", FechaVencimiento: null,
            Estado: EstadoDocumento.Faltante, ArchivoUrl: null, CentroNombre: nombreCentro, CentroId: centro);

        var resultado = Fusionar(alertas: [Faltante(Guid.NewGuid(), "Centro Norte"), Faltante(Guid.NewGuid(), "Centro Sur")]);

        resultado.Should().HaveCount(2);
        resultado.Select(i => i.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void IdDeFila_distingue_por_Centro_las_alertas_sin_documento_y_usa_el_documento_cuando_lo_hay()
    {
        var trabajador = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var centroA = Guid.NewGuid();
        var documento = Guid.NewGuid();
        AlertaDto Alerta(Guid? doc, Guid? centro) => new(
            DocumentoId: doc, TrabajadorId: trabajador, TrabajadorNombre: "Ana García",
            TipoDocumentoId: tipo, TipoDocumentoNombre: "Apto médico", FechaVencimiento: null,
            Estado: doc is null ? EstadoDocumento.Faltante : EstadoDocumento.Proximo, ArchivoUrl: null,
            CentroNombre: "Centro", CentroId: centro);

        Alerta(null, centroA).IdDeFila("alerta").Should().Be($"alerta-{trabajador}-{tipo}-{centroA}");
        Alerta(null, null).IdDeFila("alerta").Should().Be($"alerta-{trabajador}-{tipo}-sin-centro");
        Alerta(null, centroA).IdDeFila("alerta").Should().NotBe(Alerta(null, Guid.NewGuid()).IdDeFila("alerta"));
        Alerta(documento, centroA).IdDeFila("proximo").Should().Be($"proximo-{documento}");
    }

    [Fact]
    public void Las_filas_de_alertas_y_requisitos_de_la_fusion_tienen_Id_distinto_aunque_compartan_Trabajador_y_Tipo()
    {
        var trabajador = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var centro = Guid.NewGuid();
        AlertaDto NuevaAlerta(Guid? documento, Guid? centroId, EstadoDocumento estado) => new(
            DocumentoId: documento, TrabajadorId: trabajador, TrabajadorNombre: "Ana García",
            TipoDocumentoId: tipo, TipoDocumentoNombre: "Apto médico", FechaVencimiento: null,
            Estado: estado, ArchivoUrl: null, CentroNombre: "Centro", CentroId: centroId);

        var alertas = new[]
        {
            NuevaAlerta(documento: null, centroId: centro, EstadoDocumento.Faltante),
            NuevaAlerta(documento: null, centroId: Guid.NewGuid(), EstadoDocumento.Faltante),
            NuevaAlerta(documento: Guid.NewGuid(), centroId: centro, EstadoDocumento.Vencido),
            NuevaAlerta(documento: Guid.NewGuid(), centroId: centro, EstadoDocumento.Vencido),
        };

        var resultado = Fusionar(alertas: alertas, requisitos: [Requisito(), Requisito()]);

        resultado.Select(i => i.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ordena_por_prioridad_completa()
    {
        var resultado = Fusionar(
            alertas: [Alerta(EstadoDocumento.Urgente), Alerta(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Faltante)],
            revisiones: [Revision()],
            requisitos: [Requisito()],
            visitasUrgentes: [Visita(NivelUrgenciaVisita.Urgente)],
            sugerenciasVisita: [Sugerencia(fechaInicioSugerida: null)],
            detecciones: [Deteccion()]);

        // Decisiones 9 y 10 del propietario (2026-10-03): lo que bloquea el acceso va
        // primero —también por delante de la sugerencia de visita— y Vencido precede a Faltante.
        resultado.Select(i => i.Tipo).Should().Equal(
            TipoItemBandeja.RequisitoPendiente,
            TipoItemBandeja.SugerenciaVisitaUrgente,
            TipoItemBandeja.Vencido,
            TipoItemBandeja.Faltante,
            TipoItemBandeja.VisitaUrgente,
            TipoItemBandeja.Urgente,
            TipoItemBandeja.DeteccionPendiente,
            TipoItemBandeja.RevisionIa);
    }

    [Fact]
    public void Mapea_una_deteccion_pendiente_con_su_empresa_y_tipo()
    {
        var deteccion = Deteccion(TipoDeteccion.Ausente);

        var resultado = Fusionar(detecciones: [deteccion]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.DeteccionPendiente);
        item.Titulo.Should().Be("Baja detectada");
        item.Subtitulo.Should().Be("Empresa Centro S.L. — Marta Ruiz");
        item.EmpresaId.Should().Be(deteccion.EmpresaId);
    }

    [Fact]
    public void Mapea_un_pendiente_de_plataforma_con_su_proveedor_y_cliente()
    {
        var pendiente = PendientePlataforma();

        var resultado = Fusionar(pendientesPlataforma: [pendiente]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.PlataformaPendiente);
        item.Titulo.Should().Be("Formación 60h");
        item.Subtitulo.Should().Be("Iker Etxeberria");
        item.ClienteNombre.Should().Be("Cliente Norte S.A.");
        item.ProveedorNombre.Should().Be("Dokify");
        item.Fecha.Should().BeNull();
    }

    [Fact]
    public void Una_acreditacion_rechazada_entra_en_la_bandeja_como_PlataformaRechazada_con_su_motivo()
    {
        // Decisión D-4 del piloto Outbound: una Rechazada es trabajo accionable
        // del Gestor CAE (corregir y volver a subir), no una foto del estado
        // que solo se mire en Documentos. Sustituye a la regla anterior, que
        // la dejaba fuera «a propósito».
        var rechazada = PendientePlataforma(estado: EstadoAcreditacion.Rechazada, motivoRechazo: "Certificado ilegible");

        var resultado = Fusionar(pendientesPlataforma: [rechazada]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.PlataformaRechazada);
        item.Titulo.Should().Be("Formación 60h");
        item.Subtitulo.Should().Be("Iker Etxeberria — Certificado ilegible");
        item.ProveedorNombre.Should().Be("Dokify");
        item.ClienteNombre.Should().Be("Cliente Norte S.A.");
        // P0-9b: la acción «Corregir en …» lleva a esta acreditación, no a la lista.
        item.AcreditacionId.Should().Be(rechazada.Clientes.Single().Documentos.Single().AcreditacionId);
    }

    [Fact]
    public void Una_acreditacion_subida_no_entra_en_la_bandeja()
    {
        // Subida = enviada y esperando respuesta: no hay nada que hacer hasta
        // que la plataforma conteste. No es cola de trabajo.
        var subida = PendientePlataforma(estado: EstadoAcreditacion.Subida);

        Fusionar(pendientesPlataforma: [subida]).Should().BeEmpty();
    }

    [Fact]
    public void Una_acreditacion_aceptada_no_entra_en_la_bandeja()
    {
        Fusionar(pendientesPlataforma: [PendientePlataforma(estado: EstadoAcreditacion.Aceptada)]).Should().BeEmpty();
    }

    [Fact]
    public void Una_rechazada_por_plataforma_pesa_mas_que_una_visita_y_menos_que_un_vencido()
    {
        var resultado = Fusionar(
            alertas: [Alerta(EstadoDocumento.Vencido)],
            visitasUrgentes: [Visita(NivelUrgenciaVisita.Urgente)],
            pendientesPlataforma:
            [
                PendientePlataforma(),
                PendientePlataforma(estado: EstadoAcreditacion.Rechazada, motivoRechazo: "Ilegible")
            ]);

        resultado.Select(i => i.Tipo).Should().Equal(
            TipoItemBandeja.Vencido,
            TipoItemBandeja.PlataformaRechazada,
            TipoItemBandeja.VisitaUrgente,
            TipoItemBandeja.PlataformaPendiente);
    }

    [Fact]
    public void Una_alerta_faltante_o_vencida_se_ordena_por_fecha_de_vencimiento_dentro_del_mismo_tipo()
    {
        var masCercana = Alerta(EstadoDocumento.Vencido, new DateOnly(2026, 1, 1));
        var masLejana = Alerta(EstadoDocumento.Vencido, new DateOnly(2026, 6, 1));

        var resultado = Fusionar(alertas: [masLejana, masCercana]);

        resultado[0].Fecha.Should().Be(new DateOnly(2026, 1, 1));
        resultado[1].Fecha.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public void Mapea_una_revision_Ia_con_su_titulo_subtitulo_y_documento()
    {
        var revision = Revision();

        var resultado = Fusionar(revisiones: [revision]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.RevisionIa);
        item.Titulo.Should().Be("EPIs");
        item.Subtitulo.Should().Be("Luis Pérez — Confianza baja");
        item.DocumentoId.Should().Be(revision.DocumentoId);
    }

    [Fact]
    public void Mapea_un_requisito_pendiente_con_su_centro()
    {
        var requisito = Requisito();

        var resultado = Fusionar(requisitos: [requisito]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.RequisitoPendiente);
        item.Titulo.Should().Be("PSS firmado — Ana García");
        item.Subtitulo.Should().Be("Centro Sur");
        item.CentroId.Should().Be(requisito.CentroId);
        item.TrabajadorId.Should().Be(requisito.TrabajadorId);
        item.TipoDocumentoId.Should().Be(requisito.TipoDocumentoId);
    }

    [Fact]
    public void Solo_incluye_visitas_en_niveles_Urgente_o_Critica()
    {
        var resultado = Fusionar(visitasUrgentes:
        [
            Visita(NivelUrgenciaVisita.Normal),
            Visita(NivelUrgenciaVisita.EnCurso),
            Visita(NivelUrgenciaVisita.Urgente),
            Visita(NivelUrgenciaVisita.Critica)
        ]);

        resultado.Should().HaveCount(2);
        resultado.Should().OnlyContain(i => i.Tipo == TipoItemBandeja.VisitaUrgente);
    }

    [Fact]
    public void Una_sugerencia_sin_fecha_detectada_se_trata_como_visita_sorpresa_del_mismo_dia()
    {
        var resultado = Fusionar(sugerenciasVisita: [Sugerencia(fechaInicioSugerida: null)]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.SugerenciaVisitaUrgente);
        item.SugerenciaVisitaId.Should().NotBeNull();
    }

    [Fact]
    public void Una_sugerencia_con_fecha_fuera_de_la_ventana_de_aviso_no_entra_en_la_bandeja()
    {
        var resultado = Fusionar(sugerenciasVisita: [Sugerencia(fechaInicioSugerida: Hoy.AddDays(10))]);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Una_sugerencia_con_fecha_dentro_de_la_ventana_de_aviso_si_entra()
    {
        var resultado = Fusionar(sugerenciasVisita: [Sugerencia(fechaInicioSugerida: Hoy.AddDays(1))]);

        resultado.Should().ContainSingle();
    }

    /// <summary>
    /// Project-Hydra-Negocio/tecnico/docs/blueprints/OPERATIONAL-HOME.md § 6 (DDL-068): solo SugerenciaVisitaUrgente,
    /// DeteccionPendiente y RevisionIa tienen un momento real de "creación" que alimente el
    /// resumen de ausencia — el resto son estado derivado (Faltante/Vencido/Urgente/
    /// RequisitoPendiente/VisitaUrgente), sin CreadaEnUtc que propagar.
    /// </summary>
    [Fact]
    public void Solo_los_tres_tipos_con_fecha_real_de_aparicion_llevan_CreadaEnUtc()
    {
        var resultado = Fusionar(
            alertas: [Alerta(EstadoDocumento.Vencido)],
            revisiones: [Revision()],
            requisitos: [Requisito()],
            visitasUrgentes: [Visita(NivelUrgenciaVisita.Urgente)],
            sugerenciasVisita: [Sugerencia(fechaInicioSugerida: null)],
            detecciones: [Deteccion()]);

        var conFecha = resultado.Where(i => i.CreadaEnUtc is not null).Select(i => i.Tipo);
        conFecha.Should().BeEquivalentTo(
        [
            TipoItemBandeja.SugerenciaVisitaUrgente,
            TipoItemBandeja.RevisionIa,
            TipoItemBandeja.DeteccionPendiente
        ]);

        var sinFecha = resultado.Where(i => i.CreadaEnUtc is null).Select(i => i.Tipo);
        sinFecha.Should().BeEquivalentTo(
        [
            TipoItemBandeja.Vencido,
            TipoItemBandeja.RequisitoPendiente,
            TipoItemBandeja.VisitaUrgente
        ]);
    }
}
