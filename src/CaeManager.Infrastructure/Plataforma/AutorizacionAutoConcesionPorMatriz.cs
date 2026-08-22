using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IAutorizacionAutoConcesion" />
/// <remarks>
/// <para>
/// Matriz explícita y cerrada: un <c>switch</c> con un caso por capacidad y
/// <c>false</c> por defecto. Una capacidad nueva del enum <b>no</b> se vuelve
/// auto-concedible por existir — tiene que entrar aquí deliberadamente, y eso se
/// ve en la revisión.
/// </para>
///
/// <para>
/// <b>Las dos autoridades son distintas y no se reutiliza una para la otra.</b>
/// La raíz sirve para el acto fundacional y solo para él: una vez consumido el
/// bootstrap deja de responder que sí para siempre, mientras que la emisión de
/// <c>SoporteLectura</c> tiene que seguir funcionando o F2b-6 se queda sin
/// camino para abrir sesiones.
/// </para>
/// </remarks>
public class AutorizacionAutoConcesionPorMatriz(
    IRaizBootstrapPlataforma raizBootstrap,
    IPlataformaQueryContext plataformaContext) : IAutorizacionAutoConcesion
{
    public Task<bool> PuedeAutoConcederseAsync(
        Guid usuarioId, CapacidadPrivilegio capacidad, CancellationToken cancellationToken = default) =>
        capacidad switch
        {
            // El acto fundacional. Irrepetible: la raíz responde que no en cuanto
            // el bootstrap queda consumido, y no vuelve a abrirse ni aunque la
            // concesión fundacional se revoque.
            CapacidadPrivilegio.AdminPlataforma =>
                raizBootstrap.EsRaizDeConfianzaAsync(usuarioId, cancellationToken),

            // Quien administra la plataforma puede darse acceso de soporte. No
            // puede dárselo a otro: el comando no admite beneficiario.
            CapacidadPrivilegio.SoporteLectura =>
                TieneAdminPlataformaVigenteAsync(usuarioId, cancellationToken),

            _ => Task.FromResult(false),
        };

    private async Task<bool> TieneAdminPlataformaVigenteAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;

        // Vigente de verdad: estado y ventana, los mismos tres estados que
        // ADR-011 § 8.1 no permite colapsar. Una concesión revocada o caducada no
        // habilita a emitir nada.
        return await plataformaContext.ConcesionesPrivilegio.AnyAsync(
            c => c.UsuarioPlataformaId == usuarioId
                 && c.Capacidad == CapacidadPrivilegio.AdminPlataforma
                 && c.Estado == EstadoConcesionPrivilegio.Vigente
                 && c.VigenciaDesde <= ahora
                 && (c.VigenciaHasta == null || c.VigenciaHasta > ahora),
            cancellationToken);
    }
}
