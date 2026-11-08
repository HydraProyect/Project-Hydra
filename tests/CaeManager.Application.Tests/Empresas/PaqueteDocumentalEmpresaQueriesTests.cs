using System.IO.Compression;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// «Preparar paquete» de Empresa 360 (maqueta «Mapa Dia a Dia TALVEG.dc.html» § C): para un Cliente empresarial trae solo
/// lo que ese Cliente exige y está vigente, y dice antes de generar qué falta y qué no pide; «Todo lo vigente» trae todo lo
/// vigente. La puerta es la de las acciones vecinas: Tenant propietario, alcance de gestión sobre la Empresa, rol que
/// descarga y registro fallando en cerrado de cada Documento sensible que sale (DEC-36).
/// </summary>
public class PaqueteDocumentalEmpresaQueriesTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly CalculoConParesFijos _calculo = new();
    private readonly FileStorageServiceFalso _almacenamiento = new();
    private readonly RegistroQueAnota _registro = new();
    private AlcanceDatosServiceFalso _alcance = new();
    private string? _rol = "GestorCae";
    private SesionPrivilegiadaActiva? _sesionPrivilegiada;
    private long _maximoBytes = PaqueteDocumentalEmpresaService.MaximoBytes;

    private readonly Empresa _empresa = new("Contratista Demo SL", "B12345674");
    private readonly Empresa _cliente = Empresa.CrearComoCliente("Refrielectric SA", "A58818501", false, null, null);
    private readonly Empresa _otroCliente = Empresa.CrearComoCliente("Grupo Arbeko SL", "B95758389", false, null, null);
    private readonly Centro _centroDelCliente;
    private readonly Centro _centroDelOtro;
    private readonly Trabajador _ana;
    private readonly Trabajador _luis;
    private readonly TipoDocumento _reconocimiento = NuevoTipo("Reconocimiento médico", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _epi = NuevoTipo("Entrega de EPI", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _seguro = NuevoTipo("Seguro RC", AmbitoAplicacion.Empresa);
    private readonly TipoDocumento _iso = NuevoTipo("Certificado ISO", AmbitoAplicacion.Empresa);

    private readonly Documento _epiAna;
    private readonly Documento _epiLuis;
    private readonly Documento _seguroEmpresa;

    public PaqueteDocumentalEmpresaQueriesTests()
    {
        _centroDelCliente = new Centro(_cliente.Id, _empresa.Id, "Nave Norte");
        _centroDelOtro = new Centro(_otroCliente.Id, _empresa.Id, "Planta Sur");
        _ana = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Garcia", "12345678Z");
        _luis = Trabajador.DeEmpresa(_empresa.Id, "Luis", "Perez", "77189989B");

        _empresas.ListaEmpresas.AddRange([_empresa, _cliente, _otroCliente]);
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_empresa.Id, _cliente.Id, DateTime.UtcNow));
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_empresa.Id, _otroCliente.Id, DateTime.UtcNow));
        _centros.ListaCentros.AddRange([_centroDelCliente, _centroDelOtro]);
        _trabajadores.ListaTrabajadores.AddRange([_ana, _luis]);
        _tipos.ListaTiposDocumento.AddRange([_reconocimiento, _epi, _seguro, _iso]);
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _centroDelCliente.Id, Hoy.AddDays(-30)));
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_luis.Id, _centroDelOtro.Id, Hoy.AddDays(-30)));

        // El Cliente exige a Ana reconocimiento y EPI y a la Empresa el seguro; el otro Cliente, EPI a Luis y la ISO.
        _calculo.Exigir(_centroDelCliente, _ana, _reconocimiento);
        _calculo.Exigir(_centroDelCliente, _ana, _epi);
        _calculo.ExigirALaEmpresa(_centroDelCliente, _empresa, _seguro);
        _calculo.Exigir(_centroDelOtro, _luis, _epi);
        _calculo.ExigirALaEmpresa(_centroDelOtro, _empresa, _iso);

        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-400), VigenciaDocumento.VenceEl(Hoy.AddDays(-5)), "reconocimiento-vencido");
        _epiAna = DocumentoDeTrabajador(_ana, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "epi-ana");
        _epiLuis = DocumentoDeTrabajador(_luis, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "epi-luis");
        _seguroEmpresa = DocumentoDeEmpresa(_seguro, VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "seguro");
        DocumentoDeEmpresa(_iso, VigenciaDocumento.NoCaduca, "iso");
    }

    [Fact]
    public async Task Para_un_Cliente_dice_cuantos_van_cuantos_faltan_o_estan_vencidos_y_cuantos_no_pide()
    {
        var resumen = (await ResumirAsync(_cliente.Id)).Valor;

        resumen.Destinos.Select(d => d.RazonSocial).Should().Equal("Grupo Arbeko SL", "Refrielectric SA");
        resumen.Incluye.Should().Be(2, "el EPI de Ana y el seguro: lo que el Cliente exige y está vigente");
        resumen.Faltan.Should().Be(1, "el reconocimiento de Ana solo tiene una copia vencida");
        resumen.Fuera.Should().Be(2, "el EPI de Luis y la ISO están vigentes pero este Cliente no los pide");
        resumen.ListaFaltan.Should().ContainSingle()
            .Which.Should().Be(new FaltaPaqueteEmpresaDto("Reconocimiento médico", "Ana Garcia", Hoy.AddDays(-5)));
    }

    [Fact]
    public async Task La_descarga_para_un_Cliente_trae_solo_lo_exigido_y_vigente_en_carpetas_por_Trabajador()
    {
        var resultado = await DescargarAsync(_cliente.Id);

        resultado.EsFallido.Should().BeFalse();
        LeerZip(resultado.Valor.Contenido).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Empresa/Seguro RC.pdf"] = "seguro",
            ["Trabajadores/Ana Garcia/Entrega de EPI.pdf"] = "epi-ana",
        }, "el vencido nunca viaja y lo que este Cliente no pide no entra");
        resultado.Valor.NombreArchivo.Should().Be($"Contratista-Demo-SL_Refrielectric-SA_{Hoy:yyyy-MM-dd}.zip");
    }

    [Fact]
    public async Task Todo_lo_vigente_trae_todo_lo_vigente_y_nunca_un_vencido()
    {
        var resumen = (await ResumirAsync(null)).Valor;
        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        resumen.Should().BeEquivalentTo(new { Incluye = 4, Faltan = 0, Fuera = 0 });
        zip.Values.Should().BeEquivalentTo("seguro", "iso", "epi-ana", "epi-luis");
    }

    [Fact]
    public async Task Cada_documento_que_sale_se_registra_como_apertura_antes_de_entregar_el_zip()
    {
        await DescargarAsync(_cliente.Id);

        _registro.Registrados.Should().BeEquivalentTo(new[]
        {
            (_epiAna.Id, TipoAccesoDocumentoSensible.Apertura),
            (_seguroEmpresa.Id, TipoAccesoDocumentoSensible.Apertura),
        });
    }

    [Fact]
    public async Task Sin_constancia_del_acceso_a_un_documento_sensible_no_hay_zip()
    {
        _registro.Falla = true;

        var descargar = () => DescargarAsync(_cliente.Id);

        await descargar.Should().ThrowAsync<InvalidOperationException>("el registro falla en cerrado (DEC-36)");
    }

    // ---- Puerta: cartera, Tenant propietario, destino y rol

    [Fact]
    public async Task Fuera_de_la_cartera_de_gestion_responde_como_una_Empresa_inexistente_y_no_lee_nada()
    {
        _alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id], empresaIdsParaGestion: []);

        (await ResumirAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Empresa_de_otro_Tenant_que_el_filtro_de_consulta_no_devuelve_responde_como_inexistente()
    {
        // El filtro de consulta del Tenant activo (y RLS) no devuelve las Empresas de otro Tenant: para esta capa, no están.
        // Que el filtro y RLS las oculten se prueba en Integración, no aquí.
        var deOtroTenant = Guid.NewGuid();

        (await Mediador().ResumirAsync(new ObtenerResumenPaqueteEmpresaQuery(deOtroTenant, null))).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        (await Mediador().DescargarAsync(new DescargarPaqueteEmpresaQuery(deOtroTenant, null))).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
    }

    [Fact]
    public async Task Un_Cliente_empresarial_que_no_es_de_la_Empresa_no_es_destino()
    {
        var ajeno = Empresa.CrearComoCliente("Cliente Ajeno SA", "A08001851", false, null, null);
        _empresas.ListaEmpresas.Add(ajeno);

        (await ResumirAsync(ajeno.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        (await DescargarAsync(ajeno.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Un_rol_que_no_descarga_no_obtiene_los_documentos_sensibles_del_paquete(string? rol)
    {
        _rol = rol;

        (await ResumirAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.RolSinDescarga);
        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.RolSinDescarga);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_fuera_del_alcance_de_quien_prepara_el_paquete_no_entra()
    {
        _alcance = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [_ana.Id]);

        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        zip.Values.Should().NotContain("epi-luis");
        _registro.Registrados.Select(r => r.DocumentoId).Should().NotContain(_epiLuis.Id);
    }

    [Fact]
    public async Task Sin_nada_vigente_que_entregar_no_se_genera_un_zip_vacio()
    {
        _documentos.ListaDocumentos.RemoveAll(d => d.Id == _epiAna.Id || d.Id == _seguroEmpresa.Id);

        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.SinDocumentos);
    }

    [Fact]
    public async Task Todo_lo_vigente_no_trae_los_Documentos_vigentes_de_otra_Empresa_del_mismo_Tenant_propietario()
    {
        // Otra Empresa del mismo Tenant propietario, con un Documento de Empresa y un Trabajador con el suyo, todo vigente
        // y de tipos que la Empresa del paquete no tiene: si se colara, entraría sin competir con nada.
        var otraEmpresa = new Empresa("Otra Contratista SL", "B76543214");
        var plan = NuevoTipo("Plan de prevención", AmbitoAplicacion.Empresa);
        var formacion = NuevoTipo("Formación PRL", AmbitoAplicacion.Trabajador);
        var marta = Trabajador.DeEmpresa(otraEmpresa.Id, "Marta", "Ruiz", "45678912S");
        _empresas.ListaEmpresas.Add(otraEmpresa);
        _tipos.ListaTiposDocumento.AddRange([plan, formacion]);
        _trabajadores.ListaTrabajadores.Add(marta);
        var planOtra = Documento.DeEmpresa(otraEmpresa.Id, plan.Id, Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("plan-otra"));
        var formacionMarta = Documento.DeTrabajador(marta.Id, formacion.Id, Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("formacion-marta"));
        _documentos.ListaDocumentos.AddRange([planOtra, formacionMarta]);

        var resumen = (await ResumirAsync(null)).Valor;
        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        resumen.Incluye.Should().Be(4, "solo cuentan los Documentos de la Empresa del paquete y de sus Trabajadores");
        zip.Values.Should().BeEquivalentTo("seguro", "iso", "epi-ana", "epi-luis");
        _registro.Registrados.Select(r => r.DocumentoId).Should().NotContain(planOtra.Id).And.NotContain(formacionMarta.Id);
    }

    [Fact]
    public async Task Un_archivo_que_no_se_puede_abrir_no_se_omite_en_silencio_va_en_un_indice_dentro_del_zip()
    {
        _documentos.ListaDocumentos.RemoveAll(d => d.Id == _epiAna.Id);
        var epiSinArchivo = Documento.DeTrabajador(_ana.Id, _epi.Id, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "no-existe.pdf");
        _documentos.ListaDocumentos.Add(epiSinArchivo);

        var resultado = await DescargarAsync(_cliente.Id);

        var zip = LeerZip(resultado.Valor.Contenido);
        zip.Keys.Should().BeEquivalentTo("Empresa/Seguro RC.pdf", PaqueteDocumentalEmpresaService.NombreLeeme);
        zip[PaqueteDocumentalEmpresaService.NombreLeeme].Should().Contain("- Entrega de EPI · Ana Garcia");
        _registro.Registrados.Select(r => r.DocumentoId).Should().NotContain(epiSinArchivo.Id, "lo que no entró no es un acceso a su contenido");
    }

    [Fact]
    public async Task Si_no_se_puede_abrir_ningun_archivo_lo_dice_en_vez_de_decir_que_no_hay_nada_vigente()
    {
        _almacenamiento.ExcepcionAlAbrir = new IOException("almacenamiento caído");

        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.ArchivosNoDisponibles);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_ve_el_resumen_pero_no_descarga_el_paquete_de_Empresa()
    {
        // Decisión provisional de la coordinadora (2026-11-08), la más restrictiva: el Soporte TALVEG no es Gestor CAE.
        _rol = null;
        _sesionPrivilegiada = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.AdminPlataforma, null);

        var resumen = await ResumirAsync(_cliente.Id);
        var descarga = await DescargarAsync(_cliente.Id);

        resumen.EsFallido.Should().BeFalse();
        resumen.Valor.Should().BeEquivalentTo(new { Incluye = 2, PuedeDescargar = false });
        descarga.Error.Should().Be(AutorizacionPaqueteEmpresa.SesionPrivilegiadaSinDescarga);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_Trabajadores_con_el_mismo_nombre_no_comparten_carpeta_en_el_zip()
    {
        var otraAna = Trabajador.DeEmpresa(_empresa.Id, "ANA", "GARCIA", "11111111H");
        _trabajadores.ListaTrabajadores.Add(otraAna);
        DocumentoDeTrabajador(otraAna, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "epi-otra-ana");
        // El segundo por orden de Id lleva « (2)» detrás de su propio nombre.
        var esperado = _ana.Id.CompareTo(otraAna.Id) < 0
            ? new Dictionary<string, string> { ["Trabajadores/Ana Garcia/Entrega de EPI.pdf"] = "epi-ana", ["Trabajadores/ANA GARCIA (2)/Entrega de EPI.pdf"] = "epi-otra-ana" }
            : new Dictionary<string, string> { ["Trabajadores/ANA GARCIA/Entrega de EPI.pdf"] = "epi-otra-ana", ["Trabajadores/Ana Garcia (2)/Entrega de EPI.pdf"] = "epi-ana" };

        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        zip.Where(e => e.Key.StartsWith("Trabajadores/Ana", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(esperado, "cada Trabajador tiene su carpeta: el Cliente empresarial no puede mezclar documentos de dos personas");
    }

    [Fact]
    public async Task El_zip_en_memoria_se_corta_al_pasar_su_tope_de_bytes()
    {
        var grupos = new List<IReadOnlyList<ArmadoPaqueteDocumental.Copia>>
        {
            new[] { new ArmadoPaqueteDocumental.Copia(_epiAna.Id, _ana.Id, _epi.Id, _epiAna.ArchivoUrl!) },
            new[] { new ArmadoPaqueteDocumental.Copia(_epiLuis.Id, _luis.Id, _epi.Id, _epiLuis.ArchivoUrl!) },
        };

        var construir = () => ArmadoPaqueteDocumental.ConstruirZipAsync(
            grupos, c => ("Trabajadores", c.Id.ToString()), _almacenamiento, NullLogger.Instance, CancellationToken.None, maximoBytes: 10);

        await construir.Should().ThrowAsync<PaqueteDocumentalDemasiadoPesadoException>();
    }

    [Fact]
    public async Task Un_paquete_de_Empresa_que_pasa_su_tope_de_bytes_responde_como_demasiado_pesado_y_no_registra_nada()
    {
        _maximoBytes = 10;

        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.DemasiadoPesado);
        _registro.Registrados.Should().BeEmpty("no se entrega nada, así que no hay acceso que registrar");
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_resumen_permite_descargar()
    {
        (await ResumirAsync(_cliente.Id)).Valor.PuedeDescargar.Should().BeTrue();
    }

    // ---- Montaje

    private Task<Result<ResumenPaqueteEmpresaDto>> ResumirAsync(Guid? clienteId) =>
        Mediador().ResumirAsync(new ObtenerResumenPaqueteEmpresaQuery(_empresa.Id, clienteId));

    private Task<Result<PaqueteEmpresaDescargaDto>> DescargarAsync(Guid? clienteId) =>
        Mediador().DescargarAsync(new DescargarPaqueteEmpresaQuery(_empresa.Id, clienteId));

    private Handlers Mediador()
    {
        var servicio = new PaqueteDocumentalEmpresaService(
            _empresas, _trabajadores, _documentos, _tipos, _asignaciones, _centros, _calculo, _almacenamiento, _alcance,
            NullLogger<PaqueteDocumentalEmpresaService>.Instance)
        {
            MaximoBytesZip = _maximoBytes,
        };
        var usuario = new CurrentUserServiceFalso(rol: _rol);
        var sesion = new SesionPrivilegiadaFija(_sesionPrivilegiada);
        return new Handlers(
            new ObtenerResumenPaqueteEmpresaQueryHandler(_empresas, _alcance, usuario, sesion, servicio),
            new DescargarPaqueteEmpresaQueryHandler(_empresas, _alcance, usuario, sesion, servicio, _registro));
    }

    private sealed record Handlers(ObtenerResumenPaqueteEmpresaQueryHandler Resumen, DescargarPaqueteEmpresaQueryHandler Descarga)
    {
        public Task<Result<ResumenPaqueteEmpresaDto>> ResumirAsync(ObtenerResumenPaqueteEmpresaQuery query) => Resumen.Handle(query, CancellationToken.None);
        public Task<Result<PaqueteEmpresaDescargaDto>> DescargarAsync(DescargarPaqueteEmpresaQuery query) => Descarga.Handle(query, CancellationToken.None);
    }

    private Documento DocumentoDeTrabajador(Trabajador trabajador, TipoDocumento tipo, DateOnly emision, VigenciaDocumento vigencia, string contenido)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, emision, vigencia, Guardar(contenido));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private Documento DocumentoDeEmpresa(TipoDocumento tipo, VigenciaDocumento vigencia, string contenido)
    {
        var documento = Documento.DeEmpresa(_empresa.Id, tipo.Id, Hoy.AddDays(-10), vigencia, Guardar(contenido));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private string Guardar(string contenido) =>
        _almacenamiento.GuardarAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contenido)), "doc.pdf").GetAwaiter().GetResult();

    private static TipoDocumento NuevoTipo(string nombre, AmbitoAplicacion ambito) =>
        new(nombre, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambito);

    private static Dictionary<string, string> LeerZip(byte[] contenido)
    {
        using var zip = new ZipArchive(new MemoryStream(contenido), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var lector = new StreamReader(e.Open());
            return lector.ReadToEnd();
        });
    }

    private sealed class CalculoConParesFijos : ICalculoEstadoCentroService
    {
        private readonly List<ParDocumentalExigido> _pares = [];
        private readonly List<ParDocumentalDeEmpresaExigido> _paresDeEmpresa = [];

        public void Exigir(Centro centro, Trabajador trabajador, TipoDocumento tipo) =>
            _pares.Add(new ParDocumentalExigido(centro.Id, centro.ClienteId, trabajador.EmpresaId, trabajador.Id, tipo.Id, EstadoDocumento.Vigente, false, false));

        public void ExigirALaEmpresa(Centro centro, Empresa empresa, TipoDocumento tipo) =>
            _paresDeEmpresa.Add(new ParDocumentalDeEmpresaExigido(centro.Id, empresa.Id, tipo.Id, EstadoDocumento.Vigente, false, false));

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ParDocumentalExigido>>(_pares.Where(p => centroIds.Contains(p.CentroId)).ToList());

        public Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaExigidosAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ParDocumentalDeEmpresaExigido>>(_paresDeEmpresa.Where(p => centroIds.Contains(p.CentroId)).ToList());

        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RegistroQueAnota : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid DocumentoId, TipoAccesoDocumentoSensible Tipo)> Registrados { get; } = [];
        public bool Falla { get; set; }

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            if (Falla) throw new InvalidOperationException("sin constancia");
            Registrados.Add((documentoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SesionPrivilegiadaFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }
}
