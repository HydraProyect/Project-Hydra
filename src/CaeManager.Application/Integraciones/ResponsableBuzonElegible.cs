using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

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
/// <item>con acceso al Cliente empresarial del buzón: Administrador y Dirección CAE lo tienen por su rol, sin más
/// condición; Gestor CAE por su Asignación de Cartera; Coordinador CAE por la suya, la de su equipo o, si entra por
/// la vía de Operación, por el Tenant entero. Quien alcanza por el Tenant entero solo alcanza las Empresas que son
/// Cliente empresarial (<c>EsCritico</c> informado), como <c>AlcanceDatosService.ObtenerClienteIdsVisiblesAsync</c>;
/// bajo una operación acotada, solo la de esa operación. Un buzón sin Cliente empresarial es triage compartido
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
/// conversación en manos de quien no la vería.
/// </para>
///
/// <para>
/// <b>Una cuenta de un Operador CAE externo se evalúa por la vía con la que entraría</b>
/// (<c>CurrentUserService.ResolverRolParaAmbitoExplicitoAsync</c>), no por el rol guardado en su Asignación de Operador
/// Delegado: por la vía de Operación rige su perfil de origen; el rol guardado solo rige por la vía heredada. Si el
/// perfil no se puede determinar, o es Soporte TALVEG, no atiende (<see cref="DestinoCartera.PerfilDeOrigen"/>). Es el
/// perfil de la cuenta: sin sesión no se conoce una restricción de login local, y ese usuario puede recibir una
/// conversación que en esa sesión solo lea.
/// </para>
/// </summary>
public class ResponsableBuzonElegible(
    IDirectorioDestinosCartera directorioDestinos,
    IDirectorioEquipoCoordinador equipoCoordinador,
    IOperacionesQueryContext operaciones,
    IEmpresasQueryContext empresas,
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

        var ahora = DateTime.UtcNow;
        var acceso = await ResolverAccesoAsync(usuarioId, destino, ahora, cancellationToken);

        switch (acceso.Rol)
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

        return await AlcanzaAsync(usuarioId, destino, acceso, clienteEmpresarial, ahora, cancellationToken)
            ? null
            : MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa;
    }

    /// <summary>El rol con el que la cuenta entraría en el Tenant activo, y si entraría por una operación entera.</summary>
    private readonly record struct AccesoEfectivo(string? Rol, bool TenantEnteroPorOperacion = false);

    /// <summary>
    /// <see cref="TenantsBeneficiariosAutorizados.OperacionesQueAutorizan"/> desde la posición de propietario: la vía de
    /// Operación, si autoriza, manda sobre la heredada, también cuando discrepan los dos roles.
    /// </summary>
    private async Task<AccesoEfectivo> ResolverAccesoAsync(
        Guid usuarioId, DestinoCartera destino, DateTime ahora, CancellationToken cancellationToken)
    {
        if (!destino.EsOperadorDelegado) return new(destino.RolEfectivo);

        if (destino.PerfilDeOrigen is not { } perfil
            || tenantActual.TenantId is not { } propietarioTenantId
            || destino.TenantOrigenId is not { } operadorTenantId)
            return new(null);

        if (TenantsBeneficiariosAutorizados.RolesConAccesoPorOperacion.Contains(perfil)
            && await TenantsBeneficiariosAutorizados.OperaElTenantEnteroAsync(
                operaciones, operadorTenantId, propietarioTenantId, ahora, cancellationToken))
            return new(perfil, TenantEnteroPorOperacion: true);

        if (perfil == TenantsBeneficiariosAutorizados.RolAcotadoPorCartera
            && await TenantsBeneficiariosAutorizados.CarterasPorOperacion(operaciones, usuarioId, operadorTenantId, ahora)
                .AnyAsync(v => v.Operacion.PropietarioTenantId == propietarioTenantId
                               && v.Operacion.Servicio == TenantsBeneficiariosAutorizados.ServicioDelContexto,
                    cancellationToken))
            return new(perfil);

        // Vía heredada: el rol guardado, con la lista blanca de CurrentUserService.ResolverRolViaHeredadaAsync.
        return new(destino.RolEfectivo is { } guardado
                   && TenantsBeneficiariosAutorizados.RolesDelegablesPorOperacion.Contains(guardado)
            ? guardado
            : null);
    }

    private async Task<bool> AlcanzaAsync(
        Guid usuarioId, DestinoCartera destino, AccesoEfectivo acceso, Guid clienteEmpresarial, DateTime ahora,
        CancellationToken cancellationToken)
    {
        // Decisión del 2026-10-20: en un Tenant beneficiario, el Coordinador CAE de la organización que lo opera
        // entero lo alcanza sin cartera (AlcanceDatosService.ObtenerAlcanceRealAsync).
        if (acceso.TenantEnteroPorOperacion)
            return await EsClienteEmpresarialAsync(clienteEmpresarial, cancellationToken);

        if (tenantActual.TenantId is not { } propietarioTenantId || destino.TenantOrigenId is not { } operadorTenantId)
            return false;

        // El equipo se lee desde su Tenant: el de un Coordinador CAE externo no es legible aquí, y sin él solo
        // cuenta su propia cartera. Las cuentas desactivadas del equipo también cuentan, como en
        // AlcanceDatosService.ObtenerCarteraParaCoordinadorAsync.
        IReadOnlyCollection<Guid> personas = [usuarioId];
        if (acceso.Rol == RolCoordinadorCae && !destino.EsOperadorDelegado)
            personas = [usuarioId, .. (await equipoCoordinador.ObtenerEquipoAsync(usuarioId, cancellationToken)).Select(m => m.Id)];

        var ambitos = await TenantsBeneficiariosAutorizados.AmbitosEfectivosDeCarteraAsync(
            operaciones, personas, propietarioTenantId, operadorTenantId, ahora, cancellationToken);

        // Basta un ámbito del Tenant entero para que sea ese el alcance, también si otra cartera nombra esta Empresa:
        // AlcanceDatosService.ObtenerCarteraAsync no suma las listas a una cartera universal.
        return ambitos.Contains(null)
            ? await EsClienteEmpresarialAsync(clienteEmpresarial, cancellationToken)
            : ambitos.Contains(clienteEmpresarial);
    }

    // El Tenant entero de AlcanceDatosService.ObtenerClienteIdsVisiblesAsync: una Empresa sin el discriminador no
    // está en esa lista, aunque la Asignación cubra todo el Tenant.
    private Task<bool> EsClienteEmpresarialAsync(Guid empresa, CancellationToken cancellationToken) =>
        empresas.Empresas.AnyAsync(e => e.Id == empresa && e.EsCritico != null, cancellationToken);
}
