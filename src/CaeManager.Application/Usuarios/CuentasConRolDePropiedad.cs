using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios;

/// <summary>
/// Regla sobre la cuenta <b>destino</b>, simétrica de
/// <see cref="RolesReservadosAlTenantDeOrigen"/> (que mira el rol que se
/// concede): <b>quien actúa en un Tenant que no es su Tenant de origen no toca
/// una cuenta que tiene rol Administrador o Dirección CAE.</b> No la edita, no
/// la activa ni la desactiva, no la elimina, no le regenera el enlace de
/// activación y no le cambia el rol.
///
/// <para>
/// Por qué (decisión D-8, 2026-10-08): con un Encargo de administración, una
/// persona del Operador CAE externo puede tener rol efectivo Administrador
/// dentro del Tenant propietario y llegar a /usuarios. El encargo le deja
/// administrar el Tenant propietario, pero las cuentas con rol de Propiedad de
/// ese Tenant son el primero de los tres actos excluidos: regenerar la
/// activación de un Administrador pendiente equivale a quedarse con su cuenta,
/// y desactivarlo o degradarlo dejaría al Tenant propietario sin la única
/// persona que puede retirar el encargo.
/// </para>
///
/// <para>
/// <b>Sin excepción</b>, ni por encargo ni de plataforma: compara identidades
/// de Tenant, el mismo criterio que
/// <see cref="RolesReservadosAlTenantDeOrigen.EsContextoCruzado"/>, y falla
/// cerrado si alguna no se resuelve. Un Administrador propio del Tenant
/// propietario no se ve afectado: su Tenant de origen es el Tenant activo.
/// </para>
/// </summary>
public static class CuentasConRolDePropiedad
{
    public static readonly Error SoloElTenantPropietario = Error.Crear(
        "Usuarios.CuentaDePropiedadReservada",
        "Las cuentas de Administrador y de Dirección CAE de esta organización solo las gestiona " +
        "un Administrador propio de la organización, no quien accede a ella por delegación o por encargo.");

    public static bool TieneRolDePropiedad(IEnumerable<string> rolesDeLaCuenta) =>
        rolesDeLaCuenta.Any(RolesReservadosAlTenantDeOrigen.EsReservado);

    /// <summary>
    /// Fallo si la cuenta destino tiene rol de Propiedad y el Context Workspace
    /// activo no es el Tenant de origen de quien actúa.
    /// </summary>
    public static Result VerificarDestino(
        IEnumerable<string> rolesDeLaCuentaDestino, Guid? tenantOrigenId, Guid? tenantContextoId) =>
        TieneRolDePropiedad(rolesDeLaCuentaDestino)
        && RolesReservadosAlTenantDeOrigen.EsContextoCruzado(tenantOrigenId, tenantContextoId)
            ? Result.Fallo(SoloElTenantPropietario)
            : Result.Exito();
}
