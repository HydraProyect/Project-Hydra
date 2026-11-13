using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// Codex r1 sobre la PR #2190 (ASVS 4.0.3 V4.2.1): ver a un Trabajador no da acceso a todos sus Centros. Ana trabaja
/// en un Centro visible para el usuario y en otro de otro Cliente empresarial que no lo es; la documentación por Centro
/// solo puede enseñar el visible, y la segunda línea (Plataforma CAE y estado de la acreditación) solo se carga para él.
/// </summary>
public class DocumentacionPorCentroDeTrabajadorAlcanceTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly SituacionQueAcreditaTodo _situacion = new();

    private readonly Centro _visible;
    private readonly Centro _ajeno;
    private readonly Trabajador _ana;

    public DocumentacionPorCentroDeTrabajadorAlcanceTests()
    {
        var clienteVisible = Empresa.CrearComoCliente("Cliente Visible SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var clienteAjeno = Empresa.CrearComoCliente("Cliente Ajeno SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var contratista = new Empresa("Contratista Demo SL", "B12345674");

        _visible = new Centro(clienteVisible.Id, contratista.Id, "Nave Visible");
        _ajeno = new Centro(clienteAjeno.Id, contratista.Id, "Nave Ajena");
        _ana = Trabajador.DeEmpresa(contratista.Id, "Ana", "Garcia", "12345678Z");

        _centros.ListaCentros.AddRange([_visible, _ajeno]);
        _empresas.ListaEmpresas.AddRange([clienteVisible, clienteAjeno, contratista]);
        _trabajadores.ListaTrabajadores.Add(_ana);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

        var tipo = new TipoDocumento(
            "Reconocimiento médico", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(tipo);
        _documentos.ListaDocumentos.Add(Documento.DeTrabajador(_ana.Id, tipo.Id, Hoy.AddDays(-5), VigenciaDocumento.VenceEl(Hoy.AddYears(1))));

        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _visible.Id, Hoy.AddDays(-10)));
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _ajeno.Id, Hoy.AddDays(-10)));
    }

    private Task<IReadOnlyList<CentroDocumentacionTrabajadorDto>> ConsultarAsync(AlcanceDatosServiceFalso alcance) =>
        new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
                _asignaciones, _centros, _empresas, _tipos, _documentos, _configuracion, _trabajadores, alcance, _situacion)
            .Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(_ana.Id), CancellationToken.None);

    public static TheoryData<bool> LadosDeGestion => new() { true, false };

    // ladoDeGestion: true es el Gestor CAE con Asignación de Cartera limitada; false, el Usuario de Cliente (portal).
    // Para los dos, el alcance de lectura de Centros es el mismo: los de su cartera o los de su Cliente empresarial.
    [Theory]
    [MemberData(nameof(LadosDeGestion))]
    public async Task Con_alcance_limitado_solo_ve_el_centro_visible_y_sus_acreditaciones(bool ladoDeGestion)
    {
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [_ana.Id], centroIdsVisibles: [_visible.Id], ladoDeGestion: ladoDeGestion);

        var resultado = await ConsultarAsync(alcance);

        var fila = resultado.Should().ContainSingle().Which;
        fila.CentroId.Should().Be(_visible.Id);
        fila.ClienteRazonSocial.Should().Be("Cliente Visible SA");
        fila.Documentos.Should().ContainSingle().Which.AcreditacionesEnElCentro.Should().ContainSingle()
            .Which.NombrePlataforma.Should().Be(SituacionQueAcreditaTodo.Plataforma(_visible.Id), "control positivo: la segunda línea del Centro visible sigue");

        _situacion.CentrosConsultados.Should().NotContain(_ajeno.Id,
            "la Plataforma CAE y el estado de la acreditación en un Centro ajeno no se cargan siquiera");
        resultado.Should().NotContain(c => c.CentroId == _ajeno.Id || c.ClienteRazonSocial == "Cliente Ajeno SA");
    }

    [Fact]
    public async Task Sin_ningun_centro_visible_no_devuelve_nada_aunque_vea_al_trabajador()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [_ana.Id], centroIdsVisibles: []);

        var resultado = await ConsultarAsync(alcance);

        resultado.Should().BeEmpty("una lista vacía es ningún Centro (fallo cerrado), no «sin restricción»");
        _situacion.CentrosConsultados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_restriccion_devuelve_todos_los_centros_y_sus_acreditaciones()
    {
        var resultado = await ConsultarAsync(new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        resultado.Select(c => c.CentroId).Should().BeEquivalentTo([_visible.Id, _ajeno.Id]);
        foreach (var fila in resultado)
            fila.Documentos.Should().ContainSingle().Which.AcreditacionesEnElCentro.Should().ContainSingle()
                .Which.NombrePlataforma.Should().Be(SituacionQueAcreditaTodo.Plataforma(fila.CentroId));
    }

    /// <summary>
    /// Acredita cada documento pedido en cada Centro pedido, con una Plataforma CAE que nombra al Centro: así el test ve
    /// tanto qué Centros le llegan al servicio como qué acreditaciones acaban en la respuesta.
    /// </summary>
    private sealed class SituacionQueAcreditaTodo : ISituacionDocumentosEnCentrosService
    {
        public List<Guid> CentrosConsultados { get; } = [];

        public static string Plataforma(Guid centroId) => $"Plataforma {centroId}";

        public Task<SituacionDocumentosEnCentros> CargarAsync(
            IReadOnlyCollection<Guid> centroIds,
            IReadOnlyCollection<Guid> documentoIds,
            IReadOnlyCollection<Guid> trabajadorIdsConAusentes,
            IReadOnlyCollection<Guid> tipoDocumentoIdsDeAusentes,
            CancellationToken cancellationToken)
        {
            CentrosConsultados.AddRange(centroIds);
            var acreditaciones = (
                    from centroId in centroIds
                    from documentoId in documentoIds
                    select (centroId, documentoId))
                .ToDictionary(
                    clave => clave,
                    clave => (IReadOnlyList<AcreditacionResumenDto>)
                        [new AcreditacionResumenDto(Guid.NewGuid(), Plataforma(clave.centroId), EstadoAcreditacion.Rechazada)]);

            return Task.FromResult(new SituacionDocumentosEnCentros(
                acreditaciones,
                new Dictionary<Guid, UltimaReclamacionDocumentoDto>(),
                new Dictionary<(Guid, Guid), UltimaReclamacionDocumentoDto>()));
        }
    }
}
