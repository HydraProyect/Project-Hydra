using CaeManager.Application.Common;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// El privilegio de plataforma no se ejerce sobre la propia casa: el tenant
/// objetivo tiene que ser <b>ajeno</b> al tenant de origen de quien lo pide.
///
/// <para>
/// Nadie abre una sesión de soporte sobre su propio tenant — ahí ya entra por la
/// vía normal, y hacerlo por aquí sería saltarse su propio rol dentro de su
/// organización. Lo mismo vale al concederse el privilegio: emitir una concesión
/// sobre la propia casa deja preparada exactamente esa vuelta.
/// </para>
///
/// <para>
/// <b>Es una regla independiente, no un corolario de la capacidad.</b> Se
/// separan porque responden cosas distintas:
/// </para>
/// <code>
/// capacidad de la concesión  →  QUÉ puede hacerse
/// esta regla                 →  SOBRE QUÉ tenant puede ejercerse
/// </code>
/// <para>
/// Vive aquí, con nombre propio y test propio, porque antes de A0 estaba
/// enterrada dentro de la implementación de autorización que A0 retira. Un
/// control que solo existe dentro de la clase que se va a borrar se pierde en
/// silencio y ningún test lo echa de menos.
/// </para>
///
/// <para>
/// <b>Contra el tenant de ORIGEN, nunca contra <c>ITenantActual</c>.</b> Ese
/// refleja el workspace activo, así que quien ya esté operando un tenant ajeno
/// lo tendría fijado a ese tenant y podría usarlo para abrirse acceso a un
/// tercero. El de origen sale del claim de sesión y la selección de workspace no
/// lo cambia.
/// </para>
///
/// <para>
/// Sin tenant de origen, falla cerrado: fuera de un circuito autenticado no hay
/// casa propia contra la que comparar.
/// </para>
/// </summary>
public static class ReglaTenantObjetivoAjeno
{
    public static async Task<bool> SeCumpleAsync(
        ICurrentUserService currentUserService, Guid tenantObjetivoId)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return tenantOrigenId is not null && tenantOrigenId.Value != tenantObjetivoId;
    }
}
