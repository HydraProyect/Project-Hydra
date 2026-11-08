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

    /// <summary>
    /// Si el catálogo oficial tiene el calendario de ese año (<see cref="CatalogoFestivosOficiales.AniosCubiertos"/>).
    /// Sin él no se marcan festivos ni jornada intensiva, y la barra lo avisa.
    /// </summary>
    public bool CatalogoCubreElAnio { get; init; } = true;

    /// <summary>
    /// Si a alguna persona visible le faltan sus festivos locales en el catálogo
    /// (<see cref="CatalogoFestivosOficiales.IncluyeLocales"/> es false): la capa de festivos no los pinta y la barra
    /// lo avisa.
    /// </summary>
    public bool FaltanFestivosLocales { get; init; }

    /// <summary>
    /// Si a alguna de esas personas se le marca jornada intensiva este mes: puede caer en un festivo local suyo que el
    /// catálogo no tiene, y la barra lo dice.
    /// </summary>
    public bool IntensivaPuedeCaerEnFestivoLocal { get; init; }

    /// <summary>
    /// Las comunidades de personas visibles cuyos autonómicos no están en el catálogo de un año que sí lo está
    /// (<see cref="CatalogoFestivosOficiales.Cubre"/> es false): a esas personas no se les marcan autonómicos ni
    /// intensiva, y la barra lo avisa. La Val d'Aran figura con su nombre (<see cref="CatalogoFestivosOficiales.NombreValDAran"/>),
    /// no como «Cataluña». Vacía en un año sin catálogo, que ya avisa <see cref="CatalogoCubreElAnio"/>.
    /// </summary>
    public IReadOnlyList<string> ComunidadesSinAutonomicos { get; init; } = [];

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
/// Workspace operativo derivado abierto: los calendarios laborales son de ese Tenant. La visibilidad la decide el rol
/// efectivo de la sesión en ese origen (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>), con las
/// restricciones de sesión ya aplicadas, y Identity confirma que la cuenta sigue activa con ese mismo rol
/// (<see cref="IDirectorioUsuariosService.EsCuentaActivaConRolAsync"/>). Falla cerrado sin rol de negocio en la
/// sesión (Sesión Privilegiada: Soporte TALVEG no es Operador CAE) y con cualquier otro rol efectivo (Consulta,
/// Cliente), también cuando la cuenta tiene en Identity un rol de gestión y la sesión quedó limitada a Consulta.
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
            // Decide el rol efectivo de la sesión en el Tenant de origen, que ya lleva aplicadas sus restricciones
            // (una sesión de login local limitada a Consulta es Consulta aunque la cuenta sea de Coordinador CAE);
            // Identity solo confirma que la cuenta sigue activa con ese mismo rol en su Operador CAE.
            var rol = await currentUserService.ObtenerRolEfectivoAsync();
            bool veAlEquipo;
            if (rol is not null && RolesQueVenAlEquipo.Contains(rol))
                veAlEquipo = true;
            else if (rol == GestorCae)
                veAlEquipo = false;
            else
                return CapasCalendarioDto.Vacio;

            if (!await directorioUsuarios.EsCuentaActivaConRolAsync(yo.UsuarioId, yo.OperadorTenantId, rol, cancellationToken))
                return CapasCalendarioDto.Vacio;

            IReadOnlyList<Guid> personas;
            if (veAlEquipo)
            {
                var gestores = await directorioUsuarios.ObtenerCuentasActivasConRolAsync(yo.OperadorTenantId, GestorCae, cancellationToken);
                var coordinadores = await directorioUsuarios.ObtenerCuentasActivasConRolAsync(yo.OperadorTenantId, CoordinadorCae, cancellationToken);
                personas = gestores.Concat(coordinadores).Distinct().ToList();
            }
            else
            {
                personas = [yo.UsuarioId];
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
            var perfiles = personas
                .Select(id => porPersona.TryGetValue(id, out var guardado) ? guardado.Perfil : PerfilLaboral.SinConfigurar)
                .ToList();
            var calculados = perfiles
                .Select(perfil => (Perfil: perfil, Dto: new PersonaCapasCalendarioDto(
                    perfil.Comunidad?.Nombre(),
                    perfil.FestivosQueLeAplican(festivosDelMes)
                        .Select(f => new FestivoCapaDto(f.Fecha, f.Nombre, f.Ambito, LugarDe(f)))
                        .ToList(),
                    // Sin el calendario oficial de su año o de su comunidad no se sabe qué día es laborable. Sus
                    // festivos locales no están en el catálogo: la intensiva se marca igual y la barra lo avisa
                    // (IntensivaPuedeCaerEnFestivoLocal).
                    CatalogoFestivosOficiales.Cubre(request.Anio, perfil)
                        ? dias.Where(d => perfil.TieneJornadaIntensiva(d, festivosDelMes, PeriodoIntensiva.Verano)).ToList()
                        : [])))
                .ToList();
            var sinLocales = calculados
                .Where(c => !CatalogoFestivosOficiales.IncluyeLocales(request.Anio, c.Perfil))
                .ToList();
            var catalogoCubreElAnio = CatalogoFestivosOficiales.AniosCubiertos.Contains(request.Anio);

            return new CapasCalendarioDto(veAlEquipo, calculados.Select(c => c.Dto).ToList())
            {
                CatalogoCubreElAnio = catalogoCubreElAnio,
                FaltanFestivosLocales = sinLocales.Count > 0,
                IntensivaPuedeCaerEnFestivoLocal = sinLocales.Any(c => c.Dto.DiasJornadaIntensiva.Count > 0),
                ComunidadesSinAutonomicos = catalogoCubreElAnio
                    ? perfiles
                        .Where(p => p.Comunidad is not null && !CatalogoFestivosOficiales.Cubre(request.Anio, p))
                        .Select(p => CatalogoFestivosOficiales.EsDeLaValDAran(p)
                            ? CatalogoFestivosOficiales.NombreValDAran
                            : p.Comunidad!.Value.Nombre())
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.CurrentCulture).ToList()
                    : [],
            };
        }
    }

    private static string? LugarDe(FestivoOficial festivo) => festivo.Ambito switch
    {
        AmbitoFestivo.Autonomico => festivo.Comunidad?.Nombre(),
        AmbitoFestivo.Local => festivo.Municipio,
        _ => null,
    };
}
