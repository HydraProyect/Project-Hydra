using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Queries.PrepararEntregaArchivoExtension;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Antes de que el archivo salga hacia una plataforma CAE de terceros, el servidor comprueba por sí mismo la cartera, el
/// estado, el kill switch y el portal: nada de esto depende de lo que la extensión diga haber mirado.
/// </summary>
public class PrepararEntregaArchivoExtensionQueryHandlerTests
{
    private const string Portal = "app.dokify.net";
    private static readonly Guid TrabajadorId = Guid.NewGuid();

    private sealed record Escenario(
        Documento Documento, AcreditacionDocumentoPlataforma Acreditacion, PrepararEntregaArchivoExtensionQueryHandler Manejador)
    {
        public Task<CaeManager.Domain.Common.Result<EntregaArchivoExtensionDto>> PedirAsync(string? portal = Portal) =>
            Manejador.Handle(new PrepararEntregaArchivoExtensionQuery(Acreditacion.Id, portal), CancellationToken.None);
    }

    private static Escenario Crear(
        Action<AcreditacionDocumentoPlataforma>? preparar = null, bool proveedorActivo = true, Func<Guid, AlcanceDatosServiceFalso>? alcance = null,
        bool canalPorCorreo = false, ModalidadGestionCae? gestion = null, bool documentoOperativo = true)
    {
        var documento = Documento.DeTrabajador(TrabajadorId, Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Almacén Norte");
        if (gestion is { } modalidad)
            centro.EstablecerGestionCae(modalidad);
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify", activo: proveedorActivo);
        var canal = canalPorCorreo
            ? CanalGestionDocumental.PorEmail(centro.Id, "Por correo", "cae@example.test", null)
            : CanalGestionDocumental.DePlataforma(centro.Id, "Portal principal", proveedor.Id, null, null, null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        preparar?.Invoke(acreditacion);

        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var centrosContext = new CentrosQueryContextFalso();
        centrosContext.ListaCentros.Add(centro);
        centrosContext.ListaCanalesGestionDocumental.Add(canal);
        var proveedoresContext = new ProveedoresPlataformaCaeQueryContextFalso();
        proveedoresContext.ListaProveedores.Add(proveedor);
        proveedoresContext.ListaDominios.Add(new DominioProveedorPlataformaCae(proveedor.Id, "dokify.net"));
        var documentosContext = new DocumentosQueryContextFalso();
        if (documentoOperativo)
            documentosContext.ListaDocumentos.Add(documento);

        return new Escenario(documento, acreditacion, new PrepararEntregaArchivoExtensionQueryHandler(
            acreditacionRepositorio, documentoRepositorio, alcance?.Invoke(centro.Id) ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            centrosContext, proveedoresContext, documentosContext));
    }

    [Fact]
    public async Task Pendiente_visible_en_su_portal_devuelve_el_documento_y_el_host_normalizado()
    {
        var e = Crear();

        var resultado = await e.PedirAsync("https://App.Dokify.net/subir?x=1");

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        resultado.Valor.DocumentoId.Should().Be(e.Documento.Id);
        resultado.Valor.HostPortal.Should().Be("app.dokify.net");
    }

    [Fact]
    public async Task Fuera_de_la_Asignacion_de_Cartera_es_no_encontrada()
    {
        var e = Crear(alcance: _ => new AlcanceDatosServiceFalso(centroIdsParaGestion: [Guid.NewGuid()]));

        var resultado = await e.PedirAsync();

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
    }

    [Fact]
    public async Task Dentro_de_la_Asignacion_de_Cartera_se_entrega()
    {
        var e = Crear(alcance: centroId => new AlcanceDatosServiceFalso(centroIdsParaGestion: [centroId]));

        (await e.PedirAsync()).EsExitoso.Should().BeTrue("control positivo de la prueba anterior");
    }

    [Fact]
    public async Task Un_Documento_fuera_del_alcance_aunque_el_Centro_este_en_cartera_es_no_encontrada()
    {
        var fuera = Crear(alcance: centroId => new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()], centroIdsParaGestion: [centroId]));
        var dentro = Crear(alcance: centroId => new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [TrabajadorId], centroIdsParaGestion: [centroId]));

        (await fuera.PedirAsync()).Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        (await dentro.PedirAsync()).EsExitoso.Should().BeTrue("control positivo: solo cambia el Trabajador visible");
    }

    public static TheoryData<EstadoAcreditacion> EstadosNoPendientes => new()
    {
        EstadoAcreditacion.Subida, EstadoAcreditacion.Aceptada, EstadoAcreditacion.Rechazada,
    };

    [Theory]
    [MemberData(nameof(EstadosNoPendientes))]
    public async Task Lo_que_ya_no_esta_pendiente_de_subir_es_no_encontrada(EstadoAcreditacion estado)
    {
        var e = Crear(a =>
        {
            switch (estado)
            {
                case EstadoAcreditacion.Subida: a.MarcarSubida(); break;
                case EstadoAcreditacion.Aceptada: a.MarcarSubida(); a.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui); break;
                default: a.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow); break;
            }
        });

        e.Acreditacion.Estado.Should().Be(estado, "precondición de la prueba");
        (await e.PedirAsync()).Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
    }

    [Fact]
    public async Task Un_canal_que_no_es_de_plataforma_es_no_encontrada()
    {
        var e = Crear(canalPorCorreo: true);

        (await e.PedirAsync()).Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
    }

    [Fact]
    public async Task Un_Centro_sin_gestion_CAE_es_no_encontrada()
    {
        var e = Crear(gestion: ModalidadGestionCae.SinGestionCae);

        (await e.PedirAsync()).Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
    }

    [Fact]
    public async Task Un_Documento_que_ya_no_es_operativo_es_no_encontrada()
    {
        var e = Crear(documentoOperativo: false);

        (await e.PedirAsync()).Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
    }

    [Fact]
    public async Task Con_el_conector_apagado_no_se_entrega()
    {
        var e = Crear(proveedorActivo: false);

        (await e.PedirAsync()).Error.Codigo.Should().Be(ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Sin_portal_declarado_no_se_entrega(string? portal)
    {
        var e = Crear();

        (await e.PedirAsync(portal)).Error.Codigo.Should().Be(ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalNoDeclarado);
    }

    [Theory]
    [InlineData("app.nalandaglobal.com")]
    [InlineData("notdokify.net")]
    public async Task En_el_portal_de_otra_plataforma_no_se_entrega_y_nombra_la_correcta(string portal)
    {
        var e = Crear();

        var resultado = await e.PedirAsync(portal);

        resultado.Error.Codigo.Should().Be(ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalAjeno);
        resultado.Error.Mensaje.Should().Contain("Dokify");
    }
}
