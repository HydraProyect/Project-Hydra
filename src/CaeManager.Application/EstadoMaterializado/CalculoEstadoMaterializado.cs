using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Lo que una acción cambió directamente. El servicio calcula a partir de aquí, bajo el contexto del Tenant de la conexión,
/// todo lo que depende de ellos (la clausura), y rehace los pares por titular, no por Centro (diseño § 13.2):
/// <list type="bullet">
/// <item><see cref="CentroIds"/>: Centros cuyo requisito, configuración o acreditación cambió. Se rehacen enteros: su fila,
/// todos sus pares y la fila de cada Trabajador y Empresa que opera en ellos.</item>
/// <item><see cref="TrabajadorIds"/> y <see cref="EmpresaIds"/>: titulares cuyo documento o relación cambió. Se rehacen su
/// fila, la de su Empresa, sus pares en cada Centro donde operan (o donde ya tenían filas) y la fila de esos Centros; los
/// pares de los demás titulares de esos Centros no se tocan.</item>
/// <item><see cref="CentrosAfectados"/>: Centros cuya relación con los titulares raíz cambió (alta o baja de una
/// Asignación). Se rehace su fila y los pares de los titulares raíz en ellos, no el Centro entero.</item>
/// </list>
///
/// <para>
/// Quien las da (el detector de cambios, <c>DetectorCambiosEstadoInterceptor</c> e <c>InventarioDisparadoresEstado</c>) nombra <b>todas</b> las raíces de la acción, también las que dejan de estar
/// relacionadas: una baja de Asignación da el par Centro × Trabajador (<see cref="DeAsignacion"/>), porque después de la
/// baja el Trabajador ya no lleva al Centro; un Trabajador que cambia de Empresa da también la Empresa anterior, y un Trabajador borrado da también su Empresa (la clausura no ve la ficha borrada y los pares de esa Empresa en sus Centros se quedarían sin recalcular).
/// </para>
/// </summary>
public sealed record RaicesDeRecalculo(
    IReadOnlyCollection<Guid> CentroIds, IReadOnlyCollection<Guid> TrabajadorIds, IReadOnlyCollection<Guid> EmpresaIds)
{
    /// <summary>Centros cuya relación con los titulares raíz cambió; ver el resumen del tipo.</summary>
    public IReadOnlyCollection<Guid> CentrosAfectados { get; init; } = [];

    public static RaicesDeRecalculo DeCentros(params Guid[] ids) => new(ids, [], []);
    public static RaicesDeRecalculo DeTrabajadores(params Guid[] ids) => new([], ids, []);
    public static RaicesDeRecalculo DeEmpresas(params Guid[] ids) => new([], [], ids);

    /// <summary>Alta o baja de la Asignación de un Trabajador en un Centro.</summary>
    public static RaicesDeRecalculo DeAsignacion(Guid centroId, Guid trabajadorId) =>
        new([], [trabajadorId], []) { CentrosAfectados = [centroId] };

    public bool EstaVacia => CentroIds.Count == 0 && TrabajadorIds.Count == 0 && EmpresaIds.Count == 0 && CentrosAfectados.Count == 0;

    /// <summary>Cuántas raíces distintas nombra, para decidir si caben en la cola una a una.</summary>
    public int Cuantas => CentroIds.Concat(CentrosAfectados).Distinct().Count() + TrabajadorIds.Distinct().Count() + EmpresaIds.Distinct().Count();
}

/// <summary>
/// La clausura de unas raíces: los Centros, Trabajadores y Empresas cuyas filas rehace un recálculo, y los Centros que se
/// rehacen con todos sus pares (los Centros raíz). El detector la calcula, toma los cerrojos de todas sus entidades y la
/// vuelve a calcular bajo ellos (diseño § 4.4).
/// </summary>
public sealed record ClausuraDeRecalculo(
    IReadOnlyList<Guid> Centros, IReadOnlyList<Guid> Trabajadores, IReadOnlyList<Guid> Empresas,
    IReadOnlyCollection<Guid> CentrosCompletos)
{
    public static ClausuraDeRecalculo Vacia { get; } = new([], [], [], []);

    public int Entidades => Centros.Count + Trabajadores.Count + Empresas.Count;

    /// <summary>Si todo lo de <paramref name="otra"/> está también en esta.</summary>
    public bool Contiene(ClausuraDeRecalculo otra) =>
        !otra.Centros.Except(Centros).Any()
        && !otra.Trabajadores.Except(Trabajadores).Any()
        && !otra.Empresas.Except(Empresas).Any()
        && !otra.CentrosCompletos.Except(CentrosCompletos).Any();
}

/// <summary>
/// El estado calculado de un conjunto de entidades, listo para escribir, y su alcance: lo que el escritor sustituye.
/// <list type="bullet">
/// <item>La fila de cada Centro, Trabajador y Empresa de <see cref="CentroIds"/>, <see cref="TrabajadorIds"/> y
/// <see cref="EmpresaIds"/>, y las acreditaciones de esos Centros. Una entidad de esas listas que ya no existe (borrada) se
/// queda sin fila.</item>
/// <item>Los pares: todos los de <see cref="CentrosConTodosLosPares"/>, y en los demás Centros de <see cref="CentroIds"/>
/// solo los de los Trabajadores y Empresas de esas listas.</item>
/// </list>
/// Lo que el cálculo no trae dentro del alcance desaparece; lo que trae y no cambió no se reescribe.
/// </summary>
public sealed record CalculoEstadoMaterializado(
    DateOnly Dia,
    IReadOnlyList<Guid> CentroIds,
    IReadOnlyList<Guid> TrabajadorIds,
    IReadOnlyList<Guid> EmpresaIds,
    IReadOnlyList<Guid> CentrosConTodosLosPares,
    IReadOnlyList<FilaEstadoRequisito> Pares,
    IReadOnlyList<FilaEstadoEntidad> Entidades,
    IReadOnlyList<FilaEstadoEnPlataforma> Plataforma)
{
    /// <summary>Versión de la regla de cálculo que se escribe en <c>VersionCalculo</c>: se sube al cambiar la regla.</summary>
    public const int VersionDeLaRegla = 1;
}

/// <summary>Una fila de <see cref="EstadoRequisito"/>.</summary>
public sealed record FilaEstadoRequisito(
    Guid CentroId, AmbitoAplicacion AmbitoTitular, Guid TitularId, Guid TipoDocumentoId, Guid? DocumentoEfectivoId,
    EstadoTalvegRequisito EstadoTalveg, bool BloqueaAcceso, bool CuentaParaCumplimiento, EstadoAcreditacion? EstadoPlataforma,
    DateOnly? VencimientoEfectivo, DateOnly? ProximoCambioEl);

/// <summary>Una fila de <see cref="EstadoEntidad"/>.</summary>
public sealed record FilaEstadoEntidad(
    AmbitoEstadoEntidad Ambito, Guid EntidadId, short Estado, EstadoPlataformaCentro? EstadoPlataforma,
    int Vencidos, int VencidosSinBloqueo, int Urgentes, int Proximos, int Faltan, int SinConfirmar, int PendientesEnPlataforma,
    int Requeridos, int AlDia, DateOnly? ProximoCambioEl);

/// <summary>Una fila de <see cref="EstadoEnPlataforma"/>.</summary>
public sealed record FilaEstadoEnPlataforma(
    Guid CentroId, Guid CanalGestionDocumentalId, AmbitoAplicacion AmbitoTitular, Guid TitularId, Guid TipoDocumentoId,
    EstadoAcreditacion Estado, EstadoVigenciaEnPlataforma EstadoVigencia, DateOnly? FechaVencimientoEnPlataforma,
    DateOnly? ProximoCambioEl);

/// <summary>
/// Cuánto se calculó (las cinco cifras del cálculo) y cuántas filas cambiaron de verdad en la base
/// (<see cref="FilasCambiadas"/>: borradas, actualizadas o añadidas; una fila igual no cuenta).
/// </summary>
public sealed record ResultadoRecalculoEstado(int Centros, int Trabajadores, int Empresas, int Pares, int AcreditacionesEnPlataforma)
{
    public static ResultadoRecalculoEstado Ninguno { get; } = new(0, 0, 0, 0, 0);

    public int FilasCambiadas { get; init; }

    public ResultadoRecalculoEstado Mas(ResultadoRecalculoEstado otro) => new(
        Centros + otro.Centros, Trabajadores + otro.Trabajadores, Empresas + otro.Empresas,
        Pares + otro.Pares, AcreditacionesEnPlataforma + otro.AcreditacionesEnPlataforma)
    {
        FilasCambiadas = FilasCambiadas + otro.FilasCambiadas,
    };
}

/// <summary>
/// Escribe un <see cref="CalculoEstadoMaterializado"/> en las tablas del estado materializado del Tenant de la conexión, en
/// una transacción (la de la acción si ya hay una abierta). Compara con lo guardado dentro del alcance del cálculo: borra lo
/// que ya no está (un par que deja de exigirse), actualiza solo las columnas calculadas de lo que cambió (el permiso de
/// UPDATE de <c>cae_app_runtime</c> no cubre más) e inserta lo nuevo. Una fila igual no se reescribe.
/// </summary>
public interface IEscritorEstadoMaterializado
{
    /// <summary>
    /// Escribe el cálculo y, si alguna fila cambió, sube la versión del Tenant. Devuelve cuántas filas cambiaron.
    /// </summary>
    /// <param name="crearVersionSiFalta">
    /// Solo el relleno completo del Tenant: crea su fila de <see cref="VersionEstadoTenant"/>, que es lo que dirá a los
    /// lectores que el Tenant ya está entero (diseño § 7.3), aunque no cambie ninguna fila. Un recálculo parcial solo sube la
    /// versión si ya existe y si cambió alguna fila.
    /// </param>
    Task<int> EscribirAsync(CalculoEstadoMaterializado calculo, bool crearVersionSiFalta, CancellationToken cancellationToken);

    /// <summary>Escribe el cálculo sin tocar la versión del Tenant (un lote del relleno). Devuelve cuántas filas cambiaron.</summary>
    Task<int> EscribirFilasAsync(CalculoEstadoMaterializado calculo, CancellationToken cancellationToken);

    /// <summary>
    /// La versión del Tenant al final del relleno: la crea si falta (<paramref name="crearSiFalta"/>) y la sube solo si
    /// <paramref name="huboCambios"/>.
    /// </summary>
    Task SubirVersionAsync(bool crearSiFalta, bool huboCambios, CancellationToken cancellationToken);

    /// <summary>
    /// Borra las filas del Tenant de la conexión que no son de esos Centros, Trabajadores y Empresas (los que el relleno
    /// recorrió): las de una entidad que ya no existe. Devuelve cuántas borró.
    /// </summary>
    Task<int> PurgarHuerfanosAsync(
        IReadOnlyCollection<Guid> centroIds, IReadOnlyCollection<Guid> trabajadorIds, IReadOnlyCollection<Guid> empresaIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ejecuta el trabajo en una transacción (la abierta o una propia) con el cerrojo exclusivo del Tenant de la conexión:
    /// ningún recálculo en línea ni de la cola escribe el estado del Tenant mientras dura, y los que lo intentan encolan su
    /// cambio en vez de esperar (diseño § 13.8).
    /// </summary>
    Task<T> EnTransaccionConTenantExclusivoAsync<T>(Func<Task<T>> trabajo, CancellationToken cancellationToken);

    /// <summary>
    /// Los Centros que ya tienen escrita una fila (par o acreditación en la Plataforma CAE) de alguno de esos titulares
    /// (Trabajadores o Empresas). La clausura los añade porque la relación que los unía puede haberse roto en la propia
    /// acción: una acreditación del documento sustituido de un Trabajador que ya no está asignado al Centro se quedaría sin
    /// recalcular.
    /// </summary>
    Task<IReadOnlyList<Guid>> CentrosConFilasDeTitularesAsync(IReadOnlyCollection<Guid> titularIds, CancellationToken cancellationToken);
}

/// <summary>
/// Servicio de recálculo del estado materializado por pares, acotado a lo afectado (diseño del estado materializado,
/// PR 3 y 3b). Calcula con las mismas reglas que las lecturas de hoy, sin otra copia: el estado del Centro con
/// <c>ICalculoEstadoCentroService</c>, el de Empresa y Trabajador con la regla del 2026-10-23
/// (<c>RequisitosObligatoriosIncumplidos</c>) y los pares con los pares exigidos del Centro. Lo que añade es separar el
/// estado en TALVEG del estado en la Plataforma CAE, acotar la carga a la clausura de las raíces, rehacer solo los pares de
/// los titulares afectados (<see cref="RaicesDeRecalculo"/>) y calcular <c>ProximoCambioEl</c>. La fila del Centro no es una
/// suma de pares: sale entera de la regla del Centro, que cuenta también documentos que ningún par recoge (diseño § 13.2).
///
/// <para>
/// Lo llama el detector de cambios al final de la transacción de cada guardado que toca una entrada del cálculo
/// (<c>RecalculoEstadoEnLinea</c>, en Infrastructure). Las leen los lectores de pantalla (Centros, Empresas, Trabajadores y las colas de trabajo) cuando el Tenant está completo.
/// </para>
/// </summary>
public interface IRecalculoEstadoMaterializado
{
    /// <summary>Calcula, sin escribir, el estado de la clausura de las raíces.</summary>
    Task<CalculoEstadoMaterializado> CalcularAsync(RaicesDeRecalculo raices, CancellationToken cancellationToken);

    /// <summary>Calcula la clausura de las raíces y la escribe.</summary>
    Task<ResultadoRecalculoEstado> RecalcularAsync(RaicesDeRecalculo raices, CancellationToken cancellationToken);

    /// <summary>La clausura de las raíces, sin calcular su estado: lo que <see cref="RecalcularAsync"/> rehace. Solo lee.</summary>
    Task<ClausuraDeRecalculo> CalcularClausuraAsync(RaicesDeRecalculo raices, CancellationToken cancellationToken);

    /// <summary>
    /// Calcula y escribe una clausura ya calculada: la que el detector volvió a calcular bajo sus cerrojos. Si el día de
    /// negocio cambia mientras calcula, calcula otra vez con el día nuevo sobre la misma clausura.
    /// </summary>
    Task<ResultadoRecalculoEstado> RecalcularClausuraAsync(ClausuraDeRecalculo clausura, CancellationToken cancellationToken);

    /// <summary>
    /// Relleno inicial del Tenant de la conexión: todos sus Centros (por lotes de 200), Trabajadores y Empresas, y al final
    /// su fila de versión. Idempotente: repetirlo deja las mismas filas.
    /// </summary>
    Task<ResultadoRecalculoEstado> RellenarTenantAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Calcula, sin escribir, el Tenant entero de la conexión con los mismos lotes que <see cref="RellenarTenantAsync"/>, y
    /// entrega cada lote a <paramref name="porLote"/> (el cotejo nocturno lo compara con lo guardado). Devuelve las entidades
    /// que recorrió: una fila guardada de otra entidad es de una entidad que ya no existe.
    /// </summary>
    Task<EntidadesDelTenant> CalcularTenantPorLotesAsync(Func<CalculoEstadoMaterializado, Task> porLote, CancellationToken cancellationToken);
}

/// <summary>Los Centros, Trabajadores y Empresas que existen en el Tenant, en el orden en que los recorren los lotes.</summary>
public sealed record EntidadesDelTenant(IReadOnlyList<Guid> Centros, IReadOnlyList<Guid> Trabajadores, IReadOnlyList<Guid> Empresas);
