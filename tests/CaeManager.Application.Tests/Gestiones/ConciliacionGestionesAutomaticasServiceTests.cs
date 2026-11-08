using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// La conciliación de Gestiones automáticas (ALTO abierto de la ronda 3 de Codex sobre la PR #2072): los actos con
/// archivo que la ranura anterior atendió sin Gestión durante el relevo. Con el servicio de nacimiento REAL, para que
/// la regla de qué es un paso sea la misma que en el acto. El aislamiento entre Tenants y el índice único real se miden
/// bajo <c>cae_app_runtime</c> en <c>ConciliacionGestionesAutomaticasBajoRuntimeTests</c>.
/// </summary>
public class ConciliacionGestionesAutomaticasServiceTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    private readonly MundoAcreditaciones _mundo = new();
    private readonly UnitOfWorkFalso _guardado = new();
    private readonly DateTime _haceUnaHora = DateTime.UtcNow.AddHours(-1);

    [Fact]
    public async Task Crea_la_gestion_del_alta_con_archivo_que_quedo_sin_ella_con_sus_pasos_de_plataforma()
    {
        var (documento, acreditacion) = ActoSinGestion();
        var correo = AcreditacionAnteCorreo(documento);

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
    public async Task No_busca_documentos_sin_archivo_ni_sustituidos_ni_eliminados()
    {
        var (sinArchivo, _) = ActoSinGestion(archivo: null);
        var (sustituido, _) = ActoSinGestion();
        // El sustituto no se agrega al mundo: aquí solo se mide que el sustituido no sale.
        var sustituto = sustituido.NuevoDelMismoTitular(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(2)), "tenant/nuevo.pdf", null, null);
        sustituido.SustituirPor(sustituto, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var (eliminado, _) = ActoSinGestion();
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var (operativo, _) = ActoSinGestion();

        (await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora)).Should().Equal([operativo.Id],
            "solo el Documento operativo con archivo; el resto, control negativo en el mismo mundo");
        sinArchivo.ArchivoUrl.Should().BeNull();
    }

    [Fact]
    public async Task El_lote_no_hace_nacer_la_de_un_Documento_sustituido_o_eliminado_entre_la_busqueda_y_el_lote()
    {
        // La ranura anterior puede renovar o eliminar el Documento después de que la búsqueda lo encontrara: el lote
        // vuelve a filtrar por operativos y no se fía de la lista que recibe.
        var (sustituido, _) = ActoSinGestion();
        var sustituto = sustituido.NuevoDelMismoTitular(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(2)), "tenant/nuevo.pdf", null, null);
        sustituido.SustituirPor(sustituto, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var (eliminado, _) = ActoSinGestion();
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var (operativo, _) = ActoSinGestion();

        (await Servicio().ConciliarLoteAsync([sustituido.Id, eliminado.Id, operativo.Id])).Should().Be(1,
            "solo nace la del Documento que sigue operativo; los otros dos, control negativo en el mismo lote");
    }

    [Fact]
    public async Task Sin_acreditaciones_de_plataforma_del_acto_no_nace_ninguna_ni_se_guarda()
    {
        var soloCorreo = DocumentoConArchivo();
        AcreditacionAnteCorreo(soloCorreo);
        var (posterior, acreditacionPosterior) = ActoSinGestion();
        FijarCreadoEn(acreditacionPosterior, posterior.CreadoEnUtc + ConciliacionGestionesAutomaticasService.ToleranciaDelActo + TimeSpan.FromSeconds(1));
        var (enElLimite, acreditacionEnElLimite) = ActoSinGestion();
        FijarCreadoEn(acreditacionEnElLimite, enElLimite.CreadoEnUtc + ConciliacionGestionesAutomaticasService.ToleranciaDelActo);

        (await Servicio().ConciliarLoteAsync([soloCorreo.Id, posterior.Id, enElLimite.Id])).Should().Be(1);

        _mundo.Gestiones.Lista.Should().ContainSingle(
                "solo el acto con una acreditación de plataforma propia; una acreditación posterior es de otro acto")
            .Which.DocumentoId.Should().Be(enElLimite.Id);
    }

    [Fact]
    public async Task Sin_ninguna_gestion_que_nacer_no_guarda()
    {
        var soloCorreo = DocumentoConArchivo();
        AcreditacionAnteCorreo(soloCorreo);

        (await Servicio().ConciliarLoteAsync([soloCorreo.Id])).Should().Be(0);

        _guardado.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_renovacion_sin_gestion_nace_como_renovacion_y_la_anterior_abierta_se_lee_sustituida_sin_marcarla()
    {
        var (anterior, acreditacionAnterior) = ActoSinGestion();
        var gestionAnterior = GestionAutomatica.Nacer(anterior.Id, OrigenGestionAutomatica.AltaDocumento, [acreditacionAnterior.Id], null)!;
        _mundo.Gestiones.Agregar(gestionAnterior);
        var nuevo = DocumentoDelMismoTitular(anterior);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var acreditacionNueva = AcreditacionAntePlataforma(nuevo);

        var sinGestion = await Servicio().BuscarDocumentosSinGestionAsync(_haceUnaHora);
        sinGestion.Should().Equal([nuevo.Id], "el anterior es historial y además ya tiene Gestión");
        await Servicio().ConciliarLoteAsync(sinGestion);

        var nueva = _mundo.Gestiones.Lista.Single(g => g.DocumentoId == nuevo.Id);
        nueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        nueva.Pasos.Select(p => p.AcreditacionId).Should().Equal(acreditacionNueva.Id);
        gestionAnterior.EstaSustituida.Should().BeFalse("la conciliación no repite la regla de sustitución");
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
        new(_mundo.DocumentosContexto, _mundo.Gestiones, _mundo.GestionAutomatica(), _guardado);

    /// <summary>Lo que dejó la ranura anterior: el Documento con archivo y su acreditación de plataforma, sin Gestión.</summary>
    private (Documento Documento, AcreditacionDocumentoPlataforma Acreditacion) ActoSinGestion(string? archivo = "tenant/apto.pdf")
    {
        var documento = DocumentoConArchivo(archivo);
        return (documento, AcreditacionAntePlataforma(documento));
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

    private AcreditacionDocumentoPlataforma AcreditacionAntePlataforma(Documento documento) =>
        Acreditacion(documento, _mundo.AccesoPlataforma(_mundo.Centro()));

    private AcreditacionDocumentoPlataforma AcreditacionAnteCorreo(Documento documento) =>
        Acreditacion(documento, _mundo.AccesoCorreo(_mundo.Centro()));

    private AcreditacionDocumentoPlataforma Acreditacion(Documento documento, Domain.Centros.CanalGestionDocumental acceso)
    {
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        FijarCreadoEn(acreditacion, documento.CreadoEnUtc);
        _mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        return acreditacion;
    }

    private static void FijarCreadoEn(EntidadBase entidad, DateTime creadoEnUtc) =>
        typeof(EntidadBase).GetProperty(nameof(EntidadBase.CreadoEnUtc))!.SetValue(entidad, creadoEnUtc);
}
