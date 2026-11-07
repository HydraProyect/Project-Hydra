using CaeManager.Domain.CalendarioLaboral;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El calendario laboral de las personas del equipo del Operador CAE externo del piloto, para que las capas
/// «Festivos» y «Jornada intensiva» de /calendario tengan comunidades distintas que enseñar: Aragón, País Vasco y
/// Cataluña (las de la maqueta con persona en el equipo sembrado).
///
/// <para>
/// <b>Idempotente y sin pisar</b>: solo crea el calendario de quien no tiene ninguno; uno ya guardado se deja como
/// está. Por eso también rellena un entorno ya sembrado la siguiente vez que el lote llega a escribir, sin resembrar;
/// no la siguiente vez que arranca: con la fecha de la demostración fuera de margen y nada por sembrar,
/// <c>PilotoOutboundSeeder.SembrarLoteAsync</c> vuelve antes de sembrar el equipo, y el calendario con él. Se llama
/// solo desde la siembra del equipo, dentro del lote (bajo sus mismas guardas, lo fija
/// <c>PilotoOutboundSoloDesdeElModoCliTests</c>), y en el ámbito del Tenant del Operador CAE externo que ya abre
/// <c>PilotoOutboundSeeder.SembrarEquipoAsync</c>: el calendario es dato de ese Tenant.
/// </para>
/// </summary>
internal static class PilotoOutboundCalendarioLaboral
{
    internal static IReadOnlyList<(Func<PilotoOutboundEquipoIds, Guid> Persona, PerfilLaboral Perfil)> Reparto { get; } =
    [
        (e => e.GestoraPrimeraId, new PerfilLaboral(ComunidadAutonoma.Aragon, "Zaragoza", aplicaJornadaIntensiva: true)),
        (e => e.GestorSegundoId, new PerfilLaboral(ComunidadAutonoma.PaisVasco, "Bilbao", aplicaJornadaIntensiva: true)),
        (e => e.CoordinadoraId, new PerfilLaboral(ComunidadAutonoma.Cataluna, "Barcelona", aplicaJornadaIntensiva: false)),
    ];

    internal static async Task SembrarAsync(
        CaeManagerDbContext dbContext, PilotoOutboundEquipoIds equipo, CancellationToken cancellationToken)
    {
        var ids = Reparto.Select(r => r.Persona(equipo)).ToList();
        var conCalendario = await dbContext.CalendariosLaboralesPersona
            .Where(c => ids.Contains(c.UsuarioId))
            .Select(c => c.UsuarioId)
            .ToListAsync(cancellationToken);

        var nuevos = Reparto
            .Where(r => !conCalendario.Contains(r.Persona(equipo)))
            .Select(r => CalendarioLaboralPersona.Crear(r.Persona(equipo), r.Perfil))
            .ToList();
        if (nuevos.Count == 0)
            return;

        dbContext.CalendariosLaboralesPersona.AddRange(nuevos);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Las cuentas del equipo del piloto que llevan calendario laboral.</summary>
internal sealed record PilotoOutboundEquipoIds(Guid GestoraPrimeraId, Guid GestorSegundoId, Guid CoordinadoraId);
