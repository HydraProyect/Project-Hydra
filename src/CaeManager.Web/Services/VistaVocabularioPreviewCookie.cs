using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Services;

/// <summary>
/// Lee la vista previa de vocabulario (DDL-072) desde una cookie, igual que
/// <see cref="ClienteActivoSeleccionado"/> lee el Delegated Workspace activo
/// y por el mismo motivo: cambiar el perfil forzado exige un reload completo
/// (ver <c>SelectorVistaVocabulario.razor.cs</c> y el endpoint
/// <c>/cuenta/vista-vocabulario</c>) — un valor puramente en memoria vive en
/// el scope de DI del circuito de Blazor Server, y ese scope se destruye por
/// completo en cada reload.
///
/// A diferencia de <see cref="ClienteActivoSeleccionado"/>, el valor va en
/// <b>texto plano</b>, sin Data Protection: esta cookie nunca concede acceso
/// a datos de otro tenant ni cambia ningún filtro de aislamiento — solo
/// decide qué etiqueta y qué comportamiento derivado de DDL-076 usa la
/// pantalla para los datos que el usuario ya podía ver. El peor caso de una
/// cookie manipulada es ver una etiqueta equivocada de su propia pantalla,
/// no una fuga de datos — por eso no hace falta el mismo nivel de cifrado
/// que protege el secuestro de tenant (ver el comentario de
/// <see cref="ClienteActivoSeleccionado"/> sobre el hallazgo C-1). Aun así
/// se exige rol Administrador al leer, no solo al escribir: un valor que
/// sobrevive a una baja de rol no debe seguir aplicándose.
/// </summary>
public class VistaVocabularioPreviewCookie(IHttpContextAccessor httpContextAccessor) : IVistaVocabularioPreviewService
{
    public const string NombreCookie = "cae_vista_vocabulario";

    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(8);

    private PerfilVocabularioTenant? _perfilForzado;
    private bool _leidoDeCookie;

    public PerfilVocabularioTenant? PerfilForzado
    {
        get
        {
            if (!_leidoDeCookie)
            {
                _perfilForzado = LeerDeCookie();
                _leidoDeCookie = true;
            }

            return _perfilForzado;
        }
    }

    private PerfilVocabularioTenant? LeerDeCookie()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User.IsInRole(Roles.Administrador) != true)
            return null;

        var valorCookie = httpContext.Request.Cookies[NombreCookie];
        return Enum.TryParse<PerfilVocabularioTenant>(valorCookie, out var perfil) ? perfil : null;
    }
}
