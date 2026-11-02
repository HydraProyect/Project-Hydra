using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>Por qué una cuenta no puede atender las conversaciones de un buzón.</summary>
public enum MotivoResponsableNoElegible
{
    NoAlcanzable,
    Desactivado,
    RolSinGestion,
    SinCarteraSobreLaEmpresa,
}

/// <summary>
/// ¿Puede esta cuenta atender lo que entra por este buzón? La misma regla al fijar el responsable y al ingerir
/// cada conversación nueva, porque rol, cuenta y cartera cambian después de fijarlo. Se pregunta por un tercero,
/// sin sesión: en la ingesta no hay usuario, solo el Tenant propietario del buzón como Tenant activo.
///
/// <list type="number">
/// <item>alcanzable desde el Tenant activo (propia o Operador CAE externo delegado vigente);</item>
/// <item>activa;</item>
/// <item>con un rol de gestión que escribe: ni el rol Cliente de portal ni Consulta responden un correo;</item>
/// <item>con acceso al Cliente empresarial del buzón: Administrador y Dirección CAE lo tienen por su rol; Gestor
/// CAE por su Asignación de Cartera; Coordinador CAE por la suya, la de su equipo o, si viene de un Operador CAE
/// externo, por una Asignación de Operación del Tenant entero. Un buzón sin Cliente empresarial es triage compartido
/// (<see cref="AlcanceDatosServiceExtensions.ClienteOpcionalVisibleAsync"/>) y ahí decide solo el rol.</item>
/// </list>
///
/// <para>
/// El acceso al buzón no necesita otra comprobación: uno compartido lo ve la gestión del Tenant, y el de un buzón
/// personal solo lo tiene su propietario, que es siempre su responsable efectivo.
/// </para>
///
/// <para>
/// <b>Solo deniega el enrutado, no restringe datos ni concede nada.</b> Quién ve qué lo sigue decidiendo
/// <c>AlcanceDatosService</c>; esto repite su ámbito efectivo desde la posición de propietario para no dejar una
/// conversación en manos de quien no la vería. Para una cuenta delegada mira el rol guardado en su Asignación de
/// Operador Delegado, no el que ejerce (ver <see cref="DestinoCartera"/>): falla cerrado.
/// </para>
/// </summary>
public class ResponsableBuzonElegible(
    IDirectorioDestinosCartera directorioDestinos,
    IDirectorioEquipoCoordinador equipoCoordinador,
    IOperacionesQueryContext operaciones,
    ITenantActual tenantActual)
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";
    private const string RolDireccionCae = "DireccionCae";
    private const string RolCoordinadorCae = "CoordinadorCae";
    private const string RolGestorCae = "GestorCae";

    /// <returns><c>null</c> si puede atenderlo; si no, por qué.</returns>
    public async Task<MotivoResponsableNoElegible?> EvaluarAsync(
        Guid usuarioId, ConexionIntegracion buzon, CancellationToken cancellationToken)
    {
        var destino = await directorioDestinos.ObtenerAsync(usuarioId, cancellationToken);
        if (destino is null) return MotivoResponsableNoElegible.NoAlcanzable;
        if (!destino.Activa) return MotivoResponsableNoElegible.Desactivado;

        switch (destino.RolEfectivo)
        {
            case RolAdministrador or RolDireccionCae:
                return null;
            case RolCoordinadorCae or RolGestorCae:
                break;
            default:
                return MotivoResponsableNoElegible.RolSinGestion;
        }

        if (buzon.ClienteId is not { } clienteEmpresarial)
            return null;

        return await CarteraLoAlcanzaAsync(usuarioId, destino, clienteEmpresarial, cancellationToken)
            ? null
            : MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa;
    }

    private async Task<bool> CarteraLoAlcanzaAsync(
        Guid usuarioId, DestinoCartera destino, Guid clienteEmpresarial, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId || destino.TenantOrigenId is not { } operadorTenantId)
            return false;

        var ahora = DateTime.UtcNow;
        var esCoordinador = destino.RolEfectivo == RolCoordinadorCae;

        // Decisión del 2026-10-20: en un Tenant beneficiario, el Coordinador CAE de la organización que lo opera
        // entero lo alcanza sin cartera (AlcanceDatosService.ObtenerAlcanceRealAsync).
        if (esCoordinador && destino.EsOperadorDelegado
            && await TenantsBeneficiariosAutorizados.OperaElTenantEnteroAsync(
                operaciones, operadorTenantId, propietarioTenantId, ahora, cancellationToken))
            return true;

        // El equipo se lee desde su Tenant: el de un Coordinador CAE externo no es legible aquí, y sin él solo
        // cuenta su propia cartera. Las cuentas desactivadas del equipo también cuentan, como en
        // AlcanceDatosService.ObtenerCarteraParaCoordinadorAsync.
        IReadOnlyCollection<Guid> personas = [usuarioId];
        if (esCoordinador && !destino.EsOperadorDelegado)
            personas = [usuarioId, .. (await equipoCoordinador.ObtenerEquipoAsync(usuarioId, cancellationToken)).Select(m => m.Id)];

        return await TenantsBeneficiariosAutorizados.CarteraAlcanzaClienteEmpresarialAsync(
            operaciones, personas, propietarioTenantId, operadorTenantId, clienteEmpresarial, ahora, cancellationToken);
    }
}
