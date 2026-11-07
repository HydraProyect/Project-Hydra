using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.CalendarioLaboral;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Calendario.Queries;

/// <summary>
/// Las capas «Festivos» y «Jornada intensiva» del calendario de un mes (Especificación de Ausencias y
/// Coberturas, § 5.4): por cada persona visible, su comunidad, los festivos que le aplican y sus días de
/// jornada intensiva en ese mes. No lleva nombres ni identificadores de persona: la capa solo pinta fechas.
/// </summary>
public record ObtenerCapasCalendarioQuery(int Anio, int Mes) : IRequest<CapasCalendarioDto>;

/// <param name="VeAlEquipo">Si quien mira ve a todo el equipo (Coordinador CAE, Dirección CAE, Administrador) o solo a sí mismo (Gestor CAE).</param>
public record CapasCalendarioDto(bool VeAlEquipo, IReadOnlyList<PersonaCapasCalendarioDto> Personas)
{
    public static readonly CapasCalendarioDto Vacio = new(false, []);

    /// <summary>Las comunidades de las personas visibles, sin repetir y en orden alfabético: las opciones de «Festivos de».</summary>
    public IReadOnlyList<string> Comunidades => Personas
        .Select(p => p.Comunidad).OfType<string>().Distinct(StringComparer.Ordinal)
        .Order(StringComparer.CurrentCulture).ToList();
}

/// <param name="Comunidad">Nombre de su comunidad autónoma, o null sin comunidad (solo festivos nacionales).</param>
public record PersonaCapasCalendarioDto(
    string? Comunidad,
    IReadOnlyList<FestivoCapaDto> Festivos,
    IReadOnlyList<DateOnly> DiasJornadaIntensiva);

/// <param name="Lugar">La comunidad (autonómico) o el municipio (local); null en un nacional.</param>
public record FestivoCapaDto(DateOnly Fecha, string Nombre, AmbitoFestivo Ambito, string? Lugar);

/// <summary>
/// <para>
/// <b>Quién ve los festivos de quién</b> (Especificación § 2, fila «Festivos y jornada intensiva»): el Gestor CAE,
/// los suyos; el Coordinador CAE, la Dirección CAE y el Administrador, los de todo el equipo. El equipo son las
/// cuentas activas de Gestor CAE y de Coordinador CAE de la organización de origen de quien mira (el Operador CAE
/// en el que trabaja). Nunca las de otro Operador CAE: la lista sale del Tenant de origen de la sesión.
/// </para>
///
/// <para>
/// Todo se resuelve en el ámbito del Tenant de origen (<see cref="ContextoOperadorCae.EnOrigen"/>), sea cual sea el
/// Workspace operativo derivado abierto: los calendarios laborales son de ese Tenant, y el rol se lee en Identity
/// (<see cref="IDirectorioUsuariosService.EsCuentaActivaConRolAsync"/>). Falla cerrado sin rol de negocio en la
/// sesión (Sesión Privilegiada: Soporte TALVEG no es Operador CAE) y con cualquier otro rol (Consulta, Cliente).
/// </para>
/// </summary>
public class ObtenerCapasCalendarioQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICalendarioLaboralQueryContext contexto)
    : IRequestHandler<ObtenerCapasCalendarioQuery, CapasCalendarioDto>
{
    private const string GestorCae = "GestorCae";
    private const string CoordinadorCae = "CoordinadorCae";
    private static readonly string[] RolesQueVenAlEquipo = ["Administrador", "DireccionCae", CoordinadorCae];

    public async Task<CapasCalendarioDto> Handle(ObtenerCapasCalendarioQuery request, CancellationToken cancellationToken)
    {
        if (request.Mes is < 1 or > 12 || request.Anio is < 1 or > 9998)
            return CapasCalendarioDto.Vacio;
        var primerDia = new DateOnly(request.Anio, request.Mes, 1);
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);

        var contextoOperador = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contextoOperador.EsFallido)
            return CapasCalendarioDto.Vacio;
        var yo = contextoOperador.Valor;

        using (yo.EnOrigen())
        {
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return CapasCalendarioDto.Vacio;

            var veAlEquipo = false;
            foreach (var rol in RolesQueVenAlEquipo)
            {
                if (await directorioUsuarios.EsCuentaActivaConRolAsync(yo.UsuarioId, yo.OperadorTenantId, rol, cancellationToken))
                {
                    veAlEquipo = true;
                    break;
                }
            }

            IReadOnlyList<Guid> personas;
            if (veAlEquipo)
            {
                var gestores = await directorioUsuarios.ObtenerCuentasActivasConRolAsync(yo.OperadorTenantId, GestorCae, cancellationToken);
                var coordinadores = await directorioUsuarios.ObtenerCuentasActivasConRolAsync(yo.OperadorTenantId, CoordinadorCae, cancellationToken);
                personas = gestores.Concat(coordinadores).Distinct().ToList();
            }
            else if (await directorioUsuarios.EsCuentaActivaConRolAsync(yo.UsuarioId, yo.OperadorTenantId, GestorCae, cancellationToken))
            {
                personas = [yo.UsuarioId];
            }
            else
            {
                return CapasCalendarioDto.Vacio;
            }

            if (personas.Count == 0)
                return new CapasCalendarioDto(veAlEquipo, []);

            var guardados = await contexto.CalendariosLaboralesPersona
                .Where(c => personas.Contains(c.UsuarioId))
                .ToListAsync(cancellationToken);
            var porPersona = guardados.ToDictionary(c => c.UsuarioId);

            var festivosDelMes = await contexto.FestivosOficiales
                .Where(f => f.Fecha >= primerDia && f.Fecha <= ultimoDia)
                .ToListAsync(cancellationToken);

            var dias = Enumerable.Range(0, ultimoDia.Day).Select(primerDia.AddDays).ToList();
            var resultado = personas
                .Select(id => porPersona.TryGetValue(id, out var guardado) ? guardado.Perfil : PerfilLaboral.SinConfigurar)
                .Select(perfil => new PersonaCapasCalendarioDto(
                    perfil.Comunidad?.Nombre(),
                    perfil.FestivosQueLeAplican(festivosDelMes)
                        .Select(f => new FestivoCapaDto(f.Fecha, f.Nombre, f.Ambito, LugarDe(f)))
                        .ToList(),
                    dias.Where(d => perfil.TieneJornadaIntensiva(d, festivosDelMes, PeriodoIntensiva.Verano)).ToList()))
                .ToList();

            return new CapasCalendarioDto(veAlEquipo, resultado);
        }
    }

    private static string? LugarDe(FestivoOficial festivo) => festivo.Ambito switch
    {
        AmbitoFestivo.Autonomico => festivo.Comunidad?.Nombre(),
        AmbitoFestivo.Local => festivo.Municipio,
        _ => null,
    };
}
