using CaeManager.Application.Reportes.Queries;
using CaeManager.Application.Reportes.Queries.ObtenerHistorialInformes;
// AlcanceDatosServiceFalso y CurrentUserServiceFalso siguen viviendo en este
// namespace pese a que la entidad Cliente ya no existe (F3b/F3c).
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reportes;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Reportes;

/// <summary>
/// Los informes de /reportes reciben clienteId/centroId por query string
/// (/reportes/vigencia.pdf?clienteId=…), así que el selector acotado de la
/// página no basta: sin comprobar el alcance en el handler, cualquier usuario
/// con acceso a Reportes podía pedir el informe de un Cliente ajeno con solo
/// conocer su Guid, y un Gestor CAE que no tocaba el selector recibía toda la
/// documentación del tenant. Mismo criterio que AlcancePorIdTests (Issue #18):
/// fuera de cartera se comporta como "no existe" (null), nunca como un error
/// explícito que confirme que la fila está ahí.
/// </summary>
public class AlcanceDeCarteraEnReportesTests
{
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly SubcontratasQueryContextFalso _subcontratas = new();
    private readonly TiposDocumentoQueryContextFalso _tiposDocumento = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly CentrosQueryContextFalso _centros = new();

    // F3b/F3c retiraron la entidad Cliente: un cliente es hoy una Empresa
    // creada con CrearComoCliente. El test se migra al modelo actual — el
    // alcance que verifica (cartera del usuario) no cambia.
    private readonly Empresa _clienteEnCartera =
        Empresa.CrearComoCliente("Cadena Industrial Iberia S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _clienteAjeno =
        Empresa.CrearComoCliente("Cliente de otra cartera S.L.", "B87654323", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Centro _centroEnCartera;
    private readonly Centro _centroAjeno;

    private const string TrabajadorEnCartera = "Alvaro Sanchez Martin";
    private const string TrabajadorAjeno = "Nuria Ajena Ruiz";

    public AlcanceDeCarteraEnReportesTests()
    {
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

        var empresa = new Empresa("Ibertec S.A.");
        _empresas.ListaEmpresas.Add(empresa);

        _centroEnCartera = new Centro(_clienteEnCartera.Id, empresa.Id, "Planta Sevilla");
        _centroAjeno = new Centro(_clienteAjeno.Id, empresa.Id, "Planta de otra cartera");
        _empresas.ListaEmpresas.AddRange([_clienteEnCartera, _clienteAjeno]);
        _centros.ListaCentros.AddRange([_centroEnCartera, _centroAjeno]);

        var tipoDocumento = new TipoDocumento(
            "Vigilancia de la salud", vigenciaMeses: 12, aplicaVencimientoAutomatico: true,
            orden: 1, ambitoAplicacion: AmbitoAplicacion.Trabajador);
        _tiposDocumento.ListaTiposDocumento.Add(tipoDocumento);

        var trabajadorEnCartera = Trabajador.DeEmpresa(empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");
        var trabajadorAjeno = Trabajador.DeEmpresa(empresa.Id, "Nuria", "Ajena Ruiz", "12345678Z");
        _trabajadores.ListaTrabajadores.AddRange([trabajadorEnCartera, trabajadorAjeno]);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _asignaciones.ListaAsignaciones.AddRange([
            new Asignacion(trabajadorEnCartera.Id, _centroEnCartera.Id, hoy),
            new Asignacion(trabajadorAjeno.Id, _centroAjeno.Id, hoy)
        ]);

        _documentos.ListaDocumentos.AddRange([
            Documento.DeTrabajador(trabajadorEnCartera.Id, tipoDocumento.Id, hoy, hoy.AddMonths(12)),
            Documento.DeTrabajador(trabajadorAjeno.Id, tipoDocumento.Id, hoy, hoy.AddMonths(12))
        ]);
    }

    // --- Vigencia documental ---

    [Fact]
    public async Task Vigencia_devuelve_null_al_pedir_un_cliente_fuera_de_la_cartera()
    {
        var informe = await GenerarVigenciaAsync(AlcanceRestringido(), clienteId: _clienteAjeno.Id);

        informe.Should().BeNull("pedir el Guid de un Cliente ajeno tiene que comportarse igual que si no existiera");
    }

    [Fact]
    public async Task Vigencia_devuelve_null_al_pedir_un_centro_fuera_de_la_cartera()
    {
        var informe = await GenerarVigenciaAsync(AlcanceRestringido(), centroId: _centroAjeno.Id);

        informe.Should().BeNull();
    }

    [Fact]
    public async Task Vigencia_sin_filtro_explicito_solo_incluye_la_cartera_del_usuario()
    {
        // El caso que se colaba sin tocar nada: un Gestor CAE que no elige
        // cliente en el selector recibía los documentos de todo el tenant.
        var informe = await GenerarVigenciaAsync(AlcanceRestringido());

        informe.Should().NotBeNull();
        informe!.Filas.Select(f => f.TrabajadorNombre).Should().ContainSingle().Which.Should().Be(TrabajadorEnCartera);
    }

    [Fact]
    public async Task Vigencia_devuelve_el_informe_del_cliente_propio()
    {
        var informe = await GenerarVigenciaAsync(AlcanceRestringido(), clienteId: _clienteEnCartera.Id);

        informe.Should().NotBeNull();
        informe!.Filas.Select(f => f.TrabajadorNombre).Should().ContainSingle().Which.Should().Be(TrabajadorEnCartera);
    }

    [Fact]
    public async Task Vigencia_sin_restriccion_de_cartera_sigue_viendo_toda_la_organizacion()
    {
        var informe = await GenerarVigenciaAsync(new AlcanceDatosServiceFalso());

        informe.Should().NotBeNull();
        informe!.Filas.Select(f => f.TrabajadorNombre).Should().BeEquivalentTo([TrabajadorEnCartera, TrabajadorAjeno]);
    }

    [Fact]
    public async Task Vigencia_con_cartera_vacia_no_devuelve_ninguna_fila()
    {
        // Contrato de IAlcanceDatosService: lista vacía ≠ null. Es un usuario
        // con cartera todavía sin asignar, no un administrador.
        var informe = await GenerarVigenciaAsync(new AlcanceDatosServiceFalso(tieneAccesoTotal: false));

        informe.Should().NotBeNull();
        informe!.Filas.Should().BeEmpty();
    }

    // --- Asignaciones activas ---

    [Fact]
    public async Task Asignaciones_devuelve_null_al_pedir_un_cliente_fuera_de_la_cartera()
    {
        var informe = await GenerarAsignacionesAsync(AlcanceRestringido(), clienteId: _clienteAjeno.Id);

        informe.Should().BeNull();
    }

    [Fact]
    public async Task Asignaciones_devuelve_null_al_pedir_un_centro_fuera_de_la_cartera()
    {
        var informe = await GenerarAsignacionesAsync(AlcanceRestringido(), centroId: _centroAjeno.Id);

        informe.Should().BeNull();
    }

    [Fact]
    public async Task Asignaciones_sin_filtro_explicito_solo_incluye_la_cartera_del_usuario()
    {
        var informe = await GenerarAsignacionesAsync(AlcanceRestringido());

        informe.Should().NotBeNull();
        informe!.Filas.Select(f => f.CentroNombre).Should().ContainSingle().Which.Should().Be(_centroEnCartera.Nombre);
    }

    [Fact]
    public async Task Asignaciones_sin_restriccion_de_cartera_sigue_viendo_toda_la_organizacion()
    {
        var informe = await GenerarAsignacionesAsync(new AlcanceDatosServiceFalso());

        informe.Should().NotBeNull();
        informe!.Filas.Select(f => f.CentroNombre).Should().BeEquivalentTo([_centroEnCartera.Nombre, _centroAjeno.Nombre]);
    }

    // --- Historial ---

    [Fact]
    public async Task Historial_oculta_los_informes_de_clientes_de_otra_cartera()
    {
        var yo = Guid.NewGuid();
        var otroGestor = Guid.NewGuid();
        var reportes = new ReportesQueryContextFalso();
        reportes.ListaHistorialInformes.AddRange([
            new HistorialInforme("Vigencia documental", _clienteEnCartera.Id, _clienteEnCartera.RazonSocial, yo),
            new HistorialInforme("Vigencia documental", _clienteAjeno.Id, _clienteAjeno.RazonSocial, otroGestor),
            // Informe "toda la cartera" de otro gestor: no lleva ClienteId, así
            // que sin el filtro por autor se colaría en el historial de todos.
            new HistorialInforme("Asignaciones activas", null, null, otroGestor)
        ]);

        var historial = await new ObtenerHistorialInformesQueryHandler(
                reportes, AlcanceRestringido(), new CurrentUserServiceFalso(yo))
            .Handle(new ObtenerHistorialInformesQuery(), CancellationToken.None);

        historial.Select(h => h.ClienteNombre).Should().ContainSingle().Which.Should().Be(_clienteEnCartera.RazonSocial);
    }

    [Fact]
    public async Task Historial_conserva_los_informes_de_toda_la_cartera_generados_por_uno_mismo()
    {
        var yo = Guid.NewGuid();
        var reportes = new ReportesQueryContextFalso();
        reportes.ListaHistorialInformes.Add(new HistorialInforme("Asignaciones activas", null, null, yo));

        var historial = await new ObtenerHistorialInformesQueryHandler(
                reportes, AlcanceRestringido(), new CurrentUserServiceFalso(yo))
            .Handle(new ObtenerHistorialInformesQuery(), CancellationToken.None);

        historial.Should().ContainSingle();
    }

    [Fact]
    public async Task Historial_sin_restriccion_de_cartera_se_ve_entero()
    {
        var reportes = new ReportesQueryContextFalso();
        reportes.ListaHistorialInformes.AddRange([
            new HistorialInforme("Vigencia documental", _clienteEnCartera.Id, _clienteEnCartera.RazonSocial, Guid.NewGuid()),
            new HistorialInforme("Vigencia documental", _clienteAjeno.Id, _clienteAjeno.RazonSocial, Guid.NewGuid())
        ]);

        var historial = await new ObtenerHistorialInformesQueryHandler(
                reportes, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid()))
            .Handle(new ObtenerHistorialInformesQuery(), CancellationToken.None);

        historial.Should().HaveCount(2);
    }

    // --- Andamiaje ---

    /// <summary>Un Gestor CAE cuya cartera es exactamente un Cliente y su Centro.</summary>
    private AlcanceDatosServiceFalso AlcanceRestringido() => new(
        tieneAccesoTotal: false,
        clienteIdsVisibles: [_clienteEnCartera.Id],
        centroIdsVisibles: [_centroEnCartera.Id]);

    private Task<InformeVigenciaDto?> GenerarVigenciaAsync(
        AlcanceDatosServiceFalso alcance, Guid? clienteId = null, Guid? centroId = null) =>
        new GenerarInformeVigenciaQueryHandler(
                _configuracion, _documentos, _empresas, _tiposDocumento,
                _trabajadores, _asignaciones, _centros, alcance)
            .Handle(new GenerarInformeVigenciaQuery(clienteId, centroId, IncluirVigentes: true), CancellationToken.None);

    private Task<InformeAsignacionesDto?> GenerarAsignacionesAsync(
        AlcanceDatosServiceFalso alcance, Guid? clienteId = null, Guid? centroId = null) =>
        new GenerarInformeAsignacionesQueryHandler(_asignaciones, _trabajadores, _centros, _empresas, alcance)
            .Handle(new GenerarInformeAsignacionesQuery(clienteId, centroId), CancellationToken.None);
}
