using CaeManager.Application.Plataforma;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IRaizBootstrapPlataforma" />
/// <remarks>
/// <para>
/// La raíz es <b>una persona designada por el despliegue</b>, no una
/// organización. Sustituye a la implementación anterior, que respondía que sí a
/// cualquier miembro del tenant de plataforma — y ese tenant es también el tenant
/// operativo de la empresa, así que cualquier gestor podía acuñarse autoridad.
/// No era un bootstrap: era una carrera de privilegios.
/// </para>
///
/// <para>
/// Las dos condiciones van juntas y por separado ninguna basta:
/// </para>
/// <code>
/// eres la raíz designada  ∧  el bootstrap no se ha consumido
/// </code>
/// <para>
/// La segunda es lo que convierte la raíz en un <b>acto único</b> en vez de en
/// una capacidad permanente de acuñar autoridad.
/// </para>
///
/// <para>
/// <b>Sin fila de estado, no hay raíz.</b> Falla cerrado: un despliegue sin
/// designar no habilita a nadie.
/// </para>
/// </remarks>
public class RaizBootstrapPorIdentidadDesignada(
    IPlataformaQueryContext plataformaContext) : IRaizBootstrapPlataforma
{
    public async Task<bool> EsRaizDeConfianzaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var estado = await plataformaContext.EstadoBootstrapPlataforma
            .FirstOrDefaultAsync(cancellationToken);

        return estado is not null && estado.PuedeArrancar(usuarioId);
    }
}
