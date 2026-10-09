using System.Reflection;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La página «Tipo de documento 360» (<c>/documentos/tipos/{id}</c>) la ven todos los roles salvo Consulta y Cliente
/// (decisión de Chris, 2026-10-08: «todos los perfiles excepto consulta» y «que no la vea cliente»): cruza varios Clientes
/// empresariales en una sola lista.
///
/// La regla vive en dos sitios a propósito —la consulta de Application, que es quien la garantiza, y el
/// <c>[Authorize(Roles = …)]</c> de la ruta, que evita montar el circuito— y este test fija que los dos dicen lo mismo y que
/// ninguno incluye a esos dos roles. Sin él, ampliar uno de los dos lados dejaría una página que se abre y no carga, o una
/// consulta que responde a quien la ruta no deja entrar.
/// </summary>
public class TipoDocumento360SoloRolesDeGestionTests
{
    private const string Pagina = "CaeManager.Web.Features.Documentos.Pages.TipoDocumentoDetalle";
    private const string Handler = "CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento.ObtenerEstadoTipoDocumentoQueryHandler";

    private static readonly string[] RolesEsperados = ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    [Fact]
    public void La_ruta_solo_deja_entrar_a_los_cuatro_roles_de_gestion()
    {
        RolesDeLaRuta().Should().BeEquivalentTo(RolesEsperados)
            .And.NotContain(["Consulta", "Cliente"], "ni el rol Consulta ni el rol Cliente ven esta página");
    }

    [Fact]
    public void La_consulta_de_Application_autoriza_a_los_mismos_roles_que_la_ruta()
    {
        RolesDeLaConsulta().Should().BeEquivalentTo(RolesDeLaRuta(),
            "la ruta y la consulta deben autorizar a los mismos roles: la consulta es la que garantiza la regla");
    }

    private static HashSet<string> RolesDeLaRuta()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var pagina = ReflexionArquitecturaHelper.TiposDe(web).Single(t => t.FullName == Pagina);
        var autorizacion = pagina.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle("la página debe declarar exactamente un [Authorize] con Roles=")
            .Subject;

        return (autorizacion.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> RolesDeLaConsulta()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");
        var handler = ReflexionArquitecturaHelper.TiposDe(application).Single(t => t.FullName == Handler);
        var campo = handler.GetField("RolesQueVenLaPagina", BindingFlags.Public | BindingFlags.Static);
        campo.Should().NotBeNull("el handler expone la lista de roles que autoriza para que este test la compare con la ruta");

        return ((IEnumerable<string>)campo!.GetValue(null)!).ToHashSet(StringComparer.Ordinal);
    }
}
