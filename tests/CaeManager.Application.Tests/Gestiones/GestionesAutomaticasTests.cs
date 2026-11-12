using CaeManager.Application.Gestiones;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.ContratosAcceso;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// Gestión automática de varios pasos (§ 4.1-4.4 de la especificación del 2026-10-23): nacimiento dentro del acto
/// (<see cref="NacimientoGestionAutomaticaService"/>) a partir de las acreditaciones que ese acto agregó, sustitución,
/// y el universo de pasos visibles por cartera (D-7). Cómo la piden los Commands lo prueba
/// <see cref="GestionAutomaticaEnLosActosTests"/>.
/// </summary>
public class GestionesAutomaticasTests
{
    private static readonly DateOnly Hoy = new(2026, 11, 7);
    private static readonly DateTime Ahora = new(2026, 11, 7, 9, 12, 0, DateTimeKind.Utc);

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly ProveedoresPlataformaCaeQueryContextFalso _proveedores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly GestionesAutomaticasEnMemoria _gestiones = new();
    private readonly AcreditacionDocumentoPlataformaRepositorioFalso _delActo = new();
    private readonly Guid _usuarioId = Guid.NewGuid();

    private readonly Empresa _empresa = new("Construcciones Ebro S.L.", "B87654323");
    private readonly TipoDocumento _tipo;
    private readonly Trabajador _trabajador;

    public GestionesAutomaticasTests()
    {
        _empresas.ListaEmpresas.Add(_empresa);
        _tipo = new TipoDocumento("Reconocimiento médico", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(_tipo);
        _trabajador = Trabajador.DeEmpresa(_empresa.Id, "Laura", "Gil", "77189989B");
        _trabajadores.ListaTrabajadores.Add(_trabajador);
    }

    // ---------- Nacimiento ----------

    [Fact]
    public async Task Al_nacer_con_archivo_nace_una_sola_gestion_con_un_paso_por_acceso_de_plataforma()
    {
        var documento = DocumentoDelTrabajador();
        var norte = Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        var sur = Acreditar(documento, AccesoPlataforma(Centro("Almacén Sur"), "Dokify"));

        var devuelta = await Nacer(documento);

        var gestion = _gestiones.Lista.Should().ContainSingle().Subject;
        devuelta.Should().BeSameAs(gestion);
        gestion.DocumentoId.Should().Be(documento.Id);
        gestion.Origen.Should().Be(OrigenGestionAutomatica.AltaDocumento);
        gestion.NacidaPorUsuarioId.Should().Be(_usuarioId);
        gestion.Pasos.Select(p => p.AcreditacionId).Should().BeEquivalentTo([norte.Id, sur.Id]);
    }

    [Fact]
    public async Task Los_accesos_por_correo_nunca_dan_pasos()
    {
        var documento = DocumentoDelTrabajador();
        var centro = Centro("Planta Norte");
        var plataforma = Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        var correo = CanalGestionDocumental.PorEmail(centro.Id, "Correo CAE", "cae@cliente.es", null);
        _centros.ListaCanalesGestionDocumental.Add(correo);
        Acreditar(documento, correo);

        await Nacer(documento);

        _gestiones.Lista.Single().Pasos.Select(p => p.AcreditacionId).Should().Equal(plataforma.Id);
    }

    [Fact]
    public async Task Sin_accesos_de_plataforma_no_nace_gestion_porque_no_hay_nada_que_gestionar()
    {
        // Hallazgo 7 de la primera ronda de Codex, rechazado por decisión: la Gestión automática existe para
        // actualizar plataformas; un alta con archivo sin ningún acceso de plataforma no tiene pasos ni Gestión.
        var documento = DocumentoDelTrabajador();

        (await Nacer(documento)).Should().BeNull();

        _gestiones.Lista.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_alta_sin_archivo_no_hace_nacer_gestion_aunque_tenga_acreditaciones()
    {
        // Hallazgo 1 de la primera ronda de Codex: el disparador fijado es «alta y renovación con archivo».
        var documento = DocumentoDelTrabajador(archivoUrl: null);
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));

        (await Nacer(documento)).Should().BeNull();

        _gestiones.Lista.Should().BeEmpty();
    }

    [Fact]
    public async Task Los_pasos_son_solo_las_acreditaciones_que_agrego_el_acto()
    {
        // Hallazgo 8 de la primera ronda de Codex: los pasos salen de lo que el acto agregó sin guardar, no de lo que
        // haya en la base para ese Documento cuando se procese.
        var documento = DocumentoDelTrabajador();
        var delActo = Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        var yaGuardada = new AcreditacionDocumentoPlataforma(documento.Id, AccesoPlataforma(Centro("Almacén Sur"), "Dokify").Id);
        _documentos.ListaAcreditacionesDocumentoPlataforma.Add(yaGuardada);

        await Nacer(documento);

        _gestiones.Lista.Single().Pasos.Select(p => p.AcreditacionId).Should().Equal(delActo.Id);
    }

    [Fact]
    public async Task Las_acreditaciones_sin_guardar_de_otro_Documento_no_son_pasos_de_este()
    {
        // Un mismo guardado puede llevar acreditaciones agregadas de varios Documentos (división de un PDF, por ejemplo):
        // cada Gestión toma solo las de su Documento.
        var documento = DocumentoDelTrabajador();
        var otro = DocumentoDelTrabajador("tenant/otro.pdf");
        var propia = Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        Acreditar(otro, AccesoPlataforma(Centro("Almacén Sur"), "Dokify"));

        var gestion = await new NacimientoGestionAutomaticaService(
                _gestiones, _delActo, _documentos, _centros, new CurrentUserServiceFalso(_usuarioId, "GestorCae"), new RelojFijo(Ahora))
            .AgregarAsync(documento, sustituido: null, CancellationToken.None);

        gestion!.Pasos.Select(p => p.AcreditacionId).Should().Equal(propia.Id);
    }

    [Fact]
    public async Task Renovar_con_la_gestion_anterior_abierta_la_cierra_como_sustituida_con_enlace_a_la_nueva()
    {
        var acceso = AccesoPlataforma(Centro("Planta Norte"), "Nalanda");
        var anterior = DocumentoDelTrabajador();
        Acreditar(anterior, acceso);
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();

        var nuevo = RenovacionDe(anterior);
        Acreditar(nuevo, acceso);
        await Nacer(nuevo, anterior);

        var gestionNueva = _gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);
        gestionNueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        gestionNueva.Pasos.Should().ContainSingle("la acreditación del anterior es historial: no entra en la nueva");
        gestionAnterior.EstaSustituida.Should().BeTrue();
        gestionAnterior.SustituidaEnUtc.Should().Be(Ahora);
        gestionAnterior.SustituidaPorGestionId.Should().Be(gestionNueva.Id);
    }

    [Fact]
    public async Task Renovar_con_la_gestion_anterior_cerrada_no_la_sustituye()
    {
        var acceso = AccesoPlataforma(Centro("Planta Norte"), "Nalanda");
        var anterior = DocumentoDelTrabajador();
        Acreditar(anterior, acceso).MarcarNoRequerida();
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();

        var nuevo = RenovacionDe(anterior);
        Acreditar(nuevo, acceso);
        await Nacer(nuevo, anterior);

        gestionAnterior.EstaSustituida.Should().BeFalse();
        _gestiones.Lista.Should().HaveCount(2);
    }

    [Fact]
    public async Task Renovar_sin_pasos_nuevos_cierra_igualmente_la_anterior_abierta_sin_enlace()
    {
        var anterior = DocumentoDelTrabajador();
        Acreditar(anterior, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(anterior);

        var nuevo = RenovacionDe(anterior);
        await Nacer(nuevo, anterior);

        var gestionAnterior = _gestiones.Lista.Should().ContainSingle().Subject;
        gestionAnterior.EstaSustituida.Should().BeTrue();
        gestionAnterior.SustituidaPorGestionId.Should().BeNull();
    }

    // ---------- Universo visible ----------

    [Fact]
    public async Task Los_pasos_de_un_centro_fuera_de_cartera_ni_se_ven_ni_cuentan()
    {
        var documento = DocumentoDelTrabajador();
        var norte = Centro("Planta Norte");
        var sur = Centro("Almacén Sur");
        Acreditar(documento, AccesoPlataforma(norte, "Nalanda", "https://nalanda.example/norte", "Notas de Norte")).MarcarSubida();
        Acreditar(documento, AccesoPlataforma(sur, "Dokify", "https://dokify.example/sur", "Notas de Sur"));
        await Nacer(documento);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [norte.Id]);

        var listado = await Listar(alcance);
        var detalle = await Detalle(_gestiones.Lista.Single().Id, alcance);

        var fila = listado.Filas.Should().ContainSingle().Subject;
        fila.Avance.Should().Be(new AvanceGestionAutomatica(Pasos: 1, Hechos: 1, Validados: 0, PendientesDeValidar: 1));
        fila.Estado.Should().Be(EstadoGestionAutomatica.PendienteDeValidar);
        fila.Plataformas.Should().Equal("Nalanda");
        var paso = detalle!.Pasos.Should().ContainSingle().Subject;
        paso.CentroId.Should().Be(norte.Id);
        paso.ClienteNombre.Should().Be("Cliente de Planta Norte");
        paso.UrlAcceso.Should().Be("https://nalanda.example/norte");
        paso.Notas.Should().Be("Notas de Norte", "ni la URL, ni las notas, ni el Cliente empresarial del Centro ajeno salen");
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_ningun_centro_de_la_gestion_en_su_cartera_no_la_ve()
    {
        var documento = DocumentoDelTrabajador();
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(documento);
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [Guid.NewGuid()]);

        (await Listar(fueraDeCartera)).Filas.Should().BeEmpty();
        (await Detalle(_gestiones.Lista.Single().Id, fueraDeCartera)).Should().BeNull();
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_no_ve_al_Trabajador_titular_no_ve_su_gestion()
    {
        var documento = DocumentoDelTrabajador();
        var centro = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        await Nacer(documento);
        var sinElTrabajador = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()],
            centroIdsVisibles: [centro.Id]);

        (await Listar(sinElTrabajador)).Filas.Should().BeEmpty();
        (await Detalle(_gestiones.Lista.Single().Id, sinElTrabajador)).Should().BeNull();
    }

    [Fact]
    public async Task La_gestion_de_un_documento_de_Empresa_se_ve_por_la_Empresa_gestionada_y_no_fuera_de_ella()
    {
        var documento = Documento.DeEmpresa(_empresa.Id, _tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)), "tenant/escrituras.pdf");
        _documentos.ListaDocumentos.Add(documento);
        var centro = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        await Nacer(documento);

        var conLaEmpresa = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [centro.Id]);
        var sinLaEmpresa = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [Guid.NewGuid()], centroIdsVisibles: [centro.Id]);

        var fila = (await Listar(conLaEmpresa)).Filas.Should().ContainSingle().Subject;
        fila.TitularNombre.Should().Be("Construcciones Ebro S.L.");
        fila.EmpresaGestionadaId.Should().Be(_empresa.Id);
        (await Detalle(fila.Id, conLaEmpresa))!.Documento.IdentificadorTitular.Should().Be("B87654323");
        (await Listar(sinLaEmpresa)).Filas.Should().BeEmpty();
        (await Detalle(fila.Id, sinLaEmpresa)).Should().BeNull("ni el CIF ni la Empresa salen por el detalle");
    }

    [Fact]
    public async Task El_usuario_de_portal_del_Cliente_no_ve_ninguna_gestion()
    {
        var documento = DocumentoDelTrabajador();
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(documento);

        (await Listar(new AlcanceDatosServiceFalso(ladoDeGestion: false))).Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Los_pasos_se_ordenan_por_plataforma_y_despues_por_centro()
    {
        var documento = DocumentoDelTrabajador();
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        Acreditar(documento, AccesoPlataforma(Centro("Almacén Sur"), "Nalanda"));
        Acreditar(documento, AccesoPlataforma(Centro("Base Este"), "Dokify"));
        await Nacer(documento);

        var detalle = await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso());

        detalle!.Pasos.Select(p => $"{p.Plataforma} · {p.CentroNombre}").Should().Equal(
            "Dokify · Base Este", "Nalanda · Almacén Sur", "Nalanda · Planta Norte");
        detalle.Resumen.TitularNombre.Should().Be("Laura Gil");
        detalle.Resumen.EmpresaGestionadaNombre.Should().Be("Construcciones Ebro S.L.");
        detalle.Documento.IdentificadorTitular.Should().Be("77189989B");
    }

    [Fact]
    public async Task Los_recuentos_por_estado_salen_del_mismo_universo_que_las_filas()
    {
        var abierta = DocumentoDelTrabajador();
        Acreditar(abierta, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(abierta);
        var cerrada = DocumentoDelTrabajador();
        Acreditar(cerrada, AccesoPlataforma(Centro("Almacén Sur"), "Dokify")).MarcarNoRequerida();
        await Nacer(cerrada);

        var todas = await Listar(new AlcanceDatosServiceFalso());
        var enCurso = await Listar(new AlcanceDatosServiceFalso(), EstadoGestionAutomatica.EnCurso);

        todas.Filas.Should().HaveCount(2);
        todas.RecuentosPorEstado[EstadoGestionAutomatica.EnCurso].Should().Be(1);
        todas.RecuentosPorEstado[EstadoGestionAutomatica.Cerrada].Should().Be(1);
        enCurso.Filas.Select(f => f.DocumentoId).Should().Equal(abierta.Id);
        enCurso.RecuentosPorEstado.Should().BeEquivalentTo(todas.RecuentosPorEstado);
    }

    [Fact]
    public async Task Un_rechazo_posterior_reabre_la_gestion_cerrada()
    {
        var documento = DocumentoDelTrabajador();
        var acreditacion = Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        acreditacion.MarcarSubida();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        await Nacer(documento);
        (await Listar(new AlcanceDatosServiceFalso())).Filas.Single().Estado.Should().Be(EstadoGestionAutomatica.Cerrada);

        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Documento ilegible", Ahora);

        var fila = (await Listar(new AlcanceDatosServiceFalso())).Filas.Single();
        fila.Estado.Should().Be(EstadoGestionAutomatica.EnCurso);
        fila.TieneRechazo.Should().BeTrue();
    }

    // ---------- Documento ya sustituido (hallazgos 1 y 2 de la segunda ronda de Codex sobre la PR #2072) ----------

    [Fact]
    public async Task Un_rechazo_confirmado_despues_de_que_la_renovacion_leyera_los_pasos_hechos_no_deja_dos_gestiones_abiertas()
    {
        // La carrera, en el orden en que se confirma: la renovación lee la Gestión anterior Cerrada y no la marca; otro
        // Gestor CAE rechaza esa acreditación antes del commit (no toca el Documento, así que nada lo invalida).
        var acceso = AccesoPlataforma(Centro("Planta Norte"), "Nalanda");
        var anterior = DocumentoDelTrabajador();
        var acreditacionAnterior = Acreditar(anterior, acceso);
        acreditacionAnterior.MarcarSubida();
        acreditacionAnterior.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();
        var nuevo = RenovacionDe(anterior);
        Acreditar(nuevo, acceso);
        await Nacer(nuevo, anterior);
        gestionAnterior.EstaSustituida.Should().BeFalse("control: la renovación decidió con los pasos todos hechos");
        var gestionNueva = _gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);

        acreditacionAnterior.Rechazar(CausaRechazoAcreditacion.Ilegible, "Documento ilegible", Ahora.AddMinutes(1));

        var listado = await Listar(new AlcanceDatosServiceFalso());
        listado.Filas.Single(f => f.Id == gestionAnterior.Id).Estado.Should().Be(EstadoGestionAutomatica.Sustituida,
            "su Documento ya es historial: reabrirla daría dos Gestiones En curso del mismo titular y tipo");
        listado.RecuentosPorEstado[EstadoGestionAutomatica.EnCurso].Should().Be(1, "solo la de la renovación");
        listado.Filas.Single(f => f.Id == gestionNueva.Id).Estado.Should().Be(EstadoGestionAutomatica.EnCurso);
        var detalle = await Detalle(gestionAnterior.Id, new AlcanceDatosServiceFalso());
        detalle!.SustituidaEnUtc.Should().Be(Ahora, "sin marca, el instante es el de la sustitución del Documento");
        detalle.SustituidaPorGestionId.Should().Be(gestionNueva.Id, "y el enlace, la Gestión del Documento sustituto");
    }

    [Fact]
    public async Task Un_paso_cerrado_despues_de_que_la_renovacion_la_marcara_sustituida_no_la_reabre_ni_la_cierra()
    {
        // La carrera inversa: la renovación la lee abierta y la marca; el último paso se valida antes del commit. El
        // resultado es el de confirmar primero la renovación y después la validación, un orden posible (ningún comando de
        // acreditación impide actuar sobre las de un Documento histórico): Sustituida. Ya pasaba antes de esta ronda; fija
        // que la marca no la pisan los pasos, no la derivación nueva.
        var acceso = AccesoPlataforma(Centro("Planta Norte"), "Nalanda");
        var anterior = DocumentoDelTrabajador();
        var acreditacionAnterior = Acreditar(anterior, acceso);
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();
        var nuevo = RenovacionDe(anterior);
        Acreditar(nuevo, acceso);
        await Nacer(nuevo, anterior);

        acreditacionAnterior.MarcarSubida();
        acreditacionAnterior.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);

        var listado = await Listar(new AlcanceDatosServiceFalso());
        listado.Filas.Single(f => f.Id == gestionAnterior.Id).Estado.Should().Be(EstadoGestionAutomatica.Sustituida);
        listado.RecuentosPorEstado[EstadoGestionAutomatica.EnCurso].Should().Be(1);
    }

    [Fact]
    public async Task La_renovacion_de_la_ranura_anterior_de_un_relevo_no_deja_abierta_la_gestion_del_documento_historico()
    {
        // La ranura anterior no conoce las Gestiones: sustituye el Documento sin marcar la Gestión ni hacer nacer otra.
        var anterior = DocumentoDelTrabajador();
        Acreditar(anterior, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();
        (await Listar(new AlcanceDatosServiceFalso())).Filas.Single().Estado.Should().Be(EstadoGestionAutomatica.EnCurso,
            "control: antes de la renovación está abierta");

        RenovacionDe(anterior);

        var todas = await Listar(new AlcanceDatosServiceFalso());
        var enCurso = await Listar(new AlcanceDatosServiceFalso(), EstadoGestionAutomatica.EnCurso);
        gestionAnterior.EstaSustituida.Should().BeFalse("control: nadie la marcó");
        todas.Filas.Single().Estado.Should().Be(EstadoGestionAutomatica.Sustituida);
        todas.RecuentosPorEstado[EstadoGestionAutomatica.EnCurso].Should().Be(0);
        todas.RecuentosPorEstado[EstadoGestionAutomatica.Sustituida].Should().Be(1);
        enCurso.Filas.Should().BeEmpty();
        var detalle = await Detalle(gestionAnterior.Id, new AlcanceDatosServiceFalso());
        detalle!.Resumen.Estado.Should().Be(EstadoGestionAutomatica.Sustituida);
        detalle.SustituidaEnUtc.Should().Be(Ahora);
        detalle.SustituidaPorGestionId.Should().BeNull("la ranura anterior no hizo nacer ninguna");
    }

    [Fact]
    public async Task El_detalle_no_revela_la_gestion_sustituta_si_queda_fuera_de_la_cartera()
    {
        // Hallazgo 10 de la primera ronda de Codex: el enlace a la sustituta solo sale si quien consulta la ve.
        var norte = Centro("Planta Norte");
        var sur = Centro("Almacén Sur");
        var anterior = DocumentoDelTrabajador();
        Acreditar(anterior, AccesoPlataforma(norte, "Nalanda"));
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();
        var nuevo = RenovacionDe(anterior);
        Acreditar(nuevo, AccesoPlataforma(sur, "Dokify"));
        await Nacer(nuevo, anterior);
        var gestionNueva = _gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);
        var soloNorte = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [norte.Id]);

        var fueraDeCartera = await Detalle(gestionAnterior.Id, soloNorte);
        var conAccesoTotal = await Detalle(gestionAnterior.Id, new AlcanceDatosServiceFalso());

        fueraDeCartera!.SustituidaEnUtc.Should().Be(Ahora, "la sustitución en sí sí se ve: es de la Gestión visible");
        fueraDeCartera.SustituidaPorGestionId.Should().BeNull();
        conAccesoTotal!.SustituidaPorGestionId.Should().Be(gestionNueva.Id, "control positivo: quien la ve recibe el enlace");
    }

    [Fact]
    public async Task El_detalle_no_revela_la_sustituta_derivada_sin_marca_si_queda_fuera_de_la_cartera()
    {
        // BAJO de la sexta ronda de Codex: la sustituta que la lectura deriva del Documento (la Gestión anterior no se
        // marcó) pasa por el mismo filtro de cartera que la marcada.
        var norte = Centro("Planta Norte");
        var sur = Centro("Almacén Sur");
        var anterior = DocumentoDelTrabajador();
        var acreditacionAnterior = Acreditar(anterior, AccesoPlataforma(norte, "Nalanda"));
        acreditacionAnterior.MarcarSubida();
        acreditacionAnterior.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        await Nacer(anterior);
        var gestionAnterior = _gestiones.Lista.Single();
        var nuevo = RenovacionDe(anterior);
        Acreditar(nuevo, AccesoPlataforma(sur, "Dokify"));
        await Nacer(nuevo, anterior);
        gestionAnterior.EstaSustituida.Should().BeFalse("control: la renovación la leyó cerrada y no la marcó");
        acreditacionAnterior.Rechazar(CausaRechazoAcreditacion.Ilegible, "Documento ilegible", Ahora.AddMinutes(1));
        var gestionNueva = _gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);
        var soloNorte = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [norte.Id]);

        var fueraDeCartera = await Detalle(gestionAnterior.Id, soloNorte);
        var conAccesoTotal = await Detalle(gestionAnterior.Id, new AlcanceDatosServiceFalso());

        fueraDeCartera!.Resumen.Estado.Should().Be(EstadoGestionAutomatica.Sustituida, "control: es la sustitución derivada");
        fueraDeCartera.SustituidaEnUtc.Should().Be(Ahora, "el instante sale del Documento, que es de la Gestión visible");
        fueraDeCartera.SustituidaPorGestionId.Should().BeNull("la sustituta tiene todos sus pasos fuera de la cartera");
        conAccesoTotal!.SustituidaPorGestionId.Should().Be(gestionNueva.Id, "control positivo: quien la ve recibe el enlace");
    }

    // ---------- Empresa gestionada de un Documento de Trabajador (MEDIOS de la sexta ronda de Codex) ----------

    [Fact]
    public async Task Un_Gestor_CAE_que_ve_al_Trabajador_pero_no_a_su_Empresa_empleadora_ve_la_gestion_sin_esa_Empresa()
    {
        var documento = DocumentoDelTrabajador();
        var centro = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        await Nacer(documento);
        var sinLaEmpresa = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [Guid.NewGuid()], centroIdsVisibles: [centro.Id]);
        var conLaEmpresa = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [centro.Id]);

        var fila = (await Listar(sinLaEmpresa)).Filas.Should().ContainSingle("la Gestión es del Trabajador y del Centro visibles").Subject;
        var detalle = await Detalle(_gestiones.Lista.Single().Id, sinLaEmpresa);
        var filaConEmpresa = (await Listar(conLaEmpresa)).Filas.Single();
        // La regla son tres alcances: la Empresa visible solo como Cliente empresarial también se nombra.
        var comoClienteEmpresarial = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [Guid.NewGuid()], clienteIdsVisibles: [_empresa.Id], centroIdsVisibles: [centro.Id]);
        var filaComoCliente = (await Listar(comoClienteEmpresarial)).Filas.Single();

        fila.TitularNombre.Should().Be("Laura Gil");
        fila.EmpresaGestionadaId.Should().BeNull("ver al Trabajador no da a ver a quién emplea");
        fila.EmpresaGestionadaNombre.Should().BeNull();
        detalle!.Resumen.EmpresaGestionadaId.Should().BeNull();
        detalle.Resumen.EmpresaGestionadaNombre.Should().BeNull();
        detalle.Documento.EmpresaId.Should().BeNull();
        filaConEmpresa.EmpresaGestionadaId.Should().Be(_empresa.Id, "control positivo: con la Empresa en cartera sí se nombra");
        filaConEmpresa.EmpresaGestionadaNombre.Should().Be("Construcciones Ebro S.L.");
        filaComoCliente.EmpresaGestionadaId.Should().Be(_empresa.Id, "visible como Cliente empresarial");
        filaComoCliente.EmpresaGestionadaNombre.Should().Be("Construcciones Ebro S.L.");
    }

    [Fact]
    public async Task Una_Empresa_empleadora_que_la_consulta_no_encuentra_no_da_un_id_sin_nombre()
    {
        // Revisión puente de la sexta ronda: como el Mes, id y nombre van juntos. Con acceso total, una Empresa que el
        // filtro global oculta (dada de baja) no deja un EmpresaGestionadaId sin rótulo.
        var trabajador = Trabajador.DeSubcontrata(Guid.NewGuid(), "Pedro", "Ruiz", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)), "tenant/rm.pdf");
        _documentos.ListaDocumentos.Add(documento);
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(documento);

        var fila = (await Listar(new AlcanceDatosServiceFalso())).Filas.Should().ContainSingle().Subject;

        fila.TitularNombre.Should().Be("Pedro Ruiz");
        fila.EmpresaGestionadaId.Should().BeNull();
        fila.EmpresaGestionadaNombre.Should().BeNull();
    }

    [Fact]
    public async Task La_gestion_de_un_Trabajador_de_subcontrata_lleva_su_Empresa_empleadora_solo_si_esta_en_cartera()
    {
        var empleadora = new Empresa("Montajes Duero S.L.");
        _empresas.ListaEmpresas.Add(empleadora);
        var trabajador = Trabajador.DeSubcontrata(empleadora.Id, "Pedro", "Ruiz", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)), "tenant/rm.pdf");
        _documentos.ListaDocumentos.Add(documento);
        var centro = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        await Nacer(documento);
        var conLaSubcontrata = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador.Id],
            subcontrataIdsVisibles: [empleadora.Id], centroIdsVisibles: [centro.Id]);
        var sinLaSubcontrata = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador.Id],
            subcontrataIdsVisibles: [Guid.NewGuid()], centroIdsVisibles: [centro.Id]);

        var fila = (await Listar(conLaSubcontrata)).Filas.Should().ContainSingle().Subject;
        var detalle = await Detalle(_gestiones.Lista.Single().Id, conLaSubcontrata);
        var filaSinEmpresa = (await Listar(sinLaSubcontrata)).Filas.Single();

        fila.TitularNombre.Should().Be("Pedro Ruiz");
        fila.EmpresaGestionadaId.Should().Be(empleadora.Id, "la Empresa empleadora es SubcontrataId cuando EmpresaId es nulo");
        fila.EmpresaGestionadaNombre.Should().Be("Montajes Duero S.L.");
        detalle!.Resumen.EmpresaGestionadaId.Should().Be(empleadora.Id);
        filaSinEmpresa.EmpresaGestionadaId.Should().BeNull("la misma regla de cartera que con EmpresaId");
        filaSinEmpresa.EmpresaGestionadaNombre.Should().BeNull();
    }

    // ---------- Campos del DTO de detalle (ronda 7 de Codex sobre la PR #2072) ----------

    [Fact]
    public async Task El_detalle_devuelve_el_Usuario_que_hizo_nacer_la_gestion()
    {
        // Hasta la ronda 6 solo se afirmaba NacidaPorUsuarioId en la entidad al nacer; el DTO podía devolver null sin
        // que ningún test lo notara.
        var documento = DocumentoDelTrabajador();
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(documento);

        var detalle = await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso());

        detalle!.NacidaPorUsuarioId.Should().Be(_usuarioId);
    }

    [Fact]
    public async Task El_detalle_marca_en_cada_paso_si_el_canal_tiene_Usuario_guardado()
    {
        // TieneUsuario es el único dato de la credencial que sale por el detalle (nunca el Usuario ni la contraseña): el
        // caso verdadero y el falso en la misma Gestión impiden que invertirlo, o fijarlo, pase inadvertido.
        var documento = DocumentoDelTrabajador();
        var conUsuario = Centro("Planta Norte");
        var sinUsuario = Centro("Almacén Sur");
        Acreditar(documento, AccesoPlataforma(conUsuario, "Nalanda", usuario: "gestor@ebro.example"));
        Acreditar(documento, AccesoPlataforma(sinUsuario, "Dokify"));
        await Nacer(documento);

        var detalle = await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso());

        detalle!.Pasos.Single(p => p.CentroId == conUsuario.Id).TieneUsuario.Should().BeTrue();
        detalle.Pasos.Single(p => p.CentroId == sinUsuario.Id).TieneUsuario.Should().BeFalse();
    }

    // ---------- CIF del empleador y «Datos a mano» (incremento 1 de Gestiones) ----------

    [Fact]
    public async Task El_detalle_lleva_el_CIF_de_la_Empresa_gestionada_solo_si_la_cartera_deja_nombrarla()
    {
        var documento = DocumentoDelTrabajador();
        var centro = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        await Nacer(documento);
        var id = _gestiones.Lista.Single().Id;
        var conLaEmpresa = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [centro.Id]);
        var sinLaEmpresa = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [Guid.NewGuid()], centroIdsVisibles: [centro.Id]);

        var con = await Detalle(id, conLaEmpresa);
        var sin = await Detalle(id, sinLaEmpresa);

        con!.EmpresaGestionadaCif.Should().Be("B87654323");
        sin!.Resumen.EmpresaGestionadaNombre.Should().BeNull("precondición: la cartera no deja nombrarla");
        sin.EmpresaGestionadaCif.Should().BeNull("el CIF va con el nombre: lo que no se nombra no se identifica");
    }

    [Fact]
    public async Task El_detalle_lleva_los_datos_a_mano_del_Trabajador_titular_tal_como_estan_en_su_ficha()
    {
        _trabajador.ActualizarFicha(
            "501234567890", "Técnica de mantenimiento", new DateOnly(1988, 2, 21), "Española", "Indefinido",
            new DateOnly(2019, 3, 1), null, "Sí", null, null, null);
        var documento = DocumentoDelTrabajador();
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(documento);

        var datos = (await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso()))!.Trabajador;

        datos.Should().NotBeNull();
        datos!.Id.Should().Be(_trabajador.Id);
        datos.NombreCompleto.Should().Be("Laura Gil");
        datos.Dni.Should().Be("77189989B");
        datos.FechaNacimiento.Should().Be(new DateOnly(1988, 2, 21));
        datos.NumeroAfiliacionSeguridadSocial.Should().Be("501234567890");
        datos.Nacionalidad.Should().Be("Española");
        datos.Puesto.Should().Be("Técnica de mantenimiento");
        datos.TipoVinculo.Should().Be("Indefinido");
        datos.FechaAltaEmpresa.Should().Be(new DateOnly(2019, 3, 1));
        datos.FiguraEnRnt.Should().Be("Sí");
        datos.TipoPermisoTrabajo.Should().BeNull("un dato vacío llega null: la pantalla dice «Falta en TALVEG»");
        datos.PermisoTrabajoVisible.Should().BeTrue("quien consulta lee la nota interna");
        datos.FechaBajaEmpresa.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_tipo_de_permiso_de_trabajo_solo_sale_a_quien_lee_la_nota_interna_como_en_la_ficha(bool leeNotaInterna)
    {
        // La misma regla que ObtenerTrabajadorPorIdQueryHandler (decisión de Chris del 2026-10-15): el resto de datos sí
        // llega, y el permiso solo con la política.
        _trabajador.ActualizarFicha(null, null, null, null, null, null, null, null, "Residencia y trabajo", null, null);
        _trabajador.TipoPermisoTrabajo.Should().Be("Residencia y trabajo", "precondición: la ficha tiene el permiso");
        var documento = DocumentoDelTrabajador();
        Acreditar(documento, AccesoPlataforma(Centro("Planta Norte"), "Nalanda"));
        await Nacer(documento);

        var datos = (await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso(), leeNotaInterna))!.Trabajador!;

        datos.Dni.Should().Be("77189989B", "los datos que no lleva la política sí llegan");
        datos.PermisoTrabajoVisible.Should().Be(leeNotaInterna);
        datos.TipoPermisoTrabajo.Should().Be(leeNotaInterna ? "Residencia y trabajo" : null);
    }

    [Fact]
    public async Task Los_datos_del_Trabajador_no_salen_si_quien_consulta_no_ve_a_ese_Trabajador()
    {
        // La pestaña Trabajador no tiene guarda propia: la da el universo de la Gestión, que exige ver al Trabajador.
        var documento = DocumentoDelTrabajador();
        var centro = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(centro, "Nalanda"));
        await Nacer(documento);
        var id = _gestiones.Lista.Single().Id;
        var loVe = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [_trabajador.Id]);
        var noLoVe = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [Guid.NewGuid()]);

        (await Detalle(id, loVe))!.Trabajador.Should().NotBeNull("control positivo");
        (await Detalle(id, noLoVe)).Should().BeNull("sin ver al Trabajador no se ve la Gestión, ni por tanto sus datos");
    }

    [Fact]
    public async Task Cada_paso_lleva_codigo_direccion_del_Centro_y_el_inicio_de_la_Asignacion_activa_del_titular()
    {
        var documento = DocumentoDelTrabajador();
        var planta = Centro("Planta Norte");
        planta.Actualizar(planta.Nombre, "PN-01", "Polígono Malpica, calle E, 12 · 50016 Zaragoza", null, null);
        var almacen = Centro("Almacén Sur");
        Acreditar(documento, AccesoPlataforma(planta, "Nalanda"));
        Acreditar(documento, AccesoPlataforma(almacen, "Nalanda"));
        await Nacer(documento);
        var antigua = new Asignacion(_trabajador.Id, planta.Id, new DateOnly(2024, 1, 10));
        antigua.DarDeBaja(new DateOnly(2024, 6, 30));
        _asignaciones.ListaAsignaciones.Add(antigua);
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_trabajador.Id, planta.Id, new DateOnly(2023, 5, 2)));
        _asignaciones.ListaAsignaciones.Add(new Asignacion(Guid.NewGuid(), almacen.Id, new DateOnly(2025, 9, 1)));

        var pasos = (await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso()))!.Pasos;

        var enPlanta = pasos.Single(p => p.CentroId == planta.Id);
        enPlanta.CentroCodigo.Should().Be("PN-01");
        enPlanta.CentroDireccion.Should().Be("Polígono Malpica, calle E, 12 · 50016 Zaragoza");
        enPlanta.InicioAsignacion.Should().Be(new DateOnly(2023, 5, 2), "la activa manda sobre una de baja más reciente");
        var enAlmacen = pasos.Single(p => p.CentroId == almacen.Id);
        enAlmacen.CentroCodigo.Should().BeNull();
        enAlmacen.InicioAsignacion.Should().BeNull("la Asignación de otro Trabajador no es la del titular");
    }

    [Fact]
    public async Task Sin_Asignacion_activa_el_inicio_es_el_de_la_mas_reciente_de_las_de_baja()
    {
        // Desempate «ninguna activa, gana la más reciente por FechaAlta»: la más antigua va primero en la lista, para que
        // quedarse con la primera, o con la de baja más tardía, no pase inadvertido.
        var documento = DocumentoDelTrabajador();
        var planta = Centro("Planta Norte");
        Acreditar(documento, AccesoPlataforma(planta, "Nalanda"));
        await Nacer(documento);
        var antigua = new Asignacion(_trabajador.Id, planta.Id, new DateOnly(2022, 3, 1));
        antigua.DarDeBaja(new DateOnly(2025, 12, 31));
        var reciente = new Asignacion(_trabajador.Id, planta.Id, new DateOnly(2024, 2, 5));
        reciente.DarDeBaja(new DateOnly(2024, 8, 31));
        _asignaciones.ListaAsignaciones.Add(antigua);
        _asignaciones.ListaAsignaciones.Add(reciente);

        var paso = (await Detalle(_gestiones.Lista.Single().Id, new AlcanceDatosServiceFalso()))!.Pasos.Single();

        paso.InicioAsignacion.Should().Be(new DateOnly(2024, 2, 5));
    }

    // ---------- Montaje ----------

    private async Task<GestionAutomatica?> Nacer(Documento documento, Documento? sustituido = null)
    {
        var gestion = await new NacimientoGestionAutomaticaService(
                _gestiones, _delActo, _documentos, _centros, new CurrentUserServiceFalso(_usuarioId, "GestorCae"), new RelojFijo(Ahora))
            .AgregarAsync(documento, sustituido, CancellationToken.None);
        // Como el guardado del Command: lo agregado en este acto ya no es del siguiente.
        _delActo.MarcarGuardadas();
        return gestion;
    }

    private Task<ListadoGestionesAutomaticasDto> Listar(AlcanceDatosServiceFalso alcance, EstadoGestionAutomatica? estado = null) =>
        new ObtenerGestionesAutomaticasQueryHandler(
                _gestiones, _documentos, _centros, _proveedores, _trabajadores, _empresas, _tipos, alcance)
            .Handle(new ObtenerGestionesAutomaticasQuery(estado), CancellationToken.None);

    private Task<GestionAutomaticaDetalleDto?> Detalle(Guid id, AlcanceDatosServiceFalso alcance, bool leeNotaInterna = true) =>
        new ObtenerGestionAutomaticaQueryHandler(
                _gestiones, _documentos, _centros, _proveedores, _trabajadores, _empresas, _tipos, _asignaciones, alcance,
                new PoliticaNotaInternaFalsa(leeNotaInterna))
            .Handle(new ObtenerGestionAutomaticaQuery(id), CancellationToken.None);

    private Documento DocumentoDelTrabajador(string? archivoUrl = "tenant/reconocimiento.pdf")
    {
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)), archivoUrl);
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private Documento RenovacionDe(Documento anterior)
    {
        var nuevo = DocumentoDelTrabajador("tenant/reconocimiento-renovado.pdf");
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora);
        return nuevo;
    }

    private Centro Centro(string nombre)
    {
        var cliente = new Empresa($"Cliente de {nombre}");
        _empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, _empresa.Id, nombre);
        _centros.ListaCentros.Add(centro);
        return centro;
    }

    private CanalGestionDocumental AccesoPlataforma(
        Centro centro, string plataforma, string? urlAcceso = null, string? notas = null, string? usuario = null)
    {
        var proveedor = _proveedores.ListaProveedores.FirstOrDefault(p => p.Nombre == plataforma)
                        ?? new ProveedorPlataformaCae(plataforma.ToLowerInvariant(), plataforma);
        if (!_proveedores.ListaProveedores.Contains(proveedor))
            _proveedores.ListaProveedores.Add(proveedor);
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedor.Id, urlAcceso, usuario, null, notas);
        _centros.ListaCanalesGestionDocumental.Add(canal);
        return canal;
    }

    /// <summary>
    /// Una acreditación que el acto en curso acaba de agregar (sin guardar) y que, para las consultas, ya está en la
    /// base: el nacimiento la lee de lo agregado y las consultas del contexto de consulta.
    /// </summary>
    private AcreditacionDocumentoPlataforma Acreditar(Documento documento, CanalGestionDocumental canal)
    {
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        _delActo.Agregar(acreditacion);
        _documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        return acreditacion;
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }
}

/// <summary>Las Gestiones automáticas guardadas, para el repositorio y el contexto de consulta a la vez.</summary>
internal sealed class GestionesAutomaticasEnMemoria : IGestionesQueryContext, IGestionAutomaticaRepository
{
    public List<GestionAutomatica> Lista { get; } = [];

    public IQueryable<Gestion> Gestiones => new TestAsyncQueryable<Gestion>(Array.Empty<Gestion>().AsQueryable());
    public IQueryable<GestionAutomatica> GestionesAutomaticas => new TestAsyncQueryable<GestionAutomatica>(Lista.AsQueryable());
    public IQueryable<PasoGestionAutomatica> PasosGestionAutomatica =>
        new TestAsyncQueryable<PasoGestionAutomatica>(Lista.SelectMany(g => g.Pasos).AsQueryable());

    public Task<GestionAutomatica?> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lista.FirstOrDefault(g => g.DocumentoId == documentoId));

    public void Agregar(GestionAutomatica gestion) => Lista.Add(gestion);
}
