using CaeManager.Application.Gestiones;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
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
        Acreditar(documento, AccesoPlataforma(norte, "Nalanda")).MarcarSubida();
        Acreditar(documento, AccesoPlataforma(sur, "Dokify"));
        await Nacer(documento);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_trabajador.Id],
            empresaIdsVisibles: [_empresa.Id], centroIdsVisibles: [norte.Id]);

        var listado = await Listar(alcance);
        var detalle = await Detalle(_gestiones.Lista.Single().Id, alcance);

        var fila = listado.Filas.Should().ContainSingle().Subject;
        fila.Avance.Should().Be(new AvanceGestionAutomatica(Pasos: 1, Hechos: 1, Validados: 0, PendientesDeValidar: 1));
        fila.Estado.Should().Be(EstadoGestionAutomatica.PendienteDeValidar);
        fila.Plataformas.Should().Equal("Nalanda");
        detalle!.Pasos.Select(p => p.CentroId).Should().Equal(norte.Id);
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
        (await Listar(sinLaEmpresa)).Filas.Should().BeEmpty();
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

    private Task<GestionAutomaticaDetalleDto?> Detalle(Guid id, AlcanceDatosServiceFalso alcance) =>
        new ObtenerGestionAutomaticaQueryHandler(
                _gestiones, _documentos, _centros, _proveedores, _trabajadores, _empresas, _tipos, alcance)
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

    private CanalGestionDocumental AccesoPlataforma(Centro centro, string plataforma)
    {
        var proveedor = _proveedores.ListaProveedores.FirstOrDefault(p => p.Nombre == plataforma)
                        ?? new ProveedorPlataformaCae(plataforma.ToLowerInvariant(), plataforma);
        if (!_proveedores.ListaProveedores.Contains(proveedor))
            _proveedores.ListaProveedores.Add(proveedor);
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedor.Id, null, null, null);
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
