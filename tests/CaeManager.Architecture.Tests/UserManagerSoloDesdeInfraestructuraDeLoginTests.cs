using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Web no llama a los gestores de Identity fuera de la infraestructura de
/// login</b> (P1-I2 del plan de madurez 09-24).
///
/// <para>
/// Una página que llama a <c>UserManager</c> decide ella misma quién puede crear,
/// editar, activar o dar rol a una cuenta, y cualquier otro camino hasta
/// <c>UserManager</c> no hereda esa autorización. Por eso la gestión de cuentas
/// vive en Commands de Application (<c>Usuarios/Commands</c>) con su autorización
/// en el handler, y llega a Identity por el puerto <c>IGestionCuentasUsuario</c>.
/// <c>Usuarios.razor.cs</c> y <c>Roles.razor.cs</c> ya no inyectan ningún gestor.
/// </para>
///
/// <para>
/// <b>Lo que SÍ observa:</b> todo fichero <c>.cs</c> o <c>.razor</c> de
/// <c>src/CaeManager.Web</c> que nombre <c>UserManager&lt;</c>,
/// <c>SignInManager&lt;</c> o <c>RoleManager&lt;</c> fuera de un comentario
/// (inyección, parámetro de constructor o de endpoint). El conjunto tiene que ser
/// exactamente <see cref="InfraestructuraDeLogin"/> más <see cref="DeudaCongelada"/>:
/// un fichero nuevo lo hace crecer y pone esto en rojo; uno que se migre lo hace
/// menguar y también, para que se retire de la lista en el mismo commit.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados):
/// </para>
/// <list type="bullet">
/// <item>La granularidad es el fichero: uno ya congelado que añada otra llamada a
/// <c>UserManager</c> pasa en verde.</item>
/// <item>Un gestor obtenido sin nombrar su tipo genérico (por
/// <c>IServiceProvider.GetService(typeof(...))</c> o por un alias de
/// <c>using</c>) queda fuera, igual que <c>IUserStore&lt;T&gt;</c> directo.</item>
/// <item>Se descartan comentarios, no literales de texto.</item>
/// </list>
/// </summary>
public class UserManagerSoloDesdeInfraestructuraDeLoginTests
{
    /// <summary>
    /// Autenticación y credenciales de la propia cuenta: iniciar sesión, 2FA,
    /// restablecer y cambiar la contraseña, el callback externo y la resolución
    /// de la identidad de la sesión. Aquí <c>SignInManager</c> necesita el
    /// contexto HTTP y no tiene sentido fuera de Web.
    /// </summary>
    private static readonly string[] InfraestructuraDeLogin =
    [
        "src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs",
        "src/CaeManager.Web/Components/Account/Pages/CambiarContrasena.razor",
        "src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor",
        "src/CaeManager.Web/Components/Account/Pages/Login.razor",
        "src/CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor",
        "src/CaeManager.Web/Components/Account/Pages/OlvideContrasena.razor.cs",
        "src/CaeManager.Web/Components/Account/Pages/RestablecerContrasena.razor.cs",
        "src/CaeManager.Web/Program.cs",
        "src/CaeManager.Web/Services/CurrentUserService.cs",
        "src/CaeManager.Web/Services/ProveedorAutenticacionRevalidada.cs",
    ];

    /// <summary>
    /// Lo que queda por migrar, con su motivo. Ninguno administra cuentas ajenas:
    /// son lecturas (nombre o permiso de una cuenta, la propia sesión) o
    /// preferencias de la propia cuenta. Solo puede menguar.
    /// </summary>
    private static readonly string[] DeudaCongelada =
    [
        // Preferencias de la propia cuenta (escriben sobre quien está en sesión).
        "src/CaeManager.Web/Components/Account/IdiomaEndpoints.cs",
        "src/CaeManager.Web/Components/Layout/SelectorTema.razor",
        "src/CaeManager.Web/Services/ActividadUsuarioService.cs",
        // Lecturas de la propia sesión: rol, 2FA, permiso sensible.
        "src/CaeManager.Web/Components/Layout/MainLayout.razor.cs",
        "src/CaeManager.Web/Features/Auditoria/AuditoriaEndpoints.cs",
        "src/CaeManager.Web/Features/Auditoria/Pages/AccesosDocumentosSensibles.razor.cs",
        "src/CaeManager.Web/Features/Auditoria/Pages/Auditoria.razor.cs",
        "src/CaeManager.Web/Features/Dashboard/Pages/Inicio.razor.cs",
        "src/CaeManager.Web/Features/Extension/ExtensionTokenEndpoints.cs",
        "src/CaeManager.Web/Features/Extension/Pages/ConectarExtension.razor.cs",
        // Lecturas del nombre de otra cuenta para pintarlo.
        "src/CaeManager.Web/Components/Workspace/PestanaHistorial.razor",
        "src/CaeManager.Web/Features/Clientes/Components/ClientePreviewDrawer.razor.cs",
        "src/CaeManager.Web/Features/Configuracion/Pages/OrdenMenuLateral.razor.cs",
        "src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor.cs",
        "src/CaeManager.Web/Features/Importacion/Pages/Importacion.razor.cs",
        "src/CaeManager.Web/Features/Reportes/Pages/Reportes.razor.cs",
    ];

    /// <summary>
    /// El puerto no autoriza nada (lo dice su propio contrato): inyectarlo desde Web
    /// sería el mismo atajo que <c>UserManager</c> con otro nombre, saltándose los
    /// handlers que deciden (revisión puente de P1-I2). Web llega a él solo por
    /// <c>IMediator</c>.
    /// </summary>
    private static readonly Regex NombraElPuertoDeCuentas = new(
        @"\b(?:IGestionCuentasUsuario|GestionCuentasUsuarioIdentity)\b", RegexOptions.Compiled);

    private static readonly Regex NombraUnGestorDeIdentity = new(
        @"\b(?:UserManager|SignInManager|RoleManager)\s*<", RegexOptions.Compiled);

    [Fact]
    public void Web_solo_nombra_los_gestores_de_Identity_en_la_infraestructura_de_login_y_en_la_deuda_congelada()
    {
        var conGestor = FicherosDeWebQueNombranUnGestor();

        // Control positivo: si el escaneo no ve los dos consumidores que seguro
        // existen, ha dejado de observar y la igualdad de abajo no diría nada.
        conGestor.Should().Contain(
            ["src/CaeManager.Web/Program.cs", "src/CaeManager.Web/Components/Layout/MainLayout.razor.cs"],
            "el escaneo tiene que ver a los consumidores que seguro existen");

        conGestor.Should().BeEquivalentTo(InfraestructuraDeLogin.Concat(DeudaCongelada),
            "fuera del login, una página que llama a UserManager se autoriza a sí misma y otro camino hasta " +
            "Identity no lo hereda: la escritura va en un Command de Application (IGestionCuentasUsuario). " +
            "Si migraste un fichero de DeudaCongelada, quítalo de la lista en el mismo commit");
    }

    [Theory]
    [InlineData("src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor.cs")]
    [InlineData("src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor")]
    [InlineData("src/CaeManager.Web/Features/GestionRoles/Pages/Roles.razor.cs")]
    [InlineData("src/CaeManager.Web/Features/GestionRoles/Pages/Roles.razor")]
    public void Las_paginas_de_gestion_de_cuentas_no_nombran_ningun_gestor_de_Identity(string pagina)
    {
        File.Exists(Absoluta(pagina)).Should().BeTrue("control: la página sigue donde este test la busca");

        NombraUnGestorDeIdentity.IsMatch(SinComentarios(pagina)).Should().BeFalse(
            "crear, editar, activar, eliminar y dar rol a una cuenta van por los Commands de Usuarios/Commands (P1-I2)");
    }

    [Fact]
    public void Web_no_nombra_el_puerto_de_cuentas_que_no_autoriza()
    {
        var conPuerto = FicherosDeWeb().Where(r => NombraElPuertoDeCuentas.IsMatch(SinComentarios(r))).ToList();

        conPuerto.Should().BeEmpty(
            "IGestionCuentasUsuario ejecuta sobre la cuenta que le digan: fuera de los handlers de " +
            "Usuarios/Commands es UserManager con otro nombre");
    }

    private static List<string> FicherosDeWebQueNombranUnGestor() =>
        FicherosDeWeb().Where(r => NombraUnGestorDeIdentity.IsMatch(SinComentarios(r))).ToList();

    private static List<string> FicherosDeWeb()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
    }

    private static string SinComentarios(string relativo) =>
        LimpiadorDeComentarios.Quitar(File.ReadAllText(Absoluta(relativo)), razor: relativo.EndsWith(".razor", StringComparison.Ordinal));

    private static string Absoluta(string relativa) =>
        Path.Combine(RaizDelRepositorio(), relativa.Replace('/', Path.DirectorySeparatorChar));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
