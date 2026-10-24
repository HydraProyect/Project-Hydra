using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// El estado de una Empresa o de un Trabajador tal como lo pintan sus listados, Empresa 360 y la cabecera de Trabajador 360
/// (regla del 2026-10-23: cada nivel pinta su contexto y el rojo es solo lo que un Centro marca «bloquea el acceso»), leído de
/// <c>EstadosEntidad</c> en vez de calculado.
/// </summary>
/// <param name="Estado">Sin ningún documento y sin requisito obligatorio incumplido, <see cref="EstadoDocumento.SinCaducidad"/>: lo que el cálculo en lectura da a quien no tiene nada que contar.</param>
/// <param name="VencidoSinBloqueo">El <see cref="Estado"/> es Vencido solo por documentos que ningún Centro marca obligatorios: aviso, no rojo.</param>
/// <param name="DocumentosFaltantes">Nombres de los Tipos obligatorios sin ningún documento, sin repetir y por nombre.</param>
public readonly record struct EstadoLeido(EstadoDocumento Estado, bool VencidoSinBloqueo, IReadOnlyList<string> DocumentosFaltantes)
{
    /// <summary>Lo que lee una entidad sin fila: la de una Empresa o un Trabajador sin documentos ni requisitos incumplidos.</summary>
    public static EstadoLeido SinNada { get; } = new(EstadoDocumento.SinCaducidad, false, []);
}

/// <summary>
/// Lector del estado materializado de Empresa y Trabajador (PR 7 del plan). Sirve el mismo resultado que el cálculo en lectura
/// de hoy, o dice que no puede: cada «no puedo» deja al llamador en su camino de cálculo de siempre, que sigue vivo y es
/// el de referencia.
///
/// <para>
/// <b>Cuándo lee de las tablas.</b> Solo si (1) el Tenant está completo, es decir, tiene su fila en
/// <c>VersionesEstadoTenant</c> (la crea el relleno al terminar; sin ella el Tenant tiene filas a medias o ninguna), y (2) el
/// usuario alcanza todos los Centros del Tenant: el estado guardado es el de todos los Centros, y el de un usuario con
/// Asignación de Cartera parcial es un agregado que no se recompone filtrando filas (<see cref="IEvaluacionDeAccesoDelTenant"/>).
/// </para>
///
/// <para>
/// <b>Qué filas no decide.</b> La fila no guarda todo lo que el listado de hoy pinta, así que hay filas que el lector se
/// niega a servir y mandan a su llamador al cálculo: (a) una fila cuyo <c>ProximoCambioEl</c> ya llegó (el estado cambia por
/// el paso del tiempo y nadie lo ha recalculado todavía; hasta la pasada de medianoche es lo normal tras cada madrugada);
/// (b) un voluntario inactivo en rojo (el cálculo en lectura no le aplica los requisitos de Centro, el estado guardado sí);
/// (c) un Vencido cuyos documentos vencidos son todos de Tipos que ningún Centro marca obligatorios, pero con algún par que
/// un Centro bloquea por su periodicidad propia: la fila no dice si el rojo viene de ese par (necesita la tolerancia del
/// Centro, que no se guarda) y el cálculo sí.
/// </para>
/// </summary>
public interface ILectorEstadoMaterializado
{
    /// <summary>¿Puede el usuario actual leer el estado de las tablas? Ver las condiciones en el resumen de la interfaz.</summary>
    Task<bool> DisponibleAsync(CancellationToken cancellationToken);

    /// <summary>Las filas de un ámbito, para unirlas a la consulta de un listado.</summary>
    IQueryable<EstadoEntidad> Entidades(AmbitoEstadoEntidad ambito);

    /// <summary>
    /// ¿Hay entre estas entidades (subconsulta del listado, antes de paginar) alguna que la fila no decide? Si la hay, el
    /// listado entero se calcula como siempre.
    /// </summary>
    Task<bool> HayFilasQueNoDecideAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// El estado de esas entidades (una página, el desglose de una ficha). <c>null</c> si el lector no puede servirlas (no
    /// disponible, o alguna fila no decide): el llamador las calcula como siempre.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EstadoLeido>?> LeerAsync(
        AmbitoEstadoEntidad ambito, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// El estado de todas las entidades de la subconsulta del listado (antes de paginar), para la franja, el filtro y el orden
    /// por estado, repartido por la clave de estado del listado. <c>null</c> si el lector no puede servirlas (no disponible,
    /// alguna entidad sin fila o alguna fila que no decide): el listado entero se calcula como siempre. Las filas salen de
    /// <see cref="Frescura.CacheEstadoCircuito"/>; el alcance lo pone la subconsulta, que ya lo lleva.
    /// </summary>
    Task<IdsPorEstadoLeido?> LeerEstadoCompletoAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IdsPorEstadoLeido?>(null);
}

/// <summary>
/// Las entidades de un listado repartidas por la clave de estado con que el listado cuenta, filtra y ordena
/// (<c>EstadoDocumentalFiltro.ClaveOrdenConFaltantes</c>). Lo que no está en ninguna lista es Sin caducidad. Listas, no un
/// diccionario, para que la consulta del listado las pase a SQL como parámetros.
/// </summary>
public sealed record IdsPorEstadoLeido(
    List<Guid> Vencido, List<Guid> VencidoSinBloqueo, List<Guid> Faltante, List<Guid> Urgente, List<Guid> Proximo,
    List<Guid> SinConfirmar, List<Guid> Vigente)
{
    /// <summary>La misma partición que hacía la unión con <c>EstadosEntidad</c> en SQL.</summary>
    internal static IdsPorEstadoLeido De(IEnumerable<(Guid Id, short Estado, int Vencidos, int VencidosSinBloqueo)> filas)
    {
        var resultado = new IdsPorEstadoLeido([], [], [], [], [], [], []);
        foreach (var (id, estado, vencidos, vencidosSinBloqueo) in filas)
        {
            var lista = (EstadoDocumento)estado switch
            {
                EstadoDocumento.Vencido when vencidos > 0 && vencidos == vencidosSinBloqueo => resultado.VencidoSinBloqueo,
                EstadoDocumento.Vencido => resultado.Vencido,
                EstadoDocumento.Faltante => resultado.Faltante,
                EstadoDocumento.Urgente => resultado.Urgente,
                EstadoDocumento.Proximo => resultado.Proximo,
                EstadoDocumento.SinConfirmar => resultado.SinConfirmar,
                EstadoDocumento.Vigente => resultado.Vigente,
                _ => null,
            };
            lista?.Add(id);
        }

        return resultado;
    }
}

/// <summary>El lector que nunca lee: todo se calcula en lectura. Para quien construye un manejador sin inyección.</summary>
public sealed class SinLectorDeEstadoMaterializado : ILectorEstadoMaterializado
{
    public static SinLectorDeEstadoMaterializado Instancia { get; } = new();

    private SinLectorDeEstadoMaterializado()
    {
    }

    public Task<bool> DisponibleAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public IQueryable<EstadoEntidad> Entidades(AmbitoEstadoEntidad ambito) => Enumerable.Empty<EstadoEntidad>().AsQueryable();

    public Task<bool> HayFilasQueNoDecideAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<Guid, EstadoLeido>?> LeerAsync(
        AmbitoEstadoEntidad ambito, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, EstadoLeido>?>(null);
}

public sealed class LectorEstadoMaterializado(
    IEstadoMaterializadoQueryContext tablas,
    ILectorEstadoCentros lectorCentros,
    IAlcanceDatosService alcanceDatos,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    IProyectosQueryContext proyectosContext,
    IRequisitosObligatoriosIncumplidosService requisitosObligatorios,
    CacheEstadoCircuito? cache = null)
    : ILectorEstadoMaterializado
{
    private static readonly short Vencido = (short)EstadoDocumento.Vencido;
    private static readonly short Faltante = (short)EstadoDocumento.Faltante;

    public async Task<bool> DisponibleAsync(CancellationToken cancellationToken) =>
        (await DisponibilidadAsync(cancellationToken)).Completo;

    /// <summary>
    /// <see cref="DisponibleAsync"/> con la versión del Tenant leída en la misma sentencia, para la caché del circuito.
    /// </summary>
    private async Task<ComprobacionTenantEstado> DisponibilidadAsync(CancellationToken cancellationToken)
    {
        // El alcance primero: lo resuelve y memoiza el servicio de alcance (la consulta lo necesita de todos modos), así que no
        // cuesta ninguna sentencia propia, y quien no alcanza el Tenant entero (acceso total o cartera universal) no paga las dos
        // de abajo. Un rol Cliente o una cartera por lista de Clientes calculan, aunque su lista cubra todos los Centros.
        if (!await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken))
            return new ComprobacionTenantEstado(false, null);

        // Tenant completo: su fila de versión (sin ella tiene filas a medias o ninguna) y la cola de recálculos sin consumir
        // vacía (cambio de Tipo, tolerancia o umbrales, clausura grande, fallo del recálculo: lo guardado puede estar viejo).
        // La misma comprobación que los lectores de Centro, no una copia.
        return await lectorCentros.ComprobarTenantAsync(cancellationToken);
    }

    /// <summary>
    /// Las filas del ámbito en el Tenant de la conexión, todas y sin alcance (diseño § 13.11), de la caché del circuito si
    /// siguen al día con la versión leída. Quien llama se queda con las de sus entidades, que ya llevan el alcance.
    /// </summary>
    private async Task<IReadOnlyList<FilaLeida>> FilasAsync(
        AmbitoEstadoEntidad ambito, VersionEstadoLeida? version, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (cache is null)
            return await LeerFilasAsync(ambito, ids, cancellationToken);

        return await cache.ObtenerAsync(
            new ConsultaEstadoEnCache<FilaLeida>(
                $"estados-entidad-{(int)ambito}",
                f => [f.EntidadId],
                async ct => await Proyectar(Entidades(ambito)).ToListAsync(ct),
                (entidades, ct) => LeerFilasAsync(ambito, entidades, ct)),
            version, ids, cancellationToken);
    }

    private async Task<IReadOnlyList<FilaLeida>> LeerFilasAsync(
        AmbitoEstadoEntidad ambito, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var lista = ids.Distinct().ToList();
        return await Proyectar(Entidades(ambito).Where(e => lista.Contains(e.EntidadId))).ToListAsync(cancellationToken);
    }

    private static IQueryable<FilaLeida> Proyectar(IQueryable<EstadoEntidad> filas) =>
        filas.Select(e => new FilaLeida(
            e.EntidadId, e.Estado, e.Vencidos, e.VencidosSinBloqueo, e.Faltan, e.ProximoCambioEl, e.VersionCalculo));

    public async Task<IdsPorEstadoLeido?> LeerEstadoCompletoAsync(
        AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken)
    {
        var disponibilidad = await DisponibilidadAsync(cancellationToken);
        if (!disponibilidad.Completo)
            return null;

        // Las entidades del listado (con su alcance y sus filtros), una sentencia; sus filas, de la caché.
        var lista = await ids.Distinct().ToListAsync(cancellationToken);
        var filas = (await FilasAsync(ambito, disponibilidad.Version, lista, cancellationToken)).ToDictionary(f => f.EntidadId);
        var delListado = new List<FilaLeida>(lista.Count);
        foreach (var id in lista)
        {
            // (d) Una entidad sin fila no es una «sin nada»: el listado entero se calcula.
            if (!filas.TryGetValue(id, out var fila))
                return null;
            delListado.Add(fila);
        }

        if (await HayFilasQueNoDecideAsync(ambito, delListado, cancellationToken))
            return null;

        return IdsPorEstadoLeido.De(delListado.Select(f => (f.EntidadId, f.Estado, f.Vencidos, f.VencidosSinBloqueo)));
    }

    public IQueryable<EstadoEntidad> Entidades(AmbitoEstadoEntidad ambito) =>
        tablas.EstadosEntidad.Where(e => e.Ambito == ambito);

    public async Task<bool> HayFilasQueNoDecideAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken)
    {
        var existentes = Entidades(ambito);

        // (d) Una entidad sin fila no es una entidad «sin nada»: el cálculo de hoy puede darle Faltante (un alta sin documentación
        // está bloqueada). Si falta alguna, se calcula.
        if (await ids.AnyAsync(id => !existentes.Any(e => e.EntidadId == id), cancellationToken))
            return true;

        return await HayFilasQueNoDecideAsync(ambito, existentes.Where(e => ids.Contains(e.EntidadId)), cancellationToken);
    }

    private async Task<bool> HayFilasQueNoDecideAsync(
        AmbitoEstadoEntidad ambito, IQueryable<EstadoEntidad> filas, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();

        // (a) La frontera de la fila ya llegó (el estado guardado es el de un día anterior) o la calculó una versión de la regla
        // que no es la vigente.
        if (await filas.AnyAsync(
                e => (e.ProximoCambioEl != null && e.ProximoCambioEl <= hoy) || e.VersionCalculo != CalculoEstadoMaterializado.VersionDeLaRegla,
                cancellationToken))
            return true;

        // (b) Un voluntario inactivo en rojo: el listado de hoy no le aplica los requisitos de Centro.
        if (ambito == AmbitoEstadoEntidad.Trabajador)
        {
            var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy);
            if (await filas.AnyAsync(e => (e.Estado == Vencido || e.Estado == Faltante) && inactivos.Contains(e.EntidadId), cancellationToken))
                return true;
        }

        // (c) Vencido sin ningún Tipo obligatorio vencido entre los documentos vencidos, pero con un par que un Centro marca
        // «bloquea el acceso» y que su periodicidad propia ya dejó sin valer: puede ser el motivo del rojo. Una condición
        // necesaria, no suficiente: si no se cumple, el rojo solo puede venir de un documento vencido de un Tipo obligatorio, y
        // eso sí lo dicen los recuentos (Vencidos > VencidosSinBloqueo).
        // Para una Empresa los pares no bastan: no existen los de un Centro donde solo tiene voluntarios inactivos, y el estado de
        // hoy sí los cuenta. Mientras no haya una columna propia de «vencidos de Tipo obligatorio», toda Empresa con rojo
        // ambiguo se calcula.
        var titular = ambito == AmbitoEstadoEntidad.Empresa ? AmbitoAplicacion.Empresa : AmbitoAplicacion.Trabajador;
        var ambiguas = filas.Where(e => e.Estado == Vencido && e.Vencidos > 0 && e.Vencidos == e.VencidosSinBloqueo);
        if (ambito == AmbitoEstadoEntidad.Empresa)
            return await ambiguas.AnyAsync(cancellationToken);

        var pares = tablas.EstadosRequisito;
        return await ambiguas
            .AnyAsync(e => pares.Any(p => p.AmbitoTitular == titular && p.TitularId == e.EntidadId && p.BloqueaAcceso
                && p.VencimientoEfectivo != null && p.VencimientoEfectivo < hoy
                && p.EstadoTalveg != EstadoTalvegRequisito.Vencido && p.EstadoTalveg != EstadoTalvegRequisito.Faltante
                && p.EstadoTalveg != EstadoTalvegRequisito.NoAplica), cancellationToken);
    }

    private sealed record FilaLeida(
        Guid EntidadId, short Estado, int Vencidos, int VencidosSinBloqueo, int Faltan, DateOnly? ProximoCambioEl, int VersionCalculo);

    /// <summary>
    /// Las mismas guardas (a), (b) y (c) que la versión sobre consulta, sobre filas ya leídas: las dos primeras sin tocar la base,
    /// y las que necesitan otra tabla (voluntarios inactivos, pares) solo si alguna fila las activa.
    /// </summary>
    private async Task<bool> HayFilasQueNoDecideAsync(
        AmbitoEstadoEntidad ambito, IReadOnlyCollection<FilaLeida> filas, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();

        if (filas.Any(e => (e.ProximoCambioEl != null && e.ProximoCambioEl <= hoy) || e.VersionCalculo != CalculoEstadoMaterializado.VersionDeLaRegla))
            return true;

        if (ambito == AmbitoEstadoEntidad.Trabajador)
        {
            var enRojo = filas.Where(e => e.Estado == Vencido || e.Estado == Faltante).Select(e => e.EntidadId).ToList();
            if (enRojo.Count > 0)
            {
                var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy);
                if (await inactivos.AnyAsync(id => enRojo.Contains(id), cancellationToken))
                    return true;
            }
        }

        var ambiguas = filas.Where(e => e.Estado == Vencido && e.Vencidos > 0 && e.Vencidos == e.VencidosSinBloqueo)
            .Select(e => e.EntidadId).ToList();
        if (ambiguas.Count == 0)
            return false;

        // Para una Empresa los pares no bastan (ver la versión sobre consulta): todo rojo ambiguo se calcula.
        if (ambito == AmbitoEstadoEntidad.Empresa)
            return true;

        return await tablas.EstadosRequisito.AnyAsync(
            p => p.AmbitoTitular == AmbitoAplicacion.Trabajador && ambiguas.Contains(p.TitularId) && p.BloqueaAcceso
                && p.VencimientoEfectivo != null && p.VencimientoEfectivo < hoy
                && p.EstadoTalveg != EstadoTalvegRequisito.Vencido && p.EstadoTalveg != EstadoTalvegRequisito.Faltante
                && p.EstadoTalveg != EstadoTalvegRequisito.NoAplica, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, EstadoLeido>?> LeerAsync(
        AmbitoEstadoEntidad ambito, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var disponibilidad = await DisponibilidadAsync(cancellationToken);
        if (!disponibilidad.Completo)
            return null;

        var lista = ids.Distinct().ToList();
        if (lista.Count == 0)
            return new Dictionary<Guid, EstadoLeido>();

        // Una sola lectura de las filas (de la caché del circuito si siguen al día) y las guardas en memoria: solo si alguna
        // fila lo pide se hace otra consulta (voluntarios inactivos, pares del Centro). El camino común, sin ningún rojo, no
        // gasta más sentencias que esta. El alcance: solo las entidades pedidas, que ya salen de la consulta con alcance.
        var pedidas = lista.ToHashSet();
        var filas = (await FilasAsync(ambito, disponibilidad.Version, lista, cancellationToken))
            .Where(e => pedidas.Contains(e.EntidadId))
            .ToDictionary(e => e.EntidadId);

        // (d) también aquí: una entidad sin fila se calcula, no se da por «sin nada».
        if (filas.Count < lista.Count)
            return null;

        if (await HayFilasQueNoDecideAsync(ambito, filas.Values, cancellationToken))
            return null;
        var nombres = await NombresAusentesAsync(
            ambito, filas.Values.Where(f => f.Faltan > 0).ToDictionary(f => f.EntidadId, f => f.Faltan), cancellationToken);

        return lista.ToDictionary(
            id => id,
            id => filas.TryGetValue(id, out var fila)
                ? new EstadoLeido(
                    fila.Estado == EstadoEntidad.SinEstado ? EstadoDocumento.SinCaducidad : (EstadoDocumento)fila.Estado,
                    VencidoSinBloqueo: fila.Estado == Vencido && fila.Vencidos > 0 && fila.Vencidos == fila.VencidosSinBloqueo,
                    nombres.GetValueOrDefault(id) ?? [])
                : EstadoLeido.SinNada);
    }

    /// <summary>
    /// Los Tipos obligatorios que faltan sin ningún documento, de los pares guardados (Centro × titular × Tipo en
    /// <c>Faltante</c> y marcado «bloquea el acceso»). Si el recuento de un titular no coincide con el que guarda su fila, los
    /// pares y la fila describen momentos distintos: esos pocos se preguntan al servicio de siempre.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> NombresAusentesAsync(
        AmbitoEstadoEntidad ambito, IReadOnlyDictionary<Guid, int> faltanPorTitular, CancellationToken cancellationToken)
    {
        if (faltanPorTitular.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<string>>();

        var titular = ambito == AmbitoEstadoEntidad.Empresa ? AmbitoAplicacion.Empresa : AmbitoAplicacion.Trabajador;
        var titulares = faltanPorTitular.Keys.ToList();
        var ausentes = await tablas.EstadosRequisito
            .Where(p => p.AmbitoTitular == titular && titulares.Contains(p.TitularId) && p.BloqueaAcceso
                && p.EstadoTalveg == EstadoTalvegRequisito.Faltante)
            .Select(p => new { p.TitularId, p.CentroId, p.TipoDocumentoId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var porTitular = ausentes.GroupBy(p => p.TitularId).ToDictionary(g => g.Key, g => g.ToList());
        var coinciden = titulares.Where(id => porTitular.GetValueOrDefault(id)?.Count == faltanPorTitular[id]).ToList();
        var dudosos = titulares.Except(coinciden).ToList();

        var tipoIds = ausentes.Where(p => coinciden.Contains(p.TitularId)).Select(p => p.TipoDocumentoId).Distinct().ToList();
        var nombresDeTipo = tipoIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await tiposDocumentoContext.TiposDocumento
                .Where(t => tipoIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Nombre })
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var resultado = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var id in coinciden)
            resultado[id] = porTitular[id]
                .Select(p => nombresDeTipo.GetValueOrDefault(p.TipoDocumentoId) ?? string.Empty)
                .Where(n => n.Length > 0)
                .Distinct()
                .Order(StringComparer.CurrentCulture)
                .ToList();

        if (dudosos.Count > 0)
        {
            var exactos = ambito == AmbitoEstadoEntidad.Empresa
                ? await requisitosObligatorios.DeEmpresasAsync(
                    empresasContext.Empresas.Where(e => dudosos.Contains(e.Id)).Select(e => e.Id), cancellationToken)
                : await requisitosObligatorios.DeTrabajadoresAsync(
                    trabajadoresContext.Trabajadores.Where(t => dudosos.Contains(t.Id)).Select(t => t.Id), cancellationToken);
            foreach (var id in dudosos)
                resultado[id] = RequisitosObligatoriosIncumplidos.NombresAusentes(exactos.GetValueOrDefault(id) ?? []);
        }

        return resultado;
    }
}
