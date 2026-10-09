using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Los dos hechos, ajenos a la cartera, que asignan solos un principal por escalado (ADR-011
/// § 2.7, enmienda 2026-10-08, punto 4): se abre una Asignación de Operación externa, que nace
/// sin carteras, o aparece el primer usuario elegible del Operador CAE. Detrás de un puerto
/// para que los comandos de delegaciones y de cuentas no carguen con el catálogo de carteras.
///
/// <para>
/// Son escrituras de cartera disparadas por otro comando: se llaman <b>dentro de su
/// transacción</b> (<see cref="ITransaccionDeComando"/>) y con el hecho ya guardado. Devuelven
/// <c>false</c> si el guardado perdió una carrera; el comando debe fallar entero.
/// </para>
/// </summary>
public interface IAsignacionAutomaticaDePrincipal
{
    /// <summary>
    /// La operación recién abierta pasa a la única cuenta activa del primer perfil del Operador
    /// CAE que tenga alguna. Con varias, o sin nadie, no hace nada: la operación queda en la alerta.
    ///
    /// <para>
    /// Las cuentas del Operador CAE se leen con la sesión de quien abre la operación. Si la abre
    /// el propio Operador CAE, las ve y asigna. Si la abre el Administrador del Tenant propietario
    /// (autoriza a su Operador CAE externo), la RLS de cuentas no le enseña las de otro Tenant:
    /// no se encuentra a nadie, no se asigna y la operación queda en la alerta del Operador CAE,
    /// donde la toma con «Asumir» quien corresponda. No se abre esa lectura para evitarlo.
    /// </para>
    /// </summary>
    Task<bool> AlAbrirOperacionAsync(AsignacionOperacion operacion, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="usuarioId"/> acaba de recibir un rol o de reactivarse en
    /// <paramref name="operadorTenantId"/>: si es su única cuenta elegible, recibe las
    /// operaciones que sigan sin principal. Un Gestor CAE nunca.
    /// </summary>
    Task<bool> AlPrimerElegibleAsync(Guid usuarioId, Guid operadorTenantId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAsignacionAutomaticaDePrincipal" />
public class AsignacionAutomaticaDePrincipal(
    ICatalogoIncorporacionCartera catalogo,
    IDirectorioUsuariosService directorioUsuarios,
    IBloqueoCarteraUsuario bloqueoCartera)
    : IAsignacionAutomaticaDePrincipal
{
    public Task<bool> AlAbrirOperacionAsync(AsignacionOperacion operacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operacion);

        // La operación raíz es la gestión propia del Tenant propietario: no tiene Operador CAE externo.
        if (operacion.EsRaiz || operacion.OperadorTenantId == operacion.PropietarioTenantId)
            return Task.FromResult(true);

        return EscaladoDePrincipalDeCartera.AsignarAlUnicoAsync(
            catalogo, directorioUsuarios, bloqueoCartera,
            [new OperacionConPrincipal(operacion.PropietarioTenantId, operacion.Id)],
            operacion.OperadorTenantId, excluidoUsuarioId: null, cancellationToken);
    }

    public Task<bool> AlPrimerElegibleAsync(Guid usuarioId, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(
            catalogo, directorioUsuarios, bloqueoCartera, usuarioId, operadorTenantId, cancellationToken);
}
