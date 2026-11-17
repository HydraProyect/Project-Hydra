using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Guarda de pestaña obsoleta (D-6 b, R-2) con la cookie real: cada «pestaña» es un ámbito propio
/// (<see cref="ClienteActivoSeleccionado"/> sobre su propio <see cref="HttpContext"/>), igual que un circuito, que
/// memoiza la selección de su negociación y no vuelve a ver la cookie. El cambio de contexto en otra pestaña pasa por
/// <see cref="CookieDeContextoTenant"/>, el mismo código que usa <c>/cuenta/cliente-activo</c>.
/// </summary>
public class GuardaSeleccionVigenteTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly RegistroSeleccionVigente _registro = new();

    [Fact]
    public void Sin_ningun_cambio_en_el_proceso_la_pestana_escribe()
    {
        Guarda(Pestana(Token(TenantA))).Comprobar().Should().BeNull();
        Guarda(Pestana(null)).Comprobar().Should().BeNull();
    }

    [Fact]
    public void Un_cambio_a_otro_Tenant_en_otra_pestana_deja_obsoleta_la_que_pinto_con_el_anterior()
    {
        var vieja = Pestana(Token(TenantA));
        _ = vieja.TenantIdSeleccionado;

        var nueva = CambiarEnOtraPestana(TenantB);

        Guarda(vieja).Comprobar().Should().Be(SeleccionNoVigente.CambiadaEnOtraPestana);
        vieja.TenantIdSeleccionado.Should().Be(TenantA, "el circuito conserva su Tenant: la guarda es lo que impide que escriba");
        Guarda(Pestana(nueva)).Comprobar().Should().BeNull("la pestaña que recarga pinta con la selección vigente");
    }

    [Fact]
    public void Volver_al_origen_en_otra_pestana_tambien_la_deja_obsoleta()
    {
        var vieja = Pestana(Token(TenantA));
        _ = vieja.TenantIdSeleccionado;

        CookieDeContextoTenant.VolverAlOrigen(OtraPestana(), Usuario);

        Guarda(vieja).Comprobar().Should().Be(SeleccionNoVigente.CambiadaEnOtraPestana);
        Guarda(Pestana(null)).Comprobar().Should().BeNull();
    }

    [Fact]
    public void Elegir_otra_vez_el_mismo_Tenant_es_una_seleccion_nueva()
    {
        var vieja = Pestana(Token(TenantA));
        _ = vieja.TenantIdSeleccionado;

        CambiarEnOtraPestana(TenantA);

        Guarda(vieja).Comprobar().Should().Be(SeleccionNoVigente.CambiadaEnOtraPestana,
            "la versión es la de la emisión, no la del Tenant: la Asignación de Operación puede haber cambiado");
    }

    [Fact]
    public void La_seleccion_retirada_despues_de_pintar_impide_escribir_aunque_nadie_cambiara_de_pestana()
    {
        var pestana = Pestana(Token(TenantA));
        _ = pestana.TenantIdSeleccionado;

        pestana.Invalidar();

        pestana.TenantIdSeleccionado.Should().BeNull("las consultas caen al Tenant de origen, como antes");
        Guarda(pestana).Comprobar().Should().Be(SeleccionNoVigente.RetiradaTrasPintar,
            "un comando ya no puede caer al Tenant de origen desde una pantalla pintada con el retirado");
    }

    [Fact]
    public void Otra_sesion_de_navegador_del_mismo_usuario_no_deja_obsoleta_esta()
    {
        var pestana = Pestana(Token(TenantA), sid: "portatil");
        _ = pestana.TenantIdSeleccionado;

        CambiarEnOtraPestana(TenantB, sid: "movil");

        Guarda(pestana).Comprobar().Should().BeNull("la cookie es del navegador; otro navegador tiene la suya");
    }

    [Fact]
    public void El_circuito_que_se_abre_adopta_su_cookie_como_vigente()
    {
        // Una respuesta de cambio perdida: el registro tiene B, pero el navegador se quedó con A.
        var tokenA = Token(TenantA);
        var vieja = Pestana(tokenA);
        _ = vieja.TenantIdSeleccionado;
        CambiarEnOtraPestana(TenantB);

        var recargada = Pestana(tokenA);
        recargada.AdoptarComoVigente(_registro);

        Guarda(recargada).Comprobar().Should().BeNull("si no, la pestaña pediría recargar sin fin");
    }

    [Fact]
    public void El_contexto_explicito_de_la_extension_no_depende_de_la_cookie()
    {
        var extension = Pestana(Token(TenantA));
        extension.FijarDesdeExtension(TenantB, Guid.NewGuid());
        CambiarEnOtraPestana(TenantB);

        Guarda(extension).Comprobar().Should().BeNull();
    }

    [Fact]
    public void La_version_es_una_huella_que_no_contiene_el_token()
    {
        var token = Token(TenantA);
        var pestana = Pestana(token);

        pestana.VersionSeleccion.Should().Be(RegistroSeleccionVigente.Huella(token));
        pestana.VersionSeleccion.Should().NotContain(token).And.HaveLength(22);
        Pestana(null).VersionSeleccion.Should().Be(RegistroSeleccionVigente.VersionOrigen);
    }

    /// <summary>
    /// De punta a punta en Application y Web: la pestaña carga con A, otra pestaña cambia a B (o la selección se
    /// revoca) y el comando que llega después no se ejecuta, devuelve el fallo que pide recargar y queda auditado.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_cambio_o_la_revocacion_entre_la_carga_y_el_comando_lo_rechazan_y_lo_auditan(bool revocacion)
    {
        var pestana = Pestana(Token(TenantA));
        _ = pestana.TenantIdSeleccionado;
        if (revocacion) pestana.Invalidar();
        else CambiarEnOtraPestana(TenantB);

        var auditados = new List<CaeManager.Domain.Auditoria.RegistroAuditoria>();
        var behavior = new GuardaPestanaObsoletaBehavior<ComandoDePrueba, CaeManager.Domain.Common.Result>(
            Guarda(pestana),
            new CaeManager.Application.Auditoria.RegistroCambioContextoService(
                new ActorFijo(), new RepositorioFalso(auditados),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CaeManager.Application.Auditoria.RegistroCambioContextoService>.Instance));
        var ejecutado = false;

        var resultado = await behavior.Handle(new ComandoDePrueba(), _ =>
        {
            ejecutado = true;
            return Task.FromResult(CaeManager.Domain.Common.Result.Exito());
        }, default);

        ejecutado.Should().BeFalse();
        resultado.Error.Codigo.Should().Be(GuardaPestanaObsoletaBehavior<ComandoDePrueba, CaeManager.Domain.Common.Result>.CodigoError);
        auditados.Should().ContainSingle(r =>
            r.Accion == CaeManager.Domain.Auditoria.RegistroAuditoria.AccionComandoRechazadoPorContextoObsoleto
            && r.ActorRealUsuarioId == Usuario);
    }

    public sealed record ComandoDePrueba : ICommand;

    private sealed class ActorFijo : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Usuario));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Usuario);
    }

    private sealed class RepositorioFalso(List<CaeManager.Domain.Auditoria.RegistroAuditoria> guardados)
        : CaeManager.Domain.Auditoria.IRegistroAccesoDatoSensibleRepository
    {
        public Task GuardarAsync(CaeManager.Domain.Auditoria.RegistroAuditoria registro, CancellationToken cancellationToken = default)
        {
            guardados.Add(registro);
            return Task.CompletedTask;
        }
    }

    private string Token(Guid tenant) => ClienteActivoSeleccionado.Proteger(_protector, Usuario, tenant, Guid.NewGuid());

    private ClienteActivoSeleccionado Pestana(string? token, string sid = "sesion")
    {
        var http = new DefaultHttpContext { User = Principal(sid) };
        if (token is not null)
            http.Request.Headers.Cookie = ClienteActivoSeleccionado.NombreCookie + "=" + token;
        return new ClienteActivoSeleccionado(new HttpContextAccessor { HttpContext = http }, _protector);
    }

    private GuardaSeleccionVigente Guarda(ClienteActivoSeleccionado pestana) => new(pestana, _registro);

    /// <summary>El cambio de otra pestaña: emite la cookie nueva y devuelve el token que recibe el navegador.</summary>
    private string CambiarEnOtraPestana(Guid tenant, string sid = "sesion")
    {
        var http = OtraPestana(sid);
        CookieDeContextoTenant.EmitirSeleccion(http, _protector, Usuario, tenant, Guid.NewGuid());
        var cabecera = http.Response.Headers.SetCookie.Single(c => c!.StartsWith(ClienteActivoSeleccionado.NombreCookie + "=", StringComparison.Ordinal))!;
        return Uri.UnescapeDataString(cabecera.Split(';')[0][(ClienteActivoSeleccionado.NombreCookie.Length + 1)..]);
    }

    private DefaultHttpContext OtraPestana(string sid = "sesion") => new()
    {
        User = Principal(sid),
        RequestServices = new ServiceCollection().AddSingleton(_registro).BuildServiceProvider(),
    };

    private static ClaimsPrincipal Principal(string sid) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Usuario.ToString()), new Claim(SignInManagerCuentaDesactivada.ClaimSid, sid)],
        "Prueba"));
}
