namespace CaeManager.Web.Services;

/// <summary>
/// Lee, para el HTML prerenderizado de <c>App.razor</c>, el tema que ya
/// estaba aplicado en este navegador — evita el parpadeo de tema descrito en
/// <c>SelectorTema.razor.cs</c>: sin esto, el HTML servido en frío nunca
/// lleva <c>data-theme</c> (la preferencia vive en <c>ApplicationUser.Tema</c>
/// y solo se aplica tras conectar el circuito), así que hasta que el circuito
/// conecta gana el CSS por defecto (claro) aunque la cuenta tenga Oscuro.
///
/// <para>
/// No se resuelve consultando <c>ApplicationUser.Tema</c> en cada petición
/// (I/O en el camino caliente de todo el HTML servido, lo mismo que evita
/// <c>ITenantActual</c>) ni añadiendo el tema como claim de la cookie de
/// autenticación: <c>SelectorTema</c> vive en un componente
/// <c>@rendermode="InteractiveServer"</c>, y reemitir esa cookie
/// (<c>SignInManager.RefreshSignInAsync</c>) exige un <c>HttpContext</c> de
/// petición HTTP real — el mismo motivo por el que
/// <c>ClienteActivoSeleccionado</c>/<c>VistaVocabularioPreviewCookie</c> ya
/// escriben su cookie desde un endpoint POST y no desde el circuito. Cambiar
/// el tema, a diferencia de esos dos, tiene que seguir sin generar ninguna
/// petición HTTP (REC-137, ver <c>SeleccionSobreviveAlCircuitoTests</c>), así
/// que un endpoint POST no sirve aquí. La cookie la escribe
/// <c>wwwroot/js/tema.js</c> con <c>document.cookie</c> —código de
/// navegador puro, sin pasar por <c>HttpContext.Response</c>— cada vez que
/// aplica un tema explícito.
/// </para>
///
/// <para>
/// Igual que <c>VistaVocabularioPreviewCookie</c>: en texto plano, sin Data
/// Protection ni <c>HttpOnly</c> (no puede serlo — la escribe JS de
/// navegador). El peor caso de una cookie manipulada es un parpadeo o un
/// tema equivocado en el primer píxel, nunca una fuga de datos ni un cambio
/// de autorización. Es solo una caché de UI: si no coincide con la cuenta
/// (cookie de un tema distinto al guardado, o ausente en una visita nueva),
/// el circuito la corrige en cuanto conecta y reescribe la cookie — un único
/// parpadeo posible, no uno en cada recarga.
/// </para>
///
/// <para>
/// <b>Esa corrección solo es inocua mientras la cookie no pueda adelantarse a
/// la cuenta</b>, y esa invariante la sostiene <c>SelectorTema</c>, no este
/// tipo: <c>CambiarTemaAsync</c> guarda <c>ApplicationUser.Tema</c> ANTES de
/// pedirle a <c>tema.js</c> que aplique el tema —y, con ello, escriba esta
/// cookie—. Con el orden inverso, una navegación que llegue mientras el
/// <c>UPDATE</c> sigue en vuelo sirve el HTML con el tema nuevo desde esta
/// cookie y el circuito de esa página lo revierte al leer la fila sin
/// actualizar: la "corrección" pisa la elección que el usuario acaba de
/// hacer. Es el defecto que expulsó a la PR #756 de la cola de fusión el
/// 2026-09-20; ver el doc-comment de <c>SelectorTema.CambiarTemaAsync</c>.
/// </para>
/// </summary>
public class TemaCookie(IHttpContextAccessor httpContextAccessor)
{
    public const string NombreCookie = "tema";

    /// <summary>
    /// "claro" u "oscuro", o null si no hay sesión autenticada, no hay
    /// cookie, o su valor no es uno de los dos (incluido "sistema": ese caso
    /// nunca debe llevar <c>data-theme</c>, igual que hace tema.js en el
    /// cliente).
    /// </summary>
    public string? TemaAplicado
    {
        get
        {
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext?.User.Identity?.IsAuthenticated != true)
                return null;

            var valorCookie = httpContext.Request.Cookies[NombreCookie];
            return valorCookie is "claro" or "oscuro" ? valorCookie : null;
        }
    }
}
