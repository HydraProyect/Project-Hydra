using CaeManager.Application.Auditoria;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// La conciliación de Gestiones automáticas (ALTO abierto de las rondas 3 y 4 de Codex sobre la PR #2072): los actos con
/// archivo que la ranura anterior atendió sin Gestión durante el relevo. Con el servicio de nacimiento REAL, para que
/// la regla de qué es un paso y de cuándo una renovación cierra la Gestión sea la misma que en el acto. El aislamiento
/// entre Tenants, la auditoría y el índice único reales se miden bajo <c>cae_app_runtime</c> en
/// <c>ConciliacionGestionesAutomaticasBajoRuntimeTests</c>.
/// </summary>
public class ConciliacionGestionesAutomaticasServiceTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    private readonly MundoAcreditaciones _mundo = new();
    private readonly UnitOfWorkFalso _guardado = new();
    private readonly AuditoriaFalsa _auditoria = new();
    private readonly DateTime _haceUnaHora = DateTime.UtcNow.AddHours(-1);

    [Fact]
    public async Task Crea_la_gestion_del_alta_con_archivo_que_quedo_sin_ella_con_sus_pasos_de_plataforma()
    {
        var documento = DocumentoConArchivo();
        var acreditacion = AcreditacionAntePlataforma(documento);
        var correo = AcreditacionAnteCorreo(documento);
        AltaAuditada(documento);

        var sinGestion = await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora);
        sinGestion.Should().Equal(documento.Id);

        var fotos = new List<IReadOnlyList<GestionAutomatica>>();
        _guardado.AlGuardar = () => fotos.Add(_mundo.Gestiones.Lista.ToList());
        (await Servicio().ConciliarLoteAsync(sinGestion)).Should().Be(1);

        var gestion = fotos.Should().ContainSingle("todo el lote se confirma en un solo guardado").Subject
            .Should().ContainSingle().Subject;
        gestion.DocumentoId.Should().Be(documento.Id);
        gestion.Origen.Should().Be(OrigenGestionAutomatica.AltaDocumento);
        gestion.NacidaPorUsuarioId.Should().BeNull("quien hizo el acto no queda en el Documento");
        gestion.EstaSustituida.Should().BeFalse();
        gestion.Pasos.Select(p => p.AcreditacionId).Should().Equal([acreditacion.Id],
            "la acreditación ante el acceso de correo no da paso, igual que en el acto");
        correo.Should().NotBeNull();
    }

    [Fact]
    public async Task Repetirla_no_duplica_ni_vuelve_a_guardar()
    {
        var (documento, _) = ActoSinGestion();
        await Servicio().ConciliarLoteAsync([documento.Id]);

        (await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora)).Should().BeEmpty("ya tiene su Gestión");
        (await Servicio().ConciliarLoteAsync([documento.Id])).Should().Be(0,
            "el lote vuelve a mirar qué sigue sin Gestión aunque la búsqueda fuera anterior");

        _mundo.Gestiones.Lista.Should().ContainSingle();
        _guardado.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Respeta_la_ventana_y_no_toca_el_historico_anterior()
    {
        var (documento, _) = ActoSinGestion();

        (await Servicio().BuscarDocumentosSinGestionAsync(documento.CreadoEnUtc.AddMinutes(1))).Should().BeEmpty(
            "creado antes del inicio de la ventana: es histórico");
        (await Servicio().BuscarDocumentosSinGestionAsync(documento.CreadoEnUtc)).Should().Equal([documento.Id],
            "control: el límite de la ventana es inclusivo");
    }

    [Fact]
    public async Task Busca_los_sustituidos_pero_no_los_eliminados_ni_los_sin_archivo_y_los_mas_recientes_primero()
    {
        var (sinArchivo, _) = ActoSinGestion(archivo: null);
        var (sustituido, _) = ActoSinGestion(creadoHace: TimeSpan.FromMinutes(30));
        // El sustituto no se agrega al mundo: aquí solo se mide que el sustituido sale.
        var sustituto = sustituido.NuevoDelMismoTitular(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(2)), "tenant/nuevo.pdf", null, null);
        sustituido.SustituirPor(sustituto, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var (eliminado, _) = ActoSinGestion();
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var (operativo, _) = ActoSinGestion(creadoHace: TimeSpan.FromMinutes(10));

        (await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora)).Should().Equal([operativo.Id, sustituido.Id],
            "el sustituido también: su Gestión histórica habría nacido en el acto; el más reciente primero, para que el " +
            "sucesor tenga su Gestión antes que el anterior la enlace");
        sinArchivo.ArchivoUrl.Should().BeNull();
    }

    [Fact]
    public async Task El_lote_no_hace_nacer_la_de_un_Documento_eliminado_entre_la_busqueda_y_el_lote()
    {
        var (eliminado, _) = ActoSinGestion();
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var (operativo, _) = ActoSinGestion();

        (await Servicio().ConciliarLoteAsync([eliminado.Id, operativo.Id])).Should().Be(1,
            "solo nace la del Documento no eliminado; el otro, control negativo en el mismo lote");
        _mundo.Gestiones.Lista.Should().ContainSingle().Which.DocumentoId.Should().Be(operativo.Id);
    }

    [Fact]
    public async Task Un_Documento_renovado_antes_de_conciliar_hace_nacer_su_Gestion_historica_ya_Sustituida_y_enlazada()
    {
        // Hallazgo 1 de la ronda 4: la ranura anterior crea el Documento y lo renueva antes de la conciliación.
        var (anterior, acreditacionAnterior) = ActoSinGestion(creadoHace: TimeSpan.FromMinutes(20));
        var nuevo = DocumentoDelMismoTitular(anterior);
        var renovadoEn = DateTime.UtcNow.AddMinutes(-5);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, renovadoEn);
        var acreditacionNueva = AcreditacionAntePlataforma(nuevo);
        AltaAuditada(nuevo);

        var sinGestion = await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora);
        sinGestion.Should().Equal([nuevo.Id, anterior.Id]);
        (await Servicio().ConciliarLoteAsync(sinGestion)).Should().Be(2);

        var gestionNueva = _mundo.Gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);
        gestionNueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        gestionNueva.Pasos.Select(p => p.AcreditacionId).Should().Equal(acreditacionNueva.Id);
        gestionNueva.EstaSustituida.Should().BeFalse("el sucesor sigue operativo");

        var historica = _mundo.Gestiones.Lista.Single(g => g.DocumentoId == anterior.Id);
        historica.Pasos.Select(p => p.AcreditacionId).Should().Equal(acreditacionAnterior.Id);
        historica.EstaSustituida.Should().BeTrue("la renovación la habría cerrado abierta (§ 4.1 regla 4)");
        historica.SustituidaEnUtc.Should().Be(renovadoEn, "el instante de la renovación, no el de la conciliación");
        historica.SustituidaPorGestionId.Should().Be(gestionNueva.Id, "enlace a la Gestión del sucesor nacida en el mismo lote");
    }

    [Fact]
    public async Task La_Gestion_historica_enlaza_con_la_ya_guardada_del_sucesor_en_otro_lote()
    {
        var (anterior, _) = ActoSinGestion(creadoHace: TimeSpan.FromMinutes(20));
        var nuevo = DocumentoDelMismoTitular(anterior);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow.AddMinutes(-5));
        AcreditacionAntePlataforma(nuevo);
        AltaAuditada(nuevo);
        await Servicio().ConciliarLoteAsync([nuevo.Id]);
        var gestionNueva = _mundo.Gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);

        (await Servicio().ConciliarLoteAsync([anterior.Id])).Should().Be(1);

        _mundo.Gestiones.Lista.Single(g => g.DocumentoId == anterior.Id)
            .SustituidaPorGestionId.Should().Be(gestionNueva.Id);
    }

    [Fact]
    public async Task La_Gestion_historica_ya_Cerrada_no_se_marca_Sustituida_igual_que_en_la_renovacion()
    {
        var (anterior, acreditacion) = ActoSinGestion(creadoHace: TimeSpan.FromMinutes(20));
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        var nuevo = DocumentoDelMismoTitular(anterior);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow.AddMinutes(-5));
        var (abierto, _) = ActoSinGestion(creadoHace: TimeSpan.FromMinutes(15));
        var sucesorDelAbierto = DocumentoDelMismoTitular(abierto);
        abierto.SustituirPor(sucesorDelAbierto, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow.AddMinutes(-4));

        (await Servicio().ConciliarLoteAsync([anterior.Id, abierto.Id])).Should().Be(2);

        _mundo.Gestiones.Lista.Single(g => g.DocumentoId == anterior.Id).EstaSustituida.Should().BeFalse(
            "todos sus pasos validados: Cerrada, y la renovación no cierra una Gestión cerrada");
        var delAbierto = _mundo.Gestiones.Lista.Single(g => g.DocumentoId == abierto.Id);
        delAbierto.EstaSustituida.Should().BeTrue("control: con un paso por hacer, la misma regla sí la cierra");
        delAbierto.SustituidaPorGestionId.Should().BeNull("su sucesor no tiene Gestión: sin pasos no nace");
    }

    [Fact]
    public async Task Una_acreditacion_creada_despues_del_alta_auditada_no_es_del_acto_aunque_este_a_milisegundos()
    {
        // Hallazgo 2 de la ronda 4: un acceso de Plataforma creado después da al Documento una acreditación que el
        // acto no hizo nacer. Ni hace nacer una Gestión que el acto no habría creado, ni entra como paso ajeno.
        var sinPasosAlNacer = DocumentoConArchivo();
        var alta = AltaAuditada(sinPasosAlNacer);
        Acreditacion(sinPasosAlNacer, _mundo.AccesoPlataforma(_mundo.Centro()), alta.AddMilliseconds(1));

        var conPaso = DocumentoConArchivo();
        var propia = AcreditacionAntePlataforma(conPaso);
        var altaConPaso = AltaAuditada(conPaso);
        Acreditacion(conPaso, _mundo.AccesoPlataforma(_mundo.Centro()), altaConPaso.AddMilliseconds(1));

        (await Servicio().ConciliarLoteAsync([sinPasosAlNacer.Id, conPaso.Id])).Should().Be(1);

        var gestion = _mundo.Gestiones.Lista.Should().ContainSingle(
            "el Documento sin pasos al nacer no adquiere Gestión por una acreditación posterior").Subject;
        gestion.DocumentoId.Should().Be(conPaso.Id);
        gestion.Pasos.Select(p => p.AcreditacionId).Should().Equal([propia.Id], "la acreditación posterior no es un paso del acto");
    }

    [Fact]
    public async Task Una_acreditacion_del_mismo_acto_cuenta_aunque_el_acto_tardara_mas_de_un_minuto()
    {
        var lento = DocumentoConArchivo();
        var acreditacion = Acreditacion(lento, _mundo.AccesoPlataforma(_mundo.Centro()), lento.CreadoEnUtc.AddMinutes(3));
        AltaAuditada(lento, lento.CreadoEnUtc.AddMinutes(3).AddMilliseconds(1));

        (await Servicio().ConciliarLoteAsync([lento.Id])).Should().Be(1);

        _mundo.Gestiones.Lista.Should().ContainSingle().Which.Pasos.Select(p => p.AcreditacionId)
            .Should().Equal([acreditacion.Id], "confirmada en el guardado del acto: es suya, sin tolerancia que la deje fuera");
    }

    [Fact]
    public async Task Un_Documento_sin_alta_auditada_no_se_concilia_ni_bloquea_a_los_demas()
    {
        var sinRastro = DocumentoConArchivo();
        AcreditacionAntePlataforma(sinRastro);
        // Registros que no son su alta: otra acción sobre el Documento y el alta de otra entidad con el mismo Id.
        _auditoria.Agregar(nameof(Documento), sinRastro.Id, "Modificado", sinRastro.CreadoEnUtc.AddMinutes(1));
        _auditoria.Agregar(nameof(AcreditacionDocumentoPlataforma), sinRastro.Id,
            ConciliacionGestionesAutomaticasService.AccionAlta, sinRastro.CreadoEnUtc.AddMinutes(1));
        var (conRastro, _) = ActoSinGestion();

        (await Servicio().ConciliarLoteAsync([sinRastro.Id, conRastro.Id])).Should().Be(1);

        _mundo.Gestiones.Lista.Should().ContainSingle(
            "sin el instante del alta no se puede separar su acto de los posteriores").Which.DocumentoId.Should().Be(conRastro.Id);
    }

    [Fact]
    public async Task El_alta_auditada_es_el_primer_Creado_del_Documento()
    {
        var documento = DocumentoConArchivo();
        var alta = AltaAuditada(documento);
        var posterior = Acreditacion(documento, _mundo.AccesoPlataforma(_mundo.Centro()), alta.AddSeconds(5));
        // Un segundo «Creado» del mismo Documento, más tardío (un reintento del guardado, una fila repetida).
        _auditoria.Agregar(nameof(Documento), documento.Id, ConciliacionGestionesAutomaticasService.AccionAlta, alta.AddSeconds(10));

        (await Servicio().ConciliarLoteAsync([documento.Id])).Should().Be(0,
            "el instante del alta es el primer «Creado» del Documento; los registros posteriores no lo mueven");
        posterior.Should().NotBeNull();
        _guardado.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_ninguna_gestion_que_nacer_no_guarda()
    {
        var soloCorreo = DocumentoConArchivo();
        AcreditacionAnteCorreo(soloCorreo);
        AltaAuditada(soloCorreo);

        (await Servicio().ConciliarLoteAsync([soloCorreo.Id])).Should().Be(0);

        _guardado.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_renovacion_sin_gestion_nace_como_renovacion_y_la_anterior_ya_guardada_no_se_toca()
    {
        var (anterior, acreditacionAnterior) = ActoSinGestion();
        var gestionAnterior = GestionAutomatica.Nacer(anterior.Id, OrigenGestionAutomatica.AltaDocumento, [acreditacionAnterior.Id], null)!;
        _mundo.Gestiones.Agregar(gestionAnterior);
        var nuevo = DocumentoDelMismoTitular(anterior);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var acreditacionNueva = AcreditacionAntePlataforma(nuevo);
        AltaAuditada(nuevo);

        var sinGestion = await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora);
        sinGestion.Should().Equal([nuevo.Id], "el anterior ya tiene Gestión");
        await Servicio().ConciliarLoteAsync(sinGestion);

        var nueva = _mundo.Gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);
        nueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        nueva.Pasos.Select(p => p.AcreditacionId).Should().Equal(acreditacionNueva.Id);
        gestionAnterior.EstaSustituida.Should().BeFalse("la conciliación nunca modifica una Gestión ya guardada");
        GestionAutomatica.ResolverEstado(gestionAnterior.EstaSustituida, anterior.EstaSustituido, [EstadoAcreditacion.PendienteDeSubir])
            .Should().Be(EstadoGestionAutomatica.Sustituida, "la lectura derivada ya la resuelve");
    }

    [Fact]
    public async Task Un_lote_mayor_que_el_limite_se_rechaza()
    {
        var ids = Enumerable.Range(0, ConciliacionGestionesAutomaticasService.DocumentosPorLote + 1).Select(_ => Guid.NewGuid()).ToList();

        var accion = () => Servicio().ConciliarLoteAsync(ids);

        await accion.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task La_ventana_exige_un_instante_UTC()
    {
        var accion = () => Servicio().BuscarDocumentosSinGestionAsync(DateTime.SpecifyKind(_haceUnaHora, DateTimeKind.Local));

        await accion.Should().ThrowAsync<ArgumentException>();
    }

    // ---------- Montaje ----------

    private ConciliacionGestionesAutomaticasService Servicio() =>
        new(_mundo.DocumentosContexto, _mundo.Gestiones, _auditoria, _mundo.GestionAutomatica(), _guardado,
            NullLogger<ConciliacionGestionesAutomaticasService>.Instance);

    /// <summary>
    /// Lo que dejó la ranura anterior: el Documento con archivo, su acreditación de plataforma y su alta auditada en el
    /// guardado del acto, sin Gestión.
    /// </summary>
    private (Documento Documento, AcreditacionDocumentoPlataforma Acreditacion) ActoSinGestion(
        string? archivo = "tenant/apto.pdf", TimeSpan? creadoHace = null)
    {
        var documento = DocumentoConArchivo(archivo);
        if (creadoHace is { } hace)
            FijarCreadoEn(documento, DateTime.UtcNow - hace);
        var acreditacion = AcreditacionAntePlataforma(documento);
        AltaAuditada(documento);
        return (documento, acreditacion);
    }

    private Documento DocumentoConArchivo(string? archivo = "tenant/apto.pdf")
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)), archivo);
        _mundo.DocumentosContexto.ListaDocumentos.Add(documento);
        return documento;
    }

    private Documento DocumentoDelMismoTitular(Documento anterior)
    {
        var nuevo = anterior.NuevoDelMismoTitular(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(2)), $"tenant/{Guid.NewGuid():N}.pdf", null, null);
        _mundo.DocumentosContexto.ListaDocumentos.Add(nuevo);
        return nuevo;
    }

    /// <summary>El registro «Creado» que el interceptor escribe en el guardado del acto, tras construir sus acreditaciones.</summary>
    private DateTime AltaAuditada(Documento documento, DateTime? enUtc = null)
    {
        var instante = enUtc ?? documento.CreadoEnUtc.AddMilliseconds(50);
        _auditoria.Agregar(nameof(Documento), documento.Id, ConciliacionGestionesAutomaticasService.AccionAlta, instante);
        return instante;
    }

    private AcreditacionDocumentoPlataforma AcreditacionAntePlataforma(Documento documento) =>
        Acreditacion(documento, _mundo.AccesoPlataforma(_mundo.Centro()));

    private AcreditacionDocumentoPlataforma AcreditacionAnteCorreo(Documento documento) =>
        Acreditacion(documento, _mundo.AccesoCorreo(_mundo.Centro()));

    /// <summary>Por defecto, construida en el acto: a milisegundos del Documento y antes de su alta auditada.</summary>
    private AcreditacionDocumentoPlataforma Acreditacion(
        Documento documento, Domain.Centros.CanalGestionDocumental acceso, DateTime? creadaEnUtc = null)
    {
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        FijarCreadoEn(acreditacion, creadaEnUtc ?? documento.CreadoEnUtc.AddMilliseconds(5));
        _mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        return acreditacion;
    }

    private static void FijarCreadoEn(EntidadBase entidad, DateTime creadoEnUtc) =>
        typeof(EntidadBase).GetProperty(nameof(EntidadBase.CreadoEnUtc))!.SetValue(entidad, creadoEnUtc);

    private sealed class AuditoriaFalsa : IAuditoriaQueryContext
    {
        private readonly List<RegistroAuditoria> _registros = [];

        public IQueryable<RegistroAuditoria> RegistrosAuditoria => new TestAsyncQueryable<RegistroAuditoria>(_registros.AsQueryable());

        public IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible =>
            new TestAsyncQueryable<RegistroAccesoDocumentoSensible>(Array.Empty<RegistroAccesoDocumentoSensible>().AsQueryable());

        public void Agregar(string entidadTipo, Guid entidadId, string accion, DateTime fechaUtc)
        {
            var registro = new RegistroAuditoria(entidadTipo, entidadId, accion, null, null, null, TipoActorAuditoria.Desconocido);
            typeof(RegistroAuditoria).GetProperty(nameof(RegistroAuditoria.FechaUtc))!.SetValue(registro, fechaUtc);
            _registros.Add(registro);
        }
    }
}
