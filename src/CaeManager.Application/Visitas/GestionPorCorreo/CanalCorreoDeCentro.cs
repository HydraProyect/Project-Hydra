using CaeManager.Application.Centros;
using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.GestionPorCorreo;

/// <summary>El canal por correo con el que se acredita una Visita a un Centro gestionado por correo.</summary>
public record CanalCorreoCentro(string EmailsDestinatarios, string? NombreContacto);

/// <summary>
/// Decide si un Centro se gestiona <b>por correo</b> y no por una plataforma (P1-X1,
/// decisión del propietario del 2026-09-24): su canal de gestión es
/// <see cref="TipoCanalGestion.Email"/>.
///
/// <para>
/// Un Centro tiene N canales (Lote 0-E) y a lo sumo uno principal; «el» canal es el
/// principal. Si no hay principal, el Centro se gestiona por correo solo si ninguno de sus
/// canales es una plataforma: con portal y correo a la vez y sin principal no se puede
/// decir que «el canal es Email», y la acreditación va por la plataforma (P0-7).
/// Un canal Email sin destinatarios no cuenta: no hay a quién escribir.
/// </para>
///
/// <para>
/// No mira <see cref="Centro.GestionCae"/>: un Centro sin gestión CAE (P1-X2) se
/// comunica con el aviso de visita, y quien llama lo descarta antes.
/// </para>
/// </summary>
public static class CanalCorreoDeCentro
{
    public record CanalCandidato(TipoCanalGestion Tipo, bool EsPrincipal, string? EmailsDestinatarios, string? NombreContacto, Guid Id);

    public static async Task<CanalCorreoCentro?> ResolverAsync(
        ICentrosQueryContext centrosContext, Guid centroId, CancellationToken cancellationToken)
    {
        var canales = await centrosContext.CanalesGestionDocumental
            .Where(c => c.CentroId == centroId)
            .Select(c => new CanalCandidato(c.Tipo, c.EsPrincipal, c.EmailsDestinatarios, c.NombreContacto, c.Id))
            .ToListAsync(cancellationToken);

        return Elegir(canales);
    }

    /// <summary>Regla pura, sin base de datos: ver el resumen de la clase.</summary>
    public static CanalCorreoCentro? Elegir(IReadOnlyCollection<CanalCandidato> canales)
    {
        var principal = canales.FirstOrDefault(c => c.EsPrincipal);
        CanalCandidato? elegido;

        if (principal is not null)
            elegido = principal.Tipo == TipoCanalGestion.Email ? principal : null;
        else if (canales.Any(c => c.Tipo == TipoCanalGestion.Plataforma))
            elegido = null;
        else
            elegido = canales
                .Where(c => c.Tipo == TipoCanalGestion.Email && !string.IsNullOrWhiteSpace(c.EmailsDestinatarios))
                .OrderBy(c => c.Id)
                .FirstOrDefault();

        if (elegido is null || string.IsNullOrWhiteSpace(elegido.EmailsDestinatarios))
            return null;

        return new CanalCorreoCentro(elegido.EmailsDestinatarios.Trim(), string.IsNullOrWhiteSpace(elegido.NombreContacto) ? null : elegido.NombreContacto.Trim());
    }
}
