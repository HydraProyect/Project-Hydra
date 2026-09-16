using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Common;

/// <inheritdoc cref="IAutorizacionEscrituraEfectiva" />
public class AutorizacionEscrituraEfectiva(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual)
    : IAutorizacionEscrituraEfectiva
{
    private const string RolAdministrador = "Administrador";

    public async Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default)
    {
        if (await currentUserService.ObtenerRolActualAsync() == RolAdministrador)
            return true;

        // ObtenerAsync, no RevalidarAsync — al revés que en AutorizacionEscrituraBehavior,
        // y a propósito (hallazgo de Codex, revisión previa a este PR): este método SIEMPRE
        // se invoca desde DENTRO de un handler marcado IComandoDeAprovisionamiento, es decir,
        // DESPUÉS de que en el mismo comando ya revalidaron la sesión, en fresco, tanto
        // AutorizacionEscrituraBehavior como ElevacionEscrituraAprovisionamientoBehavior — el
        // segundo justo antes de elevar el rol de la conexión a cae_app_aprovisionamiento.
        // Ese rol NO tiene GRANT sobre las tablas del plano 3 de privilegio de
        // plataforma que esa revalidación consulta (deliberado: no son contenido CAE)
        // así que una TERCERA consulta aquí con RevalidarAsync fallaría con 42501 — la
        // importación bajo Aprovisionamiento no llegaría nunca a ejecutarse. ObtenerAsync
        // reutiliza la memo que ElevacionEscrituraAprovisionamientoBehavior acaba de dejar
        // fresca en ESTE MISMO comando, sin tocar la base. Esto no reintroduce el riesgo de
        // REC-067: aquel es sobre una memo que sobrevive ENTRE comandos distintos del mismo
        // circuito Blazor; aquí la memo se estableció milisegundos antes, dentro de la misma
        // ejecución de este comando, por el propio pipeline que lo autorizó — el "punto de
        // mutación" que RevalidarAsync protege ya se ejerció más afuera, en
        // AutorizacionEscrituraBehavior.
        var sesion = await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken);

        return sesion is { Capacidad: CapacidadPrivilegio.Aprovisionamiento } s
               && s.TenantObjetivoId == tenantActual.TenantId;
    }
}
