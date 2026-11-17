using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerLecturaInbound;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.ContratosAcceso;

/// <summary>
/// El archivo pedido desde la cola de validación (demo Inbound P6): la Gestora CAE del Tenant TCE abre el original que
/// subió el Tenant TEC. Falla cerrado con null (404 en Web) sin ValidarInbound, sin LeerInbound, con un rol que no valida,
/// fuera de la Asignación de Cartera, con un contrato que no ve (de otro Tenant TCE o eliminado) y cuando la función de la
/// base no concede el Documento (no autorizado, revocado, sustituido, de un tipo de salud sin marca). La traza
/// <c>Apertura</c> en <c>AccesosLecturaInbound</c> del Tenant TEC la escribe esa función en la misma sentencia que da la
/// lectura: aquí se observa como la llamada a <see cref="ILectorDocumentosInbound.ArchivoAutorizadoAsync"/>, que solo ocurre
/// después de la autoridad. Que la base escriba la fila y no la escriba en una negativa lo prueba
/// <c>EvidenciasInboundBajoRuntimeTests</c> bajo <c>cae_app_runtime</c>.
/// </summary>
public class ObtenerArchivoParaValidarInboundTests
{
    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _tenantTec = Guid.NewGuid();
    private readonly Guid _documento = Guid.NewGuid();
    private readonly ContratosAccesoQueryContextFalso _contratos = new();
    private readonly LectorFalso _lector = new();
    private readonly AlmacenOriginalesInboundFalso _originales = new();
    private readonly ContratoAcceso _contrato;

    public ObtenerArchivoParaValidarInboundTests()
    {
        _contrato = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        _contrato.VincularTenantContratista(_tenantTec);
        _contratos.ListaContratos.Add(_contrato);
        _lector.Respuesta = new ArchivoInboundDto(_documento, _tenantTec, $"{_tenantTec:N}/original.pdf", false);
        _originales.Archivos[(_tenantTec, $"{_tenantTec:N}/original.pdf")] = [7, 8, 9];
    }

    private ObtenerArchivoParaValidarInboundQueryHandler Handler(
        IAutorizacionInbound? puerta = null, string? rol = "GestorCae", Guid? clienteEnCartera = null) =>
        new(_contratos, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEnCartera ?? _cliente]),
            new UsuarioConRol(rol), _lector, _originales, puerta ?? new AutorizacionInboundQuePermite());

    private Task<ContenidoArchivoInboundDto?> PedirAsync(ObtenerArchivoParaValidarInboundQueryHandler handler, Guid? contrato = null) =>
        handler.Handle(new ObtenerArchivoParaValidarInboundQuery(contrato ?? _contrato.Id, _documento), CancellationToken.None);

    [Fact]
    public async Task Con_las_dos_capacidades_y_la_cartera_sirve_el_contenido_y_la_lectura_pasa_por_la_funcion_que_deja_la_traza()
    {
        var abierto = await PedirAsync(Handler());

        abierto!.Contenido.Should().Equal(7, 8, 9);
        abierto.TipoMime.Should().Be("application/pdf");
        _lector.Aperturas.Should().Equal([(_contrato.Id, _documento)],
            "una sola apertura, por app_inbound_archivo_autorizado, que es la que inserta la fila Apertura en el Tenant TEC");
        _originales.Aperturas.Should().Equal([(_tenantTec, $"{_tenantTec:N}/original.pdf")],
            "el binario se abre con la carpeta y la clave del Tenant TEC del contrato");
    }

    [Theory]
    [InlineData(CapacidadInbound.ValidarInbound)]
    [InlineData(CapacidadInbound.LeerInbound)]
    public async Task Sin_una_de_las_dos_capacidades_responde_no_encontrado_sin_leer_ni_dejar_traza(CapacidadInbound negada)
    {
        var abierto = await PedirAsync(Handler(new PuertaQueNiega(negada)));

        abierto.Should().BeNull();
        _lector.Aperturas.Should().BeEmpty("sin la capacidad no se llama a la base, que dejaría traza en el Tenant TEC");
        _originales.Aperturas.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Un_rol_que_no_valida_responde_no_encontrado_sin_leer(string? rol)
    {
        (await PedirAsync(Handler(rol: rol))).Should().BeNull();
        _lector.Aperturas.Should().BeEmpty();
    }

    [Fact]
    public async Task Fuera_de_la_Asignacion_de_Cartera_responde_no_encontrado_sin_leer()
    {
        (await PedirAsync(Handler(clienteEnCartera: Guid.NewGuid()))).Should().BeNull();
        _lector.Aperturas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_contrato_que_el_Tenant_TCE_no_ve_responde_igual_que_uno_fuera_de_cartera()
    {
        // El contrato de otro Tenant TCE no llega al contexto (RLS y filtro de Tenant), igual que uno inexistente; el
        // eliminado lo quita el filtro de borrado lógico.
        var eliminado = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        eliminado.VincularTenantContratista(_tenantTec);
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        _contratos.ListaContratos.Add(eliminado);

        (await PedirAsync(Handler(), contrato: Guid.NewGuid())).Should().BeNull();
        (await PedirAsync(Handler(), contrato: eliminado.Id)).Should().BeNull();
        _lector.Aperturas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_que_la_base_no_concede_responde_no_encontrado_sin_abrir_nada()
    {
        // No autorizado, revocado, sustituido o de un tipo de salud sin marca: la función responde sin fila (y sin traza).
        _lector.Respuesta = null;

        (await PedirAsync(Handler())).Should().BeNull();
        _lector.Aperturas.Should().ContainSingle("la base es quien decide el Documento");
        _originales.Aperturas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_referencia_de_otra_carpeta_o_un_binario_ausente_responden_no_encontrado()
    {
        _lector.Respuesta = new ArchivoInboundDto(_documento, _tenantTec, $"{Guid.NewGuid():N}/original.pdf", false);
        (await PedirAsync(Handler())).Should().BeNull("la referencia tiene que ser de la carpeta del Tenant TEC del contrato");
        _originales.Aperturas.Should().BeEmpty();

        _lector.Respuesta = new ArchivoInboundDto(_documento, _tenantTec, $"{_tenantTec:N}/no-esta.pdf", false);
        (await PedirAsync(Handler())).Should().BeNull("el almacén no tiene el binario");
    }

    [Fact]
    public async Task El_certificado_de_solo_conclusion_de_aptitud_se_abre_porque_lo_valida_el_Gestor_CAE()
    {
        // DN-5 (b) y DECISION_LOG 2026-11-13: el Gestor CAE del Tenant TCE abre el certificado (que por ley solo lleva la
        // conclusión) para validarlo. Ningún otro tipo de salud llega aquí: la función de la base no lo deja pasar.
        _lector.Respuesta = new ArchivoInboundDto(_documento, _tenantTec, $"{_tenantTec:N}/original.pdf", SoloConclusionAptitud: true);

        (await PedirAsync(Handler()))!.Contenido.Should().Equal(7, 8, 9);
    }

    private sealed class PuertaQueNiega(CapacidadInbound negada) : IAutorizacionInbound
    {
        public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken) =>
            Task.FromResult(capacidad != negada);
    }

    private sealed class LectorFalso : ILectorDocumentosInbound
    {
        public ArchivoInboundDto? Respuesta { get; set; }

        public List<(Guid Contrato, Guid Documento)> Aperturas { get; } = [];

        public Task<AutorizacionInboundDto?> AutorizacionDelContratoAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("La apertura no consulta la autorización: la decide la función de la apertura.");

        public Task<IReadOnlyList<DocumentoInboundDto>> DocumentosAutorizadosAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("La apertura no lista: dejaría una traza Listado que no corresponde.");

        public Task<ArchivoInboundDto?> ArchivoAutorizadoAsync(Guid contratoAccesoId, Guid documentoId, CancellationToken cancellationToken = default)
        {
            Aperturas.Add((contratoAccesoId, documentoId));
            return Task.FromResult(Respuesta);
        }
    }

    private sealed class UsuarioConRol(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
