using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Web no llama a los gestores de Identity fuera de la infraestructura de
/// login</b> (P1-I2 del plan de madurez 09-24). «Gestores de Identity» es el nombre técnico de
/// <c>UserManager</c>, <c>SignInManager</c> y <c>RoleManager</c> (ASP.NET Core Identity); no son Gestores CAE
/// (personas) ni Operadores CAE (organizaciones): deuda terminológica del código legacy, sin renombrar aquí.
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
/// (inyección, parámetro de constructor o de endpoint). Desde MJ-08 la lista es
/// <c>Congelados/Gestores-de-Identity-en-Web.txt</c> (<see cref="ListaCongelada"/>, una línea
/// por fichero y por tipo de gestor, con su recuento): la infraestructura de login y la deuda por
/// migrar, separadas por comentarios <c>#</c>; el test no distingue una de otra, lo hace el comentario de la lista. Un fichero nuevo es <c>NUEVA</c>; <b>más menciones en uno ya
/// listado es <c>CRECE</c></b> (antes la granularidad era el fichero y subir de 1 a 5 pasaba en
/// verde); uno que se migre baja la lista (<c>BAJA</c>/<c>OBSOLETA</c>) en el mismo commit.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados):
/// </para>
/// <list type="bullet">
/// <item>El recuento es de menciones del tipo genérico, no de llamadas: reordenar el uso dentro
/// de un fichero sin añadir menciones pasa en verde.</item>
/// <item>Un gestor obtenido sin nombrar su tipo genérico (por
/// <c>IServiceProvider.GetService(typeof(...))</c> o por un alias de
/// <c>using</c>) queda fuera, igual que <c>IUserStore&lt;T&gt;</c> directo.</item>
/// <item>Se descartan comentarios, no literales de texto.</item>
/// </list>
/// </summary>
public class UserManagerSoloDesdeInfraestructuraDeLoginTests
{
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

    private static readonly string[] Gestores = ["UserManager", "SignInManager", "RoleManager"];

    private const string Lista = "Gestores-de-Identity-en-Web";

    private const string Guia =
        "El comentario '#' de cada línea de la lista dice si es infraestructura de login (autenticación y credenciales de la " +
        "propia cuenta; SignInManager necesita el contexto HTTP) o deuda por migrar. " +
        "Fuera del login, una página que llama a UserManager se autoriza a sí misma y otro camino hasta Identity no lo hereda: " +
        "la escritura va en un Command de Application (IGestionCuentasUsuario). NUEVA o CRECE: no añadas ni subas la línea; " +
        "migra la llamada. BAJA u OBSOLETA: has migrado un fichero; baja o borra su línea en tests/CaeManager.Architecture.Tests/" +
        "Congelados/" + Lista + ".txt en el mismo commit.";

    [Fact]
    public void Web_solo_nombra_los_gestores_de_Identity_en_la_infraestructura_de_login_y_en_la_deuda_congelada()
    {
        var medido = MedirMenciones();

        // Control positivo: si el escaneo no ve los dos consumidores que seguro existen, ha dejado de observar y la
        // comparación con la lista no diría nada.
        medido.Keys.Select(u => u.Lugar).Should().Contain(
            ["src/CaeManager.Web/Program.cs", "src/CaeManager.Web/Services/CurrentUserService.cs"],
            "el escaneo tiene que ver a los consumidores que seguro existen");

        ListaCongelada.Verificar(Lista, medido, Guia).Should().BeNull();
    }

    private static Dictionary<Ubicacion, int> MedirMenciones()
    {
        var medido = new Dictionary<Ubicacion, int>();
        foreach (var fichero in FicherosDeWeb())
        {
            var texto = SinComentarios(fichero);
            foreach (var gestor in Gestores)
            {
                var menciones = Regex.Matches(texto, $@"\b{gestor}\s*<").Count;
                if (menciones > 0)
                    medido[new Ubicacion(fichero, gestor)] = menciones;
            }
        }

        return medido;
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
