using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.ElegirAvatarPropio;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// Catálogo de avatares de usuario (decisión de producto del 2026-10-08): qué es una
/// clave válida, y quién puede elegir el avatar de qué cuenta. La autorización vive en el
/// handler; aquí se prueba contra un doble del puerto que registra si se llegó a escribir.
/// La escritura real bajo RLS con el rol de runtime está en <c>AvatarDeCuentasBajoRuntimeTests</c>
/// (IntegrationTests).
/// </summary>
public class AvatarDeUsuarioTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Soporte = Guid.NewGuid();

    // ---------- Catálogo ----------

    [Fact]
    public void Toda_combinacion_del_catalogo_se_resuelve_a_si_misma_y_cabe_en_la_columna()
    {
        CatalogoAvatares.Motivos.Should().NotBeEmpty();
        CatalogoAvatares.Tonos.Should().NotBeEmpty();

        foreach (var motivo in CatalogoAvatares.Motivos)
            foreach (var tono in CatalogoAvatares.Tonos)
            {
                var clave = CatalogoAvatares.Clave(motivo.Clave, tono);

                clave.Length.Should().BeLessThanOrEqualTo(CatalogoAvatares.LongitudMaximaClave, clave);
                var elegido = CatalogoAvatares.Resolver(clave);
                elegido.Should().NotBeNull(clave);
                elegido!.Motivo.Should().Be(motivo);
                elegido.Tono.Should().Be(tono);
                elegido.Clave.Should().Be(clave);
            }
    }

    [Fact]
    public void Las_claves_y_los_emojis_del_catalogo_no_se_repiten()
    {
        CatalogoAvatares.Motivos.Select(m => m.Clave).Should().OnlyHaveUniqueItems();
        CatalogoAvatares.Motivos.Select(m => m.Emoji).Should().OnlyHaveUniqueItems();
        CatalogoAvatares.Tonos.Should().OnlyHaveUniqueItems();
        // La clave se guarda en base de datos y va en nombres de recurso y de clase CSS.
        CatalogoAvatares.Motivos.Select(m => m.Clave).Concat(CatalogoAvatares.Tonos)
            .Should().AllSatisfy(clave => clave.Should().MatchRegex("^[a-z]+$"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zorro")]            // sin tono
    [InlineData("-verde")]           // sin motivo
    [InlineData("zorro-fucsia")]     // tono que no existe
    [InlineData("dragon-verde")]     // motivo que no existe
    [InlineData("Zorro-Verde")]      // las claves son exactas
    [InlineData("zorro-verde ")]
    [InlineData("🦊")]               // nunca se guarda el emoji
    public void Lo_que_no_es_una_clave_del_catalogo_se_lee_como_sin_avatar(string? clave) =>
        CatalogoAvatares.Resolver(clave).Should().BeNull();

    // ---------- Elegir el avatar propio ----------

    [Fact]
    public async Task Guarda_el_avatar_elegido_en_la_propia_cuenta()
    {
        var puerto = new AvataresFalso();

        var resultado = await Handler(puerto).Handle(new ElegirAvatarPropioCommand("buho-ambar"), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        puerto.Escrituras.Should().Equal((Usuario, "buho-ambar"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Sin_clave_quita_el_avatar_y_vuelven_las_iniciales(string? clave)
    {
        var puerto = new AvataresFalso { [Usuario] = "zorro-azul" };

        var resultado = await Handler(puerto).Handle(new ElegirAvatarPropioCommand(clave), default);

        resultado.EsExitoso.Should().BeTrue();
        puerto.Escrituras.Should().Equal((Usuario, (string?)null));
    }

    [Theory]
    [InlineData("dragon-verde")]
    [InlineData("zorro-fucsia")]
    [InlineData("<script>")]
    public async Task Una_clave_que_no_esta_en_el_catalogo_no_se_guarda(string clave)
    {
        var puerto = new AvataresFalso();

        var resultado = await Handler(puerto).Handle(new ElegirAvatarPropioCommand(clave), default);

        resultado.Error.Codigo.Should().Be("Avatar.NoEstaEnElCatalogo");
        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_simula_a_otro_usuario_no_le_cambia_el_avatar()
    {
        var puerto = new AvataresFalso();
        var simulacion = new ActorAuditoria(Soporte, Usuario, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());
        var handler = new ElegirAvatarPropioCommandHandler(
            new CurrentUserServiceFalso(Usuario, "GestorCae"), new ActorFijo(simulacion), puerto);

        var resultado = await handler.Handle(new ElegirAvatarPropioCommand("zorro-azul"), default);

        resultado.Error.Codigo.Should().Be("Avatar.SoloLaPropiaCuenta");
        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_usuario_resuelto_no_guarda_nada()
    {
        var puerto = new AvataresFalso();
        var handler = new ElegirAvatarPropioCommandHandler(
            new CurrentUserServiceFalso(), new ActorFijo(ActorAuditoria.Normal(Usuario)), puerto);

        var resultado = await handler.Handle(new ElegirAvatarPropioCommand("zorro-azul"), default);

        resultado.Error.Codigo.Should().Be("Avatar.SinUsuario");
        puerto.Escrituras.Should().BeEmpty();
    }

    // ---------- Leer el avatar propio ----------

    [Fact]
    public async Task La_consulta_devuelve_la_clave_de_quien_mira_y_no_la_de_otra_cuenta()
    {
        var puerto = new AvataresFalso { [Usuario] = "rana-verde", [Soporte] = "oso-neutro" };

        var clave = await new ObtenerAvatarPropioQueryHandler(new CurrentUserServiceFalso(Usuario), puerto)
            .Handle(new ObtenerAvatarPropioQuery(), default);

        clave.Should().Be("rana-verde");
    }

    [Fact]
    public async Task Sin_sesion_la_consulta_no_lee_ninguna_cuenta()
    {
        var puerto = new AvataresFalso { [Usuario] = "rana-verde" };

        var clave = await new ObtenerAvatarPropioQueryHandler(new CurrentUserServiceFalso(), puerto)
            .Handle(new ObtenerAvatarPropioQuery(), default);

        clave.Should().BeNull();
        puerto.Lecturas.Should().Be(0);
    }

    private static ElegirAvatarPropioCommandHandler Handler(IAvatarDeCuentas puerto) =>
        new(new CurrentUserServiceFalso(Usuario, "Consulta"), new ActorFijo(ActorAuditoria.Normal(Usuario)), puerto);

    private sealed class AvataresFalso : IAvatarDeCuentas
    {
        private readonly Dictionary<Guid, string?> _avatares = [];

        public List<(Guid UsuarioId, string? Clave)> Escrituras { get; } = [];
        public int Lecturas { get; private set; }

        public string? this[Guid usuarioId]
        {
            set => _avatares[usuarioId] = value;
        }

        public Task<string?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Lecturas++;
            return Task.FromResult(_avatares.GetValueOrDefault(usuarioId));
        }

        public Task<Result> GuardarAsync(Guid usuarioId, string? clave, CancellationToken cancellationToken = default)
        {
            Escrituras.Add((usuarioId, clave));
            _avatares[usuarioId] = clave;
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
