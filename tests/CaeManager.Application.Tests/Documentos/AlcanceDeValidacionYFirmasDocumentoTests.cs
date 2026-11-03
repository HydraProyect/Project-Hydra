using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// SEG-16 (OWASP ASVS 4.0.3 V4.2.1): la validación oficial y las firmas de un Documento fuera de la Asignación de
/// Cartera de quien pide se comportan igual que un Documento inexistente, sea cual sea su ámbito, y sin depender
/// de que la pantalla lo haya comprobado antes. El alcance es el de lectura de <c>ObtenerDocumentoPorIdQuery</c>.
/// </summary>
public class AlcanceDeValidacionYFirmasDocumentoTests
{
    private static readonly DateOnly Emision = new(2026, 1, 1);
    private static readonly string Hash = new('a', FirmaEnCampoDocumento.LongitudHashSha256);

    public enum Ambito { Trabajador, Cliente, Vehiculo, Proyecto, Empresa }

    private sealed class Seleccion(bool soporte) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => soporte ? Guid.NewGuid() : null;
    }

    private sealed record Escenario(
        DocumentosQueryContextFalso Documentos, ProyectosQueryContextFalso Proyectos, Guid DocumentoId, Guid PropietarioId)
    {
        public Task<ValidacionOficialDocumentoDto?> ValidacionAsync(IAlcanceDatosService alcance, bool soporte = false) =>
            new ObtenerValidacionOficialDocumentoQueryHandler(Documentos, Proyectos, alcance, new Seleccion(soporte))
                .Handle(new(DocumentoId), CancellationToken.None);

        public Task<IReadOnlyList<FirmaEnCampoDocumentoDto>> FirmasAsync(IAlcanceDatosService alcance) =>
            new ObtenerFirmasEnCampoDocumentoQueryHandler(Documentos, Proyectos, alcance)
                .Handle(new(DocumentoId), CancellationToken.None);
    }

    private static Escenario Sembrar(Ambito ambito)
    {
        var documentos = new DocumentosQueryContextFalso();
        var proyectos = new ProyectosQueryContextFalso();
        var tipoId = Guid.NewGuid();
        var propietarioId = Guid.NewGuid();
        var documento = ambito switch
        {
            Ambito.Trabajador => Documento.DeTrabajador(propietarioId, tipoId, Emision, VigenciaDocumento.NoCaduca),
            Ambito.Cliente => Documento.DeCliente(propietarioId, tipoId, Emision, VigenciaDocumento.NoCaduca),
            Ambito.Vehiculo => Documento.DeVehiculo(propietarioId, tipoId, Emision, VigenciaDocumento.NoCaduca),
            Ambito.Empresa => Documento.DeEmpresa(propietarioId, tipoId, Emision, VigenciaDocumento.NoCaduca),
            _ => DeProyecto(proyectos, propietarioId, tipoId),
        };
        documentos.ListaDocumentos.Add(documento);
        documentos.ListaVerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            documento.Id, PerfilDocumentoOficial.CorrienteTgss, NivelConfianzaDocumental.FirmaValidaSinRevocacion,
            "CODIGO", null, null, null, null, ResultadoCotejoDocumentoOficial.Coincide,
            DecisionValidacionOficial.AutoValidado, ""));
        documentos.ListaFirmasDigitalesDocumento.Add(new FirmaDigitalDocumento(
            documento.Id, 0, EstadoFirmaPdf.Valida, cadenaConfiable: true, ComprobacionRevocacion.EmbebidaLtv,
            "Emisor", "SN-1", esSelloDeOrgano: false, "Ana López", "12345678Z", DateTime.UtcNow,
            tieneSelloDeTiempo: true, cubreDocumentoCompleto: true, motivoInvalidez: null));
        documentos.ListaFirmasEnCampoDocumento.Add(new FirmaEnCampoDocumento(
            documento.Id, Guid.NewGuid(), "Ana López", "GestorCae", DateTime.UtcNow, null, Hash));
        return new Escenario(documentos, proyectos, documento.Id, propietarioId);
    }

    private static Documento DeProyecto(ProyectosQueryContextFalso proyectos, Guid clienteEmpresarialId, Guid tipoId)
    {
        var proyecto = Proyecto.Crear(clienteEmpresarialId, Guid.NewGuid(), "Obra de la nave", Emision, null, null);
        proyectos.ListaProyectos.Add(proyecto);
        return Documento.DeProyecto(proyecto.Id, tipoId, Emision, VigenciaDocumento.NoCaduca);
    }

    private static AlcanceDatosServiceFalso AlcanceSolo(Ambito ambito, Guid propietarioId) => ambito switch
    {
        Ambito.Trabajador => new(tieneAccesoTotal: false, trabajadorIdsVisibles: [propietarioId]),
        Ambito.Vehiculo => new(tieneAccesoTotal: false, vehiculoIdsVisibles: [propietarioId]),
        Ambito.Empresa => new(tieneAccesoTotal: false, empresaIdsVisibles: [propietarioId]),
        _ => new(tieneAccesoTotal: false, clienteIdsVisibles: [propietarioId]),
    };

    [Theory]
    [InlineData(Ambito.Trabajador)]
    [InlineData(Ambito.Cliente)]
    [InlineData(Ambito.Vehiculo)]
    [InlineData(Ambito.Proyecto)]
    [InlineData(Ambito.Empresa)]
    public async Task Fuera_de_la_cartera_no_devuelve_ni_validacion_ni_firmas(Ambito ambito)
    {
        var escenario = Sembrar(ambito);
        // Trabajadores y Vehículos del doble: null es «sin restricción», así que el vacío va explícito.
        var alcanceVacio = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [], vehiculoIdsVisibles: []);

        (await escenario.ValidacionAsync(alcanceVacio)).Should().BeNull();
        (await escenario.FirmasAsync(alcanceVacio)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Ambito.Trabajador)]
    [InlineData(Ambito.Cliente)]
    [InlineData(Ambito.Vehiculo)]
    [InlineData(Ambito.Proyecto)]
    [InlineData(Ambito.Empresa)]
    public async Task Dentro_de_la_cartera_devuelve_la_validacion_con_su_NIF_y_las_firmas(Ambito ambito)
    {
        var escenario = Sembrar(ambito);
        var alcance = AlcanceSolo(ambito, escenario.PropietarioId);

        var validacion = await escenario.ValidacionAsync(alcance);

        validacion.Should().NotBeNull();
        validacion!.Firmas.Should().ContainSingle().Which.FirmanteNif.Should().Be("12345678Z");
        (await escenario.FirmasAsync(alcance)).Should().ContainSingle();
    }

    [Fact]
    public async Task El_alcance_de_Empresa_es_el_de_lectura_y_no_el_de_gestion()
    {
        // Rol Cliente de portal: lee la documentación de sus contratistas pero no las gestiona. Si la puerta fuera
        // DocumentoVisibleAsync, la pestaña «Validación» de ese usuario se quedaría vacía (REC-149).
        var escenario = Sembrar(Ambito.Empresa);
        var portal = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsVisibles: [escenario.PropietarioId], empresaIdsParaGestion: []);

        (await escenario.ValidacionAsync(portal)).Should().NotBeNull();
        (await escenario.FirmasAsync(portal)).Should().ContainSingle();
    }

    [Fact]
    public async Task Sin_el_Documento_no_expone_lo_que_cuelga_de_el()
    {
        // Documento dado de baja: el filtro global lo oculta y sus filas de validación y firmas siguen ahí.
        var escenario = Sembrar(Ambito.Empresa);
        escenario.Documentos.ListaDocumentos.Clear();
        var alcanceTotal = new AlcanceDatosServiceFalso();

        (await escenario.ValidacionAsync(alcanceTotal)).Should().BeNull();
        (await escenario.FirmasAsync(alcanceTotal)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sesion_Privilegiada_recibe_el_alcance_del_servicio_sin_excepcion_propia()
    {
        var escenario = Sembrar(Ambito.Empresa);

        (await escenario.ValidacionAsync(new AlcanceDatosServiceFalso(), soporte: true)).Should().NotBeNull(
            "con acceso total dentro del Tenant objetivo no se rebaja nada");
        (await escenario.ValidacionAsync(new AlcanceDatosServiceFalso(tieneAccesoTotal: false), soporte: true)).Should().BeNull(
            "una capacidad sin acceso total no se amplía por venir de una Sesión Privilegiada");
    }
}
