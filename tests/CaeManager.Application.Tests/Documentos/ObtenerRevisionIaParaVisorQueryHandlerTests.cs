using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// La revisión que sirve <c>GET /documentos/revision-ia/{revisionId}/archivo</c> (D-49, opción C): el Documento de ESA
/// revisión, y solo si quien pide la alcanza en su cola de Revisión IA. Todo lo demás —revisión resuelta, inexistente,
/// Documento fuera de la Asignación de Cartera del Gestor CAE, rol efectivo sin acceso a la pestaña o sin rol— contesta
/// <c>null</c>, que la ruta convierte en 404 sin abrir ni registrar nada.
///
/// <para>
/// Qué no observa: el filtro de Tenant propietario. Lo ponen el filtro global de EF y la RLS, que estos contextos en
/// memoria no tienen (igual que <see cref="ObtenerDocumentosVenceEnYRevisionIaTests"/>); lo prueba la suite de integración
/// (<c>DescargasConIdDeOtroTenantBajoRuntimeTests</c>).
/// </para>
/// </summary>
public class ObtenerRevisionIaParaVisorQueryHandlerTests
{
    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public Guid TipoId { get; } = Guid.NewGuid();

        public ObtenerRevisionIaParaVisorQueryHandler Handler(string? rol = "Administrador", AlcanceDatosServiceFalso? alcance = null) =>
            new(Documentos, alcance ?? new(), new CurrentUserServiceFalso(rol: rol));

        public Documento DeEmpresa()
        {
            var documento = Documento.DeEmpresa(Guid.NewGuid(), TipoId, new(2026, 1, 1), VigenciaDocumento.NoCaduca);
            Documentos.ListaDocumentos.Add(documento);
            return documento;
        }

        public Documento DeTrabajador(Guid trabajadorId)
        {
            var documento = Documento.DeTrabajador(trabajadorId, TipoId, new(2026, 1, 1), VigenciaDocumento.NoCaduca);
            Documentos.ListaDocumentos.Add(documento);
            return documento;
        }

        public RevisionIaDocumento EnRevisionIa(Documento documento, bool resuelta = false)
        {
            var revision = RevisionIaDocumento.Crear(documento.Id, 60, null, null, null, null, "Revisar");
            if (resuelta) revision.Resolver();
            Documentos.ListaRevisionesIaDocumento.Add(revision);
            return revision;
        }
    }

    private static Task<RevisionIaParaVisorDto?> PedirAsync(ObtenerRevisionIaParaVisorQueryHandler handler, Guid revisionId) =>
        handler.Handle(new ObtenerRevisionIaParaVisorQuery(revisionId), default);

    [Fact]
    public async Task Una_revision_pendiente_al_alcance_devuelve_su_Documento_y_desde_cuando_existe()
    {
        var e = new Escenario();
        var documento = e.DeEmpresa();
        var revision = e.EnRevisionIa(documento);

        var resultado = await PedirAsync(e.Handler(), revision.Id);

        resultado.Should().Be(new RevisionIaParaVisorDto(documento.Id));
    }

    [Fact]
    public async Task Devuelve_el_Documento_de_la_revision_pedida_y_no_el_de_otra_pendiente()
    {
        var e = new Escenario();
        var primero = e.DeEmpresa();
        var segundo = e.DeEmpresa();
        e.EnRevisionIa(primero);
        var pedida = e.EnRevisionIa(segundo);

        (await PedirAsync(e.Handler(), pedida.Id))!.DocumentoId.Should().Be(segundo.Id);
    }

    [Fact]
    public async Task Una_revision_inexistente_no_sirve_nada()
    {
        var e = new Escenario();
        e.EnRevisionIa(e.DeEmpresa());

        (await PedirAsync(e.Handler(), Guid.NewGuid())).Should().BeNull();
    }

    /// <summary>La revisión se resolvió (en otra pestaña, por otra persona): ya no está en la cola y su visor no se sirve.</summary>
    [Fact]
    public async Task Una_revision_ya_resuelta_no_sirve_nada()
    {
        var e = new Escenario();
        var documento = e.DeEmpresa();
        var resuelta = e.EnRevisionIa(documento, resuelta: true);
        var pendiente = e.EnRevisionIa(documento);

        (await PedirAsync(e.Handler(), resuelta.Id)).Should().BeNull("una revisión resuelta no está en la cola");
        (await PedirAsync(e.Handler(), pendiente.Id)).Should().NotBeNull("control: la pendiente del mismo Documento sí");
    }

    [Fact]
    public async Task Un_Documento_de_Trabajador_fuera_de_la_Asignacion_de_Cartera_del_Gestor_CAE_no_se_sirve()
    {
        var e = new Escenario();
        var trabajadorDeLaCartera = Guid.NewGuid();
        var dentro = e.EnRevisionIa(e.DeTrabajador(trabajadorDeLaCartera));
        var fuera = e.EnRevisionIa(e.DeTrabajador(Guid.NewGuid()));
        var cartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajadorDeLaCartera]);

        (await PedirAsync(e.Handler("GestorCae", cartera), fuera.Id)).Should().BeNull("el Trabajador no está en su cartera");
        (await PedirAsync(e.Handler("GestorCae", cartera), dentro.Id)).Should().NotBeNull("control: el de su cartera sí");
        (await PedirAsync(e.Handler("GestorCae"), fuera.Id)).Should().NotBeNull("control: con visión total sí");
    }

    /// <summary>Con una cartera acotada, la cola no enseña revisiones de Empresa (no mapean al modelo de cartera): tampoco su visor.</summary>
    [Fact]
    public async Task Con_cartera_acotada_un_Documento_de_Empresa_no_se_sirve()
    {
        var e = new Escenario();
        var revision = e.EnRevisionIa(e.DeEmpresa());
        var cartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()]);

        (await PedirAsync(e.Handler("GestorCae", cartera), revision.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Los_roles_que_acceden_a_la_pestana_reciben_la_revision(string rol)
    {
        var e = new Escenario();
        var revision = e.EnRevisionIa(e.DeEmpresa());

        (await PedirAsync(e.Handler(rol), revision.Id)).Should().NotBeNull();
    }

    /// <summary>
    /// Rol efectivo sin acceso a la pestaña Revisión IA, o sin rol efectivo (Sesión Privilegiada, sin sesión): falla
    /// cerrado aunque la revisión esté pendiente y el alcance sea total.
    /// </summary>
    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData("Inventado")]
    [InlineData(null)]
    public async Task Un_rol_efectivo_sin_acceso_a_la_pestana_o_sin_rol_no_recibe_nada(string? rol)
    {
        var e = new Escenario();
        var revision = e.EnRevisionIa(e.DeEmpresa());

        (await PedirAsync(e.Handler(rol), revision.Id)).Should().BeNull();
    }
}
