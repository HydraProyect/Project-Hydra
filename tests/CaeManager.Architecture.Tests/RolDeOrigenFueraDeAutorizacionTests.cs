using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El rol de origen no autoriza ni acota</b> (decisión P7, 2026-09-23).
///
/// <para>
/// <c>ICurrentUserService.ObtenerRolOrigenAsync</c> devuelve el rol de Identity de la cuenta en su
/// organización de origen: no refleja la Asignación de Cartera del Tenant operado ni las
/// restricciones de sesión (login local). Autorización y alcance usan
/// <c>ObtenerRolEfectivoAsync</c>. El riesgo que cierra: un Administrador de la organización del
/// Operador CAE externo, GestorCae en el Tenant propietario, autorizado ahí como Administrador.
/// </para>
///
/// <para>
/// <b>Lo que SÍ observa</b>, por nombre exacto de símbolo (<c>\b…\b</c>, nunca por prefijo): cada
/// fichero <c>.cs</c>/<c>.razor</c> de <c>src/</c> que nombra <c>ObtenerRolOrigenAsync</c> fuera de
/// su declaración, y, dentro de las zonas de autorización y alcance, además las lecturas directas del
/// rol de Identity (<c>GetRolesAsync</c>, <c>IsInRoleAsync</c>, <c>GetUsersInRoleAsync</c>). Los
/// consumidores se comparan por igualdad exacta de ruta con listas cerradas, así que un consumidor
/// nuevo y una entrada que ya sobra ponen el test en rojo por igual.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): el rol de origen obtenido por otra vía (un
/// <c>DbContext</c> que haga el join de <c>AspNetUserRoles</c> a mano, o un servicio intermedio que
/// lo reexponga con otro nombre); zonas de autorización fuera de las que enumera
/// <see cref="EsZonaDeAutorizacionOAlcance"/>; se descartan comentarios, no literales de texto.
/// </para>
/// </summary>
public class RolDeOrigenFueraDeAutorizacionTests
{
    private static readonly Regex NombraRolOrigen = new(@"\bObtenerRolOrigenAsync\b", RegexOptions.Compiled);

    /// <summary>La declaración en la interfaz y en su implementación no es un uso.</summary>
    private static readonly Regex DeclaraRolOrigen = new(
        @"Task<string\?>\s+ObtenerRolOrigenAsync\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex LeeRolDeIdentity = new(
        @"\b(?:GetRolesAsync|IsInRoleAsync|GetUsersInRoleAsync)\b", RegexOptions.Compiled);

    private static readonly Regex Comentarios = new(
        @"@\*.*?\*@|/\*.*?\*/|//[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Todos los consumidores de <c>ObtenerRolOrigenAsync</c>, con su motivo. Añadir uno exige
    /// justificar que es identidad, informe o pertenencia — no autorización.
    /// </summary>
    private static readonly Dictionary<string, string> ConsumidoresDelRolDeOrigen = new(StringComparer.Ordinal)
    {
        ["src/CaeManager.Infrastructure/Autorizacion/VistaDemoActual.cs"] =
            "disponibilidad del selector de la lente de demo: hecho de identidad de la cuenta; la lente solo estrecha",
    };

    /// <summary>
    /// Excepciones DENTRO de las zonas de autorización y alcance. Cada una debe probar que no puede
    /// ampliar un alcance ni conceder autoridad en un Tenant operado.
    /// </summary>
    private static readonly Dictionary<string, string> ExcepcionesEnZona = new(StringComparer.Ordinal)
    {
        ["src/CaeManager.Infrastructure/Autorizacion/VistaDemoActual.cs"] =
            "AlcanceDatosService interseca con la lente: una respuesta equivocada solo estrecha de más",
        ["src/CaeManager.Infrastructure/Autorizacion/AutorizacionDelegacionPorAdministradorDelCliente.cs"] =
            "pertenencia: solo el Administrador MIEMBRO del Tenant propietario (TenantId de la cuenta) delega su Tenant",
        ["src/CaeManager.Infrastructure/Autorizacion/DirectorioUsuariosTenant.cs"] =
            "directorio de cuentas del Tenant (GetUsersInRoleAsync): identidad de terceros para /usuarios y /roles, "
            + "no decide la autoridad del usuario actual; los Operadores delegados se muestran con su rol de cartera",
    };

    /// <summary>
    /// La interfaz nombra el símbolo en un literal (<c>[Obsolete("… ObtenerRolOrigenAsync …")]</c>): no
    /// es un consumidor. La implementación (<c>CurrentUserService</c>) NO se excluye a propósito: que
    /// su rol efectivo llamara al de origen es justo el atajo que este trinquete tiene que ver.
    /// </summary>
    private static readonly HashSet<string> DeclaranElContrato = new(StringComparer.Ordinal)
    {
        "src/CaeManager.Application/Common/ICurrentUserService.cs",
    };

    [Fact]
    public void Solo_los_consumidores_declarados_usan_el_rol_de_origen()
    {
        var consumidores = FicherosDeSrc()
            .Where(f => !DeclaranElContrato.Contains(f))
            .Where(f => UsaRolDeOrigen(Codigo(f)))
            .ToList();

        consumidores.Should().Contain("src/CaeManager.Infrastructure/Autorizacion/VistaDemoActual.cs",
            "control positivo: VistaDemoActual es un consumidor conocido; si el detector no lo ve, ha dejado de observar");

        consumidores.Should().BeEquivalentTo(ConsumidoresDelRolDeOrigen.Keys,
            "ObtenerRolOrigenAsync no autoriza ni acota (P7): un consumidor nuevo se declara con su motivo, "
            + "y uno que ya no lo usa se retira de la lista");
    }

    [Fact]
    public void Ninguna_zona_de_autorizacion_o_alcance_lee_el_rol_de_origen_salvo_las_excepciones_declaradas()
    {
        var enZona = FicherosDeSrc()
            .Where(EsZonaDeAutorizacionOAlcance)
            .Where(f => { var c = Codigo(f); return UsaRolDeOrigen(c) || LeeRolDeIdentity.IsMatch(c); })
            .ToList();

        enZona.Should().Contain("src/CaeManager.Infrastructure/Autorizacion/AutorizacionDelegacionPorAdministradorDelCliente.cs",
            "control positivo: lee IsInRoleAsync dentro de Autorizacion; si no aparece, la zona o el detector están ciegos");

        enZona.Should().BeEquivalentTo(ExcepcionesEnZona.Keys,
            "autorización y alcance usan ObtenerRolEfectivoAsync; el rol de origen solo entra con una excepción que "
            + "demuestre que no amplía nada");
    }

    [Fact]
    public void El_detector_casa_el_simbolo_exacto_y_no_prefijos_ni_comentarios_ni_la_declaracion()
    {
        UsaRolDeOrigen("var r = await currentUserService.ObtenerRolOrigenAsync();").Should().BeTrue();
        UsaRolDeOrigen("Func<Task<string?>> f = currentUserService.ObtenerRolOrigenAsync;").Should().BeTrue(
            "un grupo de métodos también es un uso");
        UsaRolDeOrigen("var r = await x.ObtenerRolOrigenAsyncCacheado();").Should().BeFalse("prefijo, no el símbolo");
        UsaRolDeOrigen("var r = await x.MiObtenerRolOrigenAsync();").Should().BeFalse("sufijo, no el símbolo");
        UsaRolDeOrigen(Comentarios.Replace("// x.ObtenerRolOrigenAsync()\nvar a = 1;", " ")).Should().BeFalse();
        UsaRolDeOrigen("    Task<string?> ObtenerRolOrigenAsync();").Should().BeFalse("declaración de la interfaz");
        UsaRolDeOrigen("    public async Task<string?> ObtenerRolOrigenAsync()\n    {").Should().BeFalse("implementación");

        LeeRolDeIdentity.IsMatch("await userManager.IsInRoleAsync(u, r)").Should().BeTrue();
        LeeRolDeIdentity.IsMatch("await userManager.IsInRoleAsyncX(u, r)").Should().BeFalse();
        LeeRolDeIdentity.IsMatch("principal.IsInRole(r)").Should().BeFalse("el claim ya es el rol efectivo");

        EsZonaDeAutorizacionOAlcance("src/CaeManager.Infrastructure/Autorizacion/AlcanceDatosService.cs").Should().BeTrue();
        EsZonaDeAutorizacionOAlcance("src/CaeManager.Web/Services/RolEfectivoDelWorkspaceMiddleware.cs").Should().BeTrue();
        EsZonaDeAutorizacionOAlcance("src/CaeManager.Application/Common/AutorizacionEscrituraBehavior.cs").Should().BeTrue();
        EsZonaDeAutorizacionOAlcance("src/CaeManager.Web/Components/Layout/MainLayout.razor.cs").Should().BeFalse();
    }

    private static bool UsaRolDeOrigen(string codigo) =>
        NombraRolOrigen.IsMatch(DeclaraRolOrigen.Replace(codigo, " "));

    /// <summary>
    /// Autorización, alcance y middleware de autenticación/autorización. Por segmento de ruta y nombre
    /// de fichero exactos.
    /// </summary>
    private static bool EsZonaDeAutorizacionOAlcance(string relativo)
    {
        var nombre = relativo[(relativo.LastIndexOf('/') + 1)..];
        return relativo.StartsWith("src/CaeManager.Infrastructure/Autorizacion/", StringComparison.Ordinal)
               || relativo.StartsWith("src/CaeManager.Infrastructure/Autenticacion/", StringComparison.Ordinal)
               || nombre.EndsWith("Middleware.cs", StringComparison.Ordinal)
               || nombre.EndsWith("Behavior.cs", StringComparison.Ordinal)
               || nombre.EndsWith("AuthorizationHandler.cs", StringComparison.Ordinal)
               || nombre.StartsWith("AutorizacionEscritura", StringComparison.Ordinal)
               || nombre.StartsWith("Alcance", StringComparison.Ordinal);
    }

    private static string Codigo(string relativo) =>
        Comentarios.Replace(File.ReadAllText(Path.Combine(Raiz(), relativo.Replace('/', Path.DirectorySeparatorChar))), " ");

    private static List<string> FicherosDeSrc()
    {
        var raiz = Raiz();
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
               ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
